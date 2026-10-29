using Gate15Load;

namespace DurableStateMachine.Tests;

public sealed class Gate15CorpusTests
{
    [Fact]
    public void Corpus_generation_is_deterministic_and_meets_scaled_structural_constraints()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gate15-corpus-{Guid.NewGuid():N}");
        try
        {
            Gate15Corpus.Generate(directory, seed: 20261029, instanceCount: 100, deliveryCount: 5000);
            var report = Gate15Corpus.Validate(directory, seed: 20261029, instanceCount: 100, deliveryCount: 5000);

            Assert.Equal(100, report.Instances);
            Assert.Equal(5000, report.Deliveries);
            Assert.True(report.ExactDuplicateDeliveries >= 100);
            Assert.True(report.LateDeliveries >= 50);
            Assert.True(report.ConcurrentPairs >= 25);
            Assert.True(report.StableOrdering);
            Assert.True(report.NoAnswerColumns);
            Assert.True(report.ManifestMatches);
            Gate15Corpus.VerifyDeterminism(seed: 20261029, instanceCount: 100, deliveryCount: 5000);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
