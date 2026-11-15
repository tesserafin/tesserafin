using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Streaming;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// The internal transport that replaces ffmpeg's anonymous HTTP fetch of the [Authorize]d
/// <c>/LiveTv/LiveStreamFiles/**</c> endpoint. Every test here carries an explicit timeout; none
/// of them sleep without a bound.
/// </summary>
public class DirectStreamPumpTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Pump_ProducerBytes_ArriveOnTheConsumerIntact()
    {
        var payload = RandomBytes(512 * 1024);
        var source = new MemoryStream(payload, writable: false);
        var destination = new NonClosingMemoryStream();

        var pump = DirectStreamPump.Start(source, destination, NullLogger.Instance, CancellationToken.None);
        await WithTimeout(pump.Completion);

        Assert.Null(pump.Fault);
        Assert.Equal(payload.Length, pump.BytesCopied);
        Assert.True(payload.AsSpan().SequenceEqual(destination.Written), "The consumer did not receive the producer's bytes byte-for-byte.");
    }

    [Fact]
    public async Task Pump_ProgressiveStream_ContinuesAcrossATemporaryEndOfFile()
    {
        // The tuner's temp file is being appended to while ffmpeg reads it, so a plain FileStream
        // hits EOF long before the stream is actually over. ProgressiveFileStream is what turns
        // that into a wait; this proves the pump inherits it rather than stopping at the first
        // zero-byte read.
        var directory = Directory.CreateTempSubdirectory("ltv-s0-pump-");
        try
        {
            var path = Path.Combine(directory.FullName, "tuner.ts");
            var first = RandomBytes(64 * 1024);
            var second = RandomBytes(64 * 1024);
            await File.WriteAllBytesAsync(path, first, TestContext.Current.CancellationToken);

            var source = new ProgressiveFileStream(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                timeoutMs: 3000);
            var destination = new NonClosingMemoryStream();

            var pump = DirectStreamPump.Start(source, destination, NullLogger.Instance, CancellationToken.None);

            // Append only after the pump has certainly drained what was there, so the second half
            // can only arrive by surviving an end-of-file.
            await WaitForAsync(() => pump.BytesCopied >= first.Length);
            await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                await append.WriteAsync(second, TestContext.Current.CancellationToken);
            }

            await WithTimeout(pump.Completion);

            Assert.Null(pump.Fault);
            Assert.Equal(first.Length + second.Length, pump.BytesCopied);
            Assert.True(first.Concat(second).ToArray().AsSpan().SequenceEqual(destination.Written));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Pump_Cancelled_StopsAndCompletes()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var source = new NeverEndingStream();
        var destination = new NonClosingMemoryStream();

        var pump = DirectStreamPump.Start(source, destination, NullLogger.Instance, cancellationTokenSource.Token);
        await WaitForAsync(() => pump.BytesCopied > 0);

        await cancellationTokenSource.CancelAsync();
        await WithTimeout(pump.Completion);

        Assert.True(pump.Completion.IsCompletedSuccessfully);
        Assert.Null(pump.Fault);
    }

    [Fact]
    public async Task StopAsync_WhileProducerIsStillRunning_ReturnsWithNoOrphanedTask()
    {
        var source = new NeverEndingStream();
        var destination = new NonClosingMemoryStream();

        var pump = DirectStreamPump.Start(source, destination, NullLogger.Instance, CancellationToken.None);
        await WaitForAsync(() => pump.BytesCopied > 0);

        await WithTimeout(pump.StopAsync());

        Assert.True(pump.Completion.IsCompleted);
        Assert.True(source.IsDisposed, "The pump must dispose the producer it owns.");
        Assert.True(destination.IsDisposed, "The pump must close the consumer's input so ffmpeg sees EOF.");
    }

    [Fact]
    public async Task Pump_ConsumerClosesEarly_DoesNotHangAndLeavesNoUnobservedException()
    {
        // ffmpeg exiting mid-stream closes its stdin; the write then fails. That is the normal end
        // of a consumer, and must not surface as a faulted task nobody awaits.
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var source = new NeverEndingStream();
            var destination = new ClosedAfterFirstWriteStream();

            var pump = DirectStreamPump.Start(source, destination, NullLogger.Instance, CancellationToken.None);
            await WithTimeout(pump.Completion);

            Assert.True(pump.Completion.IsCompletedSuccessfully);
            Assert.Null(pump.Fault);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task Pump_ProducerFails_RecordsTheFaultExplicitly()
    {
        var source = new FailingStream();
        var destination = new NonClosingMemoryStream();

        var pump = DirectStreamPump.Start(source, destination, NullLogger.Instance, CancellationToken.None);
        await WithTimeout(pump.Completion);

        Assert.True(pump.Completion.IsCompletedSuccessfully);
        var fault = Assert.IsType<IOException>(pump.Fault);
        Assert.Equal("tuner died", fault.Message);
    }

    [Fact]
    public async Task StopAsync_CalledTwice_IsSafe()
    {
        var source = new NeverEndingStream();
        var destination = new NonClosingMemoryStream();

        var pump = DirectStreamPump.Start(source, destination, NullLogger.Instance, CancellationToken.None);
        await WithTimeout(pump.StopAsync());
        await WithTimeout(pump.StopAsync());
        await WithTimeout(pump.DisposeAsync().AsTask());

        Assert.True(pump.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task UnexpectedDisposalFailure_StillClosesBothStreamsAndReportsFault()
    {
        var source = new NonClosingMemoryStream();
        var destination = new MockDisposeFailureStream();
        await using var pump = DirectStreamPump.Start(source, destination, NullLogger.Instance, TestContext.Current.CancellationToken);
        await WithTimeout(pump.Completion);
        Assert.True(source.IsDisposed);
        Assert.True(destination.IsDisposed);
        Assert.Same(destination.Failure, pump.Fault);
        Assert.True(pump.Completion.IsCompletedSuccessfully);
    }

    private static byte[] RandomBytes(int count)
    {
        var buffer = new byte[count];
        Random.Shared.NextBytes(buffer);
        return buffer;
    }

    private static async Task WithTimeout(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(_timeout));
        Assert.True(ReferenceEquals(completed, task), $"Timed out after {_timeout}.");
        await task;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Condition was still false after {_timeout}.");
            await Task.Delay(25);
        }
    }

    private sealed class MockDisposeFailureStream : MemoryStream
    {
        public Exception Failure { get; } = new InvalidOperationException("unexpected disposal failure");

        public bool IsDisposed { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            IsDisposed = true;
            throw Failure;
        }
    }

    private sealed class NonClosingMemoryStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        public byte[] Written { get; private set; } = [];

        protected override void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                Written = ToArray();
                IsDisposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NeverEndingStream : Stream
    {
        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken);
            buffer.Span[..1024].Fill(0x47);
            return 1024;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("tuner died");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("tuner died");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ClosedAfterFirstWriteStream : Stream
    {
        private bool _closed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_closed)
            {
                throw new IOException("Broken pipe");
            }

            _closed = true;
            return ValueTask.CompletedTask;
        }
    }
}
