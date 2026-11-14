using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Playback.Execution;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// PR115c: why <see cref="IPlaybackExecutionPlanResolver.Resolve(PlaybackSessionId, out PlaybackExecutionPlan?)"/>
/// did or did not produce a plan. <see cref="IPlaybackExecutionPlanResolver.Resolve(PlaybackSessionId)"/>
/// deliberately keeps collapsing <see cref="NoAuthoritativeRecord"/> and <see cref="PlanNotExecutable"/>
/// into a uniform <see langword="null"/> (PR115a's own documented design - both are ordinary, expected
/// outcomes for a dormant/non-authoritative session) - this richer overload exists only because
/// PR115c's live-wiring diagnostics (<see cref="PlaybackLiveFallbackReason"/>) need to tell the two
/// apart to be useful to an operator, without bypassing the resolver to read <see cref="IV2PlanStore"/>
/// directly.
/// </summary>
public enum PlaybackExecutionPlanResolution
{
    /// <summary>
    /// No authoritative <see cref="V2PlanRecord"/> is retained for this session at all - not in the
    /// canary cohort, effective engine mode is Legacy/Shadow, or nothing has been attached yet.
    /// </summary>
    NoAuthoritativeRecord,

    /// <summary>
    /// A record is retained (v2 WAS authoritative for this session), but its decision produced no
    /// executable plan - <see cref="V2PlanRecord.ExecutionPlan"/> is itself <see langword="null"/>.
    /// </summary>
    PlanNotExecutable,

    /// <summary>
    /// A record is retained and its execution plan is available.
    /// </summary>
    Resolved,
}

/// <summary>
/// Resolves the v2 <see cref="PlaybackExecutionPlan"/> for a live <see cref="PlaybackSessionId"/>,
/// when one is available (PR114a). Dormant by design: no streaming endpoint consumes this yet -
/// legacy stays the live source of truth until the PR115 canary cutover. This interface exists so the
/// entry point a canary can later switch on is already built, wired into DI, and tested, rather than
/// invented under pressure during that cutover.
/// </summary>
/// <remarks>
/// PR115a: resolves from the AUTHORITATIVE <see cref="V2PlanRecord"/> <see cref="IV2PlanStore"/>
/// retains for a canary/v2 session - never from <c>IShadowDiagnosticsStore</c>, which only
/// ever holds observability data and is free to be disabled or evicted without affecting this
/// resolver. The retained record's <see cref="V2PlanRecord.ExecutionPlan"/> is already built at
/// publish time by <c>ShadowPlaybackSessionPlanner</c>/<c>PlaybackSessionManager</c>, so this
/// resolver performs a plain lookup - no <c>PlaybackExecutionPlanBuilder.TryBuild</c> call here.
/// </remarks>
public interface IPlaybackExecutionPlanResolver
{
    /// <summary>
    /// Resolves the v2 execution plan for a session, if one is available.
    /// </summary>
    /// <param name="id">The session to resolve a plan for.</param>
    /// <returns>
    /// The session's <see cref="PlaybackExecutionPlan"/>, or <see langword="null"/> when the session
    /// has no authoritative v2 record (legacy/shadow-only session, out-of-cohort, engine failed), or
    /// when one is retained but its decision was refused by the builder at publish time (the
    /// retained record's <see cref="V2PlanRecord.ExecutionPlan"/> is itself <see langword="null"/>) -
    /// never throws for either case, since both are ordinary, expected outcomes for a dormant
    /// resolver.
    /// </returns>
    PlaybackExecutionPlan? Resolve(PlaybackSessionId id);

    /// <summary>
    /// Resolves the v2 execution plan for a session, distinguishing WHY no plan was produced
    /// (<see cref="PlaybackExecutionPlanResolution"/>) - PR115c's live-wiring diagnostics need this
    /// distinction; <see cref="Resolve(PlaybackSessionId)"/> keeps collapsing it to a uniform
    /// <see langword="null"/> for callers that only care about the plan itself.
    /// </summary>
    /// <param name="id">The session to resolve a plan for.</param>
    /// <param name="plan">The resolved plan, or <see langword="null"/> when the resolution is not <see cref="PlaybackExecutionPlanResolution.Resolved"/>.</param>
    /// <returns>Why a plan was or was not resolved.</returns>
    PlaybackExecutionPlanResolution Resolve(PlaybackSessionId id, out PlaybackExecutionPlan? plan);
}
