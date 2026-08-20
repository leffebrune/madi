using Madi.App;

namespace Madi.TUI;

public sealed record Suggestion(string CompletionText, string DisplayText);

public static class SuggestionEngine
{
    public static async ValueTask<IReadOnlyList<Suggestion>> GetSuggestionsAsync(
        MadiController controller,
        string input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(input);
        if (!input.StartsWith('/'))
        {
            return [];
        }

        var separatorIndex = input.IndexOfAny([' ', '\t']);
        if (separatorIndex < 0)
        {
            return controller.Commands.FindByPrefix(input)
                .Select(command => new Suggestion(
                    $"{command.Name} ",
                    $"{command.Usage} — {command.Description}"))
                .ToArray();
        }

        var commandName = input[..separatorIndex];
        var argument = input[(separatorIndex + 1)..];
        if (!controller.Commands.TryGet(commandName, out var command)
            || command.GetCompletions is null)
        {
            return [];
        }

        var completions = await command.GetCompletions(
            controller,
            argument,
            cancellationToken);
        return completions
            .Select(completion => new Suggestion(
                $"{command.Name} {completion.Value}",
                completion.IsCurrent
                    ? $"{completion.DisplayText}  [현재]"
                    : completion.DisplayText))
            .ToArray();
    }
}
