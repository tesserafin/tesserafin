using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Model.Tasks;
using Tesserafin.Server.Core.ScheduledTasks;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.ScheduledTasks;

public sealed class TaskManagerTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();
    private readonly TaskManager _manager;

    public TaskManagerTests()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.ConfigurationDirectoryPath).Returns(_directory.FullName);
        paths.SetupGet(p => p.DataPath).Returns(_directory.FullName);
        _manager = new TaskManager(paths.Object, NullLogger<TaskManager>.Instance);
    }

    [Fact]
    public async Task UnrelatedCompletionPreservesOnePendingRunWithFirstOptions()
    {
        var task = new PausedTask();
        _manager.AddTasks([task, new OtherTask()]);
        var worker = _manager.ScheduledTasks.First();
        var first = _manager.Execute(worker, new TaskOptions());
        await Wait(task.Started.Task);
        _manager.QueueScheduledTask(task, new TaskOptions { MaxRuntimeTicks = TimeSpan.FromMilliseconds(25).Ticks });
        _manager.QueueScheduledTask<PausedTask>();
        await Wait(_manager.Execute(_manager.ScheduledTasks.Last(), new TaskOptions()));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.TaskCompleted += (_, e) =>
        {
            if (e.Result.Status == TaskCompletionStatus.Cancelled)
            {
                completed.TrySetResult();
            }
        };

        task.Release.SetResult();
        await Wait(first);
        await Wait(completed.Task);
        Assert.Equal(2, task.Runs);
    }

    [Fact]
    public async Task ManualStartsJoinTheActiveRunWithoutSchedulingAnother()
    {
        var task = new PausedTask();
        _manager.AddTasks([task]);
        var worker = Assert.Single(_manager.ScheduledTasks);
        var first = _manager.Execute(worker, new TaskOptions());
        await Wait(task.Started.Task);
        var second = _manager.Execute(worker, new TaskOptions());
        _manager.QueueIfNotRunning<PausedTask>();
        task.Release.SetResult();
        await Wait(Task.WhenAll(first, second));
        Assert.Equal(1, task.Runs);
        Assert.Equal(TaskState.Idle, worker.State);
    }

    [Fact]
    public async Task CancelAndRequeuePublishesCancellationBeforeTheNextStart()
    {
        var task = new PausedTask();
        _manager.AddTasks([task]);
        var worker = Assert.Single(_manager.ScheduledTasks);
        var events = new ConcurrentQueue<string>();
        var secondCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.TaskExecuting += (_, _) => events.Enqueue("start");
        _manager.TaskCompleted += (_, e) =>
        {
            events.Enqueue(e.Result.Status.ToString());
            if (task.Runs == 2)
            {
                secondCompleted.SetResult();
            }
        };

        var first = _manager.Execute(worker, new TaskOptions());
        Assert.Equal(TaskState.Running, worker.State);
        await Wait(task.Started.Task);
        _manager.CancelIfRunningAndQueue<PausedTask>(new TaskOptions { MaxRuntimeTicks = TimeSpan.FromMilliseconds(25).Ticks });
        await Wait(first);
        await Wait(secondCompleted.Task);

        Assert.Equal(["start", "Cancelled", "start", "Cancelled"], events.ToArray());
        Assert.Equal(TaskState.Idle, worker.State);
    }

    [Fact]
    public async Task ShutdownDiscardsPendingRunAndRecordsCooperativeCancellationOnce()
    {
        var task = new PausedTask();
        _manager.AddTasks([task]);
        var worker = Assert.Single(_manager.ScheduledTasks);
        var results = new ConcurrentQueue<TaskCompletionStatus>();
        _manager.TaskCompleted += (_, e) => results.Enqueue(e.Result.Status);

        var running = _manager.Execute(worker, new TaskOptions());
        await Wait(task.Started.Task);
        _manager.QueueScheduledTask<PausedTask>();
        _manager.Dispose();
        await Wait(running);

        Assert.Equal([TaskCompletionStatus.Cancelled], results.ToArray());
        Assert.Equal(1, task.Runs);
        Assert.Equal(TaskState.Idle, worker.State);
        Assert.Equal(TaskCompletionStatus.Cancelled, worker.LastExecutionResult.Status);
    }

    [Fact]
    public async Task ShutdownTimeoutRecordsOneAbortedResultEvenIfTheTaskReturnsLater()
    {
        var task = new UncooperativeTask();
        _manager.AddTasks([task]);
        var worker = Assert.Single(_manager.ScheduledTasks);
        var results = new ConcurrentQueue<TaskCompletionStatus>();
        _manager.TaskCompleted += (_, e) => results.Enqueue(e.Result.Status);

        var running = _manager.Execute(worker, new TaskOptions());
        await Wait(task.Started.Task);
        try
        {
            _manager.Dispose();
            Assert.False(running.IsCompleted);
            Assert.Equal(TaskCompletionStatus.Aborted, worker.LastExecutionResult.Status);
        }
        finally
        {
            task.Release.SetResult();
            await Wait(running);
        }

        Assert.Equal([TaskCompletionStatus.Aborted], results.ToArray());
        Assert.Equal(TaskCompletionStatus.Aborted, worker.LastExecutionResult.Status);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _directory.Delete(true);
    }

    private static Task Wait(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private class PausedTask : IScheduledTask
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Runs { get; private set; }

        public string Name => GetType().Name;

        public string Key => Name;

        public string Description => Name;

        public string Category => Name;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

        public virtual async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            Runs++;
            if (Runs == 1)
            {
                Started.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            else
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }

    private sealed class UncooperativeTask : PausedTask
    {
        public override Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            Started.SetResult();
            return Release.Task;
        }
    }

    private sealed class OtherTask : PausedTask
    {
        public override Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
