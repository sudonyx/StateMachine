using System.Text.Json;
using System.Text.Json.Serialization;
using DurableStateMachine.Api;
using DurableStateMachine.Core;
using DurableStateMachine.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var databasePath = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB") ?? "durable-state-machine.db";
var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    DefaultTimeout = 30,
    Pooling = true
}.ToString();

builder.Services.AddDbContext<MachineDbContext>(options => options.UseSqlite(connectionString));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddOpenApi();
builder.Services.AddScoped<MachineStore>();
builder.Services.AddSingleton<InstanceLockRegistry>();
builder.Services.AddSingleton<InputFaultPlan>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<TimerProcessingService>();
builder.Services.AddSingleton<EffectFaultPlan>();
builder.Services.AddSingleton<SyntheticProviderStore>();
builder.Services.AddScoped<EffectCoordinator>();
builder.Services.AddHttpClient<HarnessExecutionClient>();
builder.Services.AddScoped<HarnessCapabilityBindings>();
builder.Services.AddScoped<HarnessEffectCoordinator>();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MachineDbContext>();
    await database.Database.EnsureCreatedAsync();
    await database.EnsureGate4SchemaAsync();
    await database.EnsureGate5SchemaAsync();
    await database.EnsureGate6SchemaAsync();
    await database.EnsureGate7SchemaAsync();
    await database.EnsureGate8SchemaAsync();
    await database.EnsureGate9SchemaAsync();
    await database.EnsureGate10SchemaAsync();
    await database.EnsureGate11SchemaAsync();
    await database.EnsureGate12SchemaAsync();
    await database.EnsureGate12AttemptColumnsAsync();
}

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception exception)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ApiErrors");
        var (status, code, message, issues) = exception switch
        {
            MachineDefinitionException validation => (StatusCodes.Status422UnprocessableEntity, "invalid-definition", validation.Message, validation.Issues),
            DefinitionConflictException conflict => (StatusCodes.Status409Conflict, "conflict", conflict.Message, (IReadOnlyList<ValidationIssue>?)null),
            ResourceNotFoundException missing => (StatusCodes.Status404NotFound, "not-found", missing.Message, (IReadOnlyList<ValidationIssue>?)null),
            EffectInterruptionException interrupted => (StatusCodes.Status503ServiceUnavailable, "effect-interrupted", interrupted.Message, (IReadOnlyList<ValidationIssue>?)null),
            InputInterruptionException interrupted => (StatusCodes.Status503ServiceUnavailable, "input-interrupted", interrupted.Message, (IReadOnlyList<ValidationIssue>?)null),
            HarnessConfigurationException configuration => (StatusCodes.Status503ServiceUnavailable, "harness-not-configured", configuration.Message, (IReadOnlyList<ValidationIssue>?)null),
            HarnessHttpException harnessError => (StatusCodes.Status502BadGateway, "harness-http-error", harnessError.Message, (IReadOnlyList<ValidationIssue>?)null),
            HttpRequestException harnessUnavailable => (StatusCodes.Status502BadGateway, "harness-unavailable", harnessUnavailable.Message, (IReadOnlyList<ValidationIssue>?)null),
            InvalidDataException invalidHarnessData => (StatusCodes.Status502BadGateway, "invalid-harness-response", invalidHarnessData.Message, (IReadOnlyList<ValidationIssue>?)null),
            BadHttpRequestException badRequest => (badRequest.StatusCode, "invalid-request", badRequest.Message, (IReadOnlyList<ValidationIssue>?)null),
            JsonException malformed => (StatusCodes.Status400BadRequest, "malformed-json", malformed.Message, (IReadOnlyList<ValidationIssue>?)null),
            ArgumentException argument => (StatusCodes.Status400BadRequest, "invalid-request", argument.Message, (IReadOnlyList<ValidationIssue>?)null),
            _ => (StatusCodes.Status500InternalServerError, "internal-error", "An unexpected server error occurred.", (IReadOnlyList<ValidationIssue>?)null)
        };
        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled API error for {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ApiError(code, message, issues), JsonDefaults.Options);
    }
});

