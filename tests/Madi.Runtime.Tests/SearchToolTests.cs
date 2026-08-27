using System.Text.Json;
using Madi.Runtime.Tools;

namespace Madi.Runtime.Tests;

public sealed class SearchToolTests
{
    [Fact]
    public void CatalogRegistersDedicatedSearchTools()
    {
        var catalog = new ToolCatalog();

        Assert.Same(catalog.Grep, catalog.Get("grep"));
        Assert.Same(catalog.Find, catalog.Get("find"));
        Assert.Contains(catalog.Grep, catalog.All);
        Assert.Contains(catalog.Find, catalog.All);
        Assert.True(catalog.Grep.SupportsParallelExecution);
        Assert.True(catalog.Find.SupportsParallelExecution);
    }

    [Fact]
    public void SearchSchemasUseStructuredArguments()
    {
        var grep = new GrepTool();
        var find = new FindTool();

        Assert.Equal("pattern", grep.Parameters.GetProperty("required")[0].GetString());
        Assert.Equal("pattern", find.Parameters.GetProperty("required")[0].GetString());
        Assert.False(grep.Parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.False(find.Parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("Prefer this tool over bash", grep.Description);
        Assert.Contains("Prefer this tool over bash", find.Description);
    }

    [Fact]
    public async Task GrepSearchesContentsAndTreatsNoMatchAsSuccess()
    {
        var executable = FindExecutable("rg");
        if (executable is null)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "sample.txt"), "alpha\nbeta\n");
        var tool = new GrepTool(directory.Path, executablePath: executable);

        var match = await tool.ExecuteAsync("beta", literal: true);
        var noMatch = await tool.ExecuteAsync("missing", literal: true);

        Assert.Contains("sample.txt", match);
        Assert.Contains(":2:beta", match);
        Assert.Equal("(no matches)", noMatch);
    }

    [Fact]
    public async Task FindLocatesFilesAndSupportsTypeFilter()
    {
        var executable = FindExecutable("fd");
        if (executable is null)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "needle.txt"), "content");
        Directory.CreateDirectory(Path.Combine(directory.Path, "needle-dir"));
        var tool = new FindTool(directory.Path, executablePath: executable);

        var files = await tool.ExecuteAsync("needle", type: "file");

        Assert.Contains("needle.txt", files);
        Assert.DoesNotContain("needle-dir", files);
    }

    [Fact]
    public async Task JsonArgumentsRejectWrongBooleanTypes()
    {
        using var document = JsonDocument.Parse("""{"pattern":"x","hidden":"yes"}""");

        await Assert.ThrowsAsync<ArgumentException>(
            () => new GrepTool().ExecuteAsync(document.RootElement));
    }

    private static string? FindExecutable(string name)
    {
        var names = OperatingSystem.IsWindows() ? new[] { $"{name}.exe", name } : new[] { name };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"madi-search-tool-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
