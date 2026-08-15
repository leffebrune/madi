using System.Text.Json.Serialization;
using Madi.Core;

namespace Madi.Runtime.Persistence;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(List<AvailableModel>))]
internal sealed partial class ModelCatalogJsonContext : JsonSerializerContext;
