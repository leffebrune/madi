namespace Madi.Core;

/// <summary>Metadata describing a model that can be selected by the user.</summary>
public sealed record AvailableModel
{
    /// <summary>Provider model name sent in API requests.</summary>
    public required string Name { get; init; }

    public required string Provider { get; init; }
    public required string Api { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    /// <summary>Whether to send prompt_cache_key; inherited from provider configuration.</summary>
    public bool SendPromptCacheKey { get; init; } = true;
    public bool SupportsReasoning { get; init; }
    public int MaxContextTokens { get; init; }
    public int? MaxOutputTokens { get; init; }

    public string QualifiedName => $"{Provider}/{Name}";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)
            || string.IsNullOrWhiteSpace(Provider)
            || string.IsNullOrWhiteSpace(Api))
        {
            throw new InvalidDataException(
                "Model name, provider, and provider API are required.");
        }

        if (MaxContextTokens <= 0)
        {
            throw new InvalidDataException($"Model '{Name}' must have a positive context window.");
        }
    }
}

public interface IModelCatalog
{
    Task<IReadOnlyList<AvailableModel>> LoadAsync(CancellationToken cancellationToken = default);
}
