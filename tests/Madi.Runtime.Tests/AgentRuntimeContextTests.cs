using System.Runtime.CompilerServices;
using Madi.Core;
using Madi.Runtime.Tools;

namespace Madi.Runtime.Tests;

public sealed class AgentRuntimeContextTests
{
    [Fact]
    public async Task ContextRefreshesBetweenTurnsButRemainsStableDuringToolRounds()
    {
        var date = new DateOnly(2026, 8, 15);
        var captures = 0;
        var tools = new ToolCatalog();
        var provider = new RecordingProvider(() => date = date.AddDays(1));
        var instructions = SystemInstructionComposer.Compose("Base instructions", [tools.Read]);
        var agent = new Agent(provider, instructions, [tools.Read], () =>
        {
            captures++;
            return new AgentRuntimeContext(date, "/workspace", "Test OS", "/bin/bash");
        });
        var session = new Session { Id = "session", Model = new ModelOptions("test") };

        await foreach (var _ in agent.RespondAsync(null, session, "first")) { }

        Assert.Equal(1, captures);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(provider.Requests[0].Instructions, provider.Requests[1].Instructions);
        Assert.StartsWith(instructions + "\n\n# Runtime context", provider.Requests[0].Instructions);
        Assert.Contains("Current date: 2026-08-15\n", provider.Requests[0].Instructions);
        Assert.Contains("Working directory: \"/workspace\"", provider.Requests[0].Instructions);
        Assert.Contains("OS: \"Test OS\"", provider.Requests[0].Instructions);
        Assert.Contains("Shell: \"/bin/bash\"", provider.Requests[0].Instructions);

        agent.SystemInstructions = "Updated base";
        await foreach (var _ in agent.RespondAsync(null, session, "second")) { }

        Assert.Equal(2, captures);
        var updated = provider.Requests[2].Instructions;
        Assert.StartsWith("Updated base\n\n# Runtime context", updated);
        Assert.Contains("Current date: 2026-08-16\n", updated);
        Assert.Equal(updated, agent.SystemInstructions);
        Assert.Equal(2, updated.Split("# Runtime context").Length);
    }

    [Fact]
    public void ToolContextUsesLocalDateAndActualShellResolution()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var shell = Path.Combine(directory, "custom-shell");
            File.WriteAllText(shell, "");
            var tools = new ToolCatalog(directory, shell);
            var context = tools.CreateRuntimeContext(new FixedTimeProvider());

            Assert.Equal(new DateOnly(2026, 8, 16), context.CurrentDate);
            Assert.Equal(Path.GetFullPath(directory), context.WorkingDirectory);
            Assert.Equal(tools.Bash.ResolveShell(), context.Shell);
            Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.OSDescription, context.OperatingSystem);

            File.Delete(shell);
            Assert.Contains("Unavailable", tools.CreateRuntimeContext().Shell);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 15, 18, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone(
            "TestLocal", TimeSpan.FromHours(9), "TestLocal", "TestLocal");
    }

    private sealed class RecordingProvider(Action advanceDate) : IModelProvider
    {
        public string Id => "test";
        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            if (Requests.Count == 1)
            {
                advanceDate();
                // Unknown tools produce a tool result without needing filesystem access.
                var call = new ModelToolCall(Id, "call", "unknown", "{}", "{}");
                yield return new ModelResponseCompleted(new ModelResponse("first", "", [call]));
            }
            else
            {
                yield return new ModelResponseCompleted(new ModelResponse("done", "done", []));
            }
        }
    }
}
