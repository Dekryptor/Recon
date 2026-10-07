using System.Diagnostics;

namespace Recon.Build;

/// <summary>What a tool said when it ran: its exit code, how long it took, and the tail of its output.</summary>
public sealed record ToolOutcome(int ExitCode, long DurationMs, List<string> Diagnostics);

/// <summary>
/// Runs one tool. The build runner uses it for compilers and linkers, and so does <c>recon link</c>
/// when it assembles and links a delinked image: one way to start a tool, one way to report what it
/// said, and Wine handled in one place rather than in each caller.
/// </summary>
public static class ProcessRunner
{
    /// <summary>How many trailing lines of output are kept.</summary>
    public const int DiagnosticLines = 20;

    public static ToolOutcome Run(BuildCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Wine ? "wine" : command.Executable,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (command.Wine)
        {
            startInfo.ArgumentList.Add(command.Executable);
        }

        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in command.Environment)
        {
            startInfo.Environment[key] = value;
        }

        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<string>();
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ToolOutcome(-1, 0, [$"could not start {command.Executable}"]);
            }

            // Both streams are drained at once. Reading one to the end before starting on the other
            // deadlocks as soon as a tool fills the pipe that is not being read: the child blocks
            // writing, this side blocks reading, and neither ever moves again. One compile that puts
            // enough on stdout and anything at all on stderr is enough to hang a build, and on
            // Windows the pipe buffers are smaller, so it happens sooner.
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();
            string standardError = standardErrorTask.GetAwaiter().GetResult();
            string standardOutput = standardOutputTask.GetAwaiter().GetResult();
            stopwatch.Stop();
            diagnostics.AddRange(Tail(standardError));
            if (diagnostics.Count == 0)
            {
                diagnostics.AddRange(Tail(standardOutput));
            }

            if (process.ExitCode != 0 && diagnostics.Count == 0)
            {
                diagnostics.Add($"{Path.GetFileName(command.Executable)} exited with code {process.ExitCode}");
            }

            return new ToolOutcome(process.ExitCode, stopwatch.ElapsedMilliseconds, diagnostics);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            stopwatch.Stop();
            return new ToolOutcome(-1, stopwatch.ElapsedMilliseconds, [$"cannot run {command.Executable}: {ex.Message}"]);
        }
    }

    /// <summary>The tail of a tool's output, which is the part that says what went wrong.</summary>
    public static List<string> Tail(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        return lines.Count <= DiagnosticLines ? lines : [.. lines[^DiagnosticLines..]];
    }
}
