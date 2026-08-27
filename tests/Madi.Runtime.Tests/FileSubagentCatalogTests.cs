using Madi.Runtime.Persistence;

namespace Madi.Runtime.Tests;

public sealed class FileSubagentCatalogTests
{
    [Fact]
    public async Task MissingSubagentsDirectoryCreatesDefaultsWithReviewerAndScanner()
    {
        using var directory = new TemporaryDirectory();
        var catalog = new FileSubagentCatalog(directory.Path);
        var subagentsDir = Path.Combine(directory.Path, "subagents");

        Assert.False(Directory.Exists(subagentsDir));

        var subagents = await catalog.LoadAsync();

        Assert.True(Directory.Exists(subagentsDir));
        Assert.True(File.Exists(Path.Combine(subagentsDir, "reviewer.md")));
        Assert.True(File.Exists(Path.Combine(subagentsDir, "scanner.md")));

        Assert.Equal(2, subagents.Count);

        var reviewer = subagents.Single(s => s.Name == "reviewer");
        Assert.Equal("OpenAI/gpt-5.6-luna:high", reviewer.Model);
        Assert.Equal(["read", "grep", "find"], reviewer.Tools);
        Assert.Contains("code review specialist", reviewer.Instructions);

        var scanner = subagents.Single(s => s.Name == "scanner");
        Assert.Equal("OpenAI/gpt-5.6-luna:medium", scanner.Model);
        Assert.Equal(["read", "grep", "find"], scanner.Tools);
        Assert.Contains("codebase exploration specialist", scanner.Instructions);
    }

    [Fact]
    public async Task UnmodifiedLegacyDefaultsMigrateToConfiguredModel()
    {
        using var directory = new TemporaryDirectory();
        await new FileSubagentCatalog(directory.Path).LoadAsync();

        var subagents = await new FileSubagentCatalog(
            directory.Path,
            "OpenAI API/api-model").LoadAsync();

        Assert.Equal(
            "OpenAI API/api-model:high",
            subagents.Single(item => item.Name == "reviewer").Model);
        Assert.Equal(
            "OpenAI API/api-model:medium",
            subagents.Single(item => item.Name == "scanner").Model);
    }

    [Fact]
    public async Task LegacyDefaultToolsMigrateToDedicatedSearchTools()
    {
        using var directory = new TemporaryDirectory();
        await new FileSubagentCatalog(directory.Path).LoadAsync();
        var path = Path.Combine(directory.Path, "subagents", "scanner.md");
        var content = await File.ReadAllTextAsync(path);
        content = content
            .Replace(
                "  - find" + Environment.NewLine,
                "  - find" + Environment.NewLine + "  - bash" + Environment.NewLine,
                StringComparison.Ordinal)
            .Replace("  - grep" + Environment.NewLine, string.Empty, StringComparison.Ordinal)
            .Replace("  - find" + Environment.NewLine, string.Empty, StringComparison.Ordinal)
            .Replace(
                Environment.NewLine + "Use grep for content searches and find for locating files.",
                string.Empty,
                StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, content);

        var subagents = await new FileSubagentCatalog(directory.Path).LoadAsync();

        Assert.Equal(
            ["read", "grep", "find"],
            subagents.Single(item => item.Name == "scanner").Tools);
    }

    [Fact]
    public async Task ExistingSubagentsDirectoryLoadsFilesWithoutOverwriting()
    {
        using var directory = new TemporaryDirectory();
        var subagentsDir = Path.Combine(directory.Path, "subagents");
        Directory.CreateDirectory(subagentsDir);
        await File.WriteAllTextAsync(
            Path.Combine(subagentsDir, "custom.md"),
            """
            ---
            name: custom
            description: A custom agent
            model: OpenAI/gpt-5.6-luna:low
            tools:
              - read
            ---

            Custom instructions.
            """);

        var catalog = new FileSubagentCatalog(directory.Path);
        var subagents = await catalog.LoadAsync();

        Assert.Single(subagents);
        Assert.Equal("custom", subagents[0].Name);
        Assert.False(File.Exists(Path.Combine(subagentsDir, "reviewer.md")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"madi-subagent-catalog-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
