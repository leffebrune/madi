using Madi.Core;

namespace Madi.Runtime.Tools;

/// <summary>
/// Convenience registry and facade over the built-in coding tools.
/// Relative paths are resolved against <paramref name="workingDirectory"/>.
/// </summary>
public sealed class ToolCatalog
{
    private readonly IReadOnlyDictionary<string, ITool> _tools;

    public ToolCatalog(
        string? workingDirectory = null,
        string? shellPath = null,
        string? utilityBinDirectory = null)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
        Read = new ReadTool(WorkingDirectory);
        Write = new WriteTool(WorkingDirectory);
        Bash = new BashTool(WorkingDirectory, shellPath, utilityBinDirectory);
        Grep = new GrepTool(WorkingDirectory, utilityBinDirectory);
        Find = new FindTool(WorkingDirectory, utilityBinDirectory);
        Edit = new EditTool(WorkingDirectory);
        All = [Read, Write, Bash, Grep, Find, Edit];
        _tools = All.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
    }

    public string WorkingDirectory { get; }
    public ReadTool Read { get; }
    public WriteTool Write { get; }
    public BashTool Bash { get; }
    public GrepTool Grep { get; }
    public FindTool Find { get; }
    public EditTool Edit { get; }
    public IReadOnlyList<ITool> All { get; }

    public ITool Get(string name) =>
        _tools.TryGetValue(name, out var tool)
            ? tool
            : throw new KeyNotFoundException($"Unknown tool: {name}");

    public Task<string> ReadAsync(string path, int? offset = null, int? limit = null,
        CancellationToken cancellationToken = default) =>
        Read.ExecuteAsync(path, offset, limit, cancellationToken);

    public Task<string> WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
        Write.ExecuteAsync(path, content, cancellationToken);

    public Task<string> BashAsync(string command, double? timeout = null,
        CancellationToken cancellationToken = default) =>
        Bash.ExecuteAsync(command, timeout, cancellationToken);

    public Task<string> GrepAsync(string pattern, string? path = null, string? glob = null,
        bool? caseSensitive = null, bool literal = false, bool hidden = false,
        CancellationToken cancellationToken = default) =>
        Grep.ExecuteAsync(pattern, path, glob, caseSensitive, literal, hidden, cancellationToken);

    public Task<string> FindAsync(string pattern, string? path = null, string? type = null,
        string? extension = null, bool? caseSensitive = null, bool literal = false, bool hidden = false,
        CancellationToken cancellationToken = default) =>
        Find.ExecuteAsync(pattern, path, type, extension, caseSensitive, literal, hidden, cancellationToken);

    public Task<string> EditAsync(string path, IReadOnlyList<TextEdit> edits,
        CancellationToken cancellationToken = default) =>
        Edit.ExecuteAsync(path, edits, cancellationToken);

    public Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default) =>
        BashAsync(command, cancellationToken: cancellationToken);
}
