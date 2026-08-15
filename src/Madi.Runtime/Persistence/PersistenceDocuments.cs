using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Madi.Core;

namespace Madi.Runtime.Persistence;

internal sealed record WorkDocument(
    string Id,
    string Title,
    string Context)
{
    public static WorkDocument FromDomain(Work work) => new(
        work.Id,
        work.Title,
        work.Context);

    public Work ToDomain()
    {
        return new Work
        {
            Id = Id,
            Title = Title,
            Context = Context,
        };
    }
}

internal sealed record ModelDocument(
    string Model,
    ReasoningEffort? ReasoningEffort)
{
    public static ModelDocument FromDomain(ModelOptions model) =>
        new(model.Model, model.ReasoningEffort);

    public ModelOptions ToDomain() => new(Model, ReasoningEffort);
}

internal sealed record SessionDocument(
    int SchemaVersion,
    string Id,
    string Title,
    string? WorkId,
    ModelDocument Model,
    int MaxToolRounds,
    string Instructions)
{
    public const int CurrentSchemaVersion = 1;

    public static SessionDocument FromDomain(Session session) => new(
        CurrentSchemaVersion,
        session.Id,
        session.Title,
        session.WorkId,
        ModelDocument.FromDomain(session.Model),
        session.MaxToolRounds,
        session.Instructions);

    public Session ToDomain()
    {
        return new Session
        {
            Id = Id,
            Title = Title,
            WorkId = WorkId,
            Model = Model.ToDomain(),
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
            Require(Output, nameof(Output))),
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
        });
}
