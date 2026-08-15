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
    string Output,
    bool IsError) : AgentEvent;

public sealed record AgentResponseCompleted(string Text) : AgentEvent;
