using System.Text.Json;
using Madi.Core;

namespace Madi.App;

public sealed record SessionNavigationSnapshot(
    string Id,
    string Title,
    string? WorkId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkNavigationSnapshot(
    string Id,
    string Title,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<SessionNavigationSnapshot> Sessions);

public sealed record NavigationSnapshot(
    string WorkingDirectory,
    IReadOnlyList<WorkNavigationSnapshot> Works,
    IReadOnlyList<SessionNavigationSnapshot> StandaloneSessions,
    int LegacyItemCount);

public enum ConversationEntryKind
{
    User,
    Assistant,
    Tool,
    Subagent,
}

public sealed record ConversationEntrySnapshot(
    ConversationEntryKind Kind,
    string Summary,
    string? Details = null,
    string? ActivityId = null,
    bool IsError = false,
    string? ActivityInput = null,
    string? ActivityName = null);

public sealed record ConversationSnapshot(
    SessionSnapshot Session,
    IReadOnlyList<ConversationEntrySnapshot> Entries);

public static class ConversationSnapshotFactory
{
    public static ConversationSnapshot Create(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var results = session.Conversation
            .OfType<ToolResult>()
            .GroupBy(static result => result.CallId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Last(),
                StringComparer.Ordinal);
        var entries = new List<ConversationEntrySnapshot>();

        foreach (var item in session.Conversation)
        {
            switch (item)
            {
                case UserMessage user:
                    entries.Add(new ConversationEntrySnapshot(
                        ConversationEntryKind.User,
                        user.Text));
                    break;
                case AssistantMessage assistant:
                    entries.Add(new ConversationEntrySnapshot(
                        ConversationEntryKind.Assistant,
                        assistant.Text));
                    break;
                case ModelAssistantOutput assistant:
                    entries.Add(new ConversationEntrySnapshot(
                        ConversationEntryKind.Assistant,
                        assistant.Text));
                    break;
                case ModelToolCall call:
                    results.TryGetValue(call.CallId, out var result);
                    entries.Add(CreateActivity(call, result));
                    break;
            }
        }

        return new ConversationSnapshot(
            new SessionSnapshot(session.Id, session.Title, session.WorkId),
            entries);
    }

    private static ConversationEntrySnapshot CreateActivity(
        ModelToolCall call,
        ToolResult? result)
    {
        if (string.Equals(call.Name, "subagent", StringComparison.Ordinal))
        {
            var (name, task) = ParseSubagent(call.Arguments);
            return new ConversationEntrySnapshot(
                ConversationEntryKind.Subagent,
                $"서브에이전트 {name}",
                $"### 프롬프트\n{task}\n\n### 결과\n{result?.Output ?? "결과 없음"}",
                call.CallId,
                result?.IsError ?? false,
                task,
                name);
        }

        return new ConversationEntrySnapshot(
            ConversationEntryKind.Tool,
            call.Name,
            $"### 인자\n```json\n{call.Arguments}\n```\n\n### 결과\n```text\n{result?.Output ?? "결과 없음"}\n```",
            call.CallId,
            result?.IsError ?? false,
            call.Arguments,
            call.Name);
    }

    private static (string Name, string Task) ParseSubagent(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            var name = root.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? "unknown"
                : "unknown";
            var task = root.TryGetProperty("task", out var taskElement)
                ? taskElement.GetString() ?? string.Empty
                : string.Empty;
            return (name, task);
        }
        catch (JsonException)
        {
            return ("unknown", arguments);
        }
    }
}
