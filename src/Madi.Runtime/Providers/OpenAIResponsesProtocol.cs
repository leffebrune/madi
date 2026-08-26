using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Madi.Core;
using Madi.Networking;

namespace Madi.Runtime.Providers;

/// <summary>Serializes provider-neutral requests and parses OpenAI Responses API streams.</summary>
internal static class OpenAIResponsesProtocol
{
    public static async IAsyncEnumerable<ModelEvent> StreamAsync(
        INetworkClient client,
        string providerId,
        ModelRequest request,
        IReadOnlyDictionary<string, string>? headers = null,
        bool includeCodexExtensions = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(request);

        var content = CreateRequestBody(request, providerId, includeCodexExtensions);
        yield return new ModelRequestStarted(content);

        var outputText = new StringBuilder();
        var output = new List<ModelOutputItem>();
        var responseId = string.Empty;
        ModelUsage? usage = null;
        var completed = false;

        await foreach (var sse in client.RequestAsync(content, headers, cancellationToken))
        {
            yield return new ModelRawEventReceived(sse.ToWireFormat());

            if (sse.Data == "[DONE]")
            {
                continue;
            }

            using var document = JsonDocument.Parse(sse.Data);
            var root = document.RootElement;
            var eventType = sse.EventType ?? GetString(root, "type");

            switch (eventType)
            {
                case "response.created":
                    if (root.TryGetProperty("response", out var createdResponse))
                    {
                        responseId = GetString(createdResponse, "id") ?? responseId;
                    }
                    break;

                case "response.output_text.delta":
                    var delta = GetString(root, "delta") ?? string.Empty;
                    outputText.Append(delta);
                    yield return new ModelTextDelta(delta);
                    break;

                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var itemElement))
                    {
                        var item = ParseOutputItem(itemElement, providerId);
                        output.Add(item);
                        yield return new ModelOutputItemCompleted(item);
                    }
                    break;

                case "response.completed":
                    if (root.TryGetProperty("response", out var completedResponse))
                    {
                        responseId = GetString(completedResponse, "id") ?? responseId;
                        usage = ParseUsage(completedResponse);
                        if (output.Count == 0
                            && completedResponse.TryGetProperty("output", out var responseOutput)
                            && responseOutput.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var responseItem in responseOutput.EnumerateArray())
                            {
                                var item = ParseOutputItem(responseItem, providerId);
                                output.Add(item);
                                yield return new ModelOutputItemCompleted(item);
                            }
                        }
                    }

                    var finalText = outputText.Length > 0
                        ? outputText.ToString()
                        : string.Concat(output.OfType<ModelAssistantOutput>().Select(static item => item.Text));
                    completed = true;
                    yield return new ModelResponseCompleted(
                        new ModelResponse(responseId, finalText, output, usage));
                    break;

