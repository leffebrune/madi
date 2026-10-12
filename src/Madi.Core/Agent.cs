using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Madi.Core;

/// <summary>
/// Default agent orchestration. Session state is supplied by the caller, while this type
/// executes model and tool calls. UI, transport, and authentication details remain outside.
/// </summary>
public sealed class Agent : IAgent
{
    // Maximum model requests per user turn, not individual tool executions.
    public const int MaxToolRounds = 200;

    private const string LoopWarning = "Runtime warning: Identical tool calls and results have repeated for three consecutive rounds. "
        + "Do not repeat the same approach. Diagnose the cause, try another approach, or explain why progress is blocked.";

    private readonly IProjectInstructionLoader? _projectInstructionLoader;
    private readonly IModelProvider _provider;
    private readonly IReadOnlyList<ITool> _tools;
    private readonly IReadOnlyDictionary<string, ITool> _toolsByName;
    private string _systemInstructions;
    private readonly Func<AgentRuntimeContext>? _runtimeContextProvider;

    public Agent(
        IModelProvider provider,
        string systemInstructions,
        IReadOnlyList<ITool>? tools = null,
        Func<AgentRuntimeContext>? runtimeContextProvider = null,
        IProjectInstructionLoader? projectInstructionLoader = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ArgumentException.ThrowIfNullOrWhiteSpace(systemInstructions);
        _systemInstructions = systemInstructions;
        _runtimeContextProvider = runtimeContextProvider;
        _projectInstructionLoader = projectInstructionLoader;
        _tools = tools ?? [];
        _toolsByName = _tools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
    }

