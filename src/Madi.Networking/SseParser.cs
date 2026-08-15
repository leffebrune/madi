using System.Runtime.CompilerServices;

namespace Madi.Networking;

/// <summary>Incrementally parses an SSE byte stream into complete events.</summary>
public static class SseParser
{
    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream);
        string? eventType = null;
        string? id = null;
        int? retry = null;
        var dataLines = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return new SseEvent(eventType, string.Join('\n', dataLines), id, retry);
                }
                eventType = null;
                retry = null;
                dataLines.Clear();
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    eventType = value;
                    break;
                case "data":
                    dataLines.Add(value);
                    break;
                case "id" when !value.Contains('\0'):
                    id = value;
                    break;
                case "retry" when int.TryParse(value, out var retryMilliseconds):
                    retry = retryMilliseconds;
                    break;
            }
        }

        if (dataLines.Count > 0)
        {
            yield return new SseEvent(eventType, string.Join('\n', dataLines), id, retry);
        }
    }
}
