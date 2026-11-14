using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// PR115d: an immutable point-in-time view of <see cref="PlaybackOperationalMetrics"/>' counters,
/// suitable for the admin diagnostics endpoint or a single structured log line - mirrors
/// <c>ShadowMetricsSnapshot</c>'s shape and role.
/// </summary>
/// <param name="ServedByV2Count">The number of live requests actually served from the v2 execution plan.</param>
/// <param name="FallbackReasonCounts">Count of fallback-to-legacy requests, per typed <see cref="PlaybackLiveFallbackReason"/>.</param>
/// <param name="TranscodeStartAttemptsV2">The number of ffmpeg transcode starts observed for v2-served sessions.</param>
/// <param name="TranscodeStartFailuresV2">Of <paramref name="TranscodeStartAttemptsV2"/>, how many never actually started - see <see cref="PlaybackOperationalMetrics"/>'s remarks for the precise definition.</param>
public sealed record PlaybackOperationalMetricsSnapshot(
    long ServedByV2Count,
    IReadOnlyDictionary<PlaybackLiveFallbackReason, long> FallbackReasonCounts,
    long TranscodeStartAttemptsV2,
    long TranscodeStartFailuresV2)
{
    /// <summary>
    /// Gets the total number of live requests that fell back to legacy, across every reason - the
    /// sum of <see cref="FallbackReasonCounts"/>.
    /// </summary>
    public long ServedByLegacyCount => FallbackReasonCounts.Values.Sum();

    /// <summary>
    /// Gets the total number of requests this decision has been made for at all: served-by-v2 plus
    /// every fallback reason.
    /// </summary>
    public long TotalDecisions => ServedByV2Count + ServedByLegacyCount;

    /// <summary>
    /// Gets the number of requests that reached <see cref="Tesserafin.Playback.Dlna.PlaybackExecutionPlanAdapter.ToStreamInfo"/>
    /// at all - served-by-v2 plus <see cref="PlaybackLiveFallbackReason.AdapterError"/> - the
    /// denominator <see cref="AdapterErrorRate"/> is computed over. Every other fallback reason
    /// (kill switch, no plan, source id mismatch, Dolby Vision exclusion, the stop threshold itself)
    /// never reached the adapter, so including them would dilute the rate with requests that were
    /// never at risk of an adapter error in the first place.
    /// </summary>
    public long AdapterAttempts => ServedByV2Count + FallbackReasonCounts.GetValueOrDefault(PlaybackLiveFallbackReason.AdapterError);

    /// <summary>
    /// Gets the fraction of <see cref="AdapterAttempts"/> that ended in
    /// <see cref="PlaybackLiveFallbackReason.AdapterError"/>, or 0.0 when there have been no attempts
    /// yet - matches <see cref="Tesserafin.Model.Configuration.PlaybackStopThresholdOptions.AdapterErrorRateThreshold"/>'s
    /// own definition of the rate.
    /// </summary>
    public double AdapterErrorRate => AdapterAttempts == 0
        ? 0.0
        : (double)FallbackReasonCounts.GetValueOrDefault(PlaybackLiveFallbackReason.AdapterError) / AdapterAttempts;

    /// <summary>
    /// Gets the fraction of <see cref="TranscodeStartAttemptsV2"/> that failed to start, or 0.0 when
    /// there have been no attempts yet.
    /// </summary>
    public double TranscodeStartFailureRate => TranscodeStartAttemptsV2 == 0
        ? 0.0
        : (double)TranscodeStartFailuresV2 / TranscodeStartAttemptsV2;

    /// <summary>
    /// Renders the snapshot as a single human-readable line.
    /// </summary>
    /// <returns>A single-line, human-readable summary of every counter.</returns>
    public string ToSummaryString()
    {
        var perReason = string.Join(
            ", ",
            FallbackReasonCounts.Select(kvp => FormattableString.Invariant($"{kvp.Key}={kvp.Value}")));

        var part1 = FormattableString.Invariant($"servedByV2={ServedByV2Count}, servedByLegacy={ServedByLegacyCount} ({perReason})");
        var part2 = FormattableString.Invariant($"adapterErrorRate={AdapterErrorRate.ToString("P1", CultureInfo.InvariantCulture)} ({AdapterAttempts} attempts)");
        var part3 = FormattableString.Invariant($"transcodeStartFailureRate={TranscodeStartFailureRate.ToString("P1", CultureInfo.InvariantCulture)} ({TranscodeStartAttemptsV2} attempts)");

        return $"{part1}, {part2}, {part3}";
    }
}
