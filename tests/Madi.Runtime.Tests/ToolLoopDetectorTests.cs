using Madi.Core;

namespace Madi.Runtime.Tests;

public sealed class ToolLoopDetectorTests
{
    private static ToolLoopDecision Observe(ToolLoopDetector detector, string arguments = "{}",
        string output = "done", bool error = false, string name = "test") =>
        detector.ObserveRound([new ModelToolCall("test", Guid.NewGuid().ToString(), name, arguments, "{}")],
            [new ToolResult("ignored", output, error)]);

    [Fact]
    public void WarnsOnceThenStopsOnFifthIdenticalRound()
    {
        var detector = new ToolLoopDetector();
        Assert.Equal(ToolLoopDecision.Continue, Observe(detector));
        Assert.Equal(ToolLoopDecision.Continue, Observe(detector));
        Assert.Equal(ToolLoopDecision.Warn, Observe(detector));
        Assert.Equal(ToolLoopDecision.Continue, Observe(detector));
        Assert.Equal(ToolLoopDecision.Stop, Observe(detector));
    }

    [Fact]
    public void CanonicalizesNestedObjectPropertiesAndFallsBackForInvalidJson()
    {
        var detector = new ToolLoopDetector();
        Observe(detector, "{\"a\":{\"x\":1,\"y\":2},\"b\":[]}");
        Observe(detector, "{\"b\":[],\"a\":{\"y\":2,\"x\":1}}");
        Assert.Equal(ToolLoopDecision.Warn, Observe(detector, "{\"a\":{\"x\":1,\"y\":2},\"b\":[]}"));
        Observe(detector, "invalid");
        Observe(detector, "invalid");
        Assert.Equal(ToolLoopDecision.Warn, Observe(detector, "invalid"));
    }

    [Theory]
    [InlineData("{\"a\":1}", "done", false, "test")]
    [InlineData("{}", "changed", false, "test")]
    [InlineData("{}", "done", true, "test")]
    [InlineData("{}", "done", false, "other")]
    public void ChangedExecutionResetsCounter(string arguments, string output, bool error, string name)
    {
        var detector = new ToolLoopDetector();
        Observe(detector); Observe(detector); Observe(detector);
        Assert.Equal(ToolLoopDecision.Continue, Observe(detector, arguments, output, error, name));
        Assert.Equal(ToolLoopDecision.Continue, Observe(detector));
    }

    [Fact]
    public void ArrayOrderIsSignificant()
    {
        var detector = new ToolLoopDetector();
        Observe(detector, "[1,2]"); Observe(detector, "[1,2]");
        Assert.Equal(ToolLoopDecision.Continue, Observe(detector, "[2,1]"));
    }
}
