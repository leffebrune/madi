using System.IO.Compression;
using System.Net;
using System.Text;
using Madi.Services;

namespace Madi.Services.Tests;

public sealed class GitHubReleaseFileDownloaderTests
{
    [Fact]
    public async Task DownloadsMatchingAssetAndExtractsRequestedFile()
    {
        using var directory = new TemporaryDirectory();
        var zip = CreateZip(("package/fd.exe", "fd contents"), ("package/readme.txt", "ignore"));
        var handler = new QueueHttpMessageHandler(
            JsonResponse("""
                {"assets":[
                  {"name":"fd-v1-aarch64-pc-windows-msvc.zip","browser_download_url":"https://downloads.test/arm.zip"},
                  {"name":"fd-v1-x86_64-pc-windows-msvc.zip","browser_download_url":"https://downloads.test/x64.zip"}
                ]}
                """),
            BytesResponse(zip));
        var destination = Path.Combine(directory.Path, "bin", "fd.exe");
        var service = new GitHubReleaseFileDownloader(new HttpClient(handler));

        await service.DownloadLatestReleaseFileAsync(
            Request(destination),
            CancellationToken.None);

        Assert.Equal("fd contents", await File.ReadAllTextAsync(destination));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            "https://api.github.com/repos/sharkdp/fd/releases/latest",
            handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("Madi/1.0", handler.Requests[0].UserAgent);
        Assert.Equal("https://downloads.test/x64.zip", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, ".fd.exe.*"));
    }

    [Theory]
    [InlineData("{\"assets\":[]}", 0)]
    [InlineData("{\"assets\":[{\"name\":\"one-x86_64-pc-windows-msvc.zip\",\"browser_download_url\":\"https://downloads.test/1\"},{\"name\":\"two-x86_64-pc-windows-msvc.zip\",\"browser_download_url\":\"https://downloads.test/2\"}]}", 2)]
    public async Task RejectsMissingOrDuplicateAssets(string json, int expectedCount)
    {
        using var directory = new TemporaryDirectory();
        var service = new GitHubReleaseFileDownloader(
            new HttpClient(new QueueHttpMessageHandler(JsonResponse(json))));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadLatestReleaseFileAsync(
                Request(Path.Combine(directory.Path, "fd.exe")),
                CancellationToken.None));

        Assert.Contains($"found {expectedCount}", error.Message);
    }

    [Fact]
    public async Task ReportsLatestReleaseHttpFailure()
    {
        using var directory = new TemporaryDirectory();
        var service = new GitHubReleaseFileDownloader(new HttpClient(
            new QueueHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("rate limited"),
            })));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.DownloadLatestReleaseFileAsync(
                Request(Path.Combine(directory.Path, "fd.exe")),
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("rate limited", error.Message);
    }

    [Fact]
    public async Task ReportsAssetDownloadHttpFailureAndCleansTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        var handler = new QueueHttpMessageHandler(
            LatestAssetResponse(),
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("gone") });
        var service = new GitHubReleaseFileDownloader(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.DownloadLatestReleaseFileAsync(
                Request(Path.Combine(directory.Path, "fd.exe")),
                CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task RejectsCorruptZipAndCleansTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        var service = new GitHubReleaseFileDownloader(new HttpClient(
            new QueueHttpMessageHandler(
                LatestAssetResponse(),
                BytesResponse(Encoding.UTF8.GetBytes("not a zip")))));

        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            service.DownloadLatestReleaseFileAsync(
                Request(Path.Combine(directory.Path, "fd.exe")),
                CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task PropagatesCancellationAndCleansTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new GitHubReleaseFileDownloader(new HttpClient(
            new QueueHttpMessageHandler(LatestAssetResponse())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadLatestReleaseFileAsync(
                Request(Path.Combine(directory.Path, "fd.exe")),
                cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    private static GitHubReleaseFileRequest Request(string destination) => new(
        "sharkdp",
        "fd",
        "x86_64-pc-windows-msvc.zip",
        "fd.exe",
        destination);

    private static HttpResponseMessage LatestAssetResponse() => JsonResponse("""
        {"assets":[{"name":"fd-v1-x86_64-pc-windows-msvc.zip","browser_download_url":"https://downloads.test/fd.zip"}]}
        """);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    private static byte[] CreateZip(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<(Uri Uri, string UserAgent)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((
                request.RequestUri!,
                request.Headers.UserAgent.ToString()));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"madi-services-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
