namespace Madi.Core;

/// <summary>Metadata describing a model that can be selected by the user.</summary>
public sealed record AvailableModel
{
    /// <summary>Application-local identifier used for selection and configuration.</summary>
    public required string Name { get; init; }

    /// <summary>Provider model name sent in API requests.</summary>
    public required string ModelName { get; init; }

    public required string Provider { get; init; }
    public required string ProviderType { get; init; }
    public string? BaseUrl { get; init; }
    public bool SupportsReasoning { get; init; }
    public int MaxContextTokens { get; init; }
    public int? MaxOutputTokens { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)
            || string.IsNullOrWhiteSpace(ModelName)
            || string.IsNullOrWhiteSpace(Provider)
            || string.IsNullOrWhiteSpace(ProviderType))
        {
            throw new InvalidDataException(
                "Model identifier, provider model name, provider, and provider type are required.");
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
