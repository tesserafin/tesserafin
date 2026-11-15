using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Tesserafin.Common;
using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Extraction;

public sealed class FfmpegFileExecutorTests
{
    [Fact]
    public async Task FileRunner_FloodsBothStreams_RetainsOnlyDiagnosticTail()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX shell child");
        using var context = new ExtractionTestContext();
        var command = Shell("printf begin; printf begin >&2; for i in $(seq 1 20000); do echo out-line-$i; echo err-line-$i >&2; done; printf out-end; printf err-end >&2");
        var result = await context.Runner.RunFileAsync(command, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(65_536, result.StandardOutput.Length);
        Assert.Equal(65_536, result.StandardError.Length);
        Assert.EndsWith("out-end", result.StandardOutput, StringComparison.Ordinal);
        Assert.EndsWith("err-end", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("begin", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileRunner_ClosesInputWithoutWaitingForServerConsole()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX shell child");
        using var context = new ExtractionTestContext();
        var result = await context.Runner.RunFileAsync(Shell("if read line; then exit 7; else echo stdin-closed; fi"), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("stdin-closed", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, false, false)]
    [InlineData(1, true, true)]
    [InlineData(7, true, false)]
    public async Task AcceptedExit_StillRequiresEveryNonemptyOutput(int code, bool acceptOne, bool succeeds)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX shell child");
        using var context = new ExtractionTestContext();
        var output = Path.Combine(context.Root, "file");
        var command = Shell($"echo extracted > $EXTRACTION_OUTPUT; exit {code}") with { EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("EXTRACTION_OUTPUT", output) };
        var error = await Record.ExceptionAsync(() => context.Executor.ExecuteAsync(command, [output], TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken, acceptOne ? [0, 1] : [0]));

        if (succeeds)
        {
            Assert.Null(error);
            Assert.Equal("extracted", (await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken)).Trim());
        }
        else
        {
            Assert.IsType<FfmpegException>(error);
            Assert.False(File.Exists(output));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrEmptyOutput_InvalidatesTheWholeAttemptOnly(bool createEmpty)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX shell child");
        using var context = new ExtractionTestContext();
        var first = Path.Combine(context.Root, "first");
        var second = Path.Combine(context.Root, "second");
        var existing = Path.Combine(context.Root, "unrelated");
        await File.WriteAllTextAsync(existing, "already cached", TestContext.Current.CancellationToken);
        var command = Shell("echo partial > first; " + (createEmpty ? ": > second" : ":")) with { WorkingDirectory = context.Root };

        await Assert.ThrowsAsync<FfmpegException>(() => context.Executor.ExecuteAsync(command, [first, second], TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Equal("already cached", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTimeout_WaitForChildBeforeRemovingItsOutput(bool timeout)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX shell child");
        using var context = new ExtractionTestContext();
        using var cancellation = new CancellationTokenSource();
        var output = Path.Combine(context.Root, "partial");
        var pidFile = Path.Combine(context.Root, "pid");
        var command = Shell("echo $$ > pid; echo partial > partial; sleep 30") with { WorkingDirectory = context.Root };
        var checkedTermination = false;
        context.FileSystem.Setup(fs => fs.DeleteFile(output)).Callback(() =>
        {
            Assert.True(File.Exists(pidFile));
            var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
            checkedTermination = true;
            File.Delete(output);
        });
        var run = context.Executor.ExecuteAsync(command, [output], timeout ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan, cancellation.Token);
        if (!timeout)
        {
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(output))
            {
                await Task.Delay(10, patience.Token);
            }

            await cancellation.CancelAsync();
        }

        var error = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        if (timeout)
        {
            Assert.IsType<FfmpegException>(error);
            Assert.Contains("timed out", error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }

        Assert.True(checkedTermination);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CleanupFailure_PreservesOriginalLaunchException()
    {
        using var context = new ExtractionTestContext();
        var output = Path.Combine(context.Root, "partial");
        context.FileSystem.Setup(fs => fs.DeleteFile(output)).Throws(new IOException("cleanup failed"));
        var command = FfmpegCommand.FromArgumentLine(Path.Combine(context.Root, "missing-executable"), string.Empty);

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => context.Executor.ExecuteAsync(command, [output], TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        context.FileSystem.Verify(fs => fs.DeleteFile(output), Times.Once);
    }

    private static FfmpegCommand Shell(string script) => FfmpegCommand.FromArgumentLine("/bin/sh", $"-c \"{script}\"");
}
