using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var mode = builder.Configuration["ASTERIA_SYNTHETIC_PROVIDER_MODE"]
    ?? throw new InvalidOperationException("Set ASTERIA_SYNTHETIC_PROVIDER_MODE.");
var supportedModes = new HashSet<string>(
    ["functional", "security", "deployment", "health"], StringComparer.Ordinal);
if (!supportedModes.Contains(mode))
{
    throw new InvalidOperationException(
        "ASTERIA_SYNTHETIC_PROVIDER_MODE must be functional, security, deployment, or health.");
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddOpenApi();
if (mode is "deployment" or "health")
{
    var databasePath = builder.Configuration["ASTERIA_SYNTHETIC_DEPLOYMENT_DB"]
        ?? throw new InvalidOperationException(
            "Set ASTERIA_SYNTHETIC_DEPLOYMENT_DB for deployment and health providers.");
    builder.Services.AddSingleton(new AsteriaSyntheticDeploymentStore(databasePath));
    builder.Services.AddHostedService<AsteriaSyntheticDeploymentWorker>();
}

var app = builder.Build();
app.MapOpenApi();
app.MapGet("/health", () => TypedResults.Ok(new { status = "ok", provider = mode }))
    .WithName("SyntheticProviderHealth")
    .WithSummary("Reports synthetic provider process health.");

if (mode is "functional" or "security")
{
    var concern = mode == "functional" ? "FunctionalVerification" : "SecurityVerification";
    Results<Ok<VerificationResponse>, BadRequest<ProviderError>> Verify(VerificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ReleaseId) ||
            request.AssessmentGeneration < 1 ||
            string.IsNullOrWhiteSpace(request.MaterialDigest) ||
            string.IsNullOrWhiteSpace(request.WorkflowInstanceId) ||
            request.ConcernId != concern ||
            string.IsNullOrWhiteSpace(request.ActivityId) ||
            string.IsNullOrWhiteSpace(request.ExpectedOperationId))
        {
            return TypedResults.BadRequest(new ProviderError("invalid_binding", "Verification request binding is incomplete or inconsistent."));
        }

        var evidenceId = StableEvidenceId(
            $"{request.ReleaseId}\0{request.AssessmentGeneration}\0{request.MaterialDigest}\0{request.ConcernId}\0{request.ActivityId}");
        return TypedResults.Ok(new VerificationResponse(
            request.ReleaseId,
            request.AssessmentGeneration,
            request.MaterialDigest,
            request.WorkflowInstanceId,
            request.ConcernId,
            request.ActivityId,
            request.ExpectedOperationId,
            "Passed",
            evidenceId));
    }

    app.MapPost("/verification", Verify)
    .WithName(mode == "functional" ? "verifyFunctionalRelease" : "verifySecurityRelease")
    .WithSummary($"Runs the deterministic synthetic {concern} check.")
    .WithDescription("Synthetic demonstration evidence only. A Passed result is not production verification or provider authentication.");
}

if (mode == "deployment")
{
    app.MapPost("/deployments", async Task<IResult> (
        DeploymentRequest request,
        AsteriaSyntheticDeploymentStore store,
        CancellationToken cancellationToken) =>
    {
        var error = Validate(request);
        if (error is not null)
        {
            return TypedResults.BadRequest(error);
        }

        try
        {
            var (status, created) = await store.RequestAsync(request, cancellationToken);
            return created
                ? TypedResults.Accepted($"/deployments/{Uri.EscapeDataString(status.DeploymentId)}", status)
                : TypedResults.Ok(status);
        }
        catch (SyntheticDeploymentConflictException)
        {
            return TypedResults.Conflict(new ProviderError(
                "idempotency_conflict",
                "The idempotency key is already associated with a different canonical request."));
        }
    })
    .WithName("requestDeployment")
    .WithSummary("Admits one idempotent synthetic deployment request.")
    .WithDescription("HTTP 202 means the synthetic operation was accepted. Confirm business status with a separate read-only lookup.");

    app.MapGet("/deployments/{deploymentId}", async Task<IResult> (
        string deploymentId,
        AsteriaSyntheticDeploymentStore store,
        CancellationToken cancellationToken) =>
    {
        var status = await store.GetByDeploymentIdAsync(deploymentId, cancellationToken);
        return status is null
            ? TypedResults.NotFound(new ProviderError("deployment_not_found", "No deployment exists for this ID."))
            : TypedResults.Ok(status);
    })
    .WithName("getDeploymentStatus")
    .WithSummary("Reads synthetic deployment status by deployment ID.");

    app.MapGet("/deployments/by-idempotency-key/{idempotencyKey}", async Task<IResult> (
        string idempotencyKey,
        AsteriaSyntheticDeploymentStore store,
        CancellationToken cancellationToken) =>
    {
        var status = await store.GetByIdempotencyKeyAsync(idempotencyKey, cancellationToken);
        return status is null
            ? TypedResults.NotFound(new ProviderError("deployment_not_found", "No deployment exists for this idempotency key."))
            : TypedResults.Ok(status);
    })
    .WithName("lookupDeploymentByIdempotencyKey")
    .WithSummary("Reads synthetic deployment status by exact idempotency key.");
}

if (mode == "health")
{
    async Task<Microsoft.AspNetCore.Http.HttpResults.Results<
        Ok<HealthVerificationResponse>,
        BadRequest<ProviderError>,
        NotFound<ProviderError>>> VerifyHealth(
        string deploymentId,
        string targetVersion,
        string environment,
        AsteriaSyntheticDeploymentStore store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deploymentId) ||
            string.IsNullOrWhiteSpace(targetVersion) ||
            environment is not ("development" or "staging" or "production"))
        {
            return TypedResults.BadRequest(new ProviderError("invalid_query", "A deployment ID, target version, and supported environment are required."));
        }

        var deployment = await store.GetByDeploymentIdAsync(deploymentId, cancellationToken);
        if (deployment is null)
        {
            return TypedResults.NotFound(new ProviderError("deployment_not_found", "The deployment is not available for verification."));
        }

        var matches = deployment.Status == "Succeeded" &&
            deployment.TargetVersion == targetVersion &&
            deployment.Environment == environment;
        var result = deployment.Status is "Pending" or "Running"
            ? "Indeterminate"
            : matches ? "Passed" : "Failed";
        var checkedAt = DateTimeOffset.UtcNow;
        return TypedResults.Ok(new HealthVerificationResponse(
            deployment.DeploymentId,
            deployment.TargetVersion,
            deployment.Environment,
            result,
            checkedAt,
            StableEvidenceId($"{deployment.DeploymentId}\0{targetVersion}\0{environment}\0{result}\0{checkedAt:O}"),
            matches ? "Synthetic health check matched the completed deployment." : "Synthetic health check did not establish the requested deployment as healthy."));
    }

    app.MapGet("/health-verifications", VerifyHealth)
    .WithName("verifyDeployedApplicationHealth")
    .WithSummary("Checks synthetic application health for an exact deployment.")
    .WithDescription("Read-only verification. HTTP 200 is not equivalent to a Passed result.");
}

if (mode is "deployment" or "health")
{
    await app.Services.GetRequiredService<AsteriaSyntheticDeploymentStore>()
        .InitializeAsync(CancellationToken.None);
}

await app.RunAsync();

static ProviderError? Validate(DeploymentRequest request)
{
    if (string.IsNullOrWhiteSpace(request.ReleaseId) ||
        request.AssessmentGeneration < 1 ||
        string.IsNullOrWhiteSpace(request.MaterialDigest) ||
        string.IsNullOrWhiteSpace(request.Product) ||
        string.IsNullOrWhiteSpace(request.TargetVersion) ||
        request.Environment is not ("development" or "staging" or "production") ||
        string.IsNullOrWhiteSpace(request.IdempotencyKey))
    {
        return new ProviderError("invalid_request", "Deployment request fields are invalid.");
    }

    return null;
}

