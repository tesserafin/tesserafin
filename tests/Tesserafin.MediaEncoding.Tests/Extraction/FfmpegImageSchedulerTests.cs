using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Extraction;

public sealed class FfmpegImageSchedulerTests
{
    private static readonly TimeSpan _patience = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ConcurrentWork_RespectsPermits(int limit)
    {
        using var context = Context();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var peak = 0;
        var calls = 0;
        var runner = Runner(async (command, _, _, _, token) =>
        {
            var active = Interlocked.Increment(ref running);
            Interlocked.Increment(ref calls);
            lock (release)
            {
                peak = Math.Max(peak, active);
            }

            if (active == limit)
            {
                occupied.TrySetResult();
            }

            await release.Task.WaitAsync(token);
            await File.WriteAllTextAsync(command.EnvironmentVariables["OUTPUT"], "image", token);
            Interlocked.Decrement(ref running);
            return Success();
        });
        using var scheduler = new FfmpegImageScheduler(runner.Object, context.FileSystem.Object, NullLogger.Instance, limit);
        var jobs = Enumerable.Range(0, 5).Select(index =>
        {
            var output = Path.Combine(context.Root, index + ".jpg");
            return scheduler.ExtractImageAsync(Command(output), output, _patience, TestContext.Current.CancellationToken);
        }).ToArray();
        try
        {
            await occupied.Task.WaitAsync(_patience, TestContext.Current.CancellationToken);
            Assert.Equal(limit, Volatile.Read(ref calls));
            Assert.All(jobs, job => Assert.False(job.IsCompleted));
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(jobs).WaitAsync(_patience, TestContext.Current.CancellationToken);
        Assert.Equal(limit, peak);
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task QueuedCancellation_NeverCreatesOutputOrEntersRunner()
    {
        using var context = Context();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = Runner(async (command, _, _, _, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            await File.WriteAllTextAsync(command.EnvironmentVariables["OUTPUT"], "image", token);
            return Success();
        });
        using var scheduler = new FfmpegImageScheduler(runner.Object, context.FileSystem.Object, NullLogger.Instance, 1);
        var firstOutput = Path.Combine(context.Root, "first.jpg");
        var first = scheduler.ExtractImageAsync(Command(firstOutput), firstOutput, _patience, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        try
        {
            await entered.Task.WaitAsync(_patience, TestContext.Current.CancellationToken);
            var queuedOutput = Path.Combine(context.Root, "queued", "second.jpg");
            var queued = scheduler.ExtractImageAsync(Command(queuedOutput), queuedOutput, _patience, cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(Directory.Exists(Path.GetDirectoryName(queuedOutput)));
            Assert.Single(runner.Invocations);
        }
        finally
        {
            release.TrySetResult();
        }

        await first;
    }

    [Fact]
    public async Task Progress_CountsOnlyNewNonemptyJpegs_AndCommandKeepsEnvironmentAndPriority()
    {
        using var context = Context();
        var directory = Path.Combine(context.Root, "images");
        var command = Command(directory);
        var runner = Runner(async (actual, _, progress, priority, token) =>
        {
            Assert.Same(command, actual);
            Assert.Equal(ProcessPriorityClass.Idle, priority);
            Assert.NotNull(progress);
            Assert.False(progress());
            await File.WriteAllTextAsync(Path.Combine(directory, "0001.jpg"), string.Empty, token);
            await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "not an image", token);
            Assert.False(progress());
            await File.WriteAllTextAsync(Path.Combine(directory, "0001.jpg"), "image", token);
            Assert.True(progress());
            Assert.False(progress());
            await File.WriteAllTextAsync(Path.Combine(directory, "0002.JPG"), "image", token);
            Assert.True(progress());
            return Success();
        });
        using var scheduler = new FfmpegImageScheduler(runner.Object, context.FileSystem.Object, NullLogger.Instance, 1);
        Assert.Equal(directory, await scheduler.ExtractIntervalAsync(command, directory, _patience, ProcessPriorityClass.Idle, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 7, false)]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 7, false)]
    [InlineData(true, 0, true)]
    public async Task FailureOrMissingOutput_RemovesAttemptBeforePermitIsReused(bool interval, int exitCode, bool timedOut)
    {
        using var context = Context();
        var output = Path.Combine(context.Root, interval ? "images" : "image.jpg");
        var runner = Runner(async (_, _, progress, _, token) =>
        {
            Assert.Equal(interval, progress is not null);
            if (exitCode != 0 || timedOut)
            {
                await File.WriteAllTextAsync(interval ? Path.Combine(output, "0001.jpg") : output, "partial", token);
            }

            return new FfmpegProcessResult(exitCode, string.Empty, "diagnostic", timedOut);
        });
        using var scheduler = new FfmpegImageScheduler(runner.Object, context.FileSystem.Object, NullLogger.Instance, 1);
        await Assert.ThrowsAsync<FfmpegException>(() => interval
            ? scheduler.ExtractIntervalAsync(Command(output), output, _patience, null, TestContext.Current.CancellationToken)
            : scheduler.ExtractImageAsync(Command(output), output, _patience, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task ConcurrentDisposal_WaitsForRunnerAndCleanup_AndRejectsFurtherWork()
    {
        using var context = Context();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Path.Combine(context.Root, "image.jpg");
        var runner = Runner(async (_, _, _, _, token) =>
        {
            await File.WriteAllTextAsync(output, "partial", token);
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                await stopped.Task;
                throw;
            }

            return Success();
        });
        var scheduler = new FfmpegImageScheduler(runner.Object, context.FileSystem.Object, NullLogger.Instance, 1);
        var job = scheduler.ExtractImageAsync(Command(output), output, _patience, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(_patience, TestContext.Current.CancellationToken);
        var first = Task.Run(scheduler.Dispose, TestContext.Current.CancellationToken);
        try
        {
            await cancelled.Task.WaitAsync(_patience, TestContext.Current.CancellationToken);
            var second = Task.Run(scheduler.Dispose, TestContext.Current.CancellationToken);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.True(File.Exists(output));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => scheduler.ExtractImageAsync(Command(output), output, _patience, CancellationToken.None));
            stopped.SetResult();
            await Task.WhenAll(first, second).WaitAsync(_patience, TestContext.Current.CancellationToken);
            Assert.False(File.Exists(output));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        }
        finally
        {
            stopped.TrySetResult();
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task NativeShutdown_KillsAndReapsActiveChild_CancelsQueuedWork()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses POSIX shell process identity");
        using var context = Context();
        var output = Path.Combine(context.Root, "active.jpg");
        var pidFile = Path.Combine(context.Root, "pid");
        var command = FfmpegCommand.FromArgumentLine("/bin/sh", "-c \"echo $$ > pid; echo partial > active.jpg; sleep 30\"", workingDirectory: context.Root);
        var scheduler = new FfmpegImageScheduler(context.Runner, context.FileSystem.Object, NullLogger.Instance, 1);
        var active = scheduler.ExtractImageAsync(command, output, _patience, TestContext.Current.CancellationToken);
        try
        {
            using var patience = new CancellationTokenSource(_patience);
            while (!File.Exists(output))
            {
                await Task.Delay(10, patience.Token);
            }

            var queuedOutput = Path.Combine(context.Root, "queued", "image.jpg");
            var queued = scheduler.ExtractImageAsync(command, queuedOutput, _patience, TestContext.Current.CancellationToken);
            var pid = int.Parse(await File.ReadAllTextAsync(pidFile, patience.Token), System.Globalization.CultureInfo.InvariantCulture);
            await Task.Run(scheduler.Dispose, patience.Token).WaitAsync(_patience, patience.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
            Assert.False(File.Exists(output));
            Assert.False(Directory.Exists(Path.GetDirectoryName(queuedOutput)));
        }
        finally
        {
            scheduler.Dispose();
        }
    }

    internal static ExtractionTestContext Context()
    {
        var context = new ExtractionTestContext();
        context.FileSystem.Setup(fs => fs.GetFilePaths(It.IsAny<string>(), false)).Returns((string path, bool _) => Directory.GetFiles(path));
        return context;
    }

    private static FfmpegCommand Command(string output)
        => FfmpegCommand.FromArgumentLine("ffmpeg", "captured arguments", ImmutableDictionary<string, string>.Empty.Add("OUTPUT", output));

    private static FfmpegProcessResult Success() => new(0, string.Empty, string.Empty, false);

    private static Mock<IFfmpegProcessRunner> Runner(Func<FfmpegCommand, TimeSpan, Func<bool>?, ProcessPriorityClass, CancellationToken, Task<FfmpegProcessResult>> run)
    {
        var runner = new Mock<IFfmpegProcessRunner>();
        runner.Setup(r => r.RunImageAsync(It.IsAny<FfmpegCommand>(), It.IsAny<TimeSpan>(), It.IsAny<Func<bool>?>(), It.IsAny<ProcessPriorityClass>(), It.IsAny<CancellationToken>())).Returns(run);
        return runner;
    }
}
