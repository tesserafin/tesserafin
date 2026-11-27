#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Configuration;
using Tesserafin.Common.Extensions;
using Tesserafin.Data.Events;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Tasks;
using Tesserafin.Server.Core.ScheduledTasks.Triggers;

namespace Tesserafin.Server.Core.ScheduledTasks;

/// <summary>
/// Class ScheduledTaskWorker.
/// </summary>
public class ScheduledTaskWorker : IScheduledTaskWorker
{
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger _logger;
    private readonly ITaskManager _taskManager;
    private readonly Lock _lastExecutionResultSyncLock = new();
    private readonly Lock _executionLock = new();
    private bool _readFromFile;
    private TaskResult _lastExecutionResult;
    private Task _lastRun = Task.CompletedTask;
    private CancellationTokenSource _cancellation;
    private Task _cancellationCallbacks = Task.CompletedTask;
    private TaskOptions _pending;
    private DateTime _startTime;
    private bool _disposed;
    private bool _reportedAborted;
    private Tuple<TaskTriggerInfo, ITaskTrigger>[] _triggers;
    private string _id;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScheduledTaskWorker" /> class.
    /// </summary>
    /// <param name="scheduledTask">The scheduled task.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="taskManager">The task manager.</param>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException">
    /// scheduledTask
    /// or
    /// applicationPaths
    /// or
    /// taskManager
    /// or
    /// jsonSerializer
    /// or
    /// logger.
    /// </exception>
    public ScheduledTaskWorker(IScheduledTask scheduledTask, IApplicationPaths applicationPaths, ITaskManager taskManager, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(scheduledTask);
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(taskManager);
        ArgumentNullException.ThrowIfNull(logger);

        ScheduledTask = scheduledTask;
        _applicationPaths = applicationPaths;
        _taskManager = taskManager;
        _logger = logger;

        InitTriggerEvents();
    }

    /// <inheritdoc />
    public event EventHandler<GenericEventArgs<double>> TaskProgress;

    /// <inheritdoc />
    public IScheduledTask ScheduledTask { get; private set; }

    /// <inheritdoc />
    public TaskResult LastExecutionResult
    {
        get
        {
            var path = GetHistoryFilePath();

            lock (_lastExecutionResultSyncLock)
            {
                if (_lastExecutionResult is null && !_readFromFile)
                {
                    if (File.Exists(path))
                    {
                        var bytes = File.ReadAllBytes(path);
                        if (bytes.Length > 0)
                        {
                            try
                            {
                                _lastExecutionResult = JsonSerializer.Deserialize<TaskResult>(bytes, _jsonOptions);
                            }
                            catch (JsonException ex)
                            {
                                _logger.LogError(ex, "Error deserializing {File}", path);
                            }
                        }
                        else
                        {
                            _logger.LogDebug("Scheduled Task history file {Path} is empty. Skipping deserialization.", path);
                        }
                    }

                    _readFromFile = true;
                }
            }

            return _lastExecutionResult;
        }

        private set
        {
            _lastExecutionResult = value;

            var path = GetHistoryFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            lock (_lastExecutionResultSyncLock)
            {
                using FileStream createStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using Utf8JsonWriter jsonStream = new Utf8JsonWriter(createStream);
                JsonSerializer.Serialize(jsonStream, value, _jsonOptions);
            }
        }
    }

    /// <inheritdoc />
    public string Name => ScheduledTask.Name;

    /// <inheritdoc />
    public string Description => ScheduledTask.Description;

    /// <inheritdoc />
    public string Category => ScheduledTask.Category;

    /// <inheritdoc />
    public TaskState State
    {
        get
        {
            lock (_executionLock)
            {
                if (_cancellation is null)
                {
                    return TaskState.Idle;
                }

                return _cancellation.IsCancellationRequested ? TaskState.Cancelling : TaskState.Running;
            }
        }
    }

    /// <inheritdoc />
    public double? CurrentProgress { get; private set; }

    /// <summary>
    /// Gets or sets the triggers that define when the task will run.
    /// </summary>
    /// <value>The triggers.</value>
    private Tuple<TaskTriggerInfo, ITaskTrigger>[] InternalTriggers
    {
        get => _triggers;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            // Cleanup current triggers
            if (_triggers is not null)
            {
                DisposeTriggers();
            }

            _triggers = value.ToArray();

            ReloadTriggerEvents(false);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TaskTriggerInfo> Triggers
    {
        get
        {
            return Array.ConvertAll(InternalTriggers, i => i.Item1);
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);

            // This null check is not great, but is needed to handle bad user input, or user mucking with the config file incorrectly
            var triggerList = value.Where(i => i is not null).ToArray();

            SaveTriggers(triggerList);

            InternalTriggers = Array.ConvertAll(triggerList, i => new Tuple<TaskTriggerInfo, ITaskTrigger>(i, GetTrigger(i)));
        }
    }

