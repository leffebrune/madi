namespace Madi.Core;

/// <summary>
/// Defines a subagent loaded from a Markdown file with YAML front matter.
/// <see cref="Instructions"/> comes from the Markdown body rather than the YAML metadata.
/// </summary>
public sealed record SubagentDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Model { get; init; }
    public required IReadOnlyList<string> Tools { get; init; }
    public int? MaxToolRounds { get; init; }
    public required string Instructions { get; init; }

    public void Validate()
    {
        if (!IsValidName(Name))
        {
            throw new InvalidDataException(
                "A subagent name must start with a lowercase letter and contain only lowercase letters, digits, and hyphens.");
        }

        if (string.IsNullOrWhiteSpace(Description))
        {
            throw new InvalidDataException($"Subagent '{Name}' requires a description.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidDataException($"Subagent '{Name}' requires a model setting.");
        }

        _ = ParseModel();

        if (Tools is null || Tools.Count == 0 || Tools.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException($"Subagent '{Name}' requires at least one valid tool name.");
        }

        if (Tools.Distinct(StringComparer.Ordinal).Count() != Tools.Count)
        {
            throw new InvalidDataException($"Subagent '{Name}' contains duplicate tool names.");
        }

        if (MaxToolRounds is <= 0)
        {
            throw new InvalidDataException($"Subagent '{Name}' must have a positive maximum tool round count.");
        }

        if (string.IsNullOrWhiteSpace(Instructions))
        {
            throw new InvalidDataException($"Subagent '{Name}' requires instructions in the Markdown body.");
        }
    }

    public ModelOptions ToModelOptions(IReadOnlyList<AvailableModel> availableModels)
    {
        ArgumentNullException.ThrowIfNull(availableModels);

        var (name, effort) = ParseModel();
        var availableModel = availableModels.FirstOrDefault(model =>
            string.Equals(model.Name, name, StringComparison.OrdinalIgnoreCase));
        if (availableModel is null)
        {
            throw new InvalidDataException(
                $"Subagent '{Name}' references unavailable model '{name}'.");
        }

        return new ModelOptions(availableModel.ModelName, effort);
    }

    private (string Name, ReasoningEffort Effort) ParseModel()
    {
        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidDataException($"Subagent '{Name}' requires a model setting.");
        }

        var separator = Model.IndexOf(':');
        if (separator <= 0
            || separator == Model.Length - 1
            || separator != Model.LastIndexOf(':'))
        {
            throw new InvalidDataException(
                $"Subagent '{Name}' model must use the '<available-model-name>:<reasoning-effort>' format.");
        }

        var modelName = Model[..separator].Trim();
        var effortName = Model[(separator + 1)..].Trim();
        if (modelName.Length == 0
            || !Enum.TryParse<ReasoningEffort>(effortName, true, out var effort)
            || !Enum.IsDefined(effort))
        {
            throw new InvalidDataException(
                $"Subagent '{Name}' has an invalid model setting '{Model}'.");
        }

        return (modelName, effort);
    }

    private static bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name[0] is < 'a' or > 'z')
        {
            return false;
        }

        return name.All(static character =>
            character is >= 'a' and <= 'z'
            || character is >= '0' and <= '9'
            || character == '-');
    }
}
