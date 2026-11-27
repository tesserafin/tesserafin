using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Configuration;
using Tesserafin.Data.Events;
using Tesserafin.Model.Tasks;

namespace Tesserafin.Server.Core.ScheduledTasks;

/// <summary>
/// Class TaskManager.
/// </summary>
public class TaskManager : ITaskManager
{
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<TaskManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskManager" /> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public TaskManager(
        IApplicationPaths applicationPaths,
        ILogger<TaskManager> logger)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;

        ScheduledTasks = [];
    }

    /// <inheritdoc />
    public event EventHandler<GenericEventArgs<IScheduledTaskWorker>>? TaskExecuting;

    /// <inheritdoc />
    public event EventHandler<TaskCompletionEventArgs>? TaskCompleted;

    /// <inheritdoc />
    public IReadOnlyList<IScheduledTaskWorker> ScheduledTasks { get; private set; }

    /// <inheritdoc />
    public void CancelIfRunningAndQueue<T>(TaskOptions options)
        where T : IScheduledTask
    {
        var task = ScheduledTasks.First(t => t.ScheduledTask.GetType() == typeof(T));
        ((ScheduledTaskWorker)task).Queue(options, cancelRunning: true);
    }

    /// <inheritdoc />
    public void CancelIfRunningAndQueue<T>()
            where T : IScheduledTask
    {
        CancelIfRunningAndQueue<T>(new TaskOptions());
    }

    /// <inheritdoc />
    public void CancelIfRunning<T>()
                where T : IScheduledTask
    {
        var task = ScheduledTasks.First(t => t.ScheduledTask.GetType() == typeof(T));
        ((ScheduledTaskWorker)task).CancelIfRunning();
    }

    /// <inheritdoc />
    public void QueueScheduledTask<T>(TaskOptions options)
        where T : IScheduledTask
    {
        var scheduledTask = ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.GetType() == typeof(T));

        if (scheduledTask is null)
        {
            _logger.LogError("Unable to find scheduled task of type {Type} in QueueScheduledTask.", typeof(T).Name);
        }
        else
        {
            ((ScheduledTaskWorker)scheduledTask).Queue(options);
        }
    }

    /// <inheritdoc />
    public void QueueScheduledTask<T>()
        where T : IScheduledTask
    {
        QueueScheduledTask<T>(new TaskOptions());
    }

    /// <inheritdoc />
    public void QueueIfNotRunning<T>()
        where T : IScheduledTask
    {
        var task = ScheduledTasks.First(t => t.ScheduledTask.GetType() == typeof(T));

        ((ScheduledTaskWorker)task).QueueIfNotRunning();
    }

    /// <inheritdoc />
    public void Execute<T>()
        where T : IScheduledTask
    {
        var scheduledTask = ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.GetType() == typeof(T));

        if (scheduledTask is null)
        {
            _logger.LogError("Unable to find scheduled task of type {Type} in Execute.", typeof(T).Name);
        }
        else
        {
            _ = Execute(scheduledTask, new TaskOptions());
        }
    }

    /// <inheritdoc />
    public void QueueScheduledTask(IScheduledTask task, TaskOptions options)
    {
        var scheduledTask = ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.GetType() == task.GetType());

        if (scheduledTask is null)
        {
            _logger.LogError("Unable to find scheduled task of type {Type} in QueueScheduledTask.", task.GetType().Name);
        }
        else
        {
            ((ScheduledTaskWorker)scheduledTask).Queue(options);
        }
    }

    /// <inheritdoc />
    public void AddTasks(IEnumerable<IScheduledTask> tasks)
    {
        var list = tasks.Select(t => new ScheduledTaskWorker(t, _applicationPaths, this, _logger));

        ScheduledTasks = ScheduledTasks.Concat(list).ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources.
    /// </summary>
    /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
    protected virtual void Dispose(bool dispose)
    {
        foreach (var task in ScheduledTasks)
        {
            task.Dispose();
        }
    }

    /// <inheritdoc />
    public void Cancel(IScheduledTaskWorker task)
    {
        ((ScheduledTaskWorker)task).Cancel();
    }

    /// <inheritdoc />
    public Task Execute(IScheduledTaskWorker task, TaskOptions options)
    {
        return ((ScheduledTaskWorker)task).Execute(options);
    }

    /// <summary>
    /// Called when [task executing].
    /// </summary>
    /// <param name="task">The task.</param>
    internal void OnTaskExecuting(IScheduledTaskWorker task)
    {
        TaskExecuting?.Invoke(this, new GenericEventArgs<IScheduledTaskWorker>(task));
    }

    /// <summary>
    /// Called when [task completed].
    /// </summary>
    /// <param name="task">The task.</param>
    /// <param name="result">The result.</param>
    internal void OnTaskCompleted(IScheduledTaskWorker task, TaskResult result)
    {
        TaskCompleted?.Invoke(task, new TaskCompletionEventArgs(task, result));
    }
}
