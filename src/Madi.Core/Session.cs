namespace Madi.Core;

public sealed class Session
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? WorkId { get; set; }
    public string? WorkingDirectory { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public required ModelOptions Model { get; set; }

    public List<ModelInputItem> Conversation { get; } = [];
}
