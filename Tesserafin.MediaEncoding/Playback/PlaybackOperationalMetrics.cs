using System;
using System.Collections.Generic;
using System.Threading;
using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// PR115d: thread-safe aggregate counters for the v2 live streaming path's serve-v2-or-fallback
/// decision (<c>MediaInfoHelper.ResolveServedStreamInfo</c>) and, best-effort, for ffmpeg transcode
/// start outcomes on v2-served sessions - the operational gate that must exist before
/// <see cref="Tesserafin.Model.Configuration.PlaybackShadowOptions.CanaryPercentage"/> is opened above 0
/// in production. Mirrors <c>ShadowMetrics</c>'s conventions
/// deliberately: one singleton shared across every live request, <see cref="Interlocked"/> rather
/// than a lock (this sits on the same hot playback-decision/streaming path ShadowMetrics already
/// avoids allocating or blocking on), and a separate immutable <see cref="PlaybackOperationalMetricsSnapshot"/>
/// for readers (tests, the admin diagnostics endpoint, <c>PlaybackStopThresholdGuard</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Served/fallback counters.</b> Every call <c>MediaInfoHelper.ResolveServedStreamInfo</c> makes
/// funnels through exactly two recording methods - <see cref="RecordServed"/> on the one success
/// path, <see cref="RecordFallback"/> (parameterized by the typed
/// <see cref="PlaybackLiveFallbackReason"/>) on every failure path - so these counters can never
/// drift out of sync with what <see cref="IPlaybackLiveWiringDiagnosticsStore"/> retains per session;
/// they are the cumulative, cross-session view of the same decision that store retains per-session.
/// </para>
/// <para>
/// <b>Transcode start counters - what "start failure" means here.</b> <c>PlaybackSessionManager</c>
/// observes <see cref="ITranscodeManager.TranscodingJobStarted"/>/<see cref="ITranscodeManager.TranscodingJobEnded"/>
/// (already wired since PR113b) and calls <see cref="RecordTranscodeStart"/> exactly once per
/// transcoding job whose play session correlates to a currently-tracked, v2-served session: a
/// "start failure" is a job that ended (<c>TranscodingJobEnded</c>) without ever having raised
/// <c>TranscodingJobStarted</c> first - ffmpeg's <c>Process.Start()</c> itself never succeeded, per
/// that event's own contract ("never for a job that failed to start"). A job that started
/// successfully and later failed mid-stream is deliberately NOT counted here: that is a different
/// failure mode (a running ffmpeg process dying), outside PR115d's "did the v2 plan even produce
/// something ffmpeg could launch" scope, and conflating the two would make the transcode-start-failure
/// rate a much noisier, less specific signal for the stop-threshold guard to act on.
/// </para>
/// <para>
/// <b>Why the success is recorded at Started, not inferred at Ended.</b> A naive implementation would
/// record both outcomes when the job ends, by checking whether a Started was ever observed. That has
/// a directional bias: a session can be evicted (<c>PlaybackStopped</c>, the TTL sweep) strictly
/// between its job's Started and Ended events, and a job that never started at all produces no
/// playback and therefore never triggers <c>PlaybackStopped</c> in the first place - so a naive
/// Ended-time check would silently drop successes (evicted before Ended, correlation lost) far more
/// often than failures (which are not exposed to that eviction path), inflating the observed failure
/// rate upward. <c>PlaybackSessionManager</c> avoids this by recording a success immediately when
/// Started fires (the session is provably still tracked at that instant - it is the very job whose
/// launch is being reported) and only checking "was this one ever started" at Ended time, to record a
/// failure when it was not. Either half can still be silently dropped when correlation is lost before
/// EITHER event fires (a job the session manager never planned) - a best-effort signal, not a hard
/// guarantee - but that residual gap is symmetric, not a bias toward either outcome.
/// </para>
/// </remarks>
public sealed class PlaybackOperationalMetrics
{
    // Indexed by (int)PlaybackLiveFallbackReason.
    private readonly long[] _fallbackReasonCounts = new long[Enum.GetValues<PlaybackLiveFallbackReason>().Length];

    private long _servedByV2Count;
    private long _transcodeStartAttemptsV2;
    private long _transcodeStartFailuresV2;

    /// <summary>
    /// Gets the current served-by-v2 count via a single lock-free read - cheap enough for the
    /// stop-threshold guard to call on every live request, unlike <see cref="GetSnapshot"/> which
    /// allocates.
    /// </summary>
    public long ServedByV2Count => Interlocked.Read(ref _servedByV2Count);

    /// <summary>
    /// Gets the current v2 transcode start attempt count via a single lock-free read.
    /// </summary>
    public long TranscodeStartAttemptsV2 => Interlocked.Read(ref _transcodeStartAttemptsV2);

    /// <summary>
    /// Gets the current v2 transcode start failure count via a single lock-free read.
    /// </summary>
    public long TranscodeStartFailuresV2 => Interlocked.Read(ref _transcodeStartFailuresV2);

    /// <summary>
    /// Records a live request actually served from the v2 execution plan.
    /// </summary>
    public void RecordServed() => Interlocked.Increment(ref _servedByV2Count);

    /// <summary>
    /// Records a live request that fell back to legacy, classified by <paramref name="reason"/>.
    /// </summary>
    /// <param name="reason">Why legacy was served instead of v2.</param>
    public void RecordFallback(PlaybackLiveFallbackReason reason) => Interlocked.Increment(ref _fallbackReasonCounts[(int)reason]);

    /// <summary>
    /// Records one ffmpeg transcode start outcome for a v2-served session - see this type's remarks
    /// for exactly what "failed" means. A no-op call site check (only call for v2-served sessions) is
    /// the caller's responsibility; this method itself does not filter by provenance.
    /// </summary>
    /// <param name="failed"><see langword="true"/> when the job ended without ever starting.</param>
    public void RecordTranscodeStart(bool failed)
    {
        Interlocked.Increment(ref _transcodeStartAttemptsV2);
        if (failed)
        {
            Interlocked.Increment(ref _transcodeStartFailuresV2);
        }
    }

    /// <summary>
    /// Gets the current count for one fallback reason via a single lock-free read - see
    /// <see cref="ServedByV2Count"/>'s remarks on why this is kept separate from <see cref="GetSnapshot"/>.
    /// </summary>
    /// <param name="reason">The reason to read the count for.</param>
    /// <returns>The current count for <paramref name="reason"/>.</returns>
    public long FallbackReasonCount(PlaybackLiveFallbackReason reason) => Interlocked.Read(ref _fallbackReasonCounts[(int)reason]);

    /// <summary>
    /// Takes an immutable snapshot of every counter - for tests and the admin diagnostics endpoint.
    /// Allocates (a dictionary per call), unlike the individual counter reads above - not intended
    /// for the hot per-request guard check.
    /// </summary>
    /// <returns>The current counters.</returns>
    public PlaybackOperationalMetricsSnapshot GetSnapshot()
    {
        var fallbackCounts = new Dictionary<PlaybackLiveFallbackReason, long>();
        foreach (var reason in Enum.GetValues<PlaybackLiveFallbackReason>())
        {
            fallbackCounts[reason] = FallbackReasonCount(reason);
        }

        return new PlaybackOperationalMetricsSnapshot(
            ServedByV2Count,
            fallbackCounts,
            TranscodeStartAttemptsV2,
            TranscodeStartFailuresV2);
    }
}
