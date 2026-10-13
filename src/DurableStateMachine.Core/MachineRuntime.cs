using System.Collections.ObjectModel;

namespace DurableStateMachine.Core;

public sealed record MachineInstance(
    string Id,
    string DefinitionId,
    string DefinitionVersion,
    string CurrentState,
    long Revision,
    IReadOnlyDictionary<string, FactValue> Facts);

public sealed record ExecutionResult(MachineInstance Instance, TransitionEvaluation Evaluation);

public sealed class MachineRuntime
{
    private readonly Dictionary<(string Id, string Version), MachineDefinition> _definitions = [];
    private readonly Dictionary<string, MachineInstance> _instances = new(StringComparer.Ordinal);

    public void Register(MachineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var key = (definition.Id, definition.Version);
        if (!_definitions.TryAdd(key, definition))
        {
            throw new InvalidOperationException($"Definition '{definition.Id}@{definition.Version}' is already registered.");
        }
    }

    public MachineInstance Create(
        string instanceId,
        string definitionId,
        string definitionVersion,
        IReadOnlyDictionary<string, FactValue>? facts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var definition = GetDefinition(definitionId, definitionVersion);
        var copiedFacts = new ReadOnlyDictionary<string, FactValue>(
            new Dictionary<string, FactValue>(facts ?? new Dictionary<string, FactValue>(), StringComparer.Ordinal));
        var instance = new MachineInstance(instanceId, definitionId, definitionVersion, definition.InitialState, 0, copiedFacts);
        if (!_instances.TryAdd(instanceId, instance))
        {
            throw new InvalidOperationException($"Instance '{instanceId}' already exists.");
        }
        return instance;
    }

    public MachineInstance GetInstance(string instanceId) =>
        _instances.TryGetValue(instanceId, out var instance)
            ? instance
            : throw new KeyNotFoundException($"Instance '{instanceId}' does not exist.");

    public ExecutionResult Send(string instanceId, string eventName)
    {
        var instance = GetInstance(instanceId);
        var definition = GetDefinition(instance.DefinitionId, instance.DefinitionVersion);
        var evaluation = TransitionEvaluator.Evaluate(definition, instance.CurrentState, eventName, instance.Facts);
        if (evaluation.Status != EvaluationStatus.Accepted)
        {
            return new ExecutionResult(instance, evaluation);
        }

        var updated = instance with
        {
            CurrentState = evaluation.TargetState!,
            Revision = checked(instance.Revision + 1)
        };
        _instances[instanceId] = updated;
        return new ExecutionResult(updated, evaluation);
    }

    private MachineDefinition GetDefinition(string definitionId, string definitionVersion) =>
        _definitions.TryGetValue((definitionId, definitionVersion), out var definition)
            ? definition
            : throw new KeyNotFoundException($"Definition '{definitionId}@{definitionVersion}' is not registered.");
}
