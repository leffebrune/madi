using System.Text;
using System.Text.Json;

namespace Madi.Core;

/// <summary>
/// Builds the exact system instruction owned by an agent and sent to the model.
/// Keep prompt inspection and runtime prompt construction on this single code path.
/// </summary>
public static class SystemInstructionComposer
{
    public static string AppendRuntimeContext(string instructions, AgentRuntimeContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        ArgumentNullException.ThrowIfNull(context);

        // Keep the static prefix intact. Serialize environment strings as data on a single line.
        return instructions.TrimEnd() + "\n\n# Runtime context\n"
            + "Current date: " + context.CurrentDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "\n"
            + "Working directory: " + JsonSerializer.Serialize(context.WorkingDirectory) + "\n"
            + "OS: " + JsonSerializer.Serialize(context.OperatingSystem) + "\n"
            + "Shell: " + JsonSerializer.Serialize(context.Shell);
    }

    public static string Compose(string systemPrompt, IReadOnlyList<ITool> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentNullException.ThrowIfNull(tools);

        var prompt = systemPrompt.Trim();
        if (tools.Count == 0)
        {
            return prompt;
        }

        var output = new StringBuilder(prompt);
        output.AppendLine();
        output.AppendLine();
        output.AppendLine("# Available tools");
        output.AppendLine();
        output.AppendLine("Use the following tools when they help complete the user's request.");
        if (tools.Any(static tool => tool.Name == "grep")
            && tools.Any(static tool => tool.Name == "find"))
        {
            output.AppendLine("When searching file contents or locating files, use grep or find instead of bash.");
        }

        foreach (var tool in tools)
        {
            output.AppendLine();
            output.Append("## ").AppendLine(tool.Name);
            output.AppendLine();
            output.AppendLine(tool.Description.Trim());
            output.AppendLine();
            output.AppendLine("Parameters:");
            output.AppendLine("```json");
            output.AppendLine(JsonSerializer.Serialize(
                tool.Parameters,
                new JsonSerializerOptions { WriteIndented = true }));
            output.AppendLine("```");
        }

        return output.ToString().TrimEnd();
    }
}
