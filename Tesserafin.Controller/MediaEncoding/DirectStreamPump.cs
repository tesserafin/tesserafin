using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Copies the bytes an <see cref="Tesserafin.Controller.Library.IDirectStreamProvider"/> is already
/// producing into one ffmpeg process's standard input.
/// </summary>
/// <remarks>
/// <para>
/// This is a transport, not an authorization mechanism. Before this existed, a Live TV transcode
/// pointed ffmpeg at the <c>/LiveTv/LiveStreamFiles/{id}/stream.ts</c> URL that
/// <c>SharedHttpStream</c> publishes as the media source path, and ffmpeg - a child process with no
/// session, no api key and no user - fetched it over HTTP. That endpoint is <c>[Authorize]</c>, so
/// the fetch answered 401 and every Live TV transcode died with ffmpeg exit code 8.
/// </para>
/// <para>
/// The fix is not to weaken that endpoint. The server already holds the open tuner stream in
/// process; handing ffmpeg a file descriptor it can read is the same trust boundary as handing it a
/// path to a library file, which is what every non-live transcode already does. No credential, no
/// token and no capability is minted, transmitted or persisted: the authorization decision was
/// taken once, by the authenticated request that opened the live stream, and the pipe carries only
/// the bytes that decision already authorized. <c>/LiveTv/LiveStreamFiles/**</c> keeps its
/// <c>[Authorize]</c> and its external behaviour unchanged.
/// </para>
/// <para>
/// Ownership is explicit. The pump owns both streams and disposes them when it ends; it ends
/// exactly once, on any of: producer EOF, producer failure, ffmpeg closing its stdin, or
/// cancellation. <see cref="Completion"/> never faults - a producer failure is recorded on
/// <see cref="Fault"/> instead - so a caller that never awaits it cannot leave an unobserved task
/// exception behind, and a caller that does await it cannot hang past cancellation.
/// </para>
/// </remarks>
public sealed class DirectStreamPump : IAsyncDisposable
{
    private const int BufferSize = 81920;

    // CA2213: both streams ARE disposed - in PumpAsync's finally block, which is the only place
    // that can know the pump has stopped reading and writing. Disposing them from DisposeAsync
    // instead would race the pumping task.
#pragma warning disable CA2213
    private readonly Stream _source;
    private readonly Stream _destination;
#pragma warning restore CA2213
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellationTokenSource;

    private long _bytesCopied;
    private int _disposed;

    private DirectStreamPump(Stream source, Stream destination, ILogger logger, CancellationToken cancellationToken)
    {
        _source = source;
        _destination = destination;
        _logger = logger;
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Completion = PumpAsync();
    }

    /// <summary>
    /// Gets a task that completes when the pump has stopped and both streams are disposed. It never
    /// faults; see <see cref="Fault"/>.
    /// </summary>
    public Task Completion { get; }

    /// <summary>
    /// Gets the producer, transport or unexpected disposal failure that ended the pump, if any. Null when the pump ended because
    /// the producer reached EOF, the consumer closed its stdin, or the pump was cancelled.
    /// </summary>
    public Exception? Fault { get; private set; }

    /// <summary>
    /// Gets the number of bytes copied into the consumer so far.
    /// </summary>
    public long BytesCopied => Interlocked.Read(ref _bytesCopied);

    /// <summary>
    /// Starts pumping <paramref name="source"/> into <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">The producer stream. The pump takes ownership and disposes it.</param>
    /// <param name="destination">The consumer stream, normally an ffmpeg process's standard input. The pump takes ownership and disposes it, which is what signals EOF to ffmpeg.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancelling this stops the pump.</param>
    /// <returns>The running pump.</returns>
    public static DirectStreamPump Start(Stream source, Stream destination, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(logger);

        return new DirectStreamPump(source, destination, logger, cancellationToken);
    }

    /// <summary>
    /// Cancels the pump and waits for it to finish. Safe to call more than once, and from a thread
    /// that is also the one that started it.
    /// </summary>
    /// <returns>A task that completes once the pump has stopped.</returns>
    public async Task StopAsync()
    {
        if (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already torn down by a concurrent StopAsync/DisposeAsync.
            }
        }

        await Completion.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            await Completion.ConfigureAwait(false);
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _cancellationTokenSource.Dispose();
    }

    private async Task PumpAsync()
    {
        // Yield first so the constructor returns before any I/O runs: the caller must be able to
        // store this instance on the job before the pump can possibly complete and be stopped.
        await Task.Yield();

        var cancellationToken = _cancellationTokenSource.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            while (true)
            {
                int read;

                try
                {
                    read = await _source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // The producer died. This is the one failure the job must hear about: without
                    // it, a tuner that drops before ffmpeg's first output is indistinguishable from
                    // a slow start.
                    Fault = ex;
                    _logger.LogError(ex, "Direct stream producer failed after {Bytes} bytes", BytesCopied);
                    break;
                }

                if (read <= 0)
                {
                    // Producer EOF. ProgressiveFileStream has already waited out any temporary
                    // end-of-file on a still-growing tuner file, so this is the real end.
                    break;
                }

                try
                {
                    await _destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    await _destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // ffmpeg closed its stdin, or exited. Normal end of a consumer, not a fault:
                    // an early close must not become a hang or an unobserved exception.
                    _logger.LogDebug("Direct stream consumer closed its input after {Bytes} bytes", BytesCopied);
                    break;
                }
                catch (Exception ex)
                {
                    Fault = ex;
                    _logger.LogError(ex, "Direct stream pump failed writing to the consumer after {Bytes} bytes", BytesCopied);
                    break;
                }

                Interlocked.Add(ref _bytesCopied, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);

            // One line per job, at Information: this is the only externally visible proof that the
            // pumping task actually ended rather than being abandoned when its job was stopped.
            _logger.LogInformation("Direct stream pump finished after {Bytes} bytes", BytesCopied);

            // Closing the consumer's input is what tells ffmpeg the stream ended, so it finalizes
            // its output instead of waiting forever.
            await DisposeQuietly(_destination).ConfigureAwait(false);
            await DisposeQuietly(_source).ConfigureAwait(false);
        }
    }

    private async ValueTask DisposeQuietly(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Ignoring stream disposal error while stopping the direct stream pump");
        }
        catch (Exception ex)
        {
            // Completion must still settle and the other owned stream must also be disposed.
            Fault ??= ex;
            _logger.LogError(ex, "Direct stream disposal failed");
        }
    }
}
