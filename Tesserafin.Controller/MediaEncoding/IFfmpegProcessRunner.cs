using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Creates managed transcode processes and runs finite probes and file extractions behind one
/// process factory. Transcode ownership is registered by the caller before launch. Finite commands
/// drain both output streams concurrently; managed transcodes redirect only stderr, whose drain
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
    /// Runs a finite file-producing command with closed stdin and concurrent output drains.
    /// Only the final 65,536 characters of each diagnostic stream are retained.
    /// </summary>
    /// <param name="command">The immutable invocation and child environment.</param>
    /// <param name="timeout">Time limit, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="cancellationToken">Cancellation kills and waits for the child before throwing.</param>
    /// <returns>The exit code, bounded diagnostics and timeout outcome.</returns>
    Task<FfmpegProcessResult> RunFileAsync(FfmpegCommand command, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Runs an image command, extending its deadline only when new output is observed.</summary>
    /// <param name="command">The immutable invocation and child environment.</param>
    /// <param name="timeout">Maximum wait without new output, or total time when no observer is supplied.</param>
    /// <param name="hasProgress">An optional observer returning true only for new nonempty image files.</param>
    /// <param name="priority">Requested child priority; inability to set it is diagnostic only.</param>
    /// <param name="cancellationToken">Cancellation kills and waits for the child before throwing.</param>
    /// <returns>Exit status and bounded, fully drained diagnostics.</returns>
    Task<FfmpegProcessResult> RunImageAsync(FfmpegCommand command, TimeSpan timeout, Func<bool>? hasProgress, ProcessPriorityClass priority, CancellationToken cancellationToken);

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
