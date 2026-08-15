namespace Madi.Networking;

/// <summary>Configuration for a reusable SSE network client.</summary>
public sealed record NetworkClientOptions(Uri Endpoint)
{
    public HttpMethod Method { get; init; } = HttpMethod.Post;

    public string ContentType { get; init; } = "application/json";

    public string Accept { get; init; } = "text/event-stream";

    public bool UseBrotliCompression { get; init; }

    public IReadOnlyDictionary<string, string> DefaultHeaders { get; init; } =
        new Dictionary<string, string>();

    public Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? GetHeadersAsync { get; init; }
}
