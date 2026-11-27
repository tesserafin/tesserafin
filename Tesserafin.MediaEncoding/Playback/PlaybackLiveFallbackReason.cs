using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// PR115c: why a live playback request fell back to the legacy <c>StreamInfo</c> instead of being
/// served from the v2 <see cref="Tesserafin.Playback.Execution.PlaybackExecutionPlan"/>, for a session
/// this server otherwise had an opportunity to serve from v2. Every value here is meant to be
/// observable (logged and retained for the admin diagnostics surface, mirroring the PR113/PR114
/// shadow diagnostics pattern) - a fallback must never be silent.
/// </summary>
public enum PlaybackLiveFallbackReason
{
    /// <summary>
    /// <c>IPlaybackExecutionPlanResolver.Resolve</c> (removed, tesserafin#129 step 7) found no
    /// authoritative <see cref="V2PlanRecord"/> for this session at all: the session is outside the
    /// canary cohort, the effective <see cref="Tesserafin.Model.Configuration.PlaybackEngineMode"/> is
    /// <c>Legacy</c>/<c>Shadow</c>, or nothing was ever attached (session created before v2 became
    /// authoritative for it).
    /// </summary>
    NoAuthoritativeRecord,

    /// <summary>
    /// A record was retained and v2 WAS authoritative for this session, but its decision produced no
    /// executable plan (for example a <c>NotViable</c> decision) - <see cref="V2PlanRecord.ExecutionPlan"/>
    /// itself was <see langword="null"/>.
    /// </summary>
    PlanNotExecutable,

    /// <summary>
    /// The resolved plan's <see cref="Tesserafin.Playback.Execution.PlaybackExecutionPlan.SourceId"/> did
    /// not match the id of the <c>MediaSourceInfo</c> actually being served. Never guessed or
    /// substituted - this is a hard safety rail (PR115c scope): a plan naming the wrong source must
    /// never reach a client.
    /// </summary>
    SourceIdMismatch,

    /// <summary>
    /// The source's selected video stream is Dolby Vision/HDR and its codec appears in the legacy
    /// candidate codec CSV - the class of session PR115b's design doc (docs/pr115-design-canary-execution.md,
    /// "Constat de sortie PR115b") identified as unsafe for the live v2 path until
    /// <see cref="Tesserafin.Controller.MediaEncoding.EncodingHelper.CanStreamCopyVideo"/>'s missing
    /// video-range compatibility gate is investigated. A hard, mandatory exclusion - not a policy
    /// knob.
    /// </summary>
    DolbyVisionExclusion,

    /// <summary>
    /// The operator-controlled kill switch forced legacy for this request, independent of cohort
    /// membership or plan executability - the effective <see cref="Tesserafin.Model.Configuration.PlaybackEngineMode"/>
    /// read at request time does not authorize serving v2 live. Takes effect on the very next
    /// request, no restart required, since it reads live server configuration on every call.
    /// </summary>
    KillSwitch,

    /// <summary>
    /// <see cref="Tesserafin.Playback.Dlna.PlaybackExecutionPlanAdapter.ToStreamInfo"/> threw while
    /// converting an otherwise-eligible plan. Never allowed to fail the request or affect the legacy
    /// path - caught, logged, and downgraded to a legacy-served response, the same "v2 must never
    /// break the live path" discipline <c>ShadowPlaybackSessionPlanner</c> already applies to the
    /// shadow run.
    /// </summary>
    AdapterError,

    /// <summary>
    /// PR115d: the operational stop-threshold guard (<c>PlaybackStopThresholdGuard</c>) found that
    /// the v2 live path's own observed error rate - <see cref="AdapterError"/> rate, or the
    /// transcode-start failure rate for v2-served sessions - crossed an operator-configured
    /// threshold (<see cref="Tesserafin.Model.Configuration.PlaybackStopThresholdOptions"/>) and forced
    /// legacy for this request, same observable effect as <see cref="KillSwitch"/> but triggered
    /// automatically by observed error signals rather than by an operator's own hand.
    /// </summary>
    StopThresholdTripped,
}
