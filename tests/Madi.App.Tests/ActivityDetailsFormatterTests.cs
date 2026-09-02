using Madi.App;

namespace Madi.App.Tests;

public sealed class ActivityDetailsFormatterTests
{
    [Fact]
    public void TruncateResultKeepsAtMostTheConfiguredNumberOfLines()
    {
        var result = string.Join('\n', Enumerable.Range(1, 12).Select(static line => $"line {line}"));

        var truncated = ActivityDetailsFormatter.TruncateResult(result);

        Assert.Contains("line 10", truncated, StringComparison.Ordinal);
        Assert.DoesNotContain("line 11", truncated, StringComparison.Ordinal);
        Assert.EndsWith("… (이후 내용 생략)", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateResultLeavesShortResultsUnchanged()
    {
        var result = string.Join("\r\n", Enumerable.Range(1, 10).Select(static line => $"line {line}"));

        Assert.Equal(result, ActivityDetailsFormatter.TruncateResult(result));
    }

    [Fact]
    public void FormatToolTruncatesOnlyTheResult()
    {
        var arguments = string.Join('\n', Enumerable.Range(1, 12).Select(static line => $"argument {line}"));
        var result = string.Join('\n', Enumerable.Range(1, 12).Select(static line => $"result {line}"));

        var details = ActivityDetailsFormatter.FormatTool(arguments, result);

        Assert.Contains("argument 12", details, StringComparison.Ordinal);
        Assert.Contains("result 10", details, StringComparison.Ordinal);
        Assert.DoesNotContain("result 11", details, StringComparison.Ordinal);
    }
}