app.MapOpenApi();

var definitions = app.MapGroup("/api/definitions").WithTags("Definitions");
definitions.MapPost("/", async (JsonElement definition, MachineStore store, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (definition.ValueKind != JsonValueKind.Object)
    {
        return Results.BadRequest(new ApiError("invalid-definition", "Definition body must be a JSON object."));
    }
    var registration = await store.RegisterDefinitionAsync(definition.GetRawText(), timeProvider.GetUtcNow(), cancellationToken);
    return Results.Ok(new { definition = registration.Definition, alreadyRegistered = registration.AlreadyRegistered });
}).WithName("RegisterDefinition").WithSummary("Validate and register an immutable definition version.");
definitions.MapGet("/", async (MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.ListDefinitionsAsync(cancellationToken)))
    .WithName("ListDefinitions").WithSummary("List all registered definition versions.");
definitions.MapGet("/{id}/{version}", async (string id, string version, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.GetDefinitionAsync(id, version, cancellationToken)).ToResponse()))
    .WithName("GetDefinition").WithSummary("Inspect a registered definition version.");

var instances = app.MapGroup("/api/instances").WithTags("Instances");
instances.MapPost("/", async (CreateInstanceRequest request, MachineStore store, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.DefinitionId) ||
        string.IsNullOrWhiteSpace(request.DefinitionVersion))
    {
        return Results.BadRequest(new ApiError("invalid-request", "Instance ID, definition ID, and definition version are required."));
    }
    var facts = new Dictionary<string, FactValue>(StringComparer.Ordinal);
    foreach (var (name, value) in request.Facts ?? [])
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new ApiError("invalid-facts", "Fact names must not be empty."));
        }
        facts.Add(name, ParseFact(value));
    }
    var instance = await store.CreateInstanceAsync(request.Id, request.DefinitionId, request.DefinitionVersion,
        facts, timeProvider.GetUtcNow(), cancellationToken);
    return Results.Created($"/api/instances/{Uri.EscapeDataString(instance.Id)}", instance.ToResponse());
}).WithName("CreateInstance").WithSummary("Create an instance at its definition's initial state.");
instances.MapGet("/{id}", async (string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.GetInstanceAsync(id, cancellationToken)).ToResponse()))
    .WithName("GetInstance").WithSummary("Inspect current durable instance state and facts.");
instances.MapGet("/{id}/concerns", async (string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.GetConcernInspectionAsync(id, cancellationToken)).ToResponse()))
    .WithName("InspectConcernStates")
    .WithSummary("Inspect the parent lifecycle and each declared durable concern state independently.");
instances.MapGet("/{id}/concerns/history", async (string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.ListConcernHistoryAsync(id, cancellationToken))
        .Select(entry => entry.ToResponse())))
    .WithName("ListConcernTransitionHistory")
    .WithSummary("List accepted concern transitions in concern-revision order.");
instances.MapGet("/{id}/concerns/replay", async (string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetConcernReplayAsync(id, cancellationToken)))
    .WithName("ReconstructConcernHistory")
    .WithSummary("Reconstruct declared concern states from immutable transition evidence without side effects.");
