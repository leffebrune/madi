namespace Madi.Services;

/// <summary>Installs a single file from a ZIP asset in a GitHub repository's latest release.</summary>
public interface IGitHubReleaseFileDownloader
{
    Task DownloadLatestReleaseFileAsync(
        GitHubReleaseFileRequest request,
        CancellationToken cancellationToken = default);
}
