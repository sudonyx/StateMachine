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

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MachineDbContext>();
    await database.Database.EnsureCreatedAsync();
    await database.EnsureGate4SchemaAsync();
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
definitions.MapPost("/", async (JsonElement definition, MachineStore store, CancellationToken cancellationToken) =>
{
    if (definition.ValueKind != JsonValueKind.Object)
    {
        return Results.BadRequest(new ApiError("invalid-definition", "Definition body must be a JSON object."));
    }
    var registration = await store.RegisterDefinitionAsync(definition.GetRawText(), DateTimeOffset.UtcNow, cancellationToken);
    return Results.Ok(new { definition = registration.Definition, alreadyRegistered = registration.AlreadyRegistered });
}).WithName("RegisterDefinition").WithSummary("Validate and register an immutable definition version.");
definitions.MapGet("/", async (MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.ListDefinitionsAsync(cancellationToken)))
    .WithName("ListDefinitions").WithSummary("List all registered definition versions.");
definitions.MapGet("/{id}/{version}", async (string id, string version, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.GetDefinitionAsync(id, version, cancellationToken)).ToResponse()))
    .WithName("GetDefinition").WithSummary("Inspect a registered definition version.");

var instances = app.MapGroup("/api/instances").WithTags("Instances");
instances.MapPost("/", async (CreateInstanceRequest request, MachineStore store, CancellationToken cancellationToken) =>
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
        facts, DateTimeOffset.UtcNow, cancellationToken);
    return Results.Created($"/api/instances/{Uri.EscapeDataString(instance.Id)}", instance.ToResponse());
}).WithName("CreateInstance").WithSummary("Create an instance at its definition's initial state.");
instances.MapGet("/{id}", async (string id, MachineStore store, CancellationToken cancellationToken) =>
    Results.Ok((await store.GetInstanceAsync(id, cancellationToken)).ToResponse()))
    .WithName("GetInstance").WithSummary("Inspect current durable instance state and facts.");
instances.MapPost("/{id}/events", async (string id, SubmitEventRequest request, MachineStore store, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Event))
    {
        return Results.BadRequest(new ApiError("invalid-request", "Event must be a non-empty string."));
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

    var decisionEvidence = request.DecisionEvidence?.ToCoreEvidence();
    var result = await store.SubmitEventAsync(id, request.Event, DateTimeOffset.UtcNow, cancellationToken,
        facts, decisionEvidence, request.FactsProvenance);
    var response = new EventSubmissionResponse(result.Attempt.ToResponse(), result.Instance.ToResponse(),
        result.Transition?.ToResponse());
    return result.ConcurrencyConflict ? Results.Conflict(response) : Results.Ok(response);
}).WithName("SubmitEvent").WithSummary("Evaluate and record an event; workflow-level refusals return an inspectable result.");
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
instances.MapGet("/{id}/pending-work", async (string id, MachineStore store, CancellationToken cancellationToken) =>
{
    await store.GetInstanceAsync(id, cancellationToken);
    return Results.Ok(new { instanceId = id, supported = false, items = Array.Empty<object>() });
})
    .WithName("ListPendingWork").WithSummary("Pending time-based and external work are not supported in language version 1.0.");
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

public partial class Program { }
