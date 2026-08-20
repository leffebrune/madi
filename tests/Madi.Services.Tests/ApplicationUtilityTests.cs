using System.Runtime.InteropServices;
using Madi.App;
using Madi.Services;

namespace Madi.Services.Tests;

public sealed class ApplicationUtilityTests
{
    [Fact]
    public async Task ExistingUtilitiesSkipDownloads()
    {
        if (!IsSupportedPlatform())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "fd.exe"), "existing");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "rg.exe"), "existing");
        var downloader = new RecordingDownloader();
        var installer = new UtilityInstaller(downloader, directory.Path);

        var events = await CollectAsync(installer.EnsureAsync());

        Assert.Empty(events);
        Assert.Empty(downloader.Requests);
    }

    [Fact]
    public async Task FailedInstallWarnsAndContinuesWithOtherTool()
    {
        if (!IsSupportedPlatform())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var downloader = new RecordingDownloader(failFileName: "fd.exe");
        var installer = new UtilityInstaller(downloader, directory.Path);

        var events = await CollectAsync(installer.EnsureAsync());

        Assert.Equal(["fd.exe", "rg.exe"], downloader.Requests.Select(x => x.ArchiveEntryFileName));
        Assert.False(File.Exists(Path.Combine(directory.Path, "fd.exe")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "rg.exe")));
        var warning = Assert.Single(events.OfType<AppMessage>());
        Assert.Equal(AppMessageKind.Warning, warning.Kind);
        Assert.Contains("경고: fd.exe", warning.Text, StringComparison.Ordinal);
    }

    private static bool IsSupportedPlatform() =>
        OperatingSystem.IsWindows()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static async Task<List<AppEvent>> CollectAsync(IAsyncEnumerable<AppEvent> source)
    {
        var result = new List<AppEvent>();
        await foreach (var item in source)
        {
            result.Add(item);
        }
        return result;
    }

    private sealed class RecordingDownloader(string? failFileName = null)
        : IGitHubReleaseFileDownloader
    {
        public List<GitHubReleaseFileRequest> Requests { get; } = [];

        public async Task DownloadLatestReleaseFileAsync(
            GitHubReleaseFileRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.ArchiveEntryFileName == failFileName)
            {
                throw new HttpRequestException("test failure");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllTextAsync(request.DestinationPath, "installed", cancellationToken);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"madi-application-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
