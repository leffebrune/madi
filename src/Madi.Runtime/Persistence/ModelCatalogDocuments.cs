using Madi.Core;

namespace Madi.Runtime.Persistence;

internal sealed record ModelProviderDocument
{
    public string Provider { get; init; } = string.Empty;
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string Api { get; init; } = string.Empty;
    public List<ModelEntryDocument> Models { get; init; } = [];

    public IReadOnlyList<AvailableModel> ToDomain()
    {
        if (string.IsNullOrWhiteSpace(Provider) || string.IsNullOrWhiteSpace(Api))
        {
            throw new InvalidDataException("Model provider name and API are required.");
        }

        if (Models is null)
        {
            throw new InvalidDataException($"Model provider '{Provider}' requires a models array.");
        }

        return Models.Select(model => model.ToDomain(this)).ToList();
    }
}

internal sealed record ModelEntryDocument
{
    public string Name { get; init; } = string.Empty;
    public bool SupportsReasoning { get; init; }
    public int MaxContextTokens { get; init; }
    public int? MaxOutputTokens { get; init; }

    public AvailableModel ToDomain(ModelProviderDocument provider)
    {
        var model = new AvailableModel
        {
            Name = Name,
            Provider = provider.Provider,
            Api = provider.Api,
            BaseUrl = provider.BaseUrl,
            ApiKey = provider.ApiKey,
            SupportsReasoning = SupportsReasoning,
            MaxContextTokens = MaxContextTokens,
            MaxOutputTokens = MaxOutputTokens,
        };
        model.Validate();
        return model;
    }
}

internal sealed record LegacyModelDocument
{
    public string? Name { get; init; }
    public string? ModelName { get; init; }
    public string? Provider { get; init; }
    public string? ProviderType { get; init; }
    public string? BaseUrl { get; init; }
    public bool SupportsReasoning { get; init; }
    public int MaxContextTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
}