instances.MapPost("/{id}/events", async (string id, SubmitEventRequest request, MachineStore store, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (await store.IsInputProtectedInstanceAsync(id, cancellationToken))
    {
        var rejected = await store.RecordLegacyEventRejectionAsync(id, request.Event ?? string.Empty,
            JsonSerializer.Serialize(request, JsonDefaults.Options), timeProvider.GetUtcNow(), cancellationToken);
        return Results.Json(new ApiError("legacy-event-endpoint-restricted",
            $"Direct event submission is disabled for this input-protected definition. Inspect inbound receipt '{rejected.Id}'."),
            statusCode: StatusCodes.Status403Forbidden);
    }
    if (string.IsNullOrWhiteSpace(request.Event))
    {
        return Results.BadRequest(new ApiError("invalid-request", "Event must be a non-empty string."));
    }
    var facts = ParseFacts(request.Facts);

    var decisionEvidence = request.DecisionEvidence?.ToCoreEvidence();
    var result = await store.SubmitEventAsync(id, request.Event, timeProvider.GetUtcNow(), cancellationToken,
        facts, decisionEvidence, request.FactsProvenance);
    var response = new EventSubmissionResponse(result.Attempt.ToResponse(), result.Instance.ToResponse(),
        result.Transition?.ToResponse());
    return result.ConcurrencyConflict ? Results.Conflict(response) : Results.Ok(response);
}).WithName("SubmitEvent").WithSummary("Evaluate and record an event; workflow-level refusals return an inspectable result.");
instances.MapPost("/{id}/commands", async (
    string id, SubmitInputRequest request, MachineStore store, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.MessageId) || string.IsNullOrWhiteSpace(request.Type) ||
        request.MessageId.Length > 128 || request.DeliveryId?.Length > 128 ||
        request.OperationId?.Length > 128 || request.ObservationId?.Length > 128 ||
        request.ExpectedRevision is null || request.ExpectedRevision < 0)
    {
        return await RecordMalformedInput(store, id, "Command", request, trustedSource: false,
            "Message ID (1-128 characters), command type, and non-negative expected revision are required.",
            timeProvider,
            cancellationToken);
    }
    IReadOnlyDictionary<string, FactValue> facts;
    ExternalDecisionEvidence? decisionEvidence;
    try
    {
        facts = ParseFacts(request.Facts);
        decisionEvidence = request.DecisionEvidence?.ToCoreEvidence();
    }
    catch (Exception exception) when (exception is ArgumentException or JsonException)
    {
        return await RecordMalformedInput(store, id, "Command", request, trustedSource: false,
            exception.Message, timeProvider, cancellationToken);
    }
    var result = await store.SubmitInputAsync(id, "Command", request.MessageId,
        request.DeliveryId, request.OperationId, request.ObservationId, request.Type,
        request.ExpectedRevision.Value, request.CorrelationId, request.Source, request.OccurredAt,
        request.Payload, facts, request.Evidence, decisionEvidence,
        request.FactsProvenance, trustedSource: false, timeProvider.GetUtcNow(), cancellationToken);
    var response = ToInputResponse(result, await store.GetInstanceAsync(id, cancellationToken));
    return result.Submission?.ConcurrencyConflict == true ? Results.Conflict(response) : Results.Ok(response);
}).WithName("SubmitCommand").WithSummary("Submit a declared command; receipt alone does not accept a workflow event.");
instances.MapPost("/{id}/observations", async (
    string id, SubmitInputRequest request, HttpRequest httpRequest, MachineStore store, TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.MessageId) || string.IsNullOrWhiteSpace(request.Type) ||
        request.MessageId.Length > 128 || request.DeliveryId?.Length > 128 ||
        request.OperationId?.Length > 128 || request.ObservationId?.Length > 128 ||
        string.IsNullOrWhiteSpace(request.CorrelationId) ||
        request.ExpectedRevision is null || request.ExpectedRevision < 0)
    {
        return await RecordMalformedInput(store, id, "Observation", request, IsTrustedIngestion(httpRequest),
            "Message ID (1-128 characters), observation type, operation correlation ID, and non-negative expected revision are required.",
            timeProvider,
            cancellationToken);
    }
    var trustedSource = IsTrustedIngestion(httpRequest);
    IReadOnlyDictionary<string, FactValue> facts;
    ExternalDecisionEvidence? decisionEvidence;
    try
    {
        facts = ParseFacts(request.Facts);
        decisionEvidence = request.DecisionEvidence?.ToCoreEvidence();
    }
    catch (Exception exception) when (exception is ArgumentException or JsonException)
    {
        return await RecordMalformedInput(store, id, "Observation", request, trustedSource,
            exception.Message, timeProvider, cancellationToken);
    }
    var result = await store.SubmitInputAsync(id, "Observation", request.MessageId,
        request.DeliveryId, request.OperationId, request.ObservationId, request.Type,
        request.ExpectedRevision.Value, request.CorrelationId, request.Source, request.OccurredAt,
        request.Payload, facts, request.Evidence, decisionEvidence,
        request.FactsProvenance, trustedSource, timeProvider.GetUtcNow(), cancellationToken);
    var response = ToInputResponse(result, await store.GetInstanceAsync(id, cancellationToken));
    return result.Submission?.ConcurrencyConflict == true ? Results.Conflict(response) : Results.Ok(response);
}).WithName("SubmitObservation").WithSummary("Submit an external claim; only the configured trusted ingestion boundary can authorize it.");
instances.MapPost("/{id}/expected-operations", async (
    string id, RegisterExpectedOperationRequest request, HttpRequest httpRequest, MachineStore store,
    TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (!IsTrustedIngestion(httpRequest))
    {
        return Results.Json(new ApiError("trusted-ingestion-required",
            "Expected operations can only be created through the configured controlled demonstration boundary."),
            statusCode: StatusCodes.Status403Forbidden);
    }
    if (string.IsNullOrWhiteSpace(request.OperationId) || string.IsNullOrWhiteSpace(request.ObservationType) ||
        string.IsNullOrWhiteSpace(request.SourceIdentity))
    {
        return Results.BadRequest(new ApiError("invalid-request", "Operation ID, observation type, and source identity are required."));
    }
    var operation = await store.RegisterExpectedOperationAsync(id, request.OperationId, request.ObservationType,
        request.SourceIdentity, request.ExpectedRevision, request.RequiredEvidence, timeProvider.GetUtcNow(), cancellationToken);
    return Results.Created($"/api/instances/{Uri.EscapeDataString(id)}/expected-operations/{Uri.EscapeDataString(operation.OperationId)}",
        operation.ToResponse());
}).WithName("RegisterExpectedOperation").WithSummary("Register an expected result through the controlled trusted demonstration boundary.");
instances.MapGet("/{id}/expected-operations", async (string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.ListExpectedOperationsAsync(id, cancellationToken)).Select(operation => operation.ToResponse())))
    .WithName("ListExpectedOperations").WithSummary("Inspect expected external results and their resolution status.");
