using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.MediaEncoding;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Extraction;

public sealed class ImageRunnerTests
{
    [Fact]
    public async Task Progress_ExtendsInactivityDeadline_ThenTimesOutWhenItStops()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX sleeping child");
        using var context = new ExtractionTestContext();
        var windows = 0;
        var elapsed = Stopwatch.StartNew();
        var result = await context.Runner.RunImageAsync(
            FfmpegCommand.FromArgumentLine("/bin/sh", "-c \"sleep 30\""),
            TimeSpan.FromMilliseconds(100),
            () => ++windows <= 2,
            ProcessPriorityClass.BelowNormal,
            TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.Equal(3, windows);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(250), "progress windows were not awaited");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ObserverFailure_IsPreservedAfterChildIsStopped()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX sleeping child");
        using var context = new ExtractionTestContext();
        var expected = new InvalidOperationException("cannot inspect image directory");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.RunImageAsync(
            FfmpegCommand.FromArgumentLine("/bin/sh", "-c \"sleep 30\""),
            TimeSpan.FromMilliseconds(100),
            () => throw expected,
            ProcessPriorityClass.BelowNormal,
            TestContext.Current.CancellationToken));
        Assert.Same(expected, error);
    }

    [Fact]
    public async Task HealthyObserver_CannotSuppressExternalCancellation()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a POSIX sleeping child");
        using var context = new ExtractionTestContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Runner.RunImageAsync(
            FfmpegCommand.FromArgumentLine("/bin/sh", "-c \"sleep 30\""),
            TimeSpan.FromMilliseconds(100),
            () => true,
            ProcessPriorityClass.BelowNormal,
            cancellation.Token));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
    }
}
