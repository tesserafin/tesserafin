using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Probing;

public sealed class MediaProbeRunnerTests
{
    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(15);
    private readonly FfmpegProcessRunner _runner = new(NullLogger<FfmpegProcessRunner>.Instance);

    [Fact]
    public async Task BinaryInput_AndConcurrentOutputFlood_AreDrainedWithoutTruncatingJson()
    {
        Posix();
        var directory = Directory.CreateTempSubdirectory("probe-binary-");
        try
        {
            var payload = new byte[256 * 1024];
            Random.Shared.NextBytes(payload);
            var source = new Input(payload);
            var opens = 0;
            var command = Shell("for i in $(seq 1 20000); do echo out-$i; echo err-$i >&2; done; cat > input.bin; echo complete") with { WorkingDirectory = directory.FullName };
            Stream OpenInput()
            {
                opens++;
                return source;
            }

            var result = await _runner.RunMediaProbeAsync(command, _deadline, OpenInput, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Contains("out-1\n", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("out-20000\n", result.StandardOutput, StringComparison.Ordinal);
            Assert.EndsWith("complete\n", result.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(64 * 1024, result.StandardError.Length);
            Assert.EndsWith("err-20000\n", result.StandardError, StringComparison.Ordinal);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(directory.FullName, "input.bin"), TestContext.Current.CancellationToken));
            Assert.Equal(1, opens);
            Assert.Equal(1, source.Disposals);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task FileProbe_ClosesUnusedStdin()
    {
        Posix();
        var result = await _runner.RunMediaProbeAsync(Shell("cat >/dev/null; echo closed"), _deadline, null, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("closed", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EarlyNativeExit_CancelsBlockedProducerAndDisposesIt()
    {
        Posix();
        var source = new Input(block: true);
        var result = await _runner.RunMediaProbeAsync(Shell("echo complete"), _deadline, () => source, TestContext.Current.CancellationToken).WaitAsync(_deadline, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, source.Disposals);
        Assert.Equal(0, source.ActiveReads);
    }

    [Fact]
    public async Task ProducerFailure_KillsChildIgnoringEof_AndPreservesFailure()
    {
        Posix();
        var fault = new IOException("tuner disconnected");
        var source = new Input(fault: fault);
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<IOException>(() => _runner.RunMediaProbeAsync(Shell("sleep 30"), _deadline, () => source, TestContext.Current.CancellationToken));
        Assert.Same(fault, error.InnerException);
        Assert.Equal(1, source.Disposals);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrDeadline_ReapsChildAndStopsBlockedInput(bool deadline)
    {
        Posix();
        var directory = Directory.CreateTempSubdirectory("probe-stop-");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var source = new Input(block: true);
            var command = Shell("echo $$ > pid; exec sleep 30") with { WorkingDirectory = directory.FullName };
            var task = _runner.RunMediaProbeAsync(command, deadline ? TimeSpan.FromSeconds(1) : _deadline, () => source, cancellation.Token);
            await source.Started.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
            var path = Path.Combine(directory.FullName, "pid");
            while (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
                Assert.False(task.IsCompleted, "Child exited before the test observed its PID");
            }

            var pid = int.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            if (deadline)
            {
                var result = await task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
                Assert.True(result.TimedOut);
                Assert.Null(result.ExitCode);
            }
            else
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(_deadline, TestContext.Current.CancellationToken));
            }

            Assert.Equal(1, source.Disposals);
            Assert.Equal(0, source.ActiveReads);
            Assert.True(HasExited(pid), "Native child must be reaped before the runner returns");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task FactoryFailure_IsPreserved()
    {
        Posix();
        var fault = new InvalidOperationException("reader cannot open");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _runner.RunMediaProbeAsync(Shell("sleep 30"), _deadline, () => throw fault, TestContext.Current.CancellationToken));
        Assert.Same(fault, error);
    }

    [Fact]
    public async Task FailedNativeStart_DoesNotOpenInput()
    {
        var opened = false;
        Stream OpenInput()
        {
            opened = true;
            return Stream.Null;
        }

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => _runner.RunMediaProbeAsync(FfmpegCommand.FromArgumentLine(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), string.Empty), _deadline, OpenInput, TestContext.Current.CancellationToken));
        Assert.False(opened);
    }

    [Fact]
    public async Task PreCancelled_DoesNotOpenInputOrLaunch()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var opened = false;
        Stream OpenInput()
        {
            opened = true;
            return Stream.Null;
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _runner.RunMediaProbeAsync(FfmpegCommand.FromArgumentLine("must-not-launch", string.Empty), _deadline, OpenInput, cancellation.Token));
        Assert.False(opened);
    }

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void Posix() => Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses POSIX shell subprocesses");

    private static FfmpegCommand Shell(string script) => FfmpegCommand.FromArgumentLine("/bin/sh", $"-c \"{script}\"");

    private sealed class Input(byte[]? payload = null, bool block = false, Exception? fault = null) : MemoryStream(payload ?? [])
    {
        public int Disposals { get; private set; }

        public int ActiveReads { get; private set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ActiveReads++;
            Started.TrySetResult();
            try
            {
                if (fault is not null)
                {
                    throw fault;
                }

                if (block)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return await base.ReadAsync(buffer, cancellationToken);
            }
            finally
            {
                ActiveReads--;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposals++;
            }

            base.Dispose(disposing);
        }
    }
}
