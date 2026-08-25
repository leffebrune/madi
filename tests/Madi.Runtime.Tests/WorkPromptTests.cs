using System.Runtime.CompilerServices;
using System.Text.Json;
using Madi.Core;
using Madi.Networking;
using Madi.Runtime.Providers;

namespace Madi.Runtime.Tests;

public sealed class WorkPromptTests
{
    [Fact]
    public async Task AgentKeepsSystemInstructionsSeparateFromWorkInstructions()
    {
        var provider = new RecordingProvider();
        var agent = new Agent(provider, "SYSTEM.md 내용");
        var work = new Work { Id = "work-1", Context = "  장기 목표와 제약  " };
        var session = new Session
        {
            Id = "session-1",
            WorkId = work.Id,
            Model = new ModelOptions("test"),
        };

        await foreach (var _ in agent.RespondAsync(work, session, "현재 요청"))
        {
        }

        var request = Assert.Single(provider.Requests);
        Assert.Equal("SYSTEM.md 내용", request.Instructions);
        Assert.Equal("장기 목표와 제약", request.WorkInstructions);
        Assert.Equal("현재 요청", Assert.IsType<UserMessage>(Assert.Single(request.Input)).Text);
    }

    [Fact]
    public async Task AgentInstructionChangesApplyToAnExistingSession()
    {
        var provider = new RecordingProvider();
        var agent = new Agent(provider, "first instructions");
        var session = new Session
        {
            Id = "session-1",
            Model = new ModelOptions("test"),
        };

        await foreach (var _ in agent.RespondAsync(null, session, "first prompt"))
        {
        }

        agent.SystemInstructions = "updated instructions";
        await foreach (var _ in agent.RespondAsync(null, session, "second prompt"))
        {
        }

        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal("first instructions", provider.Requests[0].Instructions);
        Assert.Equal("updated instructions", provider.Requests[1].Instructions);
    }

    [Fact]
    public async Task OpenAIProviderWritesWorkPromptAsFirstDeveloperInput()
    {
        var provider = new OpenAIOAuthProvider(new CompletingNetworkClient());
        var request = new ModelRequest(
            [new UserMessage("현재 요청")],
            [],
            new ModelOptions("test"),
            "SYSTEM.md 내용",
            "session-1",
            WorkInstructions: "장기 목표와 제약");

        string? body = null;
        await foreach (var item in provider.StreamAsync(request))
        {
            if (item is ModelRequestStarted started)
            {
                body = started.Content;
            }
        }

        using var document = JsonDocument.Parse(Assert.IsType<string>(body));
        var root = document.RootElement;
        Assert.Equal("SYSTEM.md 내용", root.GetProperty("instructions").GetString());
        var input = root.GetProperty("input");
        Assert.Equal(2, input.GetArrayLength());
        Assert.Equal("developer", input[0].GetProperty("role").GetString());
        Assert.Equal(
            "<work_prompt>\n장기 목표와 제약\n</work_prompt>",
            input[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("user", input[1].GetProperty("role").GetString());
    }

    private sealed class RecordingProvider : IModelProvider
    {
        public string Id => "test";
        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ModelResponseCompleted(new ModelResponse("response-1", "완료", []));
        }
    }

    private sealed class CompletingNetworkClient : INetworkClient
    {
        public async IAsyncEnumerable<SseEvent> RequestAsync(
            string content,
            IReadOnlyDictionary<string, string>? headers = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new SseEvent(
                "response.completed",
                "{\"type\":\"response.completed\",\"response\":{\"id\":\"response-1\",\"output\":[]}}");
        }
    }
}
