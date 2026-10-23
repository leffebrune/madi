using System.Diagnostics;
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
        var elapsed = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw ExplainCancellation(exception, "waiting for response headers", elapsed, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException(
                $"Model HTTP request failed while waiting for response headers "
                + $"(host: {_options.Endpoint.Host}, elapsed: {elapsed.Elapsed}, "
                + $"caller canceled: {cancellationToken.IsCancellationRequested}): {exception.Message}",
                exception, exception.StatusCode);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string body;
                try
                {
                    body = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                catch (OperationCanceledException exception)
                {
                    throw ExplainCancellation(exception, "reading HTTP error body", elapsed,
                        cancellationToken, response);
                }
                throw new HttpRequestException(
                    $"Network request failed ({(int)response.StatusCode} {response.ReasonPhrase}, "
                    + $"{ResponseDetails(response)}, elapsed: {elapsed.Elapsed}): {body}",
                    null,
                    response.StatusCode);
            }

            Stream stream;
            try
            {
                stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            }
            catch (OperationCanceledException exception)
            {
                throw ExplainCancellation(exception, "opening SSE stream", elapsed, cancellationToken, response);
            }
            await using (stream)
            {
                await using var events = SseParser.ParseAsync(stream, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                var eventCount = 0;
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await events.MoveNextAsync();
                    }
                    catch (OperationCanceledException exception)
                    {
                        throw ExplainCancellation(exception, $"reading SSE stream (events received: {eventCount})",
                            elapsed, cancellationToken, response);
                    }
                    catch (IOException exception)
                    {
                        throw new IOException($"Model SSE stream failed (events received: {eventCount}, "
                            + $"{ResponseDetails(response)}, elapsed: {elapsed.Elapsed}): {exception.Message}", exception);
                    }
                    if (!hasNext) break;
                    eventCount++;
                    yield return events.Current;
                }
            }
        }
    }

    private TaskCanceledException ExplainCancellation(
        OperationCanceledException exception,
        string phase,
        Stopwatch elapsed,
        CancellationToken cancellationToken,
        HttpResponseMessage? response = null)
    {
        var details = response is null
            ? $"host: {_options.Endpoint.Host}, HTTP response: not received"
            : ResponseDetails(response);
        return new TaskCanceledException(
            $"Model HTTP request canceled while {phase} ({details}, elapsed: {elapsed.Elapsed}, "
            + $"caller canceled: {cancellationToken.IsCancellationRequested}, "
            + $"HttpClient timeout: {_httpClient.Timeout}). "
            + $"Original: {exception.GetType().Name}: {exception.Message}"
            + (exception.InnerException is { } inner
                ? $"; inner: {inner.GetType().Name}: {inner.Message}"
                : string.Empty),
            exception);
    }

    private static string ResponseDetails(HttpResponseMessage response)
    {
        var details = $"HTTP {(int)response.StatusCode}";
        foreach (var name in new[] { "x-request-id", "apim-request-id", "request-id" })
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                var value = string.Join(", ", values).Replace('\r', ' ').Replace('\n', ' ');
                details += $", {name}: {value[..Math.Min(value.Length, 128)]}";
            }
        }
        return details;
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
