namespace Madi.Core;

public sealed class Work
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public List<Session> Sessions { get; } = [];
}
