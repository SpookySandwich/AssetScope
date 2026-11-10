using System.Text.Json.Serialization;

namespace AssetScope.Core.Api;

// Mirror of the sidecar's /v1 response envelope (SPEC §10.2).

public sealed record ApiMeta(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("generatedAt")] string GeneratedAt,
    [property: JsonPropertyName("protocolVersion")] string ProtocolVersion);

public sealed record ApiErrorBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("details")] Dictionary<string, string>? Details);

public sealed record ApiEnvelope<T>(
    [property: JsonPropertyName("data")] T? Data,
    [property: JsonPropertyName("meta")] ApiMeta? Meta,
    [property: JsonPropertyName("error")] ApiErrorBody? Error);

public sealed class ApiException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }
    public string? RequestId { get; }

    public ApiException(string code, string message, bool retryable, string? requestId)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
        RequestId = requestId;
    }
}
