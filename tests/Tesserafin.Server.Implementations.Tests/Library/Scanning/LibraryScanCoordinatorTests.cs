using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Tasks;
using Tesserafin.Server.Core.Library;
using Tesserafin.Server.Core.ScheduledTasks.Tasks;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.Scanning;

public sealed class LibraryScanCoordinatorTests
{
    private static readonly TimeSpan _deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FullScan_OrdersTraversalPostTasksAndCommit_AndReportsInlineProgress()
    {
        using var context = new Context();
        var order = new List<string>();
        var progress = new List<double>();
        context.Traversal.Setup(t => t.RefreshTopFoldersAsync(false, It.IsAny<CancellationToken>())).Callback(() => order.Add("roots")).Returns(Task.CompletedTask);
        context.Traversal.Setup(t => t.RefreshAllAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns((IProgress<double> p, CancellationToken _) =>
        {
            Assert.True(context.Coordinator.IsScanRunning);
            order.Add("all");
            p.Report(50);
            return Task.CompletedTask;
        });
        var post = new Mock<ILibraryPostScanTask>();
        post.Setup(t => t.Run(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns((IProgress<double> p, CancellationToken _) =>
        {
            order.Add("post");
            p.Report(50);
            return Task.CompletedTask;
        });
        context.Traversal.Setup(t => t.UpdateInheritedValues()).Callback(() => order.Add("commit"));
        context.Coordinator.SetPostScanTasks([post.Object]);
        await context.Coordinator.ScanAsync(new InlineProgress(progress.Add), TestContext.Current.CancellationToken);
        Assert.Equal(["roots", "all", "post", "commit"], order);
        Assert.Equal([0, 48, 96, 98, 100, 100], progress);
        Assert.False(context.Coordinator.IsScanRunning);
        context.Monitor.Verify(m => m.Stop(), Times.Once);
        context.Monitor.Verify(m => m.Start(), Times.Once);
        context.Traversal.Verify(t => t.ClearIgnoreRuleCache(), Times.Exactly(2));
    }

    [Fact]
    public async Task FailingPostTask_DoesNotSkipFollowingTasksOrCommit()
    {
        using var context = new Context();
        var failed = new Mock<ILibraryPostScanTask>();
        failed.Setup(t => t.Run(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("provider failed"));
        var next = new Mock<ILibraryPostScanTask>();
        next.Setup(t => t.Run(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        context.Coordinator.SetPostScanTasks([failed.Object, next.Object]);
        await context.Scan();
        next.Verify(t => t.Run(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once);
        context.Traversal.Verify(t => t.UpdateInheritedValues(), Times.Once);
    }

    [Fact]
    public async Task CancelledPostTask_SkipsFollowingTasksAndCommit_ButRestoresMonitor()
    {
        using var context = new Context();
        var cancelled = new Mock<ILibraryPostScanTask>();
        cancelled.Setup(t => t.Run(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        var next = new Mock<ILibraryPostScanTask>(MockBehavior.Strict);
        context.Coordinator.SetPostScanTasks([cancelled.Object, next.Object]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(context.Scan);
        Assert.Empty(next.Invocations);
        context.Traversal.Verify(t => t.UpdateInheritedValues(), Times.Never);
        context.Monitor.Verify(m => m.Start(), Times.Once);
        Assert.False(context.Coordinator.IsScanRunning);
    }

    [Theory]
    [InlineData("initial-clear")]
    [InlineData("monitor-stop")]
    [InlineData("traversal")]
    [InlineData("final-clear")]
    [InlineData("monitor-start")]
    public async Task FailureAtAnyStage_ResetsStatusAndReleasesAdmission(string stage)
    {
        using var context = new Context();
        var failure = new InvalidOperationException(stage);
        var clears = 0;
        context.Traversal.Setup(t => t.ClearIgnoreRuleCache()).Callback(() =>
        {
            clears++;
            if ((stage == "initial-clear" && clears == 1) || (stage == "final-clear" && clears == 2))
            {
                throw failure;
            }
        });
        if (stage == "monitor-stop")
        {
            context.Monitor.Setup(m => m.Stop()).Throws(failure);
        }
        else if (stage == "monitor-start")
        {
            context.Monitor.Setup(m => m.Start()).Throws(failure);
        }
        else if (stage == "traversal")
        {
            context.Traversal.Setup(t => t.RefreshTopFoldersAsync(false, It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        }

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(context.Scan));
        Assert.False(context.Coordinator.IsScanRunning);
        context.Monitor.Setup(m => m.Stop());
        context.Monitor.Setup(m => m.Start());
        context.Traversal.Setup(t => t.RefreshTopFoldersAsync(false, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await context.Scan().WaitAsync(_deadline, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TraversalFailure_WinsOverBothCleanupFailures()
    {
        using var context = new Context();
        var primary = new InvalidOperationException("traversal failed");
        context.Traversal.Setup(t => t.RefreshTopFoldersAsync(false, It.IsAny<CancellationToken>())).ThrowsAsync(primary);
        var clears = 0;
        context.Traversal.Setup(t => t.ClearIgnoreRuleCache()).Callback(() =>
        {
            if (++clears == 2)
            {
                throw new InvalidOperationException("cleanup failed");
            }
        });
        context.Monitor.Setup(m => m.Start()).Throws(new InvalidOperationException("restart failed"));
        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(context.Scan));
        context.Monitor.Verify(m => m.Start(), Times.Once);
        Assert.False(context.Coordinator.IsScanRunning);
    }

    [Fact]
    public async Task ConcurrentFullAndRootRefresh_Serialize_AndQueuedCancellationDoesNotTouchMonitor()
    {
        using var context = new Context();
        var entered = Signal();
        var release = Signal();
        context.Traversal.Setup(t => t.RefreshAllAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        try
        {
            var full = context.Scan();
            await entered.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
            using var cancelled = new CancellationTokenSource();
            var queuedFull = context.Coordinator.ScanAsync(new InlineProgress(_ => { }), cancelled.Token);
            var roots = context.Coordinator.RefreshTopFoldersAsync(TestContext.Current.CancellationToken, true);
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedFull);
            Assert.False(roots.IsCompleted);
            Assert.True(context.Coordinator.IsScanRunning);
            context.Monitor.Verify(m => m.Stop(), Times.Once);
            release.SetResult();
            await Task.WhenAll(full, roots).WaitAsync(_deadline, TestContext.Current.CancellationToken);
            context.Traversal.Verify(t => t.RefreshTopFoldersAsync(true, It.IsAny<CancellationToken>()), Times.Once);
            context.Monitor.Verify(m => m.Stop(), Times.Exactly(2));
            context.Monitor.Verify(m => m.Start(), Times.Exactly(2));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task PreCancelled_DoesNotStopMonitorOrTraverse()
    {
        using var context = new Context();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Coordinator.ScanAsync(new InlineProgress(_ => { }), cancelled.Token));
        Assert.Empty(context.Monitor.Invocations);
        Assert.Empty(context.Traversal.Invocations);
    }

    [Fact]
    public async Task Shutdown_CancelsActiveAndQueuedWork_AndJoinsCleanupBeforeDisposal()
    {
        using var context = new Context();
        var entered = Signal();
        var cancelled = Signal();
        var cleanup = Signal();
        context.Traversal.Setup(t => t.RefreshAllAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns(async (IProgress<double> _, CancellationToken token) =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                cancelled.SetResult();
                await cleanup.Task;
            }
        });
        try
        {
            var active = context.Scan();
            await entered.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
            var queued = context.Coordinator.RefreshTopFoldersAsync(TestContext.Current.CancellationToken, true);
            var stop = context.Coordinator.StopAsync();
            Assert.Same(stop, context.Coordinator.StopAsync());
            await cancelled.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
            Assert.False(stop.IsCompleted);
            Assert.True(context.Coordinator.IsScanRunning);
            await Assert.ThrowsAsync<ObjectDisposedException>(context.Scan);
            Assert.Throws<ObjectDisposedException>(context.Coordinator.RequestScan);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            cleanup.SetResult();
            await stop.WaitAsync(_deadline, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            context.Monitor.Verify(m => m.Start(), Times.Never);
            Assert.False(context.Coordinator.IsScanRunning);
        }
        finally
        {
            cleanup.TrySetResult();
        }
    }

    [Fact]
    public async Task StructureEdit_ReconcilesUnderTheScanLock_AndQueuesVisibleScanAfterRestart()
    {
        using var context = new Context();
        var order = new List<string>();
        context.Monitor.Setup(m => m.Stop()).Callback(() => order.Add("stop"));
        context.Monitor.Setup(m => m.Start()).Callback(() => order.Add("start"));
        context.Traversal.Setup(t => t.RefreshTopFoldersAsync(true, It.IsAny<CancellationToken>())).Callback(() => order.Add("roots")).Returns(Task.CompletedTask);
        context.Tasks.Setup(t => t.CancelIfRunningAndQueue<RefreshMediaLibraryTask>()).Callback(() => order.Add("queue"));
        await context.Coordinator.ApplyLibraryChangeAsync(() => order.Add("edit"), true, true, TestContext.Current.CancellationToken);
        Assert.Equal(["stop", "edit", "roots", "start", "queue"], order);
    }

    [Fact]
    public async Task FailedStructureEdit_PreservesOriginalError_AndStillReconcilesAndRestarts()
    {
        using var context = new Context();
        var primary = new InvalidOperationException("edit failed");
        context.Traversal.Setup(t => t.RefreshTopFoldersAsync(true, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("reconcile failed"));
        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(() => context.Coordinator.ApplyLibraryChangeAsync(() => throw primary, true, true, TestContext.Current.CancellationToken)));
        context.Monitor.Verify(m => m.Start(), Times.Once);
        Assert.Empty(context.Tasks.Invocations);
    }

    [Fact]
    public void WizardCompletion_QueuesOncePerChange_AndUnsubscribesOnShutdown()
    {
        using var context = new Context();
        context.Configuration.Raise(c => c.ConfigurationUpdated += null, EventArgs.Empty);
        Assert.Empty(context.Tasks.Invocations);
        context.Options.IsStartupWizardCompleted = true;
        context.Configuration.Raise(c => c.ConfigurationUpdated += null, EventArgs.Empty);
        context.Configuration.Raise(c => c.ConfigurationUpdated += null, EventArgs.Empty);
        context.Tasks.Verify(t => t.CancelIfRunningAndQueue<RefreshMediaLibraryTask>(), Times.Once);
        context.Coordinator.Dispose();
        context.Options.IsStartupWizardCompleted = false;
        context.Configuration.Raise(c => c.ConfigurationUpdated += null, EventArgs.Empty);
        context.Tasks.Verify(t => t.CancelIfRunningAndQueue<RefreshMediaLibraryTask>(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationOrHostStop_EndsActiveScan_AndOnlyCallerCancellationRestartsMonitor(bool hostStop)
    {
        using var context = new Context();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = Signal();
        context.Traversal.Setup(t => t.RefreshAllAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns(async (IProgress<double> _, CancellationToken token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var scan = context.Coordinator.ScanAsync(new InlineProgress(_ => { }), cancellation.Token);
        await entered.Task.WaitAsync(_deadline, TestContext.Current.CancellationToken);
        if (hostStop)
        {
            await ((IHostedService)context.Coordinator).StopAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(_deadline, TestContext.Current.CancellationToken));
        if (hostStop)
        {
            await context.Coordinator.StopAsync().WaitAsync(_deadline, TestContext.Current.CancellationToken);
        }

        Assert.False(context.Coordinator.IsScanRunning);
        context.Monitor.Verify(m => m.Start(), hostStop ? Times.Never() : Times.Once());
        context.Traversal.Verify(t => t.UpdateInheritedValues(), Times.Never);
    }

    [Fact]
    public async Task ScheduledTask_UsesCoordinatorWithoutConcreteManagerCast()
    {
        var scan = new Mock<ILibraryScanCoordinator>(MockBehavior.Strict);
        scan.Setup(c => c.ScanAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var task = new RefreshMediaLibraryTask(scan.Object, Mock.Of<ILocalizationManager>());
        await task.ExecuteAsync(new InlineProgress(_ => { }), TestContext.Current.CancellationToken);
        scan.Verify(c => c.ScanAsync(It.IsAny<IProgress<double>>(), TestContext.Current.CancellationToken), Times.Once);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class Context : IDisposable
    {
        public Context()
        {
            Configuration.Setup(c => c.Configuration).Returns(Options);
            Traversal.Setup(t => t.RefreshTopFoldersAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Traversal.Setup(t => t.RefreshAllAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            Coordinator = new LibraryScanCoordinator(Traversal.Object, new Lazy<ILibraryMonitor>(() => Monitor.Object), Tasks.Object, Configuration.Object, NullLogger<LibraryScanCoordinator>.Instance);
        }

        public ServerConfiguration Options { get; } = new();

        public Mock<IServerConfigurationManager> Configuration { get; } = new();

        public Mock<TraversalDouble> Traversal { get; } = new();

        public Mock<ILibraryMonitor> Monitor { get; } = new();

        public Mock<ITaskManager> Tasks { get; } = new();

        public LibraryScanCoordinator Coordinator { get; }

        public Task Scan() => Coordinator.ScanAsync(new InlineProgress(_ => { }), TestContext.Current.CancellationToken);

        public void Dispose()
        {
            Coordinator.Dispose();
        }
    }
}
