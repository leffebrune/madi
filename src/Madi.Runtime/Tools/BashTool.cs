using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Madi.Core;

namespace Madi.Runtime.Tools;

/// <summary>Executes a command with Bash and returns combined stdout/stderr.</summary>
public sealed class BashTool : ITool
{
    private const double MaxTimeoutSeconds = int.MaxValue / 1000d;
    private static readonly JsonElement Schema = ToolSupport.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "Bash command to execute" },
            "timeout": { "type": "number", "description": "Timeout in seconds (optional, no default timeout)" }
          },
          "required": ["command"],
          "additionalProperties": false
        }
        """);
    private readonly string _workingDirectory;
    private readonly string? _shellPath;
    private readonly string? _utilityBinDirectory;

    public BashTool(
        string? workingDirectory = null,
        string? shellPath = null,
        string? utilityBinDirectory = null)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
        _shellPath = shellPath;
        _utilityBinDirectory = string.IsNullOrWhiteSpace(utilityBinDirectory)
            ? null
            : Path.GetFullPath(utilityBinDirectory);
    }

    public string Name => "bash";

    public string Description =>
        "Execute a Bash command in the working directory and return combined stdout and stderr.";

    public JsonElement Parameters => Schema;

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            ToolSupport.GetRequiredString(arguments, "command"),
            ToolSupport.GetOptionalDouble(arguments, "timeout"),
            cancellationToken);

    public async Task<string> ExecuteAsync(
        string command,
        double? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (timeout is <= 0 || timeout is double.NaN or double.PositiveInfinity or double.NegativeInfinity)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be a finite positive number of seconds.");
        }
        if (timeout > MaxTimeoutSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), $"Maximum timeout is {MaxTimeoutSeconds} seconds.");
        }
        if (!Directory.Exists(_workingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Working directory does not exist: {_workingDirectory}{Environment.NewLine}Cannot execute bash commands.");
        }

        var shell = ResolveShell();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(shell, command),
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start bash shell: {shell}");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not start bash shell: {shell}", exception);
        }

        var output = new StringBuilder();
        var outputLock = new object();
        var stdout = CaptureAsync(process.StandardOutput, output, outputLock);
        var stderr = CaptureAsync(process.StandardError, output, outputLock);

        using var timeoutCancellation = timeout is null
            ? null
            : new CancellationTokenSource(TimeSpan.FromSeconds(timeout.Value));
        using var linkedCancellation = timeoutCancellation is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        using var killRegistration = linkedCancellation.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between HasExited and Kill.
            }
        });

        var interrupted = false;
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Process startup/termination errors are reported below with captured output.
            }
        }

        await Task.WhenAll(stdout, stderr);
        string fullOutput;
        lock (outputLock)
        {
            fullOutput = output.ToString();
        }

        var displayed = ToolSupport.TruncateTail(fullOutput, out var truncated, out var totalLines, out var outputLines);
        string? fullOutputPath = null;
        if (truncated)
        {
            fullOutputPath = Path.Combine(Path.GetTempPath(), $"madi-bash-{Guid.NewGuid():N}.log");
            await File.WriteAllTextAsync(fullOutputPath, fullOutput, new UTF8Encoding(false), CancellationToken.None);
            var start = totalLines - outputLines + 1;
            displayed += $"\n\n[Showing lines {start}-{totalLines} of {totalLines}. Full output: {fullOutputPath}]";
        }

        if (interrupted)
        {
            if (timeoutCancellation?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(AppendStatus(displayed, $"Command timed out after {timeout} seconds"));
            }
            throw new OperationCanceledException(AppendStatus(displayed, "Command aborted"), cancellationToken);
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(AppendStatus(displayed, $"Command exited with code {process.ExitCode}"));
        }

        return string.IsNullOrEmpty(displayed) ? "(no output)" : displayed;
    }

    private ProcessStartInfo CreateStartInfo(string shell, string command)
    {
        var info = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(command);

        // Bundled utilities (rg, fd, etc.) take precedence over the host PATH.
        var pathKey = info.Environment.Keys.FirstOrDefault(
            key => key.Equals("PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        var bundledBins = ExecutableResolver.GetBundledBinDirectories(_utilityBinDirectory);
        var currentPath = info.Environment.TryGetValue(pathKey, out var value) ? value : string.Empty;
        info.Environment[pathKey] = string.Join(
            Path.PathSeparator,
            bundledBins.Append(currentPath).Where(static entry => !string.IsNullOrWhiteSpace(entry)));
        return info;
    }

    private string ResolveShell()
    {
        if (_shellPath is not null)
        {
            var explicitPath = Path.GetFullPath(_shellPath, _workingDirectory);
            return File.Exists(explicitPath)
                ? explicitPath
                : throw new FileNotFoundException($"Custom shell path not found: {explicitPath}", explicitPath);
        }

        var bundled = ExecutableResolver.Find(
            string.Join(Path.PathSeparator, ExecutableResolver.GetBundledBinDirectories(_utilityBinDirectory)),
            "bash");
        if (bundled is not null)
        {
            return bundled;
        }

        if (OperatingSystem.IsWindows())
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.Combine(value!, "Git", "bin", "bash.exe"));
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        else if (File.Exists("/bin/bash"))
        {
            return "/bin/bash";
        }

        var onPath = ExecutableResolver.Find(Environment.GetEnvironmentVariable("PATH"), "bash");
        if (onPath is not null)
        {
            return onPath;
        }
        if (!OperatingSystem.IsWindows() && File.Exists("/bin/sh"))
        {
            return "/bin/sh";
        }

        throw new FileNotFoundException("No bash shell found. Install Bash, add it to PATH, or provide shellPath.");
    }

    private static async Task CaptureAsync(StreamReader reader, StringBuilder output, object outputLock)
    {
        var buffer = new char[4_096];
        while (await reader.ReadAsync(buffer) is var count && count > 0)
        {
            lock (outputLock)
            {
                output.Append(buffer, 0, count);
            }
        }
    }

    private static string AppendStatus(string output, string status) =>
        string.IsNullOrEmpty(output) ? status : $"{output}\n\n{status}";
}
