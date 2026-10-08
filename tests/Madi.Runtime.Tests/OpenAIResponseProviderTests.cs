using System.Runtime.CompilerServices;
using System.Text.Json;
using Madi.Core;
using Madi.Networking;
using Madi.Runtime.Providers;

namespace Madi.Runtime.Tests;

public sealed class OpenAIResponseProviderTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/responses")]
    [InlineData("https://example.test/openai/v1/", "https://example.test/openai/v1/responses")]
    [InlineData("http://localhost:8080/v1", "http://localhost:8080/v1/responses")]
    public void NetworkConfigurationBuildsResponsesEndpoint(string baseUrl, string expected)
    {
        var options = OpenAIResponseNetworkConfiguration.Create(baseUrl, "sk-test-key");

        Assert.Equal(expected, options.Endpoint.AbsoluteUri);
        Assert.Equal("Bearer sk-test-key", options.DefaultHeaders["Authorization"]);
        Assert.Equal("text/event-stream", options.Accept);
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret")]
    [InlineData("sk-")]
    [InlineData("sk-has space")]
    public void NetworkConfigurationRejectsInvalidApiKeys(string apiKey)
    {
        Assert.Throws<ArgumentException>(() =>
            OpenAIResponseNetworkConfiguration.Create("https://api.openai.com/v1", apiKey));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("http:/v1")]
    [InlineData("ftp://example.test/v1")]
    [InlineData("http://example.test/v1")]
    [InlineData("https://user@example.test/v1")]
    [InlineData("https://example.test/v1?tenant=one")]
    public void NetworkConfigurationRejectsInvalidBaseUrls(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() =>
            OpenAIResponseNetworkConfiguration.Create(baseUrl, "sk-test-key"));
    }

    [Fact]
    public async Task OAuthProviderUsesDirectResponsesSessionAndReasoningFields()
    {
        var client = RecordingNetworkClient.Completing();
        var provider = new OpenAIOAuthProvider(client);
        var request = new ModelRequest(
            [new UserMessage("hello")],
            [],
            new ModelOptions("gpt-test", ReasoningEffort.Medium),
            "instructions",
            "cache-key");

        await CollectAsync(provider.StreamAsync(request));

        Assert.NotNull(client.Headers);
        var sessionId = client.Headers["session_id"];
        Assert.Equal("cache-key", sessionId);
        Assert.False(client.Headers.ContainsKey("session-id"));
        Assert.Equal(sessionId, client.Headers["x-client-request-id"]);
        using var document = JsonDocument.Parse(Assert.IsType<string>(client.Content));
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("text", out _));
        Assert.False(root.TryGetProperty("instructions", out _));
        Assert.Equal("developer", root.GetProperty("input")[0].GetProperty("role").GetString());
        Assert.Equal("instructions", root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("max_output_tokens", out _));
        Assert.False(root.TryGetProperty("prompt_cache_retention", out _));
        Assert.False(root.TryGetProperty("prompt_cache_options", out _));
        Assert.Equal("reasoning.encrypted_content", root.GetProperty("include")[0].GetString());
        Assert.Equal("auto", root.GetProperty("reasoning").GetProperty("summary").GetString());
    }

    [Fact]
    public async Task OAuthProviderClampsCacheAndSessionKeysWithoutSplittingUnicode()
    {
        var client = RecordingNetworkClient.Completing();
        var provider = new OpenAIOAuthProvider(client);
        var key = string.Concat(Enumerable.Repeat("😀", 65));
        await CollectAsync(provider.StreamAsync(CreateRequest() with { PromptCacheKey = key }));
        var expected = string.Concat(Enumerable.Repeat("😀", 64));
        using var document = JsonDocument.Parse(client.Content!);
        Assert.Equal(expected, document.RootElement.GetProperty("prompt_cache_key").GetString());
        Assert.Equal(expected, client.Headers!["session_id"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuthProviderExplainsSubscriptionUsageLimits(bool httpFailure)
    {
        const string code = "subscription_sharing_usage_limit_exceeded";
        var client = new RecordingNetworkClient(
            [new SseEvent("response.failed", $"{{\"response\":{{\"error\":{{\"code\":\"{code}\"}}}}}}")],
            httpFailure ? new HttpRequestException(code, null, System.Net.HttpStatusCode.TooManyRequests) : null);
        var provider = new OpenAIOAuthProvider(client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(provider.StreamAsync(CreateRequest())));
        Assert.Contains(code, error.Message);
        Assert.Contains("https://chatgpt.com/settings/usage", error.Message);
    }

    [Fact]
    public async Task OAuthProviderReplaysOwnReasoningAndToolItemsWithExistingProviderId()
    {
        var client = RecordingNetworkClient.Completing();
        var reasoning = new ModelOpaqueOutput(OpenAIOAuthProvider.ProviderId, "reasoning",
            """{"type":"reasoning","id":"rs_existing","encrypted_content":"encrypted","summary":[]}""");
        var call = new ModelToolCall(OpenAIOAuthProvider.ProviderId, "call_existing", "run", "{}",
            """{"type":"function_call","id":"fc_existing","call_id":"call_existing","name":"run","arguments":"{}"}""");
        await CollectAsync(new OpenAIOAuthProvider(client).StreamAsync(
            CreateRequest([reasoning, call, new ToolResult("call_existing", "done")])));
        using var document = JsonDocument.Parse(client.Content!);
        var input = document.RootElement.GetProperty("input");
        Assert.Equal("encrypted", input[1].GetProperty("encrypted_content").GetString());
        Assert.Equal("call_existing", input[2].GetProperty("call_id").GetString());
        Assert.Equal("function_call_output", input[3].GetProperty("type").GetString());
    }

    [Fact]
    public async Task WritesStandardResponsesRequestAndReplaysOwnOutput()
    {
        var client = RecordingNetworkClient.Completing();
        var provider = new OpenAIResponseProvider(client);
        var previousOutput = new ModelAssistantOutput(
            OpenAIResponseProvider.ProviderId,
            "previous",
            "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"previous\"}]}");
        var request = new ModelRequest(
            [new UserMessage("hello"), new ToolResult("call-1", "result"), previousOutput],
            [new TestTool()],
            new ModelOptions("gpt-test", ReasoningEffort.High),
            "system instructions",
            "cache-key",
            AllowParallelToolCalls: true,
            WorkInstructions: " work context ");

        var events = await CollectAsync(provider.StreamAsync(request));

        Assert.Equal(OpenAIResponseProvider.ProviderId, provider.Id);
        Assert.IsType<ModelResponseCompleted>(events[^1]);
        Assert.Null(client.Headers);
        using var document = JsonDocument.Parse(Assert.IsType<string>(client.Content));
        var root = document.RootElement;
        Assert.Equal("gpt-test", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("system instructions", root.GetProperty("instructions").GetString());
        Assert.Equal("cache-key", root.GetProperty("prompt_cache_key").GetString());
        Assert.Equal("high", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(root.GetProperty("reasoning").TryGetProperty("summary", out _));
        Assert.False(root.TryGetProperty("text", out _));
        Assert.False(root.TryGetProperty("include", out _));
        Assert.True(root.GetProperty("parallel_tool_calls").GetBoolean());

        var input = root.GetProperty("input");
        Assert.Equal(4, input.GetArrayLength());
        Assert.Equal("developer", input[0].GetProperty("role").GetString());
        Assert.Equal("user", input[1].GetProperty("role").GetString());
        Assert.Equal("function_call_output", input[2].GetProperty("type").GetString());
        Assert.Equal("message", input[3].GetProperty("type").GetString());

        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("test_tool", tool.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SendsPromptCacheKeyOnlyWhenEnabled(bool oauth, bool sendPromptCacheKey)
    {
        var client = RecordingNetworkClient.Completing();
        IModelProvider provider = oauth
            ? new OpenAIOAuthProvider(client, sendPromptCacheKey)
            : new OpenAIResponseProvider(client, sendPromptCacheKey);
        var request = CreateRequest();

        await CollectAsync(provider.StreamAsync(request));

        using var document = JsonDocument.Parse(Assert.IsType<string>(client.Content));
        Assert.Equal(sendPromptCacheKey,
            document.RootElement.TryGetProperty("prompt_cache_key", out var key));
        if (sendPromptCacheKey)
        {
            Assert.Equal(request.PromptCacheKey, key.GetString());
        }
        Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task ParsesTextToolCallAndUsageFromStream()
    {
        var client = new RecordingNetworkClient(
        [
            new SseEvent("response.created", "{\"response\":{\"id\":\"resp-1\"}}"),
            new SseEvent("response.output_text.delta", "{\"delta\":\"Hello \"}"),
            new SseEvent("response.output_text.delta", "{\"delta\":\"world\"}"),
            new SseEvent(
                "response.output_item.done",
                "{\"item\":{\"type\":\"function_call\",\"call_id\":\"call-1\",\"name\":\"run\",\"arguments\":\"{}\"}}"),
            new SseEvent(
                "response.completed",
                "{\"response\":{\"id\":\"resp-1\",\"usage\":{\"input_tokens\":10,\"input_tokens_details\":{\"cached_tokens\":3,\"cache_write_tokens\":2},\"output_tokens\":5,\"output_tokens_details\":{\"reasoning_tokens\":1},\"total_tokens\":15}}}"),
        ]);
        var provider = new OpenAIResponseProvider(client);

        var events = await CollectAsync(provider.StreamAsync(CreateRequest()));

        Assert.Equal(["Hello ", "world"], events.OfType<ModelTextDelta>().Select(x => x.Text));
        var toolCall = Assert.IsType<ModelToolCall>(
            Assert.Single(events.OfType<ModelOutputItemCompleted>()).Item);
        Assert.Equal(OpenAIResponseProvider.ProviderId, toolCall.ProviderId);
        Assert.Equal("call-1", toolCall.CallId);
        Assert.Equal("run", toolCall.Name);

        var response = Assert.Single(events.OfType<ModelResponseCompleted>()).Response;
        Assert.Equal("resp-1", response.Id);
        Assert.Equal("Hello world", response.OutputText);
        Assert.Equal(10, response.Usage?.InputTokens);
        Assert.Equal(3, response.Usage?.CachedTokens);
        Assert.Equal(2, response.Usage?.CacheWriteTokens);
        Assert.Equal(5, response.Usage?.OutputTokens);
        Assert.Equal(1, response.Usage?.ReasoningTokens);
        Assert.Equal(15, response.Usage?.TotalTokens);
        Assert.NotEmpty(events.OfType<ModelRawEventReceived>());
    }

    [Fact]
    public async Task UsesCompletedOutputWhenNoDeltaWasReceived()
    {
        var client = new RecordingNetworkClient(
        [
            new SseEvent(
                "response.completed",
                "{\"response\":{\"id\":\"resp-2\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"final text\"}]}]}}"),
        ]);
        var provider = new OpenAIResponseProvider(client);

        var events = await CollectAsync(provider.StreamAsync(CreateRequest()));

        var response = Assert.Single(events.OfType<ModelResponseCompleted>()).Response;
        Assert.Equal("final text", response.OutputText);
        var output = Assert.IsType<ModelAssistantOutput>(Assert.Single(response.Output));
        Assert.Equal(OpenAIResponseProvider.ProviderId, output.ProviderId);
    }

    [Fact]
    public async Task RejectsOutputFromAnotherProvider()
    {
        var provider = new OpenAIResponseProvider(RecordingNetworkClient.Completing());
        var foreignOutput = new ModelOpaqueOutput("other", "reasoning", "{\"type\":\"reasoning\"}");
        var request = CreateRequest([foreignOutput]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CollectAsync(provider.StreamAsync(request)));

        Assert.Contains("other", exception.Message, StringComparison.Ordinal);
        Assert.Contains(OpenAIResponseProvider.ProviderId, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    [InlineData("error")]
    public async Task ThrowsForFailureEvents(string eventType)
    {
        var client = new RecordingNetworkClient(
            [new SseEvent(eventType, $"{{\"type\":\"{eventType}\"}}")]);
        var provider = new OpenAIResponseProvider(client);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CollectAsync(provider.StreamAsync(CreateRequest())));
    }

    [Fact]
    public async Task ThrowsWhenStreamEndsWithoutCompletion()
    {
        var provider = new OpenAIResponseProvider(new RecordingNetworkClient([]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CollectAsync(provider.StreamAsync(CreateRequest())));

        Assert.Contains("response.completed", exception.Message, StringComparison.Ordinal);
    }

    private static ModelRequest CreateRequest(IReadOnlyList<ModelInputItem>? input = null) =>
        new(
            input ?? [new UserMessage("hello")],
            [],
            new ModelOptions("gpt-test"),
            "instructions",
            "cache-key");

    private static async Task<List<ModelEvent>> CollectAsync(IAsyncEnumerable<ModelEvent> source)
    {
        var result = new List<ModelEvent>();
        await foreach (var item in source)
        {
            result.Add(item);
        }
        return result;
    }

    private sealed class RecordingNetworkClient(IReadOnlyList<SseEvent> events, Exception? failure = null) : INetworkClient
    {
        public string? Content { get; private set; }
        public IReadOnlyDictionary<string, string>? Headers { get; private set; }

        public static RecordingNetworkClient Completing() => new(
        [
            new SseEvent(
                "response.completed",
                "{\"response\":{\"id\":\"response-1\",\"output\":[]}}"),
        ]);

        public async IAsyncEnumerable<SseEvent> RequestAsync(
            string content,
            IReadOnlyDictionary<string, string>? headers = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Content = content;
            Headers = headers;
            if (failure is not null) throw failure;
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    private sealed class TestTool : ITool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{}}")
            .RootElement
            .Clone();

        public string Name => "test_tool";
        public string Description => "A test tool";
        public JsonElement Parameters => Schema;

        public Task<string> ExecuteAsync(
            JsonElement arguments,
            CancellationToken cancellationToken = default) => Task.FromResult("done");
    }
}
