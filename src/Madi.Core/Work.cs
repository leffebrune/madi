namespace Madi.Core;

public sealed class Work
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;

    public List<Session> Sessions { get; } = [];
}