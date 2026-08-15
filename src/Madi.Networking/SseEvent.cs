using System.Text;

namespace Madi.Networking;

/// <summary>A single server-sent event after SSE framing has been decoded.</summary>
public sealed record SseEvent(
    string? EventType,
    string Data,
    string? Id = null,
    int? Retry = null)
{
    /// <summary>Formats the event back into a readable SSE representation.</summary>
    public string ToWireFormat()
    {
        var output = new StringBuilder();
        if (EventType is not null)
        {
            output.Append("event: ").AppendLine(EventType);
        }
        if (Id is not null)
        {
            output.Append("id: ").AppendLine(Id);
        }
        if (Retry is not null)
        {
            output.Append("retry: ").AppendLine(Retry.Value.ToString());
        }
        foreach (var line in Data.Split('\n'))
        {
            output.Append("data: ").AppendLine(line);
        }
        output.AppendLine();
        return output.ToString();
    }

    public override string ToString() => ToWireFormat();
}
