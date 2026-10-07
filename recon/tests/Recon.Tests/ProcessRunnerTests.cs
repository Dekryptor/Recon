using Recon.Build;
using Recon.Tests.Fixtures;
using Xunit;

namespace Recon.Tests;

/// <summary>
/// Running a tool: the one place a compiler, a linker or an assembler is started from. What is
/// checked here is what a build hangs on when it is wrong — both of the tool's streams have to be
/// drained while it runs, not one after the other.
/// </summary>
public class ProcessRunnerTests
{
    /// <summary>
    /// A tool that fills both pipes is not a hypothetical: a compiler puts its warnings on one stream
    /// and its diagnostics on the other, and a build that writes more than a pipe buffer to the
    /// stream being ignored blocks the child forever while this side waits for the other one to end.
    /// The child here writes far more than any pipe buffer to both.
    /// </summary>
    [Fact]
    public void A_tool_that_fills_both_of_its_streams_is_read_to_the_end_on_both()
    {
        if (!File.Exists("/bin/sh"))
        {
            return;
        }

        using var temp = new TempDir("recon-process");
        var command = new BuildCommand
        {
            Kind = "test",
            Executable = "/bin/sh",
            Arguments =
            [
                "-c",
                // 2000 lines of ~110 bytes each way: about 220 KB per stream, several pipe buffers, so
                // a reader that takes one stream to the end before touching the other is stuck.
                "i=0; while [ $i -lt 2000 ]; do echo \"a line of output that goes to stdout, padded to 100 characters ..................................\"; echo \"a line of output that goes to stderr, padded to 100 characters ..................................\" >&2; i=$((i+1)); done",
            ],
            WorkingDirectory = temp.Path,
        };

        ToolOutcome outcome = ProcessRunner.Run(command);

        Assert.Equal(0, outcome.ExitCode);

        // The tail is what is kept: the last lines of stderr, which is the stream that says what went
        // wrong — stdout is the fallback, and a run whose stderr said something never needs it.
        Assert.Equal(20, outcome.Diagnostics.Count);
        Assert.All(outcome.Diagnostics, line => Assert.Contains("stderr", line));
        Assert.DoesNotContain(outcome.Diagnostics, line => line.Contains("stdout", StringComparison.Ordinal));
    }

    /// <summary>
    /// A tool that cannot be started is a result, not an exception: the plan stays reportable and the
    /// message names the file that was not there.
    /// </summary>
    [Fact]
    public void A_tool_that_cannot_be_started_says_so()
    {
        using var temp = new TempDir("recon-process-missing");
        ToolOutcome outcome = ProcessRunner.Run(new BuildCommand
        {
            Kind = "test",
            Executable = Path.Combine(temp.Path, "no-such-tool"),
            WorkingDirectory = temp.Path,
        });

        Assert.NotEqual(0, outcome.ExitCode);
        Assert.Contains("no-such-tool", string.Join(" ", outcome.Diagnostics));
    }
}
