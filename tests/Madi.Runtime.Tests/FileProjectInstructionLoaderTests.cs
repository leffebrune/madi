using System.Text;
using Madi.Runtime.Persistence;

namespace Madi.Runtime.Tests;

public sealed class FileProjectInstructionLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    public FileProjectInstructionLoaderTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesUtf8ContentAndReturnsAbsolutePath(bool bom)
    {
        const string content = "  한글\r\n지침\n  ";
        var path = Path.Combine(_root, "AGENTS.md");
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(bom));
        var result = await new FileProjectInstructionLoader(_root).LoadAsync();
        Assert.Equal(path, result!.SourcePath);
        Assert.Equal(content, result.Content);
    }

    [Fact]
    public async Task ReflectsCreationModificationAndDeletionWithoutSearchingOtherPaths()
    {
        var child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        Directory.CreateDirectory(Path.Combine(child, "nested"));
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "parent");
        await File.WriteAllTextAsync(Path.Combine(child, "nested", "AGENTS.md"), "nested");
        await File.WriteAllTextAsync(Path.Combine(child, "CLAUDE.md"), "other");
        var loader = new FileProjectInstructionLoader(child);
        Assert.Null(await loader.LoadAsync());
        var path = Path.Combine(child, "AGENTS.md");
        foreach (var content in new[] { "", " \r\n\t", "first", "second" })
        {
            await File.WriteAllTextAsync(path, content);
            Assert.Equal(string.IsNullOrWhiteSpace(content) ? null : content,
                (await loader.LoadAsync())?.Content);
        }
        File.Delete(path);
        Assert.Null(await loader.LoadAsync());
        Assert.Null(await new FileProjectInstructionLoader(Path.Combine(child, "missing")).LoadAsync());
    }

    [Fact]
    public async Task InvalidUtf8AndReadFailureIncludePathAndInnerException()
    {
        var path = Path.Combine(_root, "AGENTS.md");
        var loader = new FileProjectInstructionLoader(_root);
        await File.WriteAllBytesAsync(path, [0xff]);
        var error = await Assert.ThrowsAsync<IOException>(() => loader.LoadAsync());
        Assert.Contains(path, error.Message);
        Assert.IsType<DecoderFallbackException>(error.InnerException);
        File.Delete(path);
        Directory.CreateDirectory(path);
        error = await Assert.ThrowsAsync<IOException>(() => loader.LoadAsync());
        Assert.Contains(path, error.Message);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task CancellationPropagatesEvenForMissingFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FileProjectInstructionLoader(_root).LoadAsync(cancellation.Token));
    }
}
