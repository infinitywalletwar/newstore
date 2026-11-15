using System.Diagnostics;

namespace NewStore.Media.Processing;

/// <summary>How a run of ffmpeg or ffprobe ended: the exit code, the last lines it wrote to stderr, and all of stdout.</summary>
public sealed record FfmpegResult(int ExitCode, IReadOnlyList<string> LastErrorLines, string StandardOutput);

/// <summary>
/// Runs ffmpeg or ffprobe as a child process, with no shell. Every path it is given is one the worker built from ids, never a name
/// an uploader chose.
/// </summary>
public sealed class FfmpegRunner
{
    /// <summary>The stderr lines kept for <c>last_error</c>: the end is where FFmpeg says why it stopped.</summary>
    public const int KeptErrorLines = 20;

    /// <summary>
    /// Runs <paramref name="executable"/> to the end. Cancelling <paramref name="ct"/> kills the process and its children, waits for
    /// the exit, then throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<FfmpegResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        // ArgumentList escapes each argument itself, and with UseShellExecute off the process is "created directly from the executable
        // file", so no shell reads a semicolon or a quote (ProcessStartInfo.ArgumentList and UseShellExecute on Microsoft Learn).
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"{executable} did not start.");

        // Both pipes are drained from the start: a child that fills one while nobody reads it blocks, and so would we waiting for it
        // (Process.StandardOutput on Microsoft Learn). The reads end when the process closes its pipes, killed or not.
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = LastLinesAsync(process.StandardError);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Kill only signals (it "executes asynchronously"), so wait for the exit before the job's folder is deleted.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, errors);
            throw;
        }

        return new FfmpegResult(process.ExitCode, await errors, await output);
    }

    /// <summary>Reads stderr to the end and keeps the last <see cref="KeptErrorLines"/> lines, so a long encode's progress costs no memory.</summary>
    private static async Task<IReadOnlyList<string>> LastLinesAsync(StreamReader reader)
    {
        var lines = new Queue<string>(KeptErrorLines);
        while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
        {
            if (lines.Count == KeptErrorLines)
            {
                lines.Dequeue();
            }

            lines.Enqueue(line);
        }

        return [.. lines];
    }
}