instances.MapGet("/{id}/inbound-messages", async (string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.ListInboundMessagesAsync(id, cancellationToken)).Select(message => message.ToResponse())))
    .WithName("ListInboundMessages").WithSummary("Inspect command and observation receipts, authority decisions, and outcomes.");
instances.MapGet("/{id}/inbound-messages/{receiptId}", async (
    string id, string receiptId, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.GetInboundMessageAsync(id, receiptId, cancellationToken)).ToResponse()))
    .WithName("GetInboundMessage").WithSummary("Inspect one inbound message receipt.");
instances.MapPost("/{id}/inbound-messages/{receiptId}/resume", async (
    string id, string receiptId, MachineStore store, TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    var result = await store.ResumeInboundMessageAsync(id, receiptId, timeProvider.GetUtcNow(), cancellationToken);
    var response = ToInputResponse(result, await store.GetInstanceAsync(id, cancellationToken));
    return result.Submission?.ConcurrencyConflict == true ? Results.Conflict(response) : Results.Ok(response);
}).WithName("ResumeInboundMessage")
  .WithSummary("Resume one durable unresolved input from its original envelope and persisted authority context.");
instances.MapGet("/{id}/staged-observations", async (
    string id, MachineStore store, CancellationToken cancellationToken) =>
{
    var staged = await store.ListStagedObservationsAsync(id, cancellationToken);
    return Results.Ok(staged.Select(result => new
    {
        result.Id,
        result.InstanceId,
        result.ExpectedOperationId,
        result.WorkflowOperationId,
        result.HarnessExecutionId,
        result.ProviderIdentity,
        result.Capability,
        result.DefinitionId,
        result.DefinitionVersion,
        result.ExecutionRevision,
        result.SourceAuthority,
        result.ResultClassification,
        result.ObservationType,
        result.ProviderEffectId,
        result.OccurredAt,
        result.PayloadJson,
        result.EvidenceJson,
        result.EvidenceReferencesJson,
        result.PayloadDigest,
        result.ReceivedAt,
        result.Status,
        result.ReceiptId,
        result.AttemptId,
        result.Outcome,
        result.Explanation,
        result.ResolvedAt
    }));
}).WithName("ListStagedObservations")
  .WithSummary("Inspect durable external results without evaluating or invoking an external system.");
