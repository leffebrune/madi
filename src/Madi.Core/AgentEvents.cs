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
    bool IsError) : AgentEvent;

public sealed record AgentSubagentStarted(
    string CallId,
    string Name,
    string Prompt) : AgentEvent;

public sealed record AgentSubagentCompleted(
    string CallId,
    string Name,
    string Response,
    bool IsError) : AgentEvent;

public sealed record AgentUsageReported(ModelUsage Usage) : AgentEvent;

public sealed record AgentResponseCompleted(string Text) : AgentEvent;
