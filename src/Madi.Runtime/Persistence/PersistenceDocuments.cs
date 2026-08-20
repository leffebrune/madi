using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Madi.Core;

namespace Madi.Runtime.Persistence;

internal sealed record WorkDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Context { get; init; } = string.Empty;
    public string? WorkingDirectory { get; init; }
    public DateTimeOffset? CreatedAtUtc { get; init; }
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    public static WorkDocument FromDomain(Work work) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        Id = work.Id,
        Title = work.Title,
        Context = work.Context,
        WorkingDirectory = work.WorkingDirectory,
        CreatedAtUtc = work.CreatedAtUtc,
        UpdatedAtUtc = work.UpdatedAtUtc,
    };

    public Work ToDomain(DateTimeOffset fallbackCreatedAtUtc, DateTimeOffset fallbackUpdatedAtUtc)
    {
        return new Work
        {
            Id = Id,
            Title = Title,
            Context = Context,
            WorkingDirectory = NormalizeOptionalPath(WorkingDirectory),
            CreatedAtUtc = CreatedAtUtc ?? fallbackCreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc ?? fallbackUpdatedAtUtc,
        };
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}

internal sealed record ModelDocument(
    string Model,
    ReasoningEffort? ReasoningEffort)
{
    public static ModelDocument FromDomain(ModelOptions model) =>
        new(model.Model, model.ReasoningEffort);

    public ModelOptions ToDomain() => new(Model, ReasoningEffort);
}

internal sealed record SessionDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? WorkId { get; init; }
    public string? WorkingDirectory { get; init; }
    public DateTimeOffset? CreatedAtUtc { get; init; }
    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public ModelDocument? Model { get; init; }
    public int MaxToolRounds { get; init; }
    public string Instructions { get; init; } = string.Empty;

    public static SessionDocument FromDomain(Session session) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        Id = session.Id,
        Title = session.Title,
        WorkId = session.WorkId,
        WorkingDirectory = session.WorkingDirectory,
        CreatedAtUtc = session.CreatedAtUtc,
        UpdatedAtUtc = session.UpdatedAtUtc,
        Model = ModelDocument.FromDomain(session.Model),
        MaxToolRounds = session.MaxToolRounds,
        Instructions = session.Instructions,
    };

    public Session ToDomain(
        DateTimeOffset fallbackCreatedAtUtc,
        DateTimeOffset fallbackUpdatedAtUtc)
    {
        return new Session
        {
            Id = Id,
            Title = Title,
            WorkId = WorkId,
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory)
                ? null
                : Path.GetFullPath(WorkingDirectory),
            CreatedAtUtc = CreatedAtUtc ?? fallbackCreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc ?? fallbackUpdatedAtUtc,
            Model = Model?.ToDomain()
                ?? throw new InvalidDataException($"Session '{Id}' has no model metadata."),
            MaxToolRounds = MaxToolRounds,
            Instructions = Instructions,
        };
    }
}

internal sealed record CurrentDocument(string SessionId)
{
}

internal sealed record ConversationItemDocument
{
    public required int Sequence { get; init; }
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? ProviderId { get; init; }
    public string? CallId { get; init; }
    public string? Name { get; init; }
    public string? Arguments { get; init; }
    public string? Output { get; init; }
    public bool? IsError { get; init; }
    public string? OutputType { get; init; }
    public string? RawJson { get; init; }

    public static ConversationItemDocument FromDomain(int sequence, ModelInputItem item) =>
        item switch
        {
            UserMessage message => new()
            {
                Sequence = sequence,
                Type = "user_message",
                Text = message.Text,
            },
            AssistantMessage message => new()
            {
                Sequence = sequence,
                Type = "assistant_message",
                Text = message.Text,
            },
            ToolResult result => new()
            {
                Sequence = sequence,
                Type = "tool_result",
                CallId = result.CallId,
                Output = result.Output,
                IsError = result.IsError,
            },
            ModelAssistantOutput output => new()
            {
                Sequence = sequence,
                Type = "model_assistant_output",
                ProviderId = output.ProviderId,
                Text = output.Text,
                RawJson = output.RawJson,
            },
            ModelToolCall call => new()
            {
                Sequence = sequence,
                Type = "model_tool_call",
                ProviderId = call.ProviderId,
                CallId = call.CallId,
                Name = call.Name,
                Arguments = call.Arguments,
                RawJson = call.RawJson,
            },
            ModelOpaqueOutput output => new()
            {
                Sequence = sequence,
                Type = "model_opaque_output",
                ProviderId = output.ProviderId,
                OutputType = output.OutputType,
                RawJson = output.RawJson,
            },
            _ => throw new InvalidDataException(
                $"Unsupported conversation item type: {item.GetType().Name}."),
        };

    public ModelInputItem ToDomain() => Type switch
    {
        "user_message" => new UserMessage(Require(Text, nameof(Text))),
        "assistant_message" => new AssistantMessage(Require(Text, nameof(Text))),
        "tool_result" => new ToolResult(
            Require(CallId, nameof(CallId)),
            Require(Output, nameof(Output)),
            IsError ?? Require(Output, nameof(Output)).StartsWith(
                "Error:",
                StringComparison.OrdinalIgnoreCase)),
        "model_assistant_output" => new ModelAssistantOutput(
            Require(ProviderId, nameof(ProviderId)),
            Require(Text, nameof(Text)),
            Require(RawJson, nameof(RawJson))),
        "model_tool_call" => new ModelToolCall(
            Require(ProviderId, nameof(ProviderId)),
            Require(CallId, nameof(CallId)),
            Require(Name, nameof(Name)),
            Require(Arguments, nameof(Arguments)),
            Require(RawJson, nameof(RawJson))),
        "model_opaque_output" => new ModelOpaqueOutput(
            Require(ProviderId, nameof(ProviderId)),
            Require(OutputType, nameof(OutputType)),
            Require(RawJson, nameof(RawJson))),
        _ => throw new InvalidDataException(
            $"Unsupported conversation item discriminator: '{Type}'."),
    };

    private static string Require(string? value, string propertyName) =>
        value ?? throw new InvalidDataException(
            $"Conversation item '{propertyName}' is required for this type.");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(WorkDocument))]
[JsonSerializable(typeof(SessionDocument))]
[JsonSerializable(typeof(CurrentDocument))]
[JsonSerializable(typeof(ConversationItemDocument))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext
{
    public static PersistenceJsonContext Relaxed { get; } = new(
        new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        });
}