instances.MapPost("/{id}/staged-observations/{resultId}/resume", async (
    string id, string resultId, MachineStore store, TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    var result = await store.ContinueStagedObservationAsync(
        id, resultId, timeProvider.GetUtcNow(), cancellationToken);
    var response = ToInputResponse(result, await store.GetInstanceAsync(id, cancellationToken));
    return result.Submission?.ConcurrencyConflict == true
        ? Results.Conflict(response)
        : Results.Ok(response);
}).WithName("ResumeStagedObservation")
  .WithSummary("Continue one original durably staged external result through the existing Gate 5 input checks.");
instances.MapGet("/{id}/attempts", async (string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.ListAttemptsAsync(id, cancellationToken)).Select(attempt => attempt.ToResponse())))
    .WithName("ListAttempts").WithSummary("List event attempts in received order.");
instances.MapGet("/{id}/attempts/{attemptId}", async (string id, string attemptId, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.GetAttemptAsync(id, attemptId, cancellationToken)).ToResponse()))
    .WithName("GetAttempt").WithSummary("Inspect an individual event attempt.");
instances.MapGet("/{id}/history", async (string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.ListHistoryAsync(id, cancellationToken)).Select(entry => entry.ToResponse())))
    .WithName("ListTransitionHistory").WithSummary("List accepted transitions in revision order.");
instances.MapGet("/{id}/history/{historyId}", async (string id, string historyId, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.GetHistoryEntryAsync(id, historyId, cancellationToken)).ToResponse()))
    .WithName("GetTransitionHistory").WithSummary("Inspect one accepted transition.");
instances.MapGet("/{id}/replay", async (string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetHistoricalReplayAsync(id, verifyDecisions: false, cancellationToken)))
    .WithName("ReconstructInstanceHistory")
    .WithSummary("Reconstruct state from accepted history without evaluating inputs or performing side effects.");
instances.MapGet("/{id}/replay/decision-verification", async (
    string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetHistoricalReplayAsync(id, verifyDecisions: true, cancellationToken)))
    .WithName("VerifyHistoricalDecisions")
    .WithSummary("Re-evaluate recorded decisions using only immutable historical evidence and the bound definition.");
instances.MapPost("/{id}/migration-preview", async (
    string id, MigrationRequest request, MachineStore store, CancellationToken cancellationToken) =>
{
    var plan = OrionMigrationPlans.Resolve(request);
    var preview = await store.PreviewMigrationAsync(id, request, plan, cancellationToken);
    return Results.Ok(preview);
}).WithName("PreviewInstanceMigration")
  .WithSummary("Read-only eligibility preview for an explicitly named, registered migration contract.");
instances.MapPost("/{id}/migrations", async (
    string id, MigrationRequest request, HttpRequest httpRequest, MachineStore store,
    TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (!app.Environment.IsDevelopment() || !IsTrustedIngestion(httpRequest))
    {
        return Results.Json(new ApiError("controlled-administration-required",
            "Migration execution requires the configured development controlled-administration boundary."),
            statusCode: StatusCodes.Status403Forbidden);
    }
    var plan = OrionMigrationPlans.Resolve(request);
    var result = await store.ExecuteMigrationAsync(
        id, request, plan, timeProvider.GetUtcNow(), cancellationToken);
    return Results.Ok(new
    {
        migration = MigrationResponse.From(result.Migration),
        instance = result.Instance.ToResponse()
    });
}).WithName("ExecuteInstanceMigration")
  .WithSummary("Explicitly execute and durably record an eligible migration under the development administration boundary.");
instances.MapGet("/{id}/migrations", async (
    string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.ListMigrationsAsync(id, cancellationToken)).Select(MigrationResponse.From)))
    .WithName("ListInstanceMigrations")
    .WithSummary("Inspect committed and refused migration attempts without changing the instance.");
