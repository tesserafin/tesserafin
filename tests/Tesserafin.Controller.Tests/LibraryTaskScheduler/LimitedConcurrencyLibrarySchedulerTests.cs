using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.LibraryTaskScheduler;
using Tesserafin.Model.Configuration;
using Xunit;

namespace Tesserafin.Controller.Tests.LibraryTaskScheduler;

public sealed class LimitedConcurrencyLibrarySchedulerTests
{
    /// <summary>
    /// Parallel work schedules an idle cleanup sixty seconds out. Disposal must cancel that wait rather than
    /// sit it out: the host disposes the scheduler on every shutdown, so an uncancelled wait held each stop
    /// that followed library work for up to a minute.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 30_000)]
    public async Task DisposeAsync_AfterParallelWork_DoesNotWaitOutTheIdleCleanup()
    {
        var scheduler = CreateScheduler();
        var processed = 0;

        await scheduler.Enqueue(
            [1, 2, 3],
            (_, _) =>
            {
                Interlocked.Increment(ref processed);
                return Task.CompletedTask;
            },
            new Progress<double>(),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, processed);

        var stopwatch = Stopwatch.StartNew();
        await scheduler.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"DisposeAsync took {stopwatch.Elapsed}.");
    }

    /// <summary>
    /// Disposal is idempotent, and work offered after it is not run.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact(Timeout = 30_000)]
    public async Task DisposeAsync_IsIdempotent_AndLaterWorkIsNotRun()
    {
        var scheduler = CreateScheduler();
        await scheduler.DisposeAsync();
        await scheduler.DisposeAsync();

        var processed = false;
        await scheduler.Enqueue(
            [1],
            (_, _) =>
            {
                processed = true;
                return Task.CompletedTask;
            },
            new Progress<double>(),
            TestContext.Current.CancellationToken);

        Assert.False(processed);
    }

    private static LimitedConcurrencyLibraryScheduler CreateScheduler()
    {
        // A fan-out above one takes the parallel path, the only one that schedules the idle cleanup.
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration { LibraryScanFanoutConcurrency = 2 });
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        return new LimitedConcurrencyLibraryScheduler(lifetime.Object, NullLogger<LimitedConcurrencyLibraryScheduler>.Instance, configuration.Object);
    }
}
