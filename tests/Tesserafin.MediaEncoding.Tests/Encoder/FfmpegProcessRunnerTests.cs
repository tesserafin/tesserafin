using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Encoder;

/// <summary>
/// Exercises <see cref="FfmpegProcessRunner"/> against trivial shell subprocesses instead of
/// ffmpeg itself, so these run without any media tooling installed. The behaviors covered here -
/// concurrent stdout/stderr drain past the OS pipe buffer, timeout, cancellation, exit code, and
/// per-process environment variables - are exactly what a behavior-preserving swap of
/// <see cref="EncoderValidator"/>'s process handling must get right.
/// </summary>
public class FfmpegProcessRunnerTests
{
    private static readonly TimeSpan _ample = TimeSpan.FromSeconds(30);

    private readonly FfmpegProcessRunner _runner = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance);

    [Fact]
    public async Task RunProbeAsync_FloodsBothStreamsPastPipeBuffer_NoDeadlock()
    {
        // ~20000 lines per stream is comfortably past the typical 64KB OS pipe buffer that a
        // single-stream-at-a-time reader (the bug in the old EncoderValidator.GetProcessOutput)
        // would deadlock on.
        var command = ShellCommand(
            unix: "for i in $(seq 1 20000); do echo out-line-$i; echo err-line-$i 1>&2; done",
            windows: "for /L %i in (1,1,20000) do @(echo out-line-%i & echo err-line-%i 1>&2)");

        var result = await _runner.RunProbeAsync(command, _ample, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("out-line-1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("out-line-20000", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("err-line-1", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("err-line-20000", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunProbeAsync_ProcessExceedsTimeout_IsKilledAndReportsTimedOut()
    {
        var command = ShellCommand(
            unix: "sleep 30",
            windows: "ping -n 31 127.0.0.1 >NUL");

        var stopwatch = Stopwatch.StartNew();
        var result = await _runner.RunProbeAsync(command, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        stopwatch.Stop();

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Expected the process to be killed well before its own sleep finished, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RunProbeAsync_ExternalCancellation_KillsProcessAndThrows()
    {
        var command = ShellCommand(
            unix: "sleep 30",
            windows: "ping -n 31 127.0.0.1 >NUL");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runner.RunProbeAsync(command, _ample, cts.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Expected the process to be killed well before its own sleep finished, took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RunProbeAsync_ExitCodeIsCaptured()
    {
        var command = ShellCommand(
            unix: "exit 7",
            windows: "exit /b 7");

        var result = await _runner.RunProbeAsync(command, _ample, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task RunProbeAsync_EnvironmentVariables_AreDeliveredToChildProcessOnly()
    {
        var command = ShellCommand(
            unix: "echo value=$TESSERAFIN_TEST_VAR",
            windows: "echo value=%TESSERAFIN_TEST_VAR%") with
        {
            EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("TESSERAFIN_TEST_VAR", "hello-from-command"),
        };

        var result = await _runner.RunProbeAsync(command, _ample, CancellationToken.None);

        Assert.Contains("value=hello-from-command", result.StandardOutput, StringComparison.Ordinal);
        Assert.Null(Environment.GetEnvironmentVariable("TESSERAFIN_TEST_VAR"));
    }

    [Fact]
    public async Task RunProbeAsync_StandardInput_IsWrittenToChildProcess()
    {
        var command = ShellCommand(
            unix: "read line; echo got:-$line",
            windows: "set /p line= & echo got:-%line%");

        var result = await _runner.RunProbeAsync(command, _ample, CancellationToken.None, standardInput: "hello-stdin\n");

        Assert.Contains("got:-hello-stdin", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateTranscodeProcess_ConfiguresButDoesNotLaunchChild()
    {
        var command = FfmpegCommand.FromArgumentLine("ffmpeg", "-i \"a movie.mkv\" -vf \"scale=320:180\"", workingDirectory: Path.GetTempPath());
        using var process = _runner.CreateTranscodeProcess(command);

        Assert.Throws<InvalidOperationException>(() => process.Id);
        Assert.Equal(command.ArgumentLine, process.StartInfo.Arguments);
        Assert.Equal(command.WorkingDirectory, process.StartInfo.WorkingDirectory);
        Assert.True(process.StartInfo.RedirectStandardError);
        Assert.True(process.StartInfo.RedirectStandardInput);
        Assert.False(process.StartInfo.RedirectStandardOutput);
        Assert.True(process.EnableRaisingEvents);
        Assert.False(process.StartInfo.UseShellExecute);
    }

    [Fact]
    public void StartTranscode_PreCancelled_DoesNotLaunchChild()
    {
        using var process = _runner.CreateTranscodeProcess(ShellCommand("exit 0", "exit /b 0"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _runner.StartTranscode(process, cancellation.Token));
        Assert.Throws<InvalidOperationException>(() => process.Id);
    }

    [Fact]
    public async Task ConcurrentTranscodes_KeepDistinctEnvironments_AndProbeInheritsParent()
    {
        const string variable = "TESSERAFIN_TEST_JOB_DRIVER";
        var parent = Environment.GetEnvironmentVariable(variable);
        var directory = Directory.CreateTempSubdirectory("tesserafin-process-");
        var script = ShellCommand("printf '%s' $TESSERAFIN_TEST_JOB_DRIVER > $TESSERAFIN_TEST_RESULT; read line", "echo %TESSERAFIN_TEST_JOB_DRIVER% > %TESSERAFIN_TEST_RESULT% & set /p line=");
        var firstCommand = script with { EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add(variable, "first").Add("TESSERAFIN_TEST_RESULT", "first"), WorkingDirectory = directory.FullName };
        var secondCommand = script with { EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add(variable, "second").Add("TESSERAFIN_TEST_RESULT", "second"), WorkingDirectory = directory.FullName };
        using var first = _runner.CreateTranscodeProcess(firstCommand);
        using var second = _runner.CreateTranscodeProcess(secondCommand);
        try
        {
            _runner.StartTranscode(first, TestContext.Current.CancellationToken);
            _runner.StartTranscode(second, TestContext.Current.CancellationToken);
            Assert.NotEqual(first.Id, second.Id);
            Assert.False(first.HasExited);
            Assert.False(second.HasExited);
            var probe = await _runner.RunProbeAsync(ShellCommand("echo value=$TESSERAFIN_TEST_JOB_DRIVER", "echo value=%TESSERAFIN_TEST_JOB_DRIVER%"), _ample, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("first", probe.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("second", probe.StandardOutput, StringComparison.Ordinal);

            await first.StandardInput.WriteLineAsync("finish");
            await second.StandardInput.WriteLineAsync("finish");
            first.StandardInput.Close();
            second.StandardInput.Close();
            await Task.WhenAll(first.WaitForExitAsync(TestContext.Current.CancellationToken), second.WaitForExitAsync(TestContext.Current.CancellationToken)).WaitAsync(_ample, TestContext.Current.CancellationToken);
            Assert.Equal("first", (await File.ReadAllTextAsync(Path.Combine(directory.FullName, "first"), TestContext.Current.CancellationToken)).Trim());
            Assert.Equal("second", (await File.ReadAllTextAsync(Path.Combine(directory.FullName, "second"), TestContext.Current.CancellationToken)).Trim());
            Assert.Equal(parent, Environment.GetEnvironmentVariable(variable));
        }
        finally
        {
            if (!first.HasExited)
            {
                first.Kill(entireProcessTree: true);
                await first.WaitForExitAsync(CancellationToken.None);
            }

            if (!second.HasExited)
            {
                second.Kill(entireProcessTree: true);
                await second.WaitForExitAsync(CancellationToken.None);
            }

            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunProbeAsync_FloodBeforeReadingLargeInput_DrainsWhileWriting()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses POSIX shell input consumption");
        var command = ShellCommand("for i in $(seq 1 20000); do echo out-line-$i; echo err-line-$i 1>&2; done; cat >/dev/null; echo input-consumed", string.Empty);
        var result = await _runner.RunProbeAsync(command, _ample, TestContext.Current.CancellationToken, standardInput: new string('x', 200_000));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("input-consumed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("err-line-20000", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunProbeAsync_ChildNeverReadsLargeInput_TimeoutStillKillsIt()
    {
        var command = ShellCommand("sleep 30", "ping -n 31 127.0.0.1 >NUL");
        var stopwatch = Stopwatch.StartNew();
        var result = await _runner.RunProbeAsync(command, TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken, standardInput: new string('x', 200_000));

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Input blocked timeout handling for {stopwatch.Elapsed}");
    }

    /// <summary>
    /// .NET parses <see cref="ProcessStartInfo.Arguments"/> with the same quoting rules on every
    /// OS (double-quotes group, no special meaning for single-quotes) - so the script body must
    /// never contain a literal '"', and the whole script is wrapped in double quotes here purely
    /// to keep it as one argument to the shell's -c/"/c".
    /// </summary>
    private static FfmpegCommand ShellCommand(string unix, string windows)
        => OperatingSystem.IsWindows()
            ? FfmpegCommand.FromArgumentLine("cmd.exe", $"/c \"{windows}\"")
            : FfmpegCommand.FromArgumentLine("/bin/sh", $"-c \"{unix}\"");
}
