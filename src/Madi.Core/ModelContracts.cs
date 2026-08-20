namespace Madi.Core;

/// <summary>A provider-neutral request containing conversation context and available tools.</summary>
public sealed record ModelRequest(
    IReadOnlyList<ModelInputItem> Input,
    IReadOnlyList<ITool> Tools,
    ModelOptions Options,
    string Instructions,
    string PromptCacheKey,
    bool AllowParallelToolCalls = false);

/// <summary>An item that can be supplied as model input on a later turn.</summary>
public abstract record ModelInputItem;

public sealed record UserMessage(string Text) : ModelInputItem;

public sealed record AssistantMessage(string Text) : ModelInputItem;

public sealed record ToolResult(
    string CallId,
    string Output,
    bool IsError = false) : ModelInputItem;

/// <summary>
/// A completed provider output item. RawJson is intentionally opaque to Core and can be
/// replayed by the provider that created it when conversation state is managed locally.
/// </summary>
public abstract record ModelOutputItem(
    string ProviderId,
    string Type,
    string RawJson) : ModelInputItem;

public sealed record ModelAssistantOutput(
    string ProviderId,
    string Text,
    string RawJson)
    : ModelOutputItem(ProviderId, "message", RawJson);

public sealed record ModelToolCall(
    string ProviderId,
    string CallId,
    string Name,
    string Arguments,
    string RawJson)
    : ModelOutputItem(ProviderId, "function_call", RawJson);

public sealed record ModelOpaqueOutput(
    string ProviderId,
    string OutputType,
    string RawJson)
    : ModelOutputItem(ProviderId, OutputType, RawJson);

public sealed record ModelUsage(
    int? InputTokens,
    int? CachedTokens,
    int? CacheWriteTokens,
    int? OutputTokens,
    int? ReasoningTokens,
    int? TotalTokens);

public sealed record ModelResponse(
    string Id,
    string OutputText,
    IReadOnlyList<ModelOutputItem> Output,
    ModelUsage? Usage = null);

public abstract record ModelEvent;

/// <summary>Diagnostic event emitted before the provider sends a request.</summary>
public sealed record ModelRequestStarted(string Content) : ModelEvent;

/// <summary>Diagnostic event containing the original provider transport event.</summary>
public sealed record ModelRawEventReceived(string Content) : ModelEvent;

public sealed record ModelTextDelta(string Text) : ModelEvent;

public sealed record ModelOutputItemCompleted(ModelOutputItem Item) : ModelEvent;

public sealed record ModelResponseCompleted(ModelResponse Response) : ModelEvent;
