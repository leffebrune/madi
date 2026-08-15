using System.Text;
using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Tools;

/// <summary>Creates or completely overwrites a UTF-8 file.</summary>
public sealed class WriteTool : ITool
{
    private static readonly JsonElement Schema = ToolSupport.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path to the file to write (relative or absolute)" },
            "content": { "type": "string", "description": "Content to write to the file" }
          },
          "required": ["path", "content"],
          "additionalProperties": false
        }
        """);

    private readonly string _workingDirectory;

    public WriteTool(string? workingDirectory = null)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
    }

    public string Name => "write";

    public string Description =>
        "Create or overwrite a UTF-8 file, creating parent directories when necessary.";

    public JsonElement Parameters => Schema;

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            ToolSupport.GetRequiredString(arguments, "path"),
            ToolSupport.GetRequiredString(arguments, "content"),
            cancellationToken);

    public Task<string> ExecuteAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var absolutePath = ToolSupport.ResolvePath(path, _workingDirectory);
        return ToolSupport.WithMutationLockAsync(
            absolutePath,
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(absolutePath)
                    ?? throw new InvalidOperationException("The target file must have a parent directory.");
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(absolutePath, content, new UTF8Encoding(false), cancellationToken);
                return $"Successfully wrote {Encoding.UTF8.GetByteCount(content)} bytes to {path}";
            },
            cancellationToken);
    }
}