instances.MapGet("/{id}/recovery", async (
    string id, MachineStore store, TimeProvider timeProvider, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetRecoveryAssessmentAsync(id, timeProvider.GetUtcNow(), cancellationToken)))
    .WithName("AssessInstanceRecovery")
    .WithSummary("Inspect history integrity and unresolved work without contacting providers or processing timers.");
instances.MapGet("/{id}/timers", async (string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.ListTimersAsync(id, cancellationToken)).Select(timer => timer.ToResponse())))
    .WithName("ListTimers").WithSummary("Inspect durable timers and their scheduling, cancellation, and processing evidence.");
instances.MapGet("/{id}/timers/{timerId}", async (
    string id, string timerId, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.GetTimerAsync(id, timerId, cancellationToken)).ToResponse()))
    .WithName("GetTimer").WithSummary("Inspect one durable timer and its outcome evidence.");
instances.MapGet("/{id}/pending-work", async (
    string id, MachineStore store, HarnessEffectCoordinator coordinator, CancellationToken cancellationToken) =>
{
    await store.GetInstanceAsync(id, cancellationToken);
    var operations = await coordinator.ListAsync(id, cancellationToken);
    var pending = new List<EffectOperationResponse>();
    foreach (var operation in operations)
    {
        var response = await coordinator.GetResponseAsync(id, operation.OperationId, cancellationToken);
        if (response.Status is "Pending" or "AttemptInitiated" or "OutcomeUnknown" or "ProviderSucceeded")
        {
            pending.Add(response);
        }
    }
    var timers = (await store.ListTimersAsync(id, cancellationToken))
        .Where(timer => timer.Status == "Pending")
        .Select(timer => timer.ToResponse())
        .ToArray();
    return Results.Ok(new { instanceId = id, supported = true, items = pending, timers });
})
    .WithName("ListPendingWork").WithSummary("Inspect unresolved external-effect intents and pending durable timers.");

if (app.Environment.IsDevelopment() &&
    string.Equals(Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS"), "true",
        StringComparison.OrdinalIgnoreCase))
{
    app.MapPost("/api/test-controls/inputs/{instanceId}/{point}", (string instanceId, string point, InputFaultPlan plan) =>
    {
        plan.Arm(instanceId, point);
        return Results.Ok(new { instanceId, point, armed = true });
    }).WithName("ArmInputFault")
      .WithSummary("Development-only one-shot interruption for durable input recovery tests.");

    app.MapPost("/api/test-controls/inputs/{instanceId}/{point}/pause", (
        string instanceId, string point, InputFaultPlan plan) =>
    {
        plan.ArmPause(instanceId, point);
        return Results.Ok(new { instanceId, point, paused = true });
    }).WithName("ArmInputPause")
      .WithSummary("Development-only pause at an input boundary for process-termination tests.");

    app.MapGet("/api/test-controls/inputs/{instanceId}/{point}/pause-status", (
        string instanceId, string point, InputFaultPlan plan) =>
        Results.Ok(new { instanceId, point, reached = plan.IsPauseReached(instanceId, point) }))
      .WithName("GetInputPauseStatus")
      .WithSummary("Development-only observation of a deterministic process-test pause.");

instances.MapPost("/{id}/direct-effects", async (
    string id, StartEffectRequest request, HttpRequest httpRequest, EffectCoordinator coordinator,
    TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (!IsTrustedIngestion(httpRequest))
    {
        return Results.Json(new ApiError("trusted-ingestion-required",
            "Synthetic effects can only be initiated through the configured controlled demonstration boundary."),
            statusCode: StatusCodes.Status403Forbidden);
    }
    var operation = await coordinator.RequestAsync(id, request, timeProvider.GetUtcNow(), cancellationToken);
    return Results.Ok(operation);
}).WithName("StartSyntheticEffect").WithSummary("Persist an effect intent, invoke a separate synthetic provider, and submit its result through Gate 5.");

instances.MapPost("/{id}/direct-effects/{operationId}/resume", async (
    string id, string operationId, HttpRequest httpRequest, EffectCoordinator coordinator,
    TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (!IsTrustedIngestion(httpRequest))
    {
        return Results.Json(new ApiError("trusted-ingestion-required",
            "Direct Gate 6 provider recovery is restricted to the development-only comparison route."),
            statusCode: StatusCodes.Status403Forbidden);
    }
    return Results.Ok(await coordinator.ResumeAsync(id, operationId, timeProvider.GetUtcNow(), cancellationToken));
}).WithName("ResumeDirectSyntheticEffect").WithSummary("Development-only historical Gate 6 direct provider recovery route.");
}

