namespace Madi.Core;

/// <summary>The amount of reasoning requested from a model.</summary>
public enum ReasoningEffort
{
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
}

/// <summary>Provider-neutral model selection and generation settings.</summary>
public sealed record ModelOptions(
    string Model,
    ReasoningEffort? ReasoningEffort = null,
    string? Provider = null)
{
    public string Model { get; init; } =
        string.IsNullOrWhiteSpace(Model)
            ? throw new ArgumentException("A model name is required.", nameof(Model))
            : Model;

    public string? Provider { get; init; } =
        string.IsNullOrWhiteSpace(Provider) ? null : Provider;

    public string QualifiedName => Provider is null ? Model : $"{Provider}/{Model}";
}