static string StableEvidenceId(string value) =>
    "synthetic-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

public sealed record VerificationRequest(
    string ReleaseId,
    int AssessmentGeneration,
    string MaterialDigest,
    string WorkflowInstanceId,
    string ConcernId,
    string ActivityId,
    string ExpectedOperationId);

public sealed record VerificationResponse(
    string ReleaseId,
    int AssessmentGeneration,
    string MaterialDigest,
    string WorkflowInstanceId,
    string ConcernId,
    string ActivityId,
    string ExpectedOperationId,
    string Result,
    string EvidenceId);

public sealed record DeploymentRequest(
    string ReleaseId,
    int AssessmentGeneration,
    string MaterialDigest,
    string Product,
    string TargetVersion,
    string Environment,
    string IdempotencyKey);

public sealed record DeploymentStatusResponse(
    string DeploymentId,
    string IdempotencyKey,
    string ReleaseId,
    int AssessmentGeneration,
    string MaterialDigest,
    string Product,
    string TargetVersion,
    string Environment,
    string Status,
    DateTimeOffset LastUpdatedAt,
    DateTimeOffset? CompletedAt,
    string? EvidenceId,
    string? FailureReason);

public sealed record HealthVerificationResponse(
    string DeploymentId,
    string ObservedVersion,
    string Environment,
    string Result,
    DateTimeOffset CheckedAt,
    string EvidenceId,
    string? Details);

public sealed record ProviderError(string Code, string Message);