instances.MapPost("/{id}/effects", async (
    string id, StartEffectRequest request, HttpRequest httpRequest, HarnessEffectCoordinator coordinator,
    TimeProvider timeProvider, CancellationToken cancellationToken) =>
{
    if (!IsTrustedIngestion(httpRequest))
    {
        return Results.Json(new ApiError("trusted-ingestion-required",
            "Harness-backed effects can only be initiated through the configured controlled demonstration boundary."),
            statusCode: StatusCodes.Status403Forbidden);
    }
    return Results.Ok(await coordinator.RequestAsync(id, request, timeProvider.GetUtcNow(), cancellationToken));
}).WithName("StartHarnessEffect").WithSummary("Commit workflow intent, invoke the configured Generic Harness operation, and submit only verified observations through Gate 5.");

instances.MapPost("/{id}/effects/{operationId}/resume", async (
    string id, string operationId, HttpRequest httpRequest, HarnessEffectCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    if (!IsTrustedIngestion(httpRequest))
    {
        return Results.Json(new ApiError("trusted-ingestion-required",
            "Harness-backed effect inspection and continuation are restricted to the configured controlled demonstration boundary."),
            statusCode: StatusCodes.Status403Forbidden);
    }
    return Results.Ok(await coordinator.ResumeAsync(id, operationId, cancellationToken));
}).WithName("ResumeHarnessEffect").WithSummary("Inspect known harness work or dispatch durable work that has never left the local pending state; never creates a replacement execution after an unresolved dispatch.");

instances.MapGet("/{id}/effects/{operationId}/inspection", async (
    string id, string operationId, HarnessEffectCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.InspectHarnessAsync(id, operationId, cancellationToken)))
    .WithName("InspectHarnessEffect").WithSummary("Inspect the correlated harness execution, attempts, evidence and non-invoking replay-safety decision.");
instances.MapGet("/{id}/effects", async (
    string id, HarnessEffectCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var effects = await coordinator.ListAsync(id, cancellationToken);
    var results = new List<EffectOperationResponse>(effects.Count);
    foreach (var operation in effects)
    {
        results.Add(await coordinator.GetResponseAsync(id, operation.OperationId, cancellationToken));
    }
    return Results.Ok(results);
}).WithName("ListSyntheticEffects").WithSummary("Inspect durable external-effect intents and their attempts.");
instances.MapGet("/{id}/effects/{operationId}", async (
    string id, string operationId, HarnessEffectCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.GetResponseAsync(id, operationId, cancellationToken)))
    .WithName("GetSyntheticEffect").WithSummary("Inspect one external-effect operation and its known outcomes.");
app.MapGet("/api/providers/{providerIdentity}/requests", async (
    string providerIdentity, string? operationId, EffectCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.ListProviderRequestsAsync(providerIdentity, operationId, cancellationToken)))
    .WithName("InspectSyntheticProvider").WithSummary("Inspect provider-side durable request records, separate from workflow evidence.");

