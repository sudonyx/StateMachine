using DurableStateMachine.Core;

namespace DurableStateMachine.Tests;

internal static class TestSupport
{
    public static MachineDefinition LoadWorkflow(string filename) =>
        MachineDefinitionLoader.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", filename)));

    public static IReadOnlyDictionary<string, FactValue> Facts(params (string Name, FactValue Value)[] facts) =>
        facts.ToDictionary(fact => fact.Name, fact => fact.Value, StringComparer.Ordinal);

    public static string MinimalDefinition(string transitions = "") => $$"""
        {
          "languageVersion": "1.0",
          "id": "test-machine",
          "version": "1",
          "initialState": "A",
          "states": [{ "id": "A" }, { "id": "B", "terminal": true }],
          "events": ["Go"],
          "transitions": [{{transitions}}]
        }
        """;
}
