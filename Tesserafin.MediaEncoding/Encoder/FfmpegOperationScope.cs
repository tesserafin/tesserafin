using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.MediaEncoding.Encoder;

/// <summary>
/// Registers work before launch and closes admission before cancellation. Shutdown waits for
/// every operation's cleanup before disposing resources; callers keep their original outcomes.
/// </summary>
internal sealed class FfmpegOperationScope(Action? disposeResources = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _operations = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;

    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            _operations.Add(completed.Task);
        }

        return ExecuteAsync(operation, cancellation, completed);
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationTokenSource cancellation, TaskCompletionSource completed)
    {
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            return await operation(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
            lock (_gate)
            {
                _operations.Remove(completed.Task);
                completed.SetResult();
            }
        }
    }

    public Task StopAsync()
    {
        Task[] operations;
        lock (_gate)
        {
            if (_stopping)
            {
                return _closed.Task;
            }

            _stopping = true;
            operations = _operations.ToArray();
        }

        // StopCoreAsync reports every failure through _closed; no background fault is abandoned.
        _ = StopCoreAsync(operations);
        return _closed.Task;
    }

    private async Task StopCoreAsync(Task[] operations)
    {
        try
        {
            try
            {
                await _shutdown.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                await Task.WhenAll(operations).ConfigureAwait(false);
                try
                {
                    disposeResources?.Invoke();
                }
                finally
                {
                    _shutdown.Dispose();
                }
            }

            _closed.SetResult();
        }
        catch (Exception ex)
        {
            _closed.SetException(ex);
        }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
