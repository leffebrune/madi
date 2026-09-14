using Madi.Core;
using Madi.Runtime.Providers;
using Madi.Runtime.Tools;

namespace Madi.Runtime.Agents;

public sealed class SubagentRunner : ISubagentRunner
{
    private readonly IModelProvider _provider;
    private readonly IReadOnlyDictionary<string, SubagentDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, ITool> _tools;
    private readonly IReadOnlyList<AvailableModel> _availableModels;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly Func<AgentRuntimeContext>? _runtimeContextProvider;

    public SubagentRunner(
        IModelProvider provider,
        IReadOnlyList<SubagentDefinition> definitions,
        IReadOnlyList<AvailableModel> availableModels,
        IReadOnlyList<ITool> tools,
        int maxConcurrency = 4,
        Func<AgentRuntimeContext>? runtimeContextProvider = null)
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

        _runtimeContextProvider = runtimeContextProvider;
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

    public Task<string> RunAsync(
        string name,
        string task,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(name, task, null, onEvent: null, cancellationToken);

    public Task<string> RunAsync(
        string name,
        string task,
        Func<SubagentRunEvent, ValueTask> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        return RunCoreAsync(name, task, null, onEvent, cancellationToken);
    }

    public Task<string> RunAsync(
        string name,
        string task,
        ProjectInstructions? projectInstructions,
        Func<SubagentRunEvent, ValueTask> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        return RunCoreAsync(name, task, projectInstructions, onEvent, cancellationToken);
    }

    private async Task<string> RunCoreAsync(
        string name,
        string task,
        ProjectInstructions? projectInstructions,
        Func<SubagentRunEvent, ValueTask>? onEvent,
        CancellationToken cancellationToken)
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
            var model = definition.ToModelOptions(_availableModels);
            if (_provider is IModelProviderRuntime runtime)
            {
                var authentication = await runtime.GetAuthenticationAsync(model, cancellationToken);
                if (authentication.Status == ModelAuthenticationStatus.ActionRequired)
                {
                    throw new InvalidOperationException(
                        authentication.Message
                        ?? $"Subagent '{definition.Name}'의 모델 인증이 필요합니다.");
                }
            }

            var childTools = definition.Tools.Select(toolName => _tools[toolName]).ToArray();
            var systemInstructions = SystemInstructionComposer.Compose(
                definition.Instructions,
                childTools);
            IAgent agent = new Agent(_provider, systemInstructions, childTools, _runtimeContextProvider,
                new FixedProjectInstructionLoader(projectInstructions));
            var session = new Session
            {
                Id = $"subagent-{Guid.CreateVersion7()}",
                Title = $"Subagent: {definition.Name}",
                Model = model,
            };

            string? result = null;
            await foreach (var item in agent.RespondAsync(
                work: null,
                session,
                task,
                cancellationToken))
            {
                switch (item)
                {
                    case AgentToolCallCompleted started when onEvent is not null:
                        await onEvent(new SubagentToolStarted(
                            started.CallId,
                            started.Name,
                            started.Arguments));
                        break;
                    case AgentToolExecutionCompleted completed when onEvent is not null:
                        await onEvent(new SubagentToolCompleted(
                            completed.CallId,
                            completed.Name,
                            completed.IsError,
                            completed.ToolDuration));
                        break;
                    case AgentResponseCompleted completed:
                        result = completed.Text;
                        break;
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
