using System.Text.Json.Serialization;

namespace Madi.Runtime.Persistence;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(List<ModelProviderDocument>))]
[JsonSerializable(typeof(List<LegacyModelDocument>))]
internal sealed partial class ModelCatalogJsonContext : JsonSerializerContext;
