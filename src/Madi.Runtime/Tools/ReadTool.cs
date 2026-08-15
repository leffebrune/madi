using System.Text;
using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Tools;

/// <summary>Reads UTF-8 files with pi-compatible line and byte limits.</summary>
public sealed class ReadTool : ITool
{
    private static readonly JsonElement Schema = ToolSupport.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path to the file to read (relative or absolute)" },
            "offset": { "type": "integer", "description": "Line number to start reading from (1-indexed)" },
            "limit": { "type": "integer", "description": "Maximum number of lines to read" }
          },
          "required": ["path"],
          "additionalProperties": false
        }
        """);

    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
    };

    private readonly string _workingDirectory;

    public ReadTool(string? workingDirectory = null)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
    }

    public string Name => "read";

    public string Description =>
        "Read a text file, optionally starting at a 1-indexed line offset with a line limit.";

    public JsonElement Parameters => Schema;

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            ToolSupport.GetRequiredString(arguments, "path"),
            ToolSupport.GetOptionalInt32(arguments, "offset"),
            ToolSupport.GetOptionalInt32(arguments, "limit"),
            cancellationToken);

    public async Task<string> ExecuteAsync(
        string path,
        int? offset = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (offset is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be a positive, 1-indexed line number.");
        }
        if (limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        }

        var absolutePath = ToolSupport.ResolvePath(path, _workingDirectory);
        var bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken);
        if (ImageMimeTypes.TryGetValue(Path.GetExtension(absolutePath), out var mimeType))
        {
            return $"Read image file [{mimeType}] ({bytes.Length} bytes)";
        }

        var text = Encoding.UTF8.GetString(bytes);
        var allLines = text.Split('\n');
        var start = (offset ?? 1) - 1;
        if (start >= allLines.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"Offset {offset} is beyond end of file ({allLines.Length} lines total).");
        }

        var count = limit is null ? allLines.Length - start : Math.Min(limit.Value, allLines.Length - start);
        var selected = string.Join('\n', allLines.Skip(start).Take(count));
        var truncated = ToolSupport.TruncateHead(selected);
        var startDisplay = start + 1;

        if (truncated.FirstLineExceedsLimit)
        {
            var size = ToolSupport.FormatSize(Encoding.UTF8.GetByteCount(allLines[start]));
            return $"[Line {startDisplay} is {size}, exceeds {ToolSupport.FormatSize(ToolSupport.MaxBytes)} limit. " +
                   $"Use bash: sed -n '{startDisplay}p' {path} | head -c {ToolSupport.MaxBytes}]";
        }

        if (truncated.Truncated)
        {
            var endDisplay = startDisplay + truncated.OutputLines - 1;
            var byteNote = truncated.By == "bytes" ? $" ({ToolSupport.FormatSize(ToolSupport.MaxBytes)} limit)" : string.Empty;
            return $"{truncated.Content}\n\n[Showing lines {startDisplay}-{endDisplay} of {allLines.Length}{byteNote}. " +
                   $"Use offset={endDisplay + 1} to continue.]";
        }

        if (start + count < allLines.Length)
        {
            var remaining = allLines.Length - start - count;
            return $"{truncated.Content}\n\n[{remaining} more lines in file. Use offset={start + count + 1} to continue.]";
        }

        return truncated.Content;
    }
}