                case "response.failed":
                case "response.incomplete":
                case "error":
                    throw new InvalidOperationException($"OpenAI response failed: {sse.Data}");
            }
        }

        if (!completed)
        {
            throw new InvalidOperationException(
                "OpenAI response stream ended before response.completed.");
        }
    }

    private static string CreateRequestBody(
        ModelRequest request,
        string providerId,
        bool includeCodexExtensions)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.Options.Model);
            writer.WriteBoolean("store", false);
            writer.WriteBoolean("stream", true);
            writer.WriteString("instructions", request.Instructions);

            writer.WriteStartArray("input");
            if (!string.IsNullOrWhiteSpace(request.WorkInstructions))
            {
                WriteMessage(
                    writer,
                    "developer",
                    "input_text",
                    $"<work_prompt>\n{request.WorkInstructions.Trim()}\n</work_prompt>");
            }
            foreach (var item in request.Input)
            {
                WriteInputItem(writer, item, providerId);
            }
            writer.WriteEndArray();

            if (includeCodexExtensions)
            {
                writer.WriteStartObject("text");
                writer.WriteString("verbosity", "low");
                writer.WriteEndObject();
                writer.WriteStartArray("include");
                writer.WriteStringValue("reasoning.encrypted_content");
                writer.WriteEndArray();
            }

            if (request.Tools.Count > 0)
            {
                writer.WriteStartArray("tools");
                foreach (var tool in request.Tools)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "function");
                    writer.WriteString("name", tool.Name);
                    writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters");
                    tool.Parameters.WriteTo(writer);
                    writer.WriteNull("strict");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteString("prompt_cache_key", request.PromptCacheKey);
            writer.WriteString("tool_choice", "auto");
            writer.WriteBoolean("parallel_tool_calls", request.AllowParallelToolCalls);
            if (request.Options.ReasoningEffort is { } effort)
            {
                writer.WriteStartObject("reasoning");
                writer.WriteString("effort", MapReasoningEffort(effort));
                if (includeCodexExtensions)
                {
                    writer.WriteString("summary", "auto");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string MapReasoningEffort(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.Minimal => "minimal",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.XHigh => "xhigh",
        _ => throw new ArgumentOutOfRangeException(nameof(effort)),
    };

    private static void WriteInputItem(
        Utf8JsonWriter writer,
        ModelInputItem item,
        string providerId)
    {
        switch (item)
        {
            case UserMessage user:
                WriteMessage(writer, "user", "input_text", user.Text);
                break;
            case AssistantMessage assistant:
                WriteMessage(writer, "assistant", "output_text", assistant.Text);
                break;
            case ToolResult result:
                writer.WriteStartObject();
                writer.WriteString("type", "function_call_output");
                writer.WriteString("call_id", result.CallId);
                writer.WriteString("output", result.Output);
                writer.WriteEndObject();
                break;
            case ModelOutputItem providerItem when providerItem.ProviderId == providerId:
                using (var document = JsonDocument.Parse(providerItem.RawJson))
                {
                    document.RootElement.WriteTo(writer);
                }
                break;
            case ModelOutputItem providerItem:
                throw new InvalidOperationException(
                    $"Cannot replay output from provider '{providerItem.ProviderId}' through '{providerId}'.");
            default:
                throw new NotSupportedException(
                    $"Unsupported model input item: {item.GetType().Name}");
        }
    }

    private static void WriteMessage(
        Utf8JsonWriter writer,
        string role,
        string contentType,
        string text)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteStartArray("content");
        writer.WriteStartObject();
        writer.WriteString("type", contentType);
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static ModelOutputItem ParseOutputItem(JsonElement item, string providerId)
    {
        var type = GetString(item, "type") ?? "unknown";
        var rawJson = item.GetRawText();

        if (type == "function_call")
        {
            return new ModelToolCall(
                providerId,
                GetString(item, "call_id") ?? string.Empty,
                GetString(item, "name") ?? string.Empty,
                GetString(item, "arguments") ?? string.Empty,
                rawJson);
        }

        if (type == "message")
        {
            return new ModelAssistantOutput(providerId, ReadOutputText(item), rawJson);
        }

        return new ModelOpaqueOutput(providerId, type, rawJson);
    }

    private static string ReadOutputText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (GetString(part, "type") == "output_text")
            {
                text.Append(GetString(part, "text"));
            }
        }
        return text.ToString();
    }

    private static ModelUsage? ParseUsage(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var cachedTokens = GetInt32(usage, "input_tokens_details", "cached_tokens");
        var cacheWriteTokens = GetInt32(usage, "input_tokens_details", "cache_write_tokens");
        var reasoningTokens = GetInt32(usage, "output_tokens_details", "reasoning_tokens");

        return new ModelUsage(
            GetInt32(usage, "input_tokens"),
            cachedTokens,
            cacheWriteTokens,
            GetInt32(usage, "output_tokens"),
            reasoningTokens,
            GetInt32(usage, "total_tokens"));
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.TryGetInt32(out var value)
            ? value
            : null;

    private static int? GetInt32(
        JsonElement element,
        string objectPropertyName,
        string propertyName) =>
        element.TryGetProperty(objectPropertyName, out var nested)
        && nested.ValueKind == JsonValueKind.Object
            ? GetInt32(nested, propertyName)
            : null;
}
