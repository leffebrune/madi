using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Tests;

public sealed class WorkInstructionComposerTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(" \n", null)]
    [InlineData("  work\r\ncontent  ", "work\r\ncontent")]
    public void MissingOrBlankProjectPreservesExistingBehavior(string? work, string? expected)
    {
        Assert.Equal(expected, WorkInstructionComposer.Compose(null, work));
        Assert.Equal(expected, WorkInstructionComposer.Compose(new("path", " \n"), work));
    }

    [Fact]
    public void ExactCompositionPreservesContentEscapesPathAndDoesNotMutateInputs()
    {
        var project = new ProjectInstructions("C:\root\\\"quoted\"\nAGENTS.md", "  지침\r\n\n ");
        var original = project with { };
        const string work = "  work context  ";
        var section = "# Project instructions\n\nSource: "
            + JsonSerializer.Serialize(project.SourcePath) + "\n\n" + project.Content;
        Assert.Equal(section, WorkInstructionComposer.Compose(project, null));
        Assert.Equal("Apply the project instructions and work instructions below.\n"
            + "If they conflict, the work instructions take precedence.\n\n"
            + section + "\n\n# Work instructions\n\nwork context",
            WorkInstructionComposer.Compose(project, work));
        Assert.Equal(original, project);
        Assert.Equal("  work context  ", work);
        Assert.DoesNotContain('\n', JsonSerializer.Serialize(project.SourcePath));
    }
}
