using System.Text.Json;
using DurableStateMachine.Api;

var builder = WebApplication.CreateBuilder(args);
var capability = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_CAPABILITY")
    ?? builder.Configuration["Provider:Capability"]
    ?? throw new InvalidOperationException(
        "Set DURABLE_STATE_MACHINE_PROVIDER_CAPABILITY to offer-document, equipment-reservation, or customer-notification.");
if (capability is not ("offer-document" or "equipment-reservation" or "customer-notification"))
{
    throw new InvalidOperationException($"Unsupported provider capability '{capability}'.");
}

var defaultUrls = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["offer-document"] = "http://localhost:5261",
    ["equipment-reservation"] = "http://localhost:5262",
    ["customer-notification"] = "http://localhost:5263"
};
var providerUrl = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_PUBLIC_URL")
    ?? builder.Configuration["Provider:PublicUrl"]
    ?? defaultUrls[capability];
if (!Uri.TryCreate(providerUrl, UriKind.Absolute, out var providerUri) ||
    providerUri.Scheme is not ("http" or "https"))
{
    throw new InvalidOperationException(
        "DURABLE_STATE_MACHINE_PROVIDER_PUBLIC_URL must be an absolute HTTP or HTTPS URL.");
}

builder.Services.AddSingleton<SyntheticProviderStore>();
var app = builder.Build();

app.MapGet("/openapi.json", () => Results.Json(CreateProviderDescription(capability, providerUri)));
app.MapGet("/health", () => Results.Ok(new { status = "ok", capability }));

if (capability == "offer-document")
{
    app.MapPut("/offers/{operationId}", async (
        string operationId,
        OfferDocumentRequest request,
        SyntheticProviderStore store,
        CancellationToken cancellationToken) =>
    {
        var result = await store.ExecuteAsync(
            new SyntheticEffectRequest(
                operationId,
                capability,
                request.CustomerReference,
                AssetId: null,
                request.OfferReference,
                MessageTemplate: null,
                ProviderOperationId: operationId),
            forceFailure: false,
            cancellationToken);
        return Results.Ok(ToResponse(result));
    });
}
else if (capability == "equipment-reservation")
{
    app.MapPost("/reservations", async (
        ReservationRequest request,
        SyntheticProviderStore store,
        CancellationToken cancellationToken) =>
    {
        var result = await store.ExecuteAsync(
            new SyntheticEffectRequest(
                request.OperationId,
                capability,
                CustomerReference: null,
                request.AssetId,
                OfferReference: null,
                MessageTemplate: null,
                ProviderOperationId: request.OperationId),
            forceFailure: false,
            cancellationToken);
        return Results.Ok(ToResponse(result));
    });
}
else
{
    app.MapPost("/notifications", async (
        NotificationRequest request,
        SyntheticProviderStore store,
        CancellationToken cancellationToken) =>
    {
        var result = await store.ExecuteAsync(
            new SyntheticEffectRequest(
                request.OperationId,
                capability,
                request.CustomerReference,
                AssetId: null,
                OfferReference: null,
                request.MessageTemplate,
                ProviderOperationId: request.OperationId),
            forceFailure: false,
            cancellationToken);
        return Results.Ok(ToResponse(result));
    });
}

app.Run();

static object ToResponse(ProviderEffectResult result)
{
    var response = new Dictionary<string, object?>
    {
        ["outcome"] = result.Outcome,
        ["evidence"] = result.Evidence,
        ["occurredAt"] = result.OccurredAt
    };
    if (result.EffectId is not null)
    {
        response["effectId"] = result.EffectId;
    }
    return response;
}

static object CreateProviderDescription(string capability, Uri server)
{
    var output = new
    {
        type = "object",
        properties = new
        {
            outcome = new { type = "string" },
            effectId = new { type = "string" },
            evidence = new { type = "object" },
            occurredAt = new { type = "string", format = "date-time" }
        },
        required = new[] { "outcome", "evidence", "occurredAt" }
    };
    var response = new
    {
        description = "Synthetic provider operation completed; business outcome is carried in the validated JSON response.",
        content = new Dictionary<string, object>
        {
            ["application/json"] = new { schema = output }
        }
    };

    object operation;
    string path;
    string method;
    if (capability == "offer-document")
    {
        path = "/offers/{operationId}";
        method = "put";
        operation = new
        {
            operationId = "generateOfferDocument",
            parameters = new[]
            {
                new
                {
                    name = "operationId",
                    @in = "path",
                    required = true,
                    schema = new { type = "string" }
                }
            },
            requestBody = new
            {
                required = true,
                content = new Dictionary<string, object>
                {
                    ["application/json"] = new
                    {
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                customerReference = new { type = "string" },
                                offerReference = new { type = "string" }
                            },
                            required = new[] { "customerReference", "offerReference" }
                        }
                    }
                }
            },
            responses = new Dictionary<string, object> { ["200"] = response }
        };
    }
    else if (capability == "equipment-reservation")
    {
        path = "/reservations";
        method = "post";
        operation = new
        {
            operationId = "reserveEquipment",
            requestBody = new
            {
                required = true,
                content = new Dictionary<string, object>
                {
                    ["application/json"] = new
                    {
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                operationId = new { type = "string" },
                                assetId = new { type = "string" }
                            },
                            required = new[] { "operationId", "assetId" }
                        }
                    }
                }
            },
            responses = new Dictionary<string, object> { ["200"] = response }
        };
    }
    else
    {
        path = "/notifications";
        method = "post";
        operation = new
        {
            operationId = "sendCustomerNotification",
            requestBody = new
            {
                required = true,
                content = new Dictionary<string, object>
                {
                    ["application/json"] = new
                    {
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                operationId = new { type = "string" },
                                customerReference = new { type = "string" },
                                messageTemplate = new { type = "string" }
                            },
                            required = new[] { "operationId", "customerReference", "messageTemplate" }
                        }
                    }
                }
            },
            responses = new Dictionary<string, object> { ["200"] = response }
        };
    }

    var paths = new Dictionary<string, object>
    {
        [path] = new Dictionary<string, object> { [method] = operation }
    };
    return new
    {
        openapi = "3.0.3",
        info = new { title = $"Synthetic {capability} provider", version = "1.0.0" },
        servers = new[] { new { url = server.ToString().TrimEnd('/') } },
        paths
    };
}

public sealed record OfferDocumentRequest(string CustomerReference, string OfferReference);
public sealed record ReservationRequest(string OperationId, string AssetId);
public sealed record NotificationRequest(string OperationId, string CustomerReference, string MessageTemplate);

public partial class Program { }
