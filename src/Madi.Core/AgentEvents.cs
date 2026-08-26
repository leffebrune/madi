namespace Madi.Core;

public abstract record AgentEvent;

public sealed record AgentRequestStarted(string Content) : AgentEvent;

public sealed record AgentRawEventReceived(string Content) : AgentEvent;

public sealed record AgentTextDelta(string Text) : AgentEvent;

public sealed record AgentToolCallCompleted(
    string CallId,
    string Name,
    string Arguments) : AgentEvent;

public sealed record AgentToolExecutionCompleted(
    string CallId,
    string Name,
    string Result,
    bool IsError,
    TimeSpan ToolDuration = default,
    TimeSpan ApiDuration = default) : AgentEvent;

public sealed record AgentSubagentStarted(
    string CallId,
    string Name,
    string Prompt) : AgentEvent;

public sealed record AgentSubagentCompleted(
    string CallId,
    string Name,
    string Response,
    bool IsError,
    TimeSpan ToolDuration = default,
    TimeSpan ApiDuration = default) : AgentEvent;

public sealed record AgentUsageReported(ModelUsage Usage) : AgentEvent;

public sealed record AgentAssistantCompleted(
    string Text,
    TimeSpan ApiDuration) : AgentEvent;

public sealed record AgentResponseCompleted(
    string Text,
    TimeSpan ApiDuration = default) : AgentEvent;
