using System.Text;
using Madi.Core;

namespace Madi.Runtime.Persistence;

public sealed class FileProjectInstructionLoader : IProjectInstructionLoader
{
    private readonly string _sourcePath;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public FileProjectInstructionLoader(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        _sourcePath = Path.Combine(Path.GetFullPath(workingDirectory), "AGENTS.md");
    }

    public async Task<ProjectInstructions?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var bytes = await File.ReadAllBytesAsync(_sourcePath, cancellationToken);
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            var content = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            return string.IsNullOrWhiteSpace(content) ? null : new ProjectInstructions(_sourcePath, content);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new IOException($"프로젝트 지침 파일 '{_sourcePath}'을 읽을 수 없습니다.", exception);
        }
    }
}
