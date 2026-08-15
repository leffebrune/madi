using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Madi.Runtime.Tools;

internal static class ToolSupport
{
    public const int MaxLines = 2_000;
    public const int MaxBytes = 50 * 1024;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MutationLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static string ResolvePath(string path, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var cleaned = path.Trim();
        if (cleaned.StartsWith('@'))
        {
            cleaned = cleaned[1..];
        }

        if (cleaned == "~")
        {
            cleaned = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (cleaned.StartsWith($"~{Path.DirectorySeparatorChar}") || cleaned.StartsWith("~/"))
        {
            cleaned = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                cleaned[2..]);
        }

        return Path.GetFullPath(cleaned, workingDirectory);
    }

    public static JsonElement ParseSchema(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }

    public static string GetRequiredString(JsonElement arguments, string name)
    {
        ValidateArguments(arguments);
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Tool argument '{name}' must be a string.", nameof(arguments));
        }
        return value.GetString()!;
    }

    public static int? GetOptionalInt32(JsonElement arguments, string name)
    {
        ValidateArguments(arguments);
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new ArgumentException($"Tool argument '{name}' must be an integer.", nameof(arguments));
        }
        return result;
    }

    public static double? GetOptionalDouble(JsonElement arguments, string name)
    {
        ValidateArguments(arguments);
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result))
        {
            throw new ArgumentException($"Tool argument '{name}' must be a number.", nameof(arguments));
        }
        return result;
    }

    public static void ValidateArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must be a JSON object.", nameof(arguments));
        }
    }

    public static async Task<T> WithMutationLockAsync<T>(
        string absolutePath,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        var gate = MutationLocks.GetOrAdd(absolutePath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    public static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    public static string RestoreLineEndings(string value, string lineEnding) =>
        lineEnding == "\r\n" ? value.Replace("\n", "\r\n", StringComparison.Ordinal) : value;

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024d:F1}KB",
        _ => $"{bytes / (1024d * 1024d):F1}MB",
    };

    public static (string Content, int OutputLines, int TotalLines, bool Truncated, string? By,
        bool FirstLineExceedsLimit) TruncateHead(string content)
    {
        var lines = SplitForCounting(content);
        var totalBytes = Encoding.UTF8.GetByteCount(content);
        if (lines.Count <= MaxLines && totalBytes <= MaxBytes)
        {
            return (content, lines.Count, lines.Count, false, null, false);
        }

        if (lines.Count > 0 && Encoding.UTF8.GetByteCount(lines[0]) > MaxBytes)
        {
            return (string.Empty, 0, lines.Count, true, "bytes", true);
        }

        var result = new List<string>();
        var bytes = 0;
        var by = "lines";
        foreach (var line in lines.Take(MaxLines))
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line) + (result.Count == 0 ? 0 : 1);
            if (bytes + lineBytes > MaxBytes)
            {
                by = "bytes";
                break;
            }

            result.Add(line);
            bytes += lineBytes;
        }

        return (string.Join('\n', result), result.Count, lines.Count, true, by, false);
    }

    public static string TruncateTail(string content, out bool truncated, out int totalLines, out int outputLines)
    {
        var lines = SplitForCounting(content);
        totalLines = lines.Count;
        var totalBytes = Encoding.UTF8.GetByteCount(content);
        truncated = lines.Count > MaxLines || totalBytes > MaxBytes;
        if (!truncated)
        {
            outputLines = lines.Count;
            return content;
        }

        var selected = new LinkedList<string>();
        var bytes = 0;
        for (var i = lines.Count - 1; i >= 0 && selected.Count < MaxLines; i--)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(lines[i]) + (selected.Count == 0 ? 0 : 1);
            if (bytes + lineBytes > MaxBytes)
            {
                if (selected.Count == 0)
                {
                    selected.AddFirst(TakeUtf8Tail(lines[i], MaxBytes));
                }
                break;
            }

            selected.AddFirst(lines[i]);
            bytes += lineBytes;
        }

        outputLines = selected.Count;
        return string.Join('\n', selected);
    }

    private static List<string> SplitForCounting(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var lines = content.Split('\n').ToList();
        if (content.EndsWith('\n'))
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines;
    }

    private static string TakeUtf8Tail(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        var start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xc0) == 0x80)
        {
            start++;
        }
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }
}
