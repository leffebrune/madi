namespace Madi.Runtime.Tools;

internal static class ExecutableResolver
{
    public static string Resolve(string name, string? explicitPath, string? utilityBinDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            return File.Exists(fullPath)
                ? fullPath
                : throw new FileNotFoundException($"Executable not found: {fullPath}", fullPath);
        }

        var bundled = Find(string.Join(Path.PathSeparator, GetBundledBinDirectories(utilityBinDirectory)), name);
        if (bundled is not null)
        {
            return bundled;
        }

        var onPath = Find(Environment.GetEnvironmentVariable("PATH"), name);
        return onPath ?? throw new FileNotFoundException(
            $"Executable '{name}' was not found. Install it, add it to PATH, or install the bundled Madi utilities.");
    }

    public static string? Find(string? searchPath, string name)
    {
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return null;
        }

        var names = OperatingSystem.IsWindows() ? new[] { $"{name}.exe", name } : new[] { name };
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var fileName in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static IEnumerable<string> GetBundledBinDirectories(string? utilityBinDirectory) =>
        new[]
        {
            utilityBinDirectory,
            Path.Combine(AppContext.BaseDirectory, "bin"),
        }
        .Where(static path => !string.IsNullOrWhiteSpace(path))
        .Select(static path => Path.GetFullPath(path!))
        .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
