using System.Text.Json;

namespace DurableStateMachine.AsteriaClient;

internal static class AsteriaHarnessResponseBody
{
    public static string Read(JsonElement response)
    {
        if (!response.TryGetProperty("body", out var body) ||
            body.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException(
                "Generic Harness response did not contain a complete provider response body.");
        }

        return body.ValueKind switch
        {
            JsonValueKind.String => body.GetString()
                ?? throw new InvalidDataException(
                    "Generic Harness response contained a null provider response body."),
            JsonValueKind.Object or JsonValueKind.Array => body.GetRawText(),
            _ => throw new InvalidDataException(
                "Generic Harness response body must be a JSON object, array, or serialized JSON string.")
        };
    }
}
