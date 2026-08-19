namespace Madi.Services;

/// <summary>Describes a file to install from a ZIP asset in a GitHub repository's latest release.</summary>
public sealed record GitHubReleaseFileRequest(
    string RepositoryOwner,
    string RepositoryName,
    string AssetNameSuffix,
    string ArchiveEntryFileName,
    string DestinationPath);