public sealed class AsteriaSyntheticDeploymentStore(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        DefaultTimeout = 30,
        Pooling = true
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(
            new SqliteConnectionStringBuilder(_connectionString).DataSource)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SyntheticDeployments (
                DeploymentId TEXT NOT NULL PRIMARY KEY,
                IdempotencyKey TEXT NOT NULL UNIQUE,
                RequestJson TEXT NOT NULL,
                ReleaseId TEXT NOT NULL,
                AssessmentGeneration INTEGER NOT NULL,
                MaterialDigest TEXT NOT NULL,
                Product TEXT NOT NULL,
                TargetVersion TEXT NOT NULL,
                Environment TEXT NOT NULL,
                Status TEXT NOT NULL CHECK (Status IN ('Pending', 'Running', 'Succeeded')),
                AcceptedAt TEXT NOT NULL,
                LastUpdatedAt TEXT NOT NULL,
                CompletedAt TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(DeploymentStatusResponse Status, bool Created)> RequestAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var requestJson = JsonSerializer.Serialize(request);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT RequestJson FROM SyntheticDeployments WHERE IdempotencyKey = $key";
        lookup.Parameters.AddWithValue("$key", request.IdempotencyKey);
        var existingJson = await lookup.ExecuteScalarAsync(cancellationToken) as string;
        if (existingJson is not null)
        {
            if (!string.Equals(existingJson, requestJson, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new SyntheticDeploymentConflictException();
            }

            var existing = await ReadByIdempotencyKeyAsync(connection, transaction, request.IdempotencyKey, cancellationToken)
                ?? throw new InvalidDataException("Synthetic deployment disappeared during idempotency replay.");
            await transaction.CommitAsync(cancellationToken);
            return (existing, false);
        }

        var now = DateTimeOffset.UtcNow;
        var deploymentId = Guid.NewGuid().ToString();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO SyntheticDeployments (
                DeploymentId, IdempotencyKey, RequestJson, ReleaseId, AssessmentGeneration,
                MaterialDigest, Product, TargetVersion, Environment, Status, AcceptedAt,
                LastUpdatedAt, CompletedAt)
            VALUES (
                $id, $key, $request, $release, $generation, $digest, $product,
                $version, $environment, 'Pending', $now, $now, NULL)
            """;
        insert.Parameters.AddWithValue("$id", deploymentId);
        insert.Parameters.AddWithValue("$key", request.IdempotencyKey);
        insert.Parameters.AddWithValue("$request", requestJson);
        insert.Parameters.AddWithValue("$release", request.ReleaseId);
        insert.Parameters.AddWithValue("$generation", request.AssessmentGeneration);
        insert.Parameters.AddWithValue("$digest", request.MaterialDigest);
        insert.Parameters.AddWithValue("$product", request.Product);
        insert.Parameters.AddWithValue("$version", request.TargetVersion);
        insert.Parameters.AddWithValue("$environment", request.Environment);
        insert.Parameters.AddWithValue("$now", now.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        var created = await ReadByIdempotencyKeyAsync(connection, transaction, request.IdempotencyKey, cancellationToken)
            ?? throw new InvalidDataException("Synthetic deployment was not persisted.");
        await transaction.CommitAsync(cancellationToken);
        return (created, true);
    }

    public Task<DeploymentStatusResponse?> GetByDeploymentIdAsync(
        string deploymentId,
        CancellationToken cancellationToken) =>
        ReadAsync("DeploymentId", deploymentId, cancellationToken);

    public Task<DeploymentStatusResponse?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ReadAsync("IdempotencyKey", idempotencyKey, cancellationToken);

    public async Task<bool> CompleteDueDeploymentsAsync(
        TimeSpan completionDelay,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(completionDelay).ToString("O");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SyntheticDeployments
            SET Status = 'Succeeded', LastUpdatedAt = $now, CompletedAt = $now
            WHERE Status IN ('Pending', 'Running') AND AcceptedAt <= $cutoff
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<DeploymentStatusResponse?> ReadAsync(
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        if (column is not ("DeploymentId" or "IdempotencyKey"))
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var result = await ReadAsync(connection, transaction, column, value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<DeploymentStatusResponse?> ReadByIdempotencyKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        CancellationToken cancellationToken) =>
        await ReadAsync(connection, transaction, "IdempotencyKey", key, cancellationToken);

    private static async Task<DeploymentStatusResponse?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT DeploymentId, IdempotencyKey, ReleaseId, AssessmentGeneration,
                   MaterialDigest, Product, TargetVersion, Environment, Status,
                   LastUpdatedAt, CompletedAt
            FROM SyntheticDeployments WHERE {column} = $value
            """;
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var status = reader.GetString(8);
        var id = reader.GetString(0);
        var completedAt = reader.IsDBNull(10)
            ? (DateTimeOffset?)null
            : DateTimeOffset.Parse(reader.GetString(10));
        return new DeploymentStatusResponse(
            id,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            status,
            DateTimeOffset.Parse(reader.GetString(9)),
            completedAt,
            status == "Succeeded" ? StableEvidenceId(id) : null,
            null);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string StableEvidenceId(string id) =>
        "synthetic-deployment-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
}

public sealed class AsteriaSyntheticDeploymentWorker(
    AsteriaSyntheticDeploymentStore store,
    IConfiguration configuration,
    ILogger<AsteriaSyntheticDeploymentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.InitializeAsync(stoppingToken);
        var delayMilliseconds = configuration.GetValue("ASTERIA_SYNTHETIC_DEPLOYMENT_DELAY_MS", 250);
        if (delayMilliseconds < 0)
        {
            throw new InvalidOperationException("ASTERIA_SYNTHETIC_DEPLOYMENT_DELAY_MS cannot be negative.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                _ = await store.CompleteDueDeploymentsAsync(
                    TimeSpan.FromMilliseconds(delayMilliseconds), stoppingToken);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 5)
            {
                logger.LogWarning(exception, "Synthetic deployment database was busy; completion will be retried.");
            }
        }
    }
}

public sealed class SyntheticDeploymentConflictException : Exception
{
}

public partial class Program
{
}
