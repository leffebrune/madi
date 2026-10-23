using System.Net;
using System.Text;
using Madi.Networking;

namespace Madi.Runtime.Tests;

public sealed class NetworkClientDiagnosticsTests
{
    private static readonly NetworkClientOptions Options = new(new Uri("https://example.test/v1/responses"));

    [Fact]
    public async Task CancellationBeforeHeadersReportsPhaseAndPreservesCause()
    {
        var cause = new TaskCanceledException("socket closed", new IOException("connection reset"));
        var client = new NetworkClient(Options, new HttpClient(new Handler((_, _) => throw cause)));

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() => CollectAsync(client));

        Assert.Contains("waiting for response headers", error.Message);
        Assert.Contains("HTTP response: not received", error.Message);
        Assert.Contains("caller canceled: False", error.Message);
        Assert.Contains("connection reset", error.Message);
        Assert.Same(cause, error.InnerException);
        Assert.DoesNotContain("/v1/responses", error.Message);
    }

    [Fact]
    public async Task CancellationWhileReadingStreamReportsHttpStatusAndRequestId()
    {
        var cause = new TaskCanceledException("read interrupted");
        var client = new NetworkClient(Options, new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FailingStream(cause)),
            };
            response.Headers.TryAddWithoutValidation("x-request-id", "req-123");
            return Task.FromResult(response);
        })));

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() => CollectAsync(client));

        Assert.Contains("reading SSE stream", error.Message);
        Assert.Contains("HTTP 200", error.Message);
        Assert.Contains("req-123", error.Message);
        Assert.Contains("caller canceled: False", error.Message);
        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public async Task HttpErrorIncludesApiBodyAndRequestId()
    {
        var client = new NetworkClient(Options, new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":\"rate limit\"}", Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("x-request-id", "req-456");
            return Task.FromResult(response);
        })));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => CollectAsync(client));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("rate limit", error.Message);
        Assert.Contains("req-456", error.Message);
    }

    private static async Task CollectAsync(NetworkClient client)
    {
        await foreach (var _ in client.RequestAsync("{}")) { }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FailingStream(Exception failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) => Task.FromException<int>(failure);
    }
}