    public string SystemInstructions
    {
        get
        {
            var instructions = Volatile.Read(ref _systemInstructions);
            return _runtimeContextProvider is null
                ? instructions
                : SystemInstructionComposer.AppendRuntimeContext(instructions, _runtimeContextProvider());
        }
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            Volatile.Write(ref _systemInstructions, value);
        }
    }

    public IAsyncEnumerable<AgentEvent> RespondAsync(
        Work? work, Session session, string prompt, CancellationToken cancellationToken = default) =>
        RespondAsync(work, session, prompt, null, cancellationToken);

    public async IAsyncEnumerable<AgentEvent> RespondAsync(
        Work? work,
        Session session,
        string prompt,
        AgentInputQueue? inputs,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ValidateWorkSessionRelationship(work, session);

        // Keep a single user turn consistent. Runtime updates apply from the next turn,
        // including turns on sessions that are already in progress.
        var systemInstructions = SystemInstructions;
        var model = session.Model;
        var workContext = work?.Context;
        var projectInstructions = _projectInstructionLoader is null
            ? null
            : await _projectInstructionLoader.LoadAsync(cancellationToken);
        var workInstructions = WorkInstructionComposer.Compose(projectInstructions, workContext);
        var pendingConversation = new List<ModelInputItem>(session.Conversation)
        {
            new UserMessage(prompt),
        };

        var loopDetector = new ToolLoopDetector();
        var loopWarningActive = false;
        for (var round = 0; round < MaxToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var additions = inputs?.Drain() ?? [];
            var additionsStart = pendingConversation.Count;
            foreach (var input in additions)
                pendingConversation.Add(new UserMessage(input.Text));
            var announcedInputs = false;
            ModelResponse? response = null;
            var apiStopwatch = Stopwatch.StartNew();

            await foreach (var item in _provider.StreamAsync(
                new ModelRequest(
                    pendingConversation.ToArray(),
                    _tools,
                    model,
                    loopWarningActive ? systemInstructions + "\n\n" + LoopWarning : systemInstructions,
                    session.Id,
                    _tools.Any(static tool => tool.SupportsParallelExecution),
                    workInstructions),
                cancellationToken))
            {
                // The provider has begun processing this immutable request. This is not
                // a server acknowledgement or a promise that the instruction was obeyed.
                if (!announcedInputs)
                {
                    announcedInputs = true;
                    for (var index = 0; index < additions.Count; index++)
                        yield return new AgentInputIncluded(additions[index], additionsStart + index);
                }
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
                    case ModelResponseCompleted completed:
                        response = completed.Response;
                        break;
                }
            }

            apiStopwatch.Stop();
            var apiDuration = apiStopwatch.Elapsed;

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
            if (toolCalls.Length > 0)
            {
                var assistantText = string.IsNullOrWhiteSpace(response.OutputText)
                    ? $"도구 호출 : {string.Join(", ", toolCalls.Select(static call => call.Name))}"
                    : response.OutputText;
                yield return new AgentAssistantCompleted(assistantText, apiDuration);

                foreach (var toolCall in toolCalls.Where(call => !IsSubagentTool(call.Name)))
                {
                    yield return new AgentToolCallCompleted(
                        toolCall.CallId,
                        toolCall.Name,
                        toolCall.Arguments);
                }
            }

            if (toolCalls.Length == 0)
            {
                if (inputs is not null && !inputs.TryClose())
                {
                    yield return new AgentAssistantCompleted(response.OutputText, apiDuration);
                    continue;
                }
                CommitConversation(session, pendingConversation);
                yield return new AgentResponseCompleted(response.OutputText, apiDuration);
                yield break;
            }

            var resultsStart = pendingConversation.Count;
            using var toolCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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

                var updates = Channel.CreateUnbounded<ToolExecutionUpdate>(
                    new UnboundedChannelOptions { SingleReader = true });
                var executions = toolCalls
                    .Select((call, index) => ExecuteAndPublishAsync(
                        index,
                        call,
                        projectInstructions,
                        updates.Writer,
                        toolCancellation.Token))
                    .ToArray();
                var results = new ToolExecutionResult[toolCalls.Length];
                var completedCount = 0;
                try
                {
                while (completedCount < toolCalls.Length)
                {
                    var update = await updates.Reader.ReadAsync(cancellationToken);
                    if (update is ToolExecutionProgress progress)
                    {
                        yield return progress.Event;
                        continue;
                    }

                    var completed = (ToolExecutionFinished)update;
                    completedCount++;
                    results[completed.Index] = completed.Result;
                    var call = toolCalls[completed.Index];
                    if (GetSubagentName(call) is { } subagentName)
                    {
                        yield return new AgentSubagentCompleted(
                            call.CallId,
                            subagentName,
                            completed.Result.Output,
                            completed.Result.IsError,
                            completed.Result.Duration,
                            apiDuration);
                    }
                    else
                    {
                        yield return new AgentToolExecutionCompleted(
                            call.CallId,
                            call.Name,
                            completed.Result.Output,
                            completed.Result.IsError,
                            completed.Result.Duration,
                            apiDuration);
                    }
                }
                }
                finally
                {
                    await toolCancellation.CancelAsync();
                    await Task.WhenAll(executions);
                }

                for (var index = 0; index < toolCalls.Length; index++)
                {
                    pendingConversation.Add(new ToolResult(
                        toolCalls[index].CallId,
                        results[index].Output,
                        results[index].IsError));
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

                    var updates = Channel.CreateUnbounded<ToolExecutionUpdate>(
                        new UnboundedChannelOptions { SingleReader = true });
                    var execution = ExecuteAndPublishAsync(
                        0,
                        toolCall,
                        projectInstructions,
                        updates.Writer,
                        toolCancellation.Token);
                    ToolExecutionResult? result = null;
                    try
                    {
                    while (result is null)
                    {
                        var update = await updates.Reader.ReadAsync(cancellationToken);
                        if (update is ToolExecutionProgress progress)
                        {
                            yield return progress.Event;
                        }
                        else
                        {
                            result = ((ToolExecutionFinished)update).Result;
                        }
                    }
                    }
                    finally
                    {
                        if (!execution.IsCompleted) await toolCancellation.CancelAsync();
                        await execution;
                    }
                    pendingConversation.Add(new ToolResult(
                        toolCall.CallId,
                        result.Output,
                        result.IsError));
                    if (subagentName is not null)
                    {
                        yield return new AgentSubagentCompleted(
                            toolCall.CallId,
                            subagentName,
                            result.Output,
                            result.IsError,
                            result.Duration,
                            apiDuration);
                    }
                    else
                    {
                        yield return new AgentToolExecutionCompleted(
                            toolCall.CallId,
                            toolCall.Name,
                            result.Output,
                            result.IsError,
                            result.Duration,
                            apiDuration);
                    }
                }
            }

            var decision = loopDetector.ObserveRound(
                toolCalls,
                pendingConversation.Skip(resultsStart).Cast<ToolResult>().ToArray());
            loopWarningActive = loopDetector.WarningActive;
            if (decision == ToolLoopDecision.Stop)
            {
                CommitConversation(session, pendingConversation);
                yield return new AgentResponseStopped(
                    AgentStopReason.ToolLoop,
                    "동일한 도구 호출과 결과가 5개 라운드 연속 반복되어 중단했습니다. 완료된 대화와 도구 결과는 보존됩니다.");
                yield break;
            }
        }

        CommitConversation(session, pendingConversation);
        yield return new AgentResponseStopped(
            AgentStopReason.RoundLimit,
            $"사용자 턴당 최대 모델 호출 라운드 {MaxToolRounds}회에 도달하여 중단했습니다. 완료된 대화와 도구 결과는 보존됩니다.");
    }

    private static void CommitConversation(Session session, List<ModelInputItem> conversation)
    {
        session.Conversation.Clear();
        session.Conversation.AddRange(conversation);
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

    private async Task ExecuteAndPublishAsync(
        int index,
        ModelToolCall call,
        ProjectInstructions? projectInstructions,
        ChannelWriter<ToolExecutionUpdate> writer,
        CancellationToken cancellationToken)
    {
        try
        {
        var result = await ExecuteToolAsync(
            call,
            projectInstructions,
            item => writer.WriteAsync(
                new ToolExecutionProgress(item),
                cancellationToken),
            cancellationToken);
        await writer.WriteAsync(
            new ToolExecutionFinished(index, result),
            cancellationToken);
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
            throw;
        }
    }

    private async Task<ToolExecutionResult> ExecuteToolAsync(
        ModelToolCall call,
        ProjectInstructions? projectInstructions,
        Func<AgentEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!_toolsByName.TryGetValue(call.Name, out var tool))
        {
            return ToolExecutionResult.Error($"Unknown tool: {call.Name}", stopwatch.Elapsed);
        }

        try
        {
            using var arguments = JsonDocument.Parse(call.Arguments);
            var output = tool is ISubagentTool subagentTool
                ? await subagentTool.ExecuteAsync(
                    arguments.RootElement,
                    projectInstructions,
                    item => onEvent(MapSubagentEvent(call.CallId, item)),
                    cancellationToken)
                : await tool.ExecuteAsync(arguments.RootElement, cancellationToken);
            return ToolExecutionResult.Success(output, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            return ToolExecutionResult.Error(
                $"Invalid arguments for tool '{call.Name}': {exception.Message}",
                stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            return ToolExecutionResult.Error(
                $"Tool '{call.Name}' failed: {exception.Message}",
                stopwatch.Elapsed);
        }
    }

    private static AgentEvent MapSubagentEvent(
        string subagentCallId,
        SubagentRunEvent item) => item switch
    {
        SubagentToolStarted started => new AgentSubagentToolStarted(
            subagentCallId,
            started.CallId,
            started.Name,
            started.Arguments),
        SubagentToolCompleted completed => new AgentSubagentToolCompleted(
            subagentCallId,
            completed.CallId,
            completed.Name,
            completed.IsError,
            completed.ToolDuration),
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };

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

    private abstract record ToolExecutionUpdate;

    private sealed record ToolExecutionProgress(AgentEvent Event) : ToolExecutionUpdate;

    private sealed record ToolExecutionFinished(
        int Index,
        ToolExecutionResult Result) : ToolExecutionUpdate;

    private sealed record ToolExecutionResult(
        string Output,
        bool IsError,
        TimeSpan Duration)
    {
        public static ToolExecutionResult Success(string output, TimeSpan duration) =>
            new(output, false, duration);

        public static ToolExecutionResult Error(string message, TimeSpan duration) =>
            new($"Error: {message}", true, duration);
    }
}
