namespace Madi.App;

public sealed record CommandCompletion(
    string Value,
    string DisplayText,
    bool IsCurrent = false);

public delegate IAsyncEnumerable<AppEvent> AppCommandHandler(
    MadiController controller,
    string argument,
    CancellationToken cancellationToken);

public delegate ValueTask<IReadOnlyList<CommandCompletion>> CommandCompletionProvider(
    MadiController controller,
    string argument,
    CancellationToken cancellationToken);

public sealed record AppCommand(
    string Name,
    string Usage,
    string Description,
    AppCommandHandler Handler,
    CommandCompletionProvider? GetCompletions = null);

public sealed class CommandCatalog
{
    private readonly IReadOnlyList<AppCommand> _commands;
    private readonly IReadOnlyDictionary<string, AppCommand> _commandsByName;

    public CommandCatalog(IEnumerable<AppCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands.ToArray();
        _commandsByName = _commands.ToDictionary(
            static command => command.Name,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AppCommand> All => _commands;

    public bool TryGet(string name, out AppCommand command) =>
        _commandsByName.TryGetValue(name, out command!);

    public IReadOnlyList<AppCommand> FindByPrefix(string prefix) =>
        _commands
            .Where(command => command.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
}
