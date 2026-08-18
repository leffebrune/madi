using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Madi.Core;

/// <summary>
/// Default agent orchestration. Session state is supplied by the caller, while this type
/// executes model and tool calls. UI, transport, and authentication details remain outside.
/// </summary>
public sealed class Agent : IAgent
{
    private readonly IModelProvider _provider;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IReadOnlyDictionary<string, ITool> _toolsByName;

    public Agent(
        IModelProvider provider,
        IReadOnlyList<ITool>? tools = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _tools = tools ?? [];
        _toolsByName = _tools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
    }

    public async IAsyncEnumerable<AgentEvent> RespondAsync(
        Work? work,
        Session session,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ValidateWorkSessionRelationship(work, session);

        if (session.MaxToolRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(session),
                "The session's maximum tool rounds must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(session.Instructions))
        {
            throw new ArgumentException(
                "The session's instructions are required.",
                nameof(session));
        }

        var pendingConversation = new List<ModelInputItem>(session.Conversation)
        {
            new UserMessage(prompt),
        };

        for (var round = 0; round < session.MaxToolRounds; round++)
        {
            ModelResponse? response = null;

            await foreach (var item in _provider.StreamAsync(
                new ModelRequest(
                    pendingConversation,
                    _tools,
                    session.Model,
                    BuildInstructions(work, session),
                    session.Id,
                    _tools.Any(static tool => tool.SupportsParallelExecution)),
                cancellationToken))
            {
                switch (item)
                {
                    case ModelRequestStarted started:
                        yield return new AgentRequestStarted(started.Content);
                        break;
                    case ModelRawEventReceived raw:
                        yield return new AgentRawEventReceived(raw.Content);
                        break;
                    case ModelTextDelta delta:
                        yield return new AgentTextDelta(delta.Text);
                        break;
                    case ModelOutputItemCompleted completed
                        when completed.Item is ModelToolCall toolCall:
                        if (!IsSubagentTool(toolCall.Name))
                        {
                            yield return new AgentToolCallCompleted(
                                toolCall.CallId,
                                toolCall.Name,
                                toolCall.Arguments);
                        }
                        break;
                    case ModelResponseCompleted completed:
                        response = completed.Response;
                        break;
                }
            }

            if (response is null)
            {
                throw new InvalidOperationException(
                    $"Provider '{_provider.Id}' ended without a completed response.");
            }

            if (response.Usage is { } usage)
            {
                yield return new AgentUsageReported(usage);
            }

            pendingConversation.AddRange(response.Output);
            var toolCalls = response.Output.OfType<ModelToolCall>().ToArray();
            if (toolCalls.Length == 0)
            {
                session.Conversation.Clear();
                session.Conversation.AddRange(pendingConversation);
                yield return new AgentResponseCompleted(response.OutputText);
                yield break;
            }

            var runInParallel = toolCalls.Length > 1
                                && toolCalls.All(CanRunInParallel);
            if (runInParallel)
            {
                foreach (var toolCall in toolCalls)
                {
                    if (GetSubagentName(toolCall) is { } subagentName
                        && GetSubagentPrompt(toolCall) is { } subagentPrompt)
                    {
                        yield return new AgentSubagentStarted(
                            toolCall.CallId,
                            subagentName,
                            subagentPrompt);
                    }
                }

                var executions = toolCalls
                    .Select((call, index) => new PendingToolExecution(
                        index,
                        call,
                        ExecuteToolAsync(call, cancellationToken)))
                    .ToList();
                var results = new ToolExecutionResult[toolCalls.Length];
                while (executions.Count > 0)
                {
                    var completedTask = await Task.WhenAny(
                        executions.Select(static execution => execution.Task));
                    var completedIndex = executions.FindIndex(
                        execution => ReferenceEquals(execution.Task, completedTask));
                    var execution = executions[completedIndex];
                    executions.RemoveAt(completedIndex);
                    var result = await completedTask;
                    results[execution.Index] = result;
                    if (GetSubagentName(execution.Call) is { } subagentName)
                    {
                        yield return new AgentSubagentCompleted(
                            execution.Call.CallId,
                            subagentName,
                            result.Output,
                            result.IsError);
                    }
                    else
                    {
                        yield return new AgentToolExecutionCompleted(
                            execution.Call.CallId,
                            execution.Call.Name,
                            result.IsError);
                    }
                }

                for (var index = 0; index < toolCalls.Length; index++)
                {
                    pendingConversation.Add(new ToolResult(
                        toolCalls[index].CallId,
                        results[index].Output));
                }
            }
            else
            {
                foreach (var toolCall in toolCalls)
                {
                    var subagentName = GetSubagentName(toolCall);
                    if (subagentName is not null
                        && GetSubagentPrompt(toolCall) is { } subagentPrompt)
                    {
                        yield return new AgentSubagentStarted(
                            toolCall.CallId,
                            subagentName,
                            subagentPrompt);
                    }

                    var result = await ExecuteToolAsync(toolCall, cancellationToken);
                    pendingConversation.Add(new ToolResult(toolCall.CallId, result.Output));
                    if (subagentName is not null)
                    {
                        yield return new AgentSubagentCompleted(
                            toolCall.CallId,
                            subagentName,
                            result.Output,
                            result.IsError);
                    }
                    else
                    {
                        yield return new AgentToolExecutionCompleted(
                            toolCall.CallId,
                            toolCall.Name,
                            result.IsError);
                    }
                }
            }
        }

        throw new InvalidOperationException(
            $"The model exceeded the maximum of {session.MaxToolRounds} consecutive tool-call rounds.");
    }

    private static void ValidateWorkSessionRelationship(Work? work, Session session)
    {
        if (work is null && session.WorkId is not null)
        {
            throw new ArgumentException(
                "A session assigned to a work requires that work to be supplied.",
                nameof(work));
        }

        if (work is not null
            && !string.Equals(work.Id, session.WorkId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The supplied work does not own the session.",
                nameof(work));
        }
    }

    private static string BuildInstructions(Work? work, Session session)
    {
        if (work is null || string.IsNullOrWhiteSpace(work.Context))
        {
            return session.Instructions;
        }

        return $"{session.Instructions}\n\nWork context:\n{work.Context}";
    }

    private async Task<ToolExecutionResult> ExecuteToolAsync(
        ModelToolCall call,
        CancellationToken cancellationToken)
    {
        if (!_toolsByName.TryGetValue(call.Name, out var tool))
        {
            return ToolExecutionResult.Error($"Unknown tool: {call.Name}");
        }

        try
        {
            using var arguments = JsonDocument.Parse(call.Arguments);
            var output = await tool.ExecuteAsync(arguments.RootElement, cancellationToken);
            return ToolExecutionResult.Success(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            return ToolExecutionResult.Error($"Invalid arguments for tool '{call.Name}': {exception.Message}");
        }
        catch (Exception exception)
        {
            return ToolExecutionResult.Error($"Tool '{call.Name}' failed: {exception.Message}");
        }
    }

    private bool CanRunInParallel(ModelToolCall call) =>
        _toolsByName.TryGetValue(call.Name, out var tool)
        && tool.SupportsParallelExecution;

    private bool IsSubagentTool(string name) =>
        _toolsByName.TryGetValue(name, out var tool)
        && tool is ISubagentTool;

    private string? GetSubagentName(ModelToolCall call)
    {
        if (!_toolsByName.TryGetValue(call.Name, out var tool)
            || tool is not ISubagentTool subagentTool)
        {
            return null;
        }

        try
        {
            using var arguments = JsonDocument.Parse(call.Arguments);
            return subagentTool.GetSubagentName(arguments.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return "unknown";
        }
    }

    private string? GetSubagentPrompt(ModelToolCall call)
    {
        if (!_toolsByName.TryGetValue(call.Name, out var tool)
            || tool is not ISubagentTool subagentTool)
        {
            return null;
        }

        try
        {
            using var arguments = JsonDocument.Parse(call.Arguments);
            return subagentTool.GetSubagentPrompt(arguments.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record PendingToolExecution(
        int Index,
        ModelToolCall Call,
        Task<ToolExecutionResult> Task);

    private sealed record ToolExecutionResult(string Output, bool IsError)
    {
        public static ToolExecutionResult Success(string output) => new(output, false);

        public static ToolExecutionResult Error(string message) => new($"Error: {message}", true);
    }
}
