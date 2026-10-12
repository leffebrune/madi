using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Madi.Core;

public enum ToolLoopDecision { Continue, Warn, Stop }

/// <summary>Detects exact consecutive tool rounds, ignoring call IDs and JSON property order.</summary>
public sealed class ToolLoopDetector
{
    private string? _previous;
    private int _repetitions;

    internal bool WarningActive => _repetitions >= 3;

    public ToolLoopDecision ObserveRound(
        IReadOnlyList<ModelToolCall> calls,
        IReadOnlyList<ToolResult> results)
    {
        if (calls.Count == 0 || calls.Count != results.Count)
            throw new ArgumentException("A round must contain matching calls and results.");

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            for (var index = 0; index < calls.Count; index++)
            {
                writer.Write(calls[index].Name);
                writer.Write(NormalizeArguments(calls[index].Arguments));
                writer.Write(results[index].IsError);
                writer.Write(Hash(results[index].Output));
            }
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        _repetitions = fingerprint == _previous ? _repetitions + 1 : 1;
        _previous = fingerprint;
        return _repetitions >= 5 ? ToolLoopDecision.Stop
            : _repetitions == 3 ? ToolLoopDecision.Warn : ToolLoopDecision.Continue;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string NormalizeArguments(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                WriteCanonical(writer, document.RootElement);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return arguments;
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
