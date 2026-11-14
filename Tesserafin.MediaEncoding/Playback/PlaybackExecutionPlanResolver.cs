using System;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Playback.Execution;

namespace Tesserafin.MediaEncoding.Playback;

/// <inheritdoc cref="IPlaybackExecutionPlanResolver"/>
public sealed class PlaybackExecutionPlanResolver : IPlaybackExecutionPlanResolver
{
    private readonly IV2PlanStore _v2PlanStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackExecutionPlanResolver"/> class.
    /// </summary>
    /// <param name="v2PlanStore">
    /// PR115a: the store to read a session's retained AUTHORITATIVE <see cref="V2PlanRecord"/> from -
    /// the same singleton <see cref="PlaybackSessionManager"/> attaches records to. This resolver no
    /// longer reads <c>IShadowDiagnosticsStore</c>: that store only ever holds observability
    /// data, never an authoritative decision.
    /// </param>
    public PlaybackExecutionPlanResolver(IV2PlanStore v2PlanStore)
    {
        ArgumentNullException.ThrowIfNull(v2PlanStore);
        _v2PlanStore = v2PlanStore;
    }

    /// <inheritdoc/>
    public PlaybackExecutionPlan? Resolve(PlaybackSessionId id)
    {
        // PR115a: the resolver now reads the session's AUTHORITATIVE v2 plan, never the shadow
        // diagnostics store. The plan is already built at publish time by the planner (only when
        // v2 was authoritative for that call), so there is no TryBuild here anymore - just a lookup.
        // Returns null uniformly whether no record is retained at all, or one is retained but its
        // decision was refused by the builder at publish time (ExecutionPlan null on the record).
        // PR115c: implemented in terms of the richer overload below - same lookup, same result for
        // this signature, zero behavior change for existing callers/tests.
        Resolve(id, out var plan);
        return plan;
    }

    /// <inheritdoc/>
    public PlaybackExecutionPlanResolution Resolve(PlaybackSessionId id, out PlaybackExecutionPlan? plan)
    {
        if (!_v2PlanStore.TryGet(id, out var record) || record is null)
        {
            plan = null;
            return PlaybackExecutionPlanResolution.NoAuthoritativeRecord;
        }

        plan = record.ExecutionPlan;
        return plan is not null ? PlaybackExecutionPlanResolution.Resolved : PlaybackExecutionPlanResolution.PlanNotExecutable;
    }
}