    /// <inheritdoc />
    public string Id
    {
        get
        {
            return _id ??= ScheduledTask.GetType().FullName.GetMD5().ToString("N", CultureInfo.InvariantCulture);
        }
    }

    private void InitTriggerEvents()
    {
        _triggers = LoadTriggers();
        ReloadTriggerEvents(true);
    }

    /// <inheritdoc />
    public void ReloadTriggerEvents()
    {
        ReloadTriggerEvents(false);
    }

    /// <summary>
    /// Reloads the trigger events.
    /// </summary>
    /// <param name="isApplicationStartup">if set to <c>true</c> [is application startup].</param>
    private void ReloadTriggerEvents(bool isApplicationStartup)
    {
        foreach (var triggerInfo in InternalTriggers)
        {
            var trigger = triggerInfo.Item2;

            trigger.Stop();

            trigger.Triggered -= OnTriggerTriggered;
            trigger.Triggered += OnTriggerTriggered;
            trigger.Start(LastExecutionResult, _logger, Name, isApplicationStartup);
        }
    }

    /// <summary>
    /// Handles the Triggered event of the trigger control.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">The <see cref="EventArgs" /> instance containing the event data.</param>
    private async void OnTriggerTriggered(object sender, EventArgs e)
    {
        var trigger = (ITaskTrigger)sender;

        if (ScheduledTask is IConfigurableScheduledTask configurableTask && !configurableTask.IsEnabled)
        {
            return;
        }

        _logger.LogDebug("{0} fired for task: {1}", trigger.GetType().Name, Name);

        trigger.Stop();

        Queue(trigger.TaskOptions);

        await Task.Delay(1000).ConfigureAwait(false);

        lock (_executionLock)
        {
            if (!_disposed && InternalTriggers.Any(entry => entry.Item2 == trigger))
            {
                trigger.Start(LastExecutionResult, _logger, Name, false);
            }
        }
    }

