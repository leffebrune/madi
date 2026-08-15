using System.Text.Json;

namespace Madi.Core;

/// <summary>A single coding tool that can be invoked with structured arguments.</summary>
public interface ITool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema for the arguments accepted by this tool.</summary>
    JsonElement Parameters { get; }

    Task<string> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken = default);
}
