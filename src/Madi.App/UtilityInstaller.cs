using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Madi.Services;

namespace Madi.App;

public sealed class UtilityInstaller
{
    private readonly IGitHubReleaseFileDownloader _downloader;
    private readonly string _utilityBinDirectory;

    public UtilityInstaller(
        IGitHubReleaseFileDownloader downloader,
        string utilityBinDirectory)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        ArgumentException.ThrowIfNullOrWhiteSpace(utilityBinDirectory);
        _utilityBinDirectory = Path.GetFullPath(utilityBinDirectory);
    }

    public async IAsyncEnumerable<AppEvent> EnsureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            yield return new AppMessage(
                "경고: fd/rg 자동 설치는 Windows x64에서만 지원됩니다.",
                AppMessageKind.Warning);
            yield break;
        }

        var utilities = new[]
        {
            new GitHubReleaseFileRequest(
                "sharkdp",
                "fd",
                "x86_64-pc-windows-msvc.zip",
                "fd.exe",
                Path.Combine(_utilityBinDirectory, "fd.exe")),
            new GitHubReleaseFileRequest(
                "BurntSushi",
                "ripgrep",
                "x86_64-pc-windows-msvc.zip",
                "rg.exe",
                Path.Combine(_utilityBinDirectory, "rg.exe")),
        };

        foreach (var utility in utilities)
        {
            if (File.Exists(utility.DestinationPath))
            {
                continue;
            }

            Exception? failure = null;
            try
            {
                await _downloader.DownloadLatestReleaseFileAsync(utility, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (failure is not null)
            {
                yield return new AppMessage(
                    $"경고: {utility.ArchiveEntryFileName}을(를) 설치하지 못했습니다: " +
                    failure.Message,
                    AppMessageKind.Warning);
            }
        }
    }
}
