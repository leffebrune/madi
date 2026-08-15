namespace Madi.Core;

public sealed class Session
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? WorkId { get; set; }

    public required ModelOptions Model { get; set; }

    public int MaxToolRounds { get; set; } = 16;

    public string Instructions { get; set; } = "You are a helpful assistant.";

    public List<ModelInputItem> Conversation { get; } = [];
}
