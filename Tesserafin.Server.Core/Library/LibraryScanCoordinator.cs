using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Tasks;
using Tesserafin.Server.Core.ScheduledTasks.Tasks;

namespace Tesserafin.Server.Core.Library;

/// <summary>One owner for scan admission, monitor suspension, progression and post-scan tasks.</summary>
internal sealed class LibraryScanCoordinator : ILibraryScanCoordinator, IHostedService, IDisposable
{
    private readonly ILibraryScanTraversal _traversal;
    private readonly Lazy<ILibraryMonitor> _monitor;
    private readonly ITaskManager _tasks;
    private readonly IServerConfigurationManager _configuration;
    private readonly ILogger<LibraryScanCoordinator> _logger;
    private readonly AsyncNonKeyedLocker _slot = new(1);
    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ILibraryPostScanTask[] _postScanTasks = [];
    private bool _wizardCompleted;
    private int _scanning;
    private int _active;
    private bool _stopping;

    public LibraryScanCoordinator(ILibraryScanTraversal traversal, Lazy<ILibraryMonitor> monitor, ITaskManager tasks, IServerConfigurationManager configuration, ILogger<LibraryScanCoordinator> logger)
    {
        _traversal = traversal;
        _monitor = monitor;
        _tasks = tasks;
        _configuration = configuration;
        _logger = logger;
        _wizardCompleted = configuration.Configuration.IsStartupWizardCompleted;
        configuration.ConfigurationUpdated += ConfigurationUpdated;
    }

    public bool IsScanRunning => Volatile.Read(ref _scanning) != 0;

    public void RequestScan()
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _tasks.CancelIfRunningAndQueue<RefreshMediaLibraryTask>();
        }
    }

    public void ClearIgnoreRuleCache() => _traversal.ClearIgnoreRuleCache();

    public void SetPostScanTasks(IEnumerable<ILibraryPostScanTask> tasks) => Volatile.Write(ref _postScanTasks, tasks.ToArray());

    public Task ScanAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => RunAsync(
            async token =>
            {
                progress.Report(0);
                _logger.LogInformation("Validating media library");
                await _traversal.RefreshTopFoldersAsync(token, false).ConfigureAwait(false);
                await _traversal.RefreshAllAsync(new InlineProgress(pct => progress.Report(pct * 0.96)), token).ConfigureAwait(false);
                progress.Report(96);
                await RunPostScanTasksAsync(new InlineProgress(pct => progress.Report(96 + (pct * 0.04))), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                _traversal.UpdateInheritedValues();
                progress.Report(100);
            },
            fullScan: true,
            cancellationToken);

    public Task RefreshTopFoldersAsync(CancellationToken cancellationToken, bool removeRoot = false)
        => RunAsync(token => _traversal.RefreshTopFoldersAsync(token, removeRoot), fullScan: false, cancellationToken);

    public async Task ApplyLibraryChangeAsync(Action change, bool refreshLibrary, bool removeRoot, CancellationToken cancellationToken)
    {
        await RunAsync(
            async token =>
            {
                Exception? failure = null;
                try
                {
                    change();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                try
                {
                    // Once an edit has started, request cancellation must not skip reconciliation.
                    // Give reconciliation a cooperative budget independent of the cancelled request.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _traversal.RefreshTopFoldersAsync(cleanup.Token, removeRoot).ConfigureAwait(false);
                    if (!refreshLibrary)
                    {
                        // Let filesystem notifications from the structure edit settle before restart.
                        await Task.Delay(1000, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (failure is not null)
                {
                    _logger.LogError(ex, "Error reconciling library roots after a failed structure edit");
                }

                if (failure is not null)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                token.ThrowIfCancellationRequested();
            },
            fullScan: false,
            cancellationToken).ConfigureAwait(false);
        if (refreshLibrary)
        {
            RequestScan();
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, bool fullScan, CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            _active++;
        }

        try
        {
            await ExecuteAsync(work, fullScan, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            linked.Dispose();
            lock (_lifetimeGate)
            {
                if (--_active == 0 && _stopping)
                {
                    _drained.TrySetResult();
                }
            }
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> work, bool fullScan, CancellationToken cancellationToken)
    {
        using (await _slot.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fullScan)
            {
                Volatile.Write(ref _scanning, 1);
            }

            Exception? failure = null;
            ILibraryMonitor? monitor = null;
            try
            {
                ClearIgnoreRuleCache();
                monitor = _monitor.Value;
                monitor.Stop();
                await work(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                try
                {
                    ClearIgnoreRuleCache();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error clearing ignore lookups after library traversal");
                    failure ??= ex;
                }

                try
                {
                    lock (_lifetimeGate)
                    {
                        if (!_stopping)
                        {
                            monitor?.Start();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error restoring library monitor after traversal");
                    failure ??= ex;
                }
                finally
                {
                    Volatile.Write(ref _scanning, 0);
                }
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }

    private async Task RunPostScanTasksAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var tasks = Volatile.Read(ref _postScanTasks);
        for (var i = 0; i < tasks.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = i;
            try
            {
                await tasks[i].Run(new InlineProgress(pct => progress.Report(((index + (pct / 100)) / tasks.Length) * 100)), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running post-scan task {Task}", tasks[i].GetType().Name);
            }

            progress.Report(((i + 1.0) / tasks.Length) * 100);
        }
    }

    private void ConfigurationUpdated(object? sender, EventArgs args)
    {
        var completed = _configuration.Configuration.IsStartupWizardCompleted;
        if (completed != _wizardCompleted)
        {
            _wizardCompleted = completed;
            RequestScan();
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopAsync();

    internal Task StopAsync()
    {
        lock (_lifetimeGate)
        {
            if (_stopping)
            {
                return _stopped.Task;
            }

            _stopping = true;
            if (_active == 0)
            {
                _drained.TrySetResult();
            }
        }

        _configuration.ConfigurationUpdated -= ConfigurationUpdated;
        _ = StopCoreAsync();
        return _stopped.Task;
    }

    private async Task StopCoreAsync()
    {
        try
        {
            try
            {
                await _shutdown.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                await _drained.Task.ConfigureAwait(false);
                _slot.Dispose();
                _shutdown.Dispose();
            }

            _stopped.TrySetResult();
        }
        catch (Exception ex)
        {
            _stopped.TrySetException(ex);
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
