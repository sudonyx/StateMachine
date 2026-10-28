using System.Security.Cryptography;
using System.Text;

namespace DurableStateMachine.AsteriaClient;

internal static class AsteriaTestSynchronization
{
    public const string VerificationAfterDispatchPending = "verification-after-dispatch-pending";
    public const string VerificationAfterRemoteResponse = "verification-after-remote-response";
    public const string VerificationAfterResponseArchived = "verification-after-response-archived";
    public const string VerificationAfterObservationAccepted = "verification-after-observation-accepted";
    public const string VerificationBeforeWorkAccepted = "verification-before-work-accepted";
    public const string DeploymentAfterDispatchClaim = "deployment-after-dispatch-claim";
    public const string DeploymentAfterRemoteResponse = "deployment-after-remote-response";
    public const string DeploymentAfterAdmissionArchived = "deployment-after-admission-archived";
    public const string DeploymentAfterResultAccepted = "deployment-after-result-accepted";
    public const string HealthAfterDispatchPending = "health-after-dispatch-pending";
    public const string HealthAfterRemoteResponse = "health-after-remote-response";
    public const string HealthAfterResponseArchived = "health-after-response-archived";
    public const string HealthAfterObservationAccepted = "health-after-observation-accepted";
    public const string HealthBeforeWorkAccepted = "health-before-work-accepted";
    public const string ReadinessBeforeObservation = "readiness-before-observation";
    public const string ReadinessAfterObservationAccepted = "readiness-after-observation-accepted";
    public const string ReadinessBeforeWorkAccepted = "readiness-before-work-accepted";

    private const string EnabledVariable = "ASTERIA_TEST_SYNCHRONIZATION";
    private const string BoundaryVariable = "ASTERIA_TEST_SYNCHRONIZATION_BOUNDARY";
    private const string DirectoryVariable = "ASTERIA_TEST_SYNCHRONIZATION_DIRECTORY";
    private const string RunIdVariable = "ASTERIA_TEST_SYNCHRONIZATION_RUN_ID";

    public static bool IsEnabledForCurrentProcess =>
        string.Equals(
            Environment.GetEnvironmentVariable(EnabledVariable),
            "true",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);

    public static async Task PauseAsync(string boundary, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        if (!IsEnabledForCurrentProcess)
        {
            return;
        }

        var selectedBoundary = Environment.GetEnvironmentVariable(BoundaryVariable);
        if (string.IsNullOrWhiteSpace(selectedBoundary))
        {
            throw new InvalidOperationException(
                $"Asteria test synchronization is enabled but {BoundaryVariable} is missing.");
        }
        if (!string.Equals(selectedBoundary, boundary, StringComparison.Ordinal))
        {
            return;
        }

        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        var runId = Environment.GetEnvironmentVariable(RunIdVariable);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(runId))
        {
            throw new InvalidOperationException(
                $"Asteria test synchronization is enabled but {DirectoryVariable} or {RunIdVariable} is missing.");
        }

        var fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Asteria test synchronization directory '{fullDirectory}' does not exist.");
        }

        var safeRunId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
        var reachedPath = Path.Combine(fullDirectory, $"{safeRunId}.{boundary}.reached");
        var releasePath = Path.Combine(fullDirectory, $"{safeRunId}.{boundary}.release");
        if (File.Exists(reachedPath))
        {
            return;
        }
        try
        {
            var pendingPath = reachedPath + $".{Environment.ProcessId}.pending";
            await using (var signal = new FileStream(
                             pendingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous))
            {
                var message = Encoding.UTF8.GetBytes(
                    $"{Environment.ProcessId}:{boundary}:{DateTimeOffset.UtcNow:O}");
                await signal.WriteAsync(message, cancellationToken);
                await signal.FlushAsync(cancellationToken);
            }
            File.Move(pendingPath, reachedPath);
        }
        catch (IOException) when (File.Exists(reachedPath))
        {
            return;
        }

        while (!File.Exists(releasePath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }
}
