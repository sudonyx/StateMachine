namespace DurableStateMachine.Persistence;

public sealed class InputFaultPlan
{
    private readonly HashSet<(string InstanceId, string Point)> _armed = [];
    private readonly Dictionary<(string InstanceId, string Point), PauseBoundary> _pauses = [];
    private readonly object _sync = new();

    public static IReadOnlyList<string> SupportedPoints { get; } =
    [
        "BeforeIntakeCommit",
        "AfterIntakeCommitBeforeEvaluation",
        "DuringGuardEvaluation",
        "BeforeFinalPersistence",
        "DuringProjectionUpdate",
        "BeforeExternalResultStaging",
        "DuringExternalResultStagingPersistence",
        "AfterExternalResultStagingCommit",
        "BeforeStagedObservationEvaluation",
        "DuringStagedObservationFinalization",
        "AfterStagedObservationCommit"
    ];

    public void Arm(string instanceId, string point)
    {
        if (!SupportedPoints.Contains(point, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unsupported Gate 10 input fault point '{point}'.", nameof(point));
        }
        lock (_sync)
        {
            _armed.Add((instanceId, point));
        }
    }

    public void ThrowIfArmed(string instanceId, string point)
    {
        lock (_sync)
        {
            if (_armed.Remove((instanceId, point)))
            {
                throw new InputInterruptionException(
                    $"Deterministic Gate 10 interruption at '{point}' for instance '{instanceId}'.");
            }
        }
    }

    public void ArmPause(string instanceId, string point)
    {
        if (!SupportedPoints.Contains(point, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unsupported Gate 10 input pause point '{point}'.", nameof(point));
        }
        lock (_sync)
        {
            _pauses[(instanceId, point)] = new PauseBoundary();
        }
    }

    public async Task WaitIfPausedAsync(string instanceId, string point, CancellationToken cancellationToken)
    {
        PauseBoundary? boundary;
        lock (_sync)
        {
            boundary = _pauses.TryGetValue((instanceId, point), out var pause) ? pause : null;
        }
        if (boundary is not null)
        {
            boundary.Reached.TrySetResult();
            await boundary.Release.Task.WaitAsync(cancellationToken);
        }
    }

    public bool IsPauseReached(string instanceId, string point)
    {
        lock (_sync)
        {
            return _pauses.TryGetValue((instanceId, point), out var boundary) &&
                boundary.Reached.Task.IsCompleted;
        }
    }

    private sealed class PauseBoundary
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class InputInterruptionException(string message) : Exception(message);
