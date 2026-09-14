using System.Text.Json;

namespace Madi.Core;

public static class WorkInstructionComposer
{
    private const string Policy = "Apply the project instructions and work instructions below.\n"
        + "If they conflict, the work instructions take precedence.\n\n";

    public static string? Compose(ProjectInstructions? projectInstructions, string? workContext)
    {
        var workInstructions = string.IsNullOrWhiteSpace(workContext) ? null : workContext.Trim();
        if (string.IsNullOrWhiteSpace(projectInstructions?.Content))
        {
            return workInstructions;
        }

        var projectSection = "# Project instructions\n\nSource: "
            + JsonSerializer.Serialize(projectInstructions.SourcePath)
            + "\n\n" + projectInstructions.Content;
        return workInstructions is null
            ? projectSection
            : Policy + projectSection + "\n\n# Work instructions\n\n" + workInstructions;
    }
}
