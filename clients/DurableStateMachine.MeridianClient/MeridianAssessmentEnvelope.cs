using System.Text.Json;

namespace DurableStateMachine.MeridianClient;

public sealed record MeridianAssessmentEnvelope(
    int FormatVersion,
    string OperationId,
    string IdempotencyKey,
    string RequestJson)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public MeridianAssessmentRequest ToRequest()
    {
        if (FormatVersion != 1)
        {
            throw new InvalidDataException($"Unsupported assessment envelope version '{FormatVersion}'.");
        }
        return MeridianAssessmentRequest.Create(OperationId, IdempotencyKey, RequestJson);
    }

    public static MeridianAssessmentEnvelope FromRequest(MeridianAssessmentRequest request)
    {
        request.Validate();
        return new MeridianAssessmentEnvelope(1, request.OperationId, request.IdempotencyKey, request.RequestJson);
    }

    public async Task SaveNewAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<MeridianAssessmentEnvelope> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var envelope = await JsonSerializer.DeserializeAsync<MeridianAssessmentEnvelope>(
            stream,
            JsonOptions,
            cancellationToken) ?? throw new InvalidDataException("Assessment request envelope is empty.");
        _ = envelope.ToRequest();
        return envelope;
    }
}
