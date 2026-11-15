using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Creates managed transcode processes and runs short-lived ffmpeg/ffprobe probes behind one
/// process factory. Transcode ownership is registered by the caller before launch. Probes drain
/// both stdout and stderr concurrently; managed transcodes redirect only stderr, whose drain
/// belongs to the caller. Every redirected output pipe must have a reader.
/// </summary>
public interface IFfmpegProcessRunner
{
    /// <summary>Creates an unstarted child configured for managed transcoding.</summary>
    /// <param name="command">The immutable invocation and child environment.</param>
    /// <returns>A process with stderr/stdin redirected and stdout unredirected.</returns>
    Process CreateTranscodeProcess(FfmpegCommand command);

    /// <summary>Starts a registered child under the caller's ownership lock.</summary>
    /// <param name="process">The unstarted child.</param>
    /// <param name="cancellationToken">Cancellation checked immediately before launch.</param>
    void StartTranscode(Process process, CancellationToken cancellationToken);

    /// <summary>
    /// Starts <paramref name="command"/>, drains stdout and stderr concurrently, and waits for
    /// exit or <paramref name="timeout"/>, whichever comes first.
    /// </summary>
    /// <param name="command">The command to run.</param>
    /// <param name="timeout">Maximum time to wait before killing the process and returning with <see cref="FfmpegProcessResult.TimedOut"/> set.</param>
    /// <param name="cancellationToken">
    /// External cancellation. Unlike <paramref name="timeout"/>, cancelling this token kills the
    /// process and throws <see cref="OperationCanceledException"/>, per standard async cancellation.
    /// </param>
    /// <param name="standardInput">Text to write to the process's standard input and close, or null to leave stdin unredirected.</param>
    /// <returns>The process outcome.</returns>
    Task<FfmpegProcessResult> RunProbeAsync(FfmpegCommand command, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null);
}
