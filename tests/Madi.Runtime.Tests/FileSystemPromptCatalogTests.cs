using Madi.Runtime.Persistence;

namespace Madi.Runtime.Tests;

public sealed class FileSystemPromptCatalogTests
{
    [Fact]
    public async Task MissingSystemMdCreatesDefaultFileAndReturnsContent()
    {
        using var directory = new TemporaryDirectory();
        var catalog = new FileSystemPromptCatalog(directory.Path);
        var promptPath = Path.Combine(directory.Path, "SYSTEM.md");

        Assert.False(File.Exists(promptPath));

        var prompt = await catalog.LoadAsync();

        Assert.True(File.Exists(promptPath));
        Assert.False(string.IsNullOrWhiteSpace(prompt));
        Assert.Contains("You are Madi", prompt);

        var savedContent = await File.ReadAllTextAsync(promptPath);
        Assert.Equal(prompt, savedContent);
    }

    [Fact]
    public async Task ExistingSystemMdLoadsContentWithoutOverwriting()
    {
        using var directory = new TemporaryDirectory();
        var promptPath = Path.Combine(directory.Path, "SYSTEM.md");
        const string customPrompt = "You are a custom AI agent tailored for this project.";
        await File.WriteAllTextAsync(promptPath, customPrompt);

        var catalog = new FileSystemPromptCatalog(directory.Path);
        var prompt = await catalog.LoadAsync();

        Assert.Equal(customPrompt, prompt);
        var fileContent = await File.ReadAllTextAsync(promptPath);
        Assert.Equal(customPrompt, fileContent);
    }

    [Fact]
    public async Task EmptySystemMdThrowsInvalidDataException()
    {
        using var directory = new TemporaryDirectory();
        var promptPath = Path.Combine(directory.Path, "SYSTEM.md");
        await File.WriteAllTextAsync(promptPath, "   \n\r\t  ");

        var catalog = new FileSystemPromptCatalog(directory.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.LoadAsync());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"madi-system-prompt-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
