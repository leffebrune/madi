using Madi.Core;
using Madi.Runtime.Tools;

namespace Madi.Runtime.Agents;

public sealed class SubagentRunner : ISubagentRunner
{
    private readonly IModelProvider _provider;
    private readonly IReadOnlyDictionary<string, SubagentDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly IReadOnlyList<AvailableModel> _availableModels;
    private readonly SemaphoreSlim _concurrencyGate;

    public SubagentRunner(
        IModelProvider provider,
        IReadOnlyList<SubagentDefinition> definitions,
        IReadOnlyList<AvailableModel> availableModels,
        IReadOnlyList<ITool> tools,
        int maxConcurrency = 4)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(availableModels);
        ArgumentNullException.ThrowIfNull(tools);
        if (maxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrency),
                "Maximum subagent concurrency must be greater than zero.");
        }

        _availableModels = availableModels;
        _tools = tools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        _definitions = definitions.ToDictionary(
            static definition => definition.Name,
            StringComparer.Ordinal);
        _concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        foreach (var definition in definitions)
        {
            definition.Validate();
            _ = definition.ToModelOptions(_availableModels);
            foreach (var toolName in definition.Tools)
            {
                if (string.Equals(toolName, "subagent", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Subagent '{definition.Name}' cannot delegate to another subagent.");
                }

                if (!_tools.ContainsKey(toolName))
                {
                    throw new InvalidDataException(
                        $"Subagent '{definition.Name}' references unknown tool '{toolName}'.");
                }
            }
        }
    }

    public async Task<string> RunAsync(
        string name,
        string task,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        if (!_definitions.TryGetValue(name, out var definition))
        {
            throw new KeyNotFoundException($"Unknown subagent: {name}");
        }

        await _concurrencyGate.WaitAsync(cancellationToken);
        try
        {
            var childTools = definition.Tools.Select(toolName => _tools[toolName]).ToArray();
            var systemInstructions = SystemInstructionComposer.Compose(
                definition.Instructions,
                childTools);
            IAgent agent = new Agent(_provider, systemInstructions, childTools);
            var session = new Session
            {
                Id = $"subagent-{Guid.CreateVersion7()}",
                Title = $"Subagent: {definition.Name}",
                Model = definition.ToModelOptions(_availableModels),
            };

            string? result = null;
            await foreach (var item in agent.RespondAsync(
                work: null,
                session,
                task,
                cancellationToken))
            {
                if (item is AgentResponseCompleted completed)
                {
                    result = completed.Text;
                }
            }

            return result
                ?? throw new InvalidOperationException(
                    $"Subagent '{definition.Name}' completed without a final response.");
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }
}
