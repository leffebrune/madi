using System.Text.Json;
using Madi.GUI.Presentation;

namespace Madi.GUI.Tests;

public sealed class ActivityDescriptionFormatterTests
{
    [Fact]
    public void FormatsBuiltInToolsAndPreservesMultipleLines()
    {
        var formatter = new ActivityDescriptionFormatter();

        var bash = formatter.FormatTool("bash", """{"command":"echo first\necho second"}""");
        var read = formatter.FormatTool("read", """{"path":"src/Main.cs","offset":10,"limit":20}""");

        Assert.Equal("echo first\necho second", bash);
        Assert.Equal($"src/Main.cs{Environment.NewLine}offset 10 · limit 20", read);
    }

    [Fact]
    public void AllowsToolFormatterCustomization()
    {
        var formatter = new ActivityDescriptionFormatter();
        formatter.ToolFormatters["custom"] = static arguments =>
            arguments.GetProperty("first").GetString()
            + Environment.NewLine
            + arguments.GetProperty("second").GetString();

        var description = formatter.FormatTool(
            "custom",
            """{"first":"첫 줄","second":"둘째 줄"}""");

        Assert.Equal($"첫 줄{Environment.NewLine}둘째 줄", description);
    }
}
