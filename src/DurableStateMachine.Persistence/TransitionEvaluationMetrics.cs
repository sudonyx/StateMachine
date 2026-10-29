using System.Diagnostics;

namespace DurableStateMachine.Persistence;

public sealed class TransitionEvaluationMetrics(bool enabled)
{
    private static readonly double[] BucketUpperBoundsMilliseconds =
    [
        0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5,
        1, 2, 5, 10, 20, 50, 100, 250, 500, 1000
    ];

    private readonly object _sync = new();
    private readonly Dictionary<string, OutcomeAggregate> _outcomes = new(StringComparer.Ordinal)
    {
        ["Accepted"] = new(BucketUpperBoundsMilliseconds.Length + 1),
        ["Rejected"] = new(BucketUpperBoundsMilliseconds.Length + 1),
        ["Other"] = new(BucketUpperBoundsMilliseconds.Length + 1),
    };

    public bool Enabled { get; } = enabled;

    public void Record(string outcome, long elapsedTicks)
    {
        if (!Enabled)
        {
            return;
        }
        var category = outcome switch
        {
            "Accepted" => "Accepted",
            "Rejected" => "Rejected",
            _ => "Other",
        };
        var elapsedMilliseconds = elapsedTicks * 1000d / Stopwatch.Frequency;
        var bucket = Array.FindIndex(BucketUpperBoundsMilliseconds,
            upperBound => elapsedMilliseconds <= upperBound);
        if (bucket < 0)
        {
            bucket = BucketUpperBoundsMilliseconds.Length;
        }
        lock (_sync)
        {
            _outcomes[category].Add(elapsedMilliseconds, bucket);
        }
    }

    public TransitionEvaluationMetricsSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new TransitionEvaluationMetricsSnapshot(
                Enabled,
                _outcomes.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.Snapshot(BucketUpperBoundsMilliseconds),
                    StringComparer.Ordinal));
        }
    }

    private sealed class OutcomeAggregate(int bucketCount)
    {
        private readonly long[] _buckets = new long[bucketCount];
        private long _count;
        private double _sum;
        private double _minimum = double.PositiveInfinity;
        private double _maximum;

        public void Add(double elapsedMilliseconds, int bucket)
        {
            _count++;
            _sum += elapsedMilliseconds;
            _minimum = Math.Min(_minimum, elapsedMilliseconds);
            _maximum = Math.Max(_maximum, elapsedMilliseconds);
            _buckets[bucket]++;
        }

        public TransitionEvaluationOutcomeSnapshot Snapshot(double[] bucketUpperBoundsMilliseconds) =>
            new(
                _count,
                _count == 0 ? 0 : _sum / _count,
                _count == 0 ? 0 : _minimum,
                _count == 0 ? 0 : _maximum,
                _buckets.ToArray(),
                bucketUpperBoundsMilliseconds);
    }
}

public sealed record TransitionEvaluationMetricsSnapshot(
    bool Enabled,
    IReadOnlyDictionary<string, TransitionEvaluationOutcomeSnapshot> Outcomes);

public sealed record TransitionEvaluationOutcomeSnapshot(
    long Count,
    double MeanMilliseconds,
    double MinimumMilliseconds,
    double MaximumMilliseconds,
    IReadOnlyList<long> HistogramBuckets,
    IReadOnlyList<double> BucketUpperBoundsMilliseconds);
