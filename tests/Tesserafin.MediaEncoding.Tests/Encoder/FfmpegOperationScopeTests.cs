using System;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.MediaEncoding.Encoder;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Encoder;

public sealed class FfmpegOperationScopeTests
{
    [Fact]
    public async Task TransferredResource_DisposesExactlyOnce_AfterCancelledWorkFinishesCleanup()
    {
        var disposals = 0;
        using var scope = new FfmpegOperationScope(() => Interlocked.Increment(ref disposals));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Run(CancellationToken token)
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }
            finally
            {
                cancelled.SetResult();
                await cleanup.Task;
            }
        }

        var operation = scope.RunAsync(Run, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var stopped = scope.StopAsync();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Same(stopped, scope.StopAsync());
            Assert.False(stopped.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref disposals));
            var concurrentDispose = Task.Run(scope.Dispose, TestContext.Current.CancellationToken);
            cleanup.SetResult();
            await Task.WhenAll(stopped, concurrentDispose).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            scope.Dispose();
            Assert.Equal(1, Volatile.Read(ref disposals));
        }
        finally
        {
            cleanup.TrySetResult();
        }
    }
}
