using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Madi.Core;
using Madi.Runtime.Authentication;
using Madi.Services;
using Madi.UI;

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
        var ui = new RecordingUserInterface();
        var application = CreateApplication(directory.Path, downloader, ui);

        var exitCode = await application.RunAsync(["unknown"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(downloader.Requests);
    }

    [Fact]
    public async Task FailedInstallWarnsAndContinuesWithOtherToolAndCommand()
    {
        if (!IsSupportedPlatform())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var downloader = new RecordingDownloader(failFileName: "fd.exe");
        var ui = new RecordingUserInterface();
        var application = CreateApplication(directory.Path, downloader, ui);

        var exitCode = await application.RunAsync(["unknown"], CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(["fd.exe", "rg.exe"], downloader.Requests.Select(x => x.ArchiveEntryFileName));
        Assert.False(File.Exists(Path.Combine(directory.Path, "fd.exe")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "rg.exe")));
        Assert.Contains(ui.Lines, line => line.Contains("경고: fd.exe", StringComparison.Ordinal));
        Assert.Contains(ui.Lines, line => line.Contains("사용법:", StringComparison.Ordinal));
    }

    private static bool IsSupportedPlatform() =>
        OperatingSystem.IsWindows()
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static Application CreateApplication(
        string utilityBinDirectory,
        IGitHubReleaseFileDownloader downloader,
        IUserInterface ui) => new(
            new StubAgent(),
            new StubStore(),
            new ModelOptions("test", ReasoningEffort.Medium),
            [],
            new StubAuthentication(),
            ui,
            downloader,
            utilityBinDirectory);

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

    private sealed class RecordingUserInterface : IUserInterface
    {
        public List<string> Lines { get; } = [];

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public void Write(string message) { }

        public void WriteLine(string message) => Lines.Add(message);
    }

    private sealed class StubAgent : IAgent
    {
        public async IAsyncEnumerable<AgentEvent> RespondAsync(
            Work? work,
            Session session,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubStore : IWorkSessionStore
    {
        public Task<IReadOnlyList<Work>> LoadWorksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Work>>([]);
        public Task<IReadOnlyList<Session>> LoadSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Session>>([]);
        public Task<CurrentConversation?> LoadCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CurrentConversation?>(null);
        public Task SaveWorkAsync(Work work, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveSessionAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendConversationAsync(Session session, int startIndex, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetCurrentAsync(Session session, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubAuthentication : IOAuthAuthentication
    {
        public Task<string> GetAuthorizationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
        public Task LoginAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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
