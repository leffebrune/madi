using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Madi.Networking;

/// <summary>
/// General-purpose client for configured HTTP endpoints that return server-sent events.
/// </summary>
public sealed class NetworkClient : INetworkClient
{
    private readonly NetworkClientOptions _options;
    private readonly HttpClient _httpClient;

    public NetworkClient(NetworkClientOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? new HttpClient();
    }

    public async IAsyncEnumerable<SseEvent> RequestAsync(
        string content,
        IReadOnlyDictionary<string, string>? headers = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var dynamicHeaders = _options.GetHeadersAsync is null
            ? null
            : await _options.GetHeadersAsync(cancellationToken);
        using var request = CreateRequest(content, dynamicHeaders, headers);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Network request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var item in SseParser.ParseAsync(stream, cancellationToken))
        {
            yield return item;
        }
    }

    private HttpRequestMessage CreateRequest(
        string content,
        IReadOnlyDictionary<string, string>? dynamicHeaders,
        IReadOnlyDictionary<string, string>? requestHeaders)
    {
        var request = new HttpRequestMessage(_options.Method, _options.Endpoint);
        AddHeaders(request, _options.DefaultHeaders);
        AddHeaders(request, dynamicHeaders);
        AddHeaders(request, requestHeaders);

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(_options.Accept));
        var bytes = Encoding.UTF8.GetBytes(content);
        request.Content = new ByteArrayContent(
            _options.UseBrotliCompression ? CompressBrotli(bytes) : bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(_options.ContentType);
        if (_options.UseBrotliCompression)
        {
            request.Content.Headers.ContentEncoding.Add("br");
        }
        return request;
    }

    private static void AddHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static byte[] CompressBrotli(byte[] input)
    {
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(input);
        }
        return output.ToArray();
    }
}
