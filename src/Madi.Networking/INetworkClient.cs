namespace Madi.Networking;

/// <summary>Sends content to a configured SSE endpoint.</summary>
public interface INetworkClient
{
    IAsyncEnumerable<SseEvent> RequestAsync(
        string content,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default);
}
