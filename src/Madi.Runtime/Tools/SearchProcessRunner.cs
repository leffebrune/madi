using System.Diagnostics;
using System.Text;

namespace Madi.Runtime.Tools;

internal static class SearchProcessRunner
{
    public static async Task<SearchProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start executable: {executable}");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Could not start executable: {executable}", exception);
        }

        var stdout = CaptureHeadAsync(process.StandardOutput, cancellationToken);
        var stderr = CaptureHeadAsync(process.StandardError, cancellationToken);
        using var killRegistration = cancellationToken.Register(() =>
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

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return new SearchProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Preserve the original cancellation.
            }
            throw;
        }
    }

    public static string FormatOutput(SearchProcessResult result)
    {
        var output = result.StandardOutput;
        if (string.IsNullOrEmpty(output.Content))
        {
            return "(no matches)";
        }

        var truncated = ToolSupport.TruncateHead(output.Content.TrimEnd('\r', '\n'));
        if (!output.Truncated && !truncated.Truncated)
        {
            return truncated.Content;
        }

        var totalLines = output.TotalLines;
        var suffix = truncated.FirstLineExceedsLimit
            ? $"[First result exceeds {ToolSupport.MaxBytes} bytes; output omitted.]"
            : $"[Showing first {truncated.OutputLines} of {totalLines} results; output truncated.]";
        return string.IsNullOrEmpty(truncated.Content) ? suffix : $"{truncated.Content}\n\n{suffix}";
    }

    public static string FormatFailure(string tool, SearchProcessResult result)
    {
        var details = string.IsNullOrWhiteSpace(result.StandardError.Content)
            ? result.StandardOutput.Content.Trim()
            : result.StandardError.Content.Trim();
        var status = $"{tool} exited with code {result.ExitCode}";
        return string.IsNullOrEmpty(details) ? status : $"{details}\n\n{status}";
    }

    private static async Task<CapturedOutput> CaptureHeadAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(capacity: ToolSupport.MaxBytes);
        var buffer = new char[4_096];
        var totalLines = 0;
        var sawContent = false;
        var endedWithNewLine = false;
        var truncated = false;

        while (await reader.ReadAsync(buffer, cancellationToken) is var count && count > 0)
        {
            sawContent = true;
            for (var index = 0; index < count; index++)
            {
                endedWithNewLine = buffer[index] == '\n';
                if (endedWithNewLine)
                {
                    totalLines++;
                }
            }

            var remaining = ToolSupport.MaxBytes - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(remaining, count));
            }
            if (count > remaining)
            {
                truncated = true;
            }
        }

        if (sawContent && !endedWithNewLine)
        {
            totalLines++;
        }
        return new CapturedOutput(output.ToString(), totalLines, truncated);
    }
}

internal sealed record SearchProcessResult(
    int ExitCode,
    CapturedOutput StandardOutput,
    CapturedOutput StandardError);

internal sealed record CapturedOutput(string Content, int TotalLines, bool Truncated);