if (app.Environment.IsDevelopment() &&
    string.Equals(Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS"), "true",
        StringComparison.OrdinalIgnoreCase))
{
    app.MapPost("/api/test-controls/effects/{operationId}/{point}", (string operationId, string point, EffectFaultPlan plan) =>
    {
        plan.Arm(operationId, point);
        return Results.Ok(new { operationId, point, armed = true });
    }).WithName("ArmSyntheticEffectFault").WithSummary("Development-only one-shot fault injection for deterministic Gate 6 demonstrations.");

    app.MapPost("/api/test-controls/effects/{operationId}/{point}/pause", (
        string operationId, string point, EffectFaultPlan plan) =>
    {
        plan.ArmPause(operationId, point);
        return Results.Ok(new { operationId, point, paused = true });
    }).WithName("ArmSyntheticEffectPause")
      .WithSummary("Development-only pause for process-termination testing at a synthetic effect boundary.");

    app.MapGet("/api/test-controls/effects/{operationId}/{point}/pause-status", (
        string operationId, string point, EffectFaultPlan plan) =>
        Results.Ok(new { operationId, point, reached = plan.IsPauseReached(operationId, point) }))
      .WithName("GetSyntheticEffectPauseStatus")
      .WithSummary("Development-only observation of a deterministic synthetic effect pause.");

    app.MapPost("/api/test-controls/timers/process-due", async (
        int? limit,
        HttpRequest httpRequest,
        TimerProcessingService processor,
        CancellationToken cancellationToken) =>
    {
        if (!IsTrustedIngestion(httpRequest))
        {
            return Results.Json(new ApiError("trusted-ingestion-required",
                "Timer processing requires the configured controlled-administration boundary."),
                statusCode: StatusCodes.Status403Forbidden);
        }
        var processed = await processor.ProcessDueAsync(limit ?? 100, cancellationToken);
        return Results.Ok(processed.Select(result => result.ToResponse()));
    }).WithName("ProcessDueTimers")
      .WithSummary("Development-only: process currently due durable timers; callers cannot declare a timer authoritative.");
}
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Operations");

app.Run();

static FactValue ParseFact(JsonElement value) => value.ValueKind switch
{
    JsonValueKind.String => FactValue.From(value.GetString()!),
    JsonValueKind.Number when value.TryGetDecimal(out var number) => FactValue.From(number),
    JsonValueKind.True => FactValue.From(true),
    JsonValueKind.False => FactValue.From(false),
    JsonValueKind.Null => FactValue.Null,
    _ => throw new ArgumentException("Fact values must be scalar strings, numbers, booleans or null.")
};

static IReadOnlyDictionary<string, FactValue> ParseFacts(Dictionary<string, JsonElement>? values)
{
    var facts = new Dictionary<string, FactValue>(StringComparer.Ordinal);
    foreach (var (name, value) in values ?? [])
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Fact names must not be empty.");
        }
        facts.Add(name, ParseFact(value));
    }
    return facts;
}

static InputProcessingResponse ToInputResponse(InboundProcessingResult result, MachineInstanceDetails currentInstance)
{
    var submission = result.Submission;
    return new InputProcessingResponse(result.Message.ToResponse(),
        submission?.Attempt.ToResponse(), (submission?.Instance ?? currentInstance).ToResponse(),
        submission?.Transition?.ToResponse(),
        submission?.ConcernTransitions?.Select(entry => entry.ToResponse()).ToArray());
}

static async Task<IResult> RecordMalformedInput(
    MachineStore store,
    string instanceId,
    string inputKind,
    SubmitInputRequest request,
    bool trustedSource,
    string explanation,
    TimeProvider timeProvider,
    CancellationToken cancellationToken)
{
    var receipt = await store.RecordMalformedInputAsync(
        instanceId, inputKind, request.MessageId, request.DeliveryId, request.OperationId,
        request.ObservationId, request.Type, request.ExpectedRevision,
        request.CorrelationId, request.Source, request.OccurredAt,
        JsonSerializer.Serialize(request, JsonDefaults.Options), trustedSource, explanation,
        timeProvider.GetUtcNow(), cancellationToken);
    return Results.BadRequest(new ApiError("malformed-input",
        $"{explanation} Inspect inbound receipt '{receipt.Id}'."));
}

static bool IsTrustedIngestion(HttpRequest request)
{
    var configuredKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
    if (string.IsNullOrEmpty(configuredKey) ||
        !request.Headers.TryGetValue("X-State-Machine-Trusted-Ingestion-Key", out var suppliedKey) ||
        string.IsNullOrEmpty(suppliedKey))
    {
        return false;
    }
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(configuredKey),
        System.Text.Encoding.UTF8.GetBytes(suppliedKey.ToString()));
}

public partial class Program { }