    /// <summary>
    /// Executes the task.
    /// </summary>
    /// <param name="options">Task options.</param>
    /// <returns>Task.</returns>
    public Task Execute(TaskOptions options)
    {
        lock (_executionLock)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            return _cancellation is null ? StartLocked(options) : _lastRun;
        }
    }

    internal void Queue(TaskOptions options, bool cancelRunning = false)
    {
        lock (_executionLock)
        {
            if (_disposed)
            {
                return;
            }

            if (_cancellation is null)
            {
                StartLocked(options);
                return;
            }

            // Multiple requests during one run share the first requested rerun.
            _pending ??= options ?? new TaskOptions();
            if (cancelRunning)
            {
                CancelLocked();
            }
        }
    }

    internal void QueueIfNotRunning()
    {
        lock (_executionLock)
        {
            if (State != TaskState.Running)
            {
                Queue(new TaskOptions());
            }
        }
    }

    private Task StartLocked(TaskOptions options)
    {
        var cancellation = new CancellationTokenSource();
        var startTime = DateTime.UtcNow;
        var previous = _lastRun;
        _cancellation = cancellation;
        _startTime = startTime;
        _lastRun = Task.Run(async () =>
        {
            // Publish the previous result and completion event before the next start event.
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await ExecuteInternal(options, cancellation, startTime).ConfigureAwait(false);
        });
        return _lastRun;
    }

    private async Task ExecuteInternal(TaskOptions options, CancellationTokenSource cancellation, DateTime startTime)
    {
        var progress = new Progress<double>();
        progress.ProgressChanged += OnProgressChanged;
        var status = TaskCompletionStatus.Completed;
        Exception failureException = null;
        try
        {
            _logger.LogDebug("Executing {Name}", Name);
            ((TaskManager)_taskManager).OnTaskExecuting(this);
            if (options?.MaxRuntimeTicks is long maxRuntimeTicks)
            {
                cancellation.CancelAfter(TimeSpan.FromTicks(maxRuntimeTicks));
            }

            cancellation.Token.ThrowIfCancellationRequested();
            await ScheduledTask.ExecuteAsync(progress, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            status = TaskCompletionStatus.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing Scheduled Task");
            failureException = ex;
            status = TaskCompletionStatus.Failed;
        }

        progress.ProgressChanged -= OnProgressChanged;
        var endTime = DateTime.UtcNow;
        Task callbacks;
        bool reportCompletion;
        lock (_executionLock)
        {
            _cancellation = null;
            CurrentProgress = null;
            callbacks = _cancellationCallbacks;
            _cancellationCallbacks = Task.CompletedTask;
            reportCompletion = !_reportedAborted;
            if (_pending is not null)
            {
                var next = _pending;
                _pending = null;
                StartLocked(next);
            }
        }

        try
        {
            await callbacks.ConfigureAwait(false);
            if (reportCompletion)
            {
                OnTaskCompleted(startTime, endTime, status, failureException);
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Progress_s the progress changed.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The e.</param>
    private void OnProgressChanged(object sender, double e)
    {
        e = Math.Min(e, 100);

        CurrentProgress = e;

        TaskProgress?.Invoke(this, new GenericEventArgs<double>(e));
    }

    /// <summary>
    /// Stops the task if it is currently executing.
    /// </summary>
    /// <exception cref="InvalidOperationException">Cannot cancel a Task unless it is in the Running state.</exception>
    public void Cancel()
    {
        lock (_executionLock)
        {
            if (State != TaskState.Running)
            {
                throw new InvalidOperationException("Cannot cancel a Task unless it is in the Running state.");
            }

            CancelLocked();
        }
    }

    /// <summary>
    /// Cancels if running.
    /// </summary>
    public void CancelIfRunning()
    {
        lock (_executionLock)
        {
            CancelLocked();
        }
    }

    private void CancelLocked()
    {
        if (_cancellation is { IsCancellationRequested: false })
        {
            _logger.LogInformation("Attempting to cancel Scheduled Task {Name}", Name);
            _cancellationCallbacks = ObserveCancellationAsync(_cancellation.CancelAsync());
        }
    }

    private async Task ObserveCancellationAsync(Task callbacks)
    {
        try
        {
            await callbacks.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling scheduled task {Name}", Name);
        }
    }

    /// <summary>
    /// Gets the scheduled tasks configuration directory.
    /// </summary>
    /// <returns>System.String.</returns>
    private string GetScheduledTasksConfigurationDirectory()
    {
        return Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "ScheduledTasks");
    }

    /// <summary>
    /// Gets the scheduled tasks data directory.
    /// </summary>
    /// <returns>System.String.</returns>
    private string GetScheduledTasksDataDirectory()
    {
        return Path.Combine(_applicationPaths.DataPath, "ScheduledTasks");
    }

    /// <summary>
    /// Gets the history file path.
    /// </summary>
    /// <value>The history file path.</value>
    private string GetHistoryFilePath()
    {
        return Path.Combine(GetScheduledTasksDataDirectory(), new Guid(Id) + ".js");
    }

    /// <summary>
    /// Gets the configuration file path.
    /// </summary>
    /// <returns>System.String.</returns>
    private string GetConfigurationFilePath()
    {
        return Path.Combine(GetScheduledTasksConfigurationDirectory(), new Guid(Id) + ".js");
    }

    /// <summary>
    /// Loads the triggers.
    /// </summary>
    /// <returns>IEnumerable{BaseTaskTrigger}.</returns>
    private Tuple<TaskTriggerInfo, ITaskTrigger>[] LoadTriggers()
    {
        // This null check is not great, but is needed to handle bad user input, or user mucking with the config file incorrectly
        var settings = LoadTriggerSettings().Where(i => i is not null);

        return settings.Select(i => new Tuple<TaskTriggerInfo, ITaskTrigger>(i, GetTrigger(i))).ToArray();
    }

    private TaskTriggerInfo[] LoadTriggerSettings()
    {
        string path = GetConfigurationFilePath();
        TaskTriggerInfo[] list = null;
        if (File.Exists(path))
        {
            var bytes = File.ReadAllBytes(path);
            list = JsonSerializer.Deserialize<TaskTriggerInfo[]>(bytes, _jsonOptions);
        }

        // Return defaults if file doesn't exist.
        return list ?? GetDefaultTriggers();
    }

    private TaskTriggerInfo[] GetDefaultTriggers()
    {
        try
        {
            return ScheduledTask.GetDefaultTriggers().ToArray();
        }
        catch
        {
            return
            [
                new()
                {
                    IntervalTicks = TimeSpan.FromDays(1).Ticks,
                    Type = TaskTriggerInfoType.IntervalTrigger
                }
            ];
        }
    }

    /// <summary>
    /// Saves the triggers.
    /// </summary>
    /// <param name="triggers">The triggers.</param>
    private void SaveTriggers(TaskTriggerInfo[] triggers)
    {
        var path = GetConfigurationFilePath();

        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using FileStream createStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using Utf8JsonWriter jsonWriter = new Utf8JsonWriter(createStream);
        JsonSerializer.Serialize(jsonWriter, triggers, _jsonOptions);
    }

    /// <summary>
    /// Called when [task completed].
    /// </summary>
    /// <param name="startTime">The start time.</param>
    /// <param name="endTime">The end time.</param>
    /// <param name="status">The status.</param>
    /// <param name="ex">The exception.</param>
    private void OnTaskCompleted(DateTime startTime, DateTime endTime, TaskCompletionStatus status, Exception ex)
    {
        var elapsedTime = endTime - startTime;

        _logger.LogInformation("{0} {1} after {2} minute(s) and {3} seconds", Name, status, Math.Truncate(elapsedTime.TotalMinutes), elapsedTime.Seconds);

        var result = new TaskResult
        {
            StartTimeUtc = startTime,
            EndTimeUtc = endTime,
            Status = status,
            Name = Name,
            Id = Id
        };

        result.Key = ScheduledTask.Key;

        if (ex is not null)
        {
            result.ErrorMessage = ex.Message;
            result.LongErrorMessage = ex.StackTrace;
        }

        LastExecutionResult = result;

        ((TaskManager)_taskManager).OnTaskCompleted(this, result);
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
        if (!dispose)
        {
            return;
        }

        Task task;
        lock (_executionLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = null;
            CancelLocked();
            task = _lastRun;
        }

        DisposeTriggers();
        try
        {
            if (task.Wait(TimeSpan.FromSeconds(2)))
            {
                return;
            }
        }
        catch (AggregateException ex)
        {
            _logger.LogError(ex, "Error waiting for scheduled task {Name} to stop", Name);
            return;
        }

        lock (_executionLock)
        {
            if (_cancellation is null)
            {
                return;
            }

            _reportedAborted = true;
        }

        _logger.LogWarning("{Name}: Timed out waiting for task to stop", Name);
        OnTaskCompleted(_startTime, DateTime.UtcNow, TaskCompletionStatus.Aborted, null);
    }

    /// <summary>
    /// Converts a TaskTriggerInfo into a concrete BaseTaskTrigger.
    /// </summary>
    /// <param name="info">The info.</param>
    /// <returns>BaseTaskTrigger.</returns>
    /// <exception cref="ArgumentException">Invalid trigger type:  + info.Type.</exception>
    private ITaskTrigger GetTrigger(TaskTriggerInfo info)
    {
        var options = new TaskOptions
        {
            MaxRuntimeTicks = info.MaxRuntimeTicks
        };

        if (info.Type == TaskTriggerInfoType.DailyTrigger)
        {
            if (!info.TimeOfDayTicks.HasValue)
            {
                throw new ArgumentException("Info did not contain a TimeOfDayTicks.", nameof(info));
            }

            return new DailyTrigger(TimeSpan.FromTicks(info.TimeOfDayTicks.Value), options);
        }

        if (info.Type == TaskTriggerInfoType.WeeklyTrigger)
        {
            if (!info.TimeOfDayTicks.HasValue)
            {
                throw new ArgumentException("Info did not contain a TimeOfDayTicks.", nameof(info));
            }

            if (!info.DayOfWeek.HasValue)
            {
                throw new ArgumentException("Info did not contain a DayOfWeek.", nameof(info));
            }

            return new WeeklyTrigger(TimeSpan.FromTicks(info.TimeOfDayTicks.Value), info.DayOfWeek.Value, options);
        }

        if (info.Type == TaskTriggerInfoType.IntervalTrigger)
        {
            if (!info.IntervalTicks.HasValue)
            {
                throw new ArgumentException("Info did not contain a IntervalTicks.", nameof(info));
            }

            return new IntervalTrigger(TimeSpan.FromTicks(info.IntervalTicks.Value), options);
        }

        if (info.Type == TaskTriggerInfoType.StartupTrigger)
        {
            return new StartupTrigger(options);
        }

        throw new ArgumentException("Unrecognized trigger type: " + info.Type);
    }

    /// <summary>
    /// Disposes each trigger.
    /// </summary>
    private void DisposeTriggers()
    {
        foreach (var triggerInfo in InternalTriggers)
        {
            var trigger = triggerInfo.Item2;
            trigger.Triggered -= OnTriggerTriggered;
            trigger.Stop();
            if (trigger is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
