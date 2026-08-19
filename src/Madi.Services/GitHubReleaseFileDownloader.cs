using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Madi.Services;

public sealed class GitHubReleaseFileDownloader : IGitHubReleaseFileDownloader
{
    private const string GitHubApiVersion = "2022-11-28";
    private readonly HttpClient _httpClient;

    public GitHubReleaseFileDownloader(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task DownloadLatestReleaseFileAsync(
        GitHubReleaseFileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var destinationPath = Path.GetFullPath(request.DestinationPath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("Destination path must include a directory.", nameof(request));
        Directory.CreateDirectory(destinationDirectory);

        var archivePath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.download");
        var extractedPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.install");

        try
        {
            var assetUri = await ResolveLatestAssetUriAsync(request, cancellationToken);
            await DownloadAsync(assetUri, archivePath, cancellationToken);
            await ExtractFileAsync(
                archivePath,
                request.ArchiveEntryFileName,
                extractedPath,
                cancellationToken);
            File.Move(extractedPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(archivePath);
            TryDelete(extractedPath);
        }
    }

    private async Task<Uri> ResolveLatestAssetUriAsync(
        GitHubReleaseFileRequest request,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            $"https://api.github.com/repos/{Uri.EscapeDataString(request.RepositoryOwner)}/" +
            $"{Uri.EscapeDataString(request.RepositoryName)}/releases/latest");
        using var message = CreateRequest(HttpMethod.Get, uri, acceptGitHubJson: true);
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "GitHub latest release request", cancellationToken);

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub latest release response does not contain an assets array.");
        }

        var matches = new List<(string Name, string Url)>();
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement)
                || !asset.TryGetProperty("browser_download_url", out var urlElement))
            {
                continue;
            }

            var name = nameElement.GetString();
            var url = urlElement.GetString();
            if (name?.EndsWith(request.AssetNameSuffix, StringComparison.OrdinalIgnoreCase) == true
                && !string.IsNullOrWhiteSpace(url))
            {
                matches.Add((name, url));
            }
        }

        if (matches.Count != 1)
        {
            throw new InvalidDataException(
                $"Expected exactly one release asset ending in '{request.AssetNameSuffix}' for " +
                $"{request.RepositoryOwner}/{request.RepositoryName}, but found {matches.Count}.");
        }

        return Uri.TryCreate(matches[0].Url, UriKind.Absolute, out var assetUri)
            ? assetUri
            : throw new InvalidDataException(
                $"Release asset '{matches[0].Name}' has an invalid download URL.");
    }

    private async Task DownloadAsync(
        Uri assetUri,
        string archivePath,
        CancellationToken cancellationToken)
    {
        using var message = CreateRequest(HttpMethod.Get, assetUri, acceptGitHubJson: false);
        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "GitHub release asset download", cancellationToken);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task ExtractFileAsync(
        string archivePath,
        string archiveEntryFileName,
        string extractedPath,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var matches = archive.Entries
            .Where(entry => string.Equals(
                Path.GetFileName(entry.FullName),
                archiveEntryFileName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
        {
            throw new InvalidDataException(
                $"Expected exactly one '{archiveEntryFileName}' entry in the release archive, " +
                $"but found {matches.Count}.");
        }

        await using var source = matches[0].Open();
        await using var destination = new FileStream(
            extractedPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        bool acceptGitHubJson)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Madi", "1.0"));
        request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
        if (acceptGitHubJson)
        {
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"{operation} failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}",
            null,
            response.StatusCode);
    }

    private static void Validate(GitHubReleaseFileRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AssetNameSuffix);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArchiveEntryFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        if (Path.GetFileName(request.ArchiveEntryFileName) != request.ArchiveEntryFileName)
        {
            throw new ArgumentException("Archive entry file name must not contain a path.", nameof(request));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Preserve the original download or extraction error.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original download or extraction error.
        }
    }
}
