using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Extensions;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Dlna;
using Tesserafin.Playback.Engine;
using Tesserafin.Playback.Execution;

namespace Tesserafin.Playback.Shadow;

/// <summary>
/// Decorates an <see cref="IPlaybackSessionPlanner"/> to run the v2 <see cref="IPlaybackEngine"/> in
/// shadow alongside it (PR98): legacy stays the source of truth for the plan returned to callers;
/// v2 runs only to have its decision projected, compared against the legacy plan, and logged as a
/// classified divergence. NO client-facing change: the shadow run never affects, and can never
/// throw into, the live path - every exception the v2 side might raise (including on inputs it
/// mishandles, such as a source with no streams) is caught and logged, not propagated.
/// </summary>
/// <remarks>
/// PR100 hardening: the shadow run is now opt-in (<see cref="PlaybackShadowOptions.Enabled"/>,
/// default <see langword="false"/>), samplable (<see cref="PlaybackShadowOptions.SampleRate"/>),
/// time-budgeted (<see cref="PlaybackShadowOptions.MaxExecutionMs"/>), and aggregated: individual
/// benign outcomes (equivalence, expected improvement, known v2 limitations) are no longer logged
/// one-by-one - only genuine regressions/exceptions still log individually (Warning), while
/// everything is counted in <see cref="ShadowMetrics"/> and periodically summarized in a single
/// Information log line. When disabled or sampled out, this class does zero mapping, zero
/// allocation, and zero engine work: the legacy path is untouched.
/// </remarks>
/// <remarks>
/// PR111e: the v2 shadow inputs (<see cref="ClientCapabilities"/>/<see cref="PlaybackConstraints"/>/
/// <see cref="MediaSourceSnapshot"/>s/<see cref="PlaybackRequestContext"/>) are now captured BEFORE
/// <see cref="_inner"/> runs, not after. Legacy's <c>StreamBuilder</c> mutates the shared
/// <c>MediaSourceInfo.Container</c> in place as a side effect of planning (normalizing a raw ffprobe
/// multi-value container CSV like <c>"mov,mp4,m4a,3gp,3g2,mj2"</c> down to a single value) - mapping
/// v2's inputs afterward meant v2 was silently handed legacy's already-degraded view of the source,
/// not the real one, which produced a spurious container divergence unrelated to any actual v2 bug
/// (see <c>OracleCaseFixtures.ApprovedDivergences</c> pre-PR111e history). The enabled/sampling
/// decision is made first (still zero work when disabled or sampled out); when it is a "go", the
/// pre-legacy capture runs inside its own try/catch so a mapping exception can NEVER prevent legacy
/// from running - legacy is invoked exactly once, unconditionally, immediately after the capture
/// attempt regardless of whether it succeeded. Only when capture succeeds does the post-legacy phase
/// (engine + comparison) run, against the pre-captured snapshot - there is no re-mapping after legacy.
/// <c>mappingStopwatch</c> now measures this pre-legacy capture; the total duration fed to
/// <see cref="ShadowMetrics"/>/the budget check is the SUM of the mapping/engine/comparison phase
/// durations, never a single wall-clock span across the (unmeasured, potentially slow) legacy call
/// that runs between capture and completion.
/// </remarks>
public sealed class ShadowPlaybackSessionPlanner : IPlaybackSessionPlanner
{
    private readonly IPlaybackSessionPlanner _inner;
    private readonly IPlaybackEngine _engine;
    private readonly ILogger<ShadowPlaybackSessionPlanner> _logger;
    private readonly Func<PlaybackShadowOptions> _optionsAccessor;
    private readonly IShadowDiagnosticsStore _diagnosticsStore;
    private readonly IV2PlanStore _v2PlanStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShadowPlaybackSessionPlanner"/> class with
    /// shadow mode always enabled at full sample rate and a fresh <see cref="ShadowMetrics"/>
    /// instance. Used by comparison fixtures; production
    /// wiring should use the overload that accepts an options accessor bound to live server
    /// configuration.
    /// </summary>
    /// <param name="inner">The legacy planner. Its result is the source of truth, returned unchanged.</param>
    /// <param name="engine">The v2 decision engine, run only in shadow.</param>
    /// <param name="logger">The logger the shadow comparison is reported through.</param>
    public ShadowPlaybackSessionPlanner(IPlaybackSessionPlanner inner, IPlaybackEngine engine, ILogger<ShadowPlaybackSessionPlanner> logger)
        : this(inner, engine, logger, static () => new PlaybackShadowOptions { Enabled = true, SampleRate = 1.0 })
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShadowPlaybackSessionPlanner"/> class.
    /// </summary>
    /// <param name="inner">The legacy planner. Its result is the source of truth, returned unchanged.</param>
    /// <param name="engine">The v2 decision engine, run only in shadow.</param>
    /// <param name="logger">The logger the shadow comparison is reported through.</param>
    /// <param name="optionsAccessor">
    /// Returns the current <see cref="PlaybackShadowOptions"/> on every call, so live server
    /// configuration changes (enable/disable, sample rate, budget) take effect without restart.
    /// </param>
    /// <param name="metrics">
    /// The aggregate metrics sink. Defaults to a private instance if not supplied; pass a shared
    /// one if the metrics need to be observed from outside (e.g. a diagnostics endpoint).
    /// </param>
    /// <param name="diagnosticsStore">
    /// PR113: where a successful shadow run publishes its <see cref="ShadowDiagnosticRecord"/> for
    /// later correlation by <see cref="PlaybackSessionManager"/>. Defaults to a no-op instance when
    /// not supplied; the shadow run then retains nothing.
    /// </param>
    /// <param name="v2PlanStore">
    /// PR115a: where an AUTHORITATIVE v2 run (canary cohort member, or full v2 mode) publishes its
    /// <see cref="V2PlanRecord"/> for later correlation by <see cref="PlaybackSessionManager"/> -
    /// deliberately a separate channel from <paramref name="diagnosticsStore"/>, which stays a pure
    /// observability projection. Defaults to a no-op instance when not supplied: v2 is then never
    /// authoritative, matching every pre-PR115a call site.
    /// </param>
    public ShadowPlaybackSessionPlanner(
        IPlaybackSessionPlanner inner,
        IPlaybackEngine engine,
        ILogger<ShadowPlaybackSessionPlanner> logger,
        Func<PlaybackShadowOptions> optionsAccessor,
        ShadowMetrics? metrics = null,
        IShadowDiagnosticsStore? diagnosticsStore = null,
        IV2PlanStore? v2PlanStore = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(optionsAccessor);

        _inner = inner;
        _engine = engine;
        _logger = logger;
        _optionsAccessor = optionsAccessor;
        Metrics = metrics ?? new ShadowMetrics();
        _diagnosticsStore = diagnosticsStore ?? NoOpShadowDiagnosticsStore.Instance;
        _v2PlanStore = v2PlanStore ?? NoOpV2PlanStore.Instance;
    }

    /// <summary>
    /// Gets the aggregate shadow metrics accumulated by this instance.
    /// </summary>
    public ShadowMetrics Metrics { get; }

    /// <inheritdoc/>
    public PlaybackPlan? PlanAudio(MediaOptions options)
    {
        var prepared = PrepareShadow(PlaybackMediaKind.Audio, options);
        var plan = _inner.PlanAudio(options);
        CompleteShadow(prepared, plan);
        return plan;
    }

    /// <inheritdoc/>
    public PlaybackPlan? PlanVideo(MediaOptions options)
    {
        var prepared = PrepareShadow(PlaybackMediaKind.Video, options);
        var plan = _inner.PlanVideo(options);
        CompleteShadow(prepared, plan);
        return plan;
    }

    /// <summary>
    /// PR111e: the pre-legacy half of the shadow run. Strictly short-circuits (returning
    /// <see langword="null"/>) when shadow mode is disabled or this call is sampled out: no mapping,
    /// no allocation beyond reading the current options - identical to the pre-PR111e fast path,
    /// just evaluated before <see cref="_inner"/> runs instead of after. When shadow mode is live for
    /// this call, captures the v2 engine's inputs from <paramref name="options"/> - the object legacy
    /// is about to mutate - strictly before legacy gets a chance to touch it. A mapping exception here
    /// is caught, logged, and counted exactly like a post-legacy shadow exception used to be (see
    /// <see cref="CompleteShadow"/>); it also returns <see langword="null"/>, so <see cref="CompleteShadow"/>
    /// does nothing further for this call - but the caller always invokes <see cref="_inner"/>
    /// regardless of what this method returns, so legacy planning is never affected.
    /// </summary>
    private PreparedShadowContext? PrepareShadow(PlaybackMediaKind kind, MediaOptions options)
    {
        var shadowOptions = _optionsAccessor();
        var mode = shadowOptions.GetEffectiveMode();
        if (mode == PlaybackEngineMode.Legacy)
        {
            options.LegacyPlanIsFallbackOnly = false;
            return null;
        }

        // PR115a: whether this planning call's v2 outcome is execution authority (published to
        // IV2PlanStore) or pure observability. Canary cohort membership is a deterministic
        // user/device hash - the same pair always gets the same engine, never a per-request draw.
        var authoritative = mode switch
        {
            PlaybackEngineMode.V2 => true,
            PlaybackEngineMode.Canary => CanaryCohort.IsInCohort(options.UserId, options.DeviceId, shadowOptions.CanaryPercentage),
            _ => false,
        };
        options.LegacyPlanIsFallbackOnly = authoritative;

        // Sampling only ever gates pure observability runs: an authoritative run must happen for
        // every planning call it is authoritative for, or the session would silently flip between
        // engines depending on a random draw.
        if (!authoritative && shadowOptions.SampleRate < 1.0 && Random.Shared.NextDouble() >= shadowOptions.SampleRate)
        {
            return null;
        }

        var mappingStopwatch = Stopwatch.StartNew();
        try
        {
            var capabilities = DlnaPlaybackAdapter.ToCapabilities(options.Profile);
            var constraints = DlnaPlaybackAdapter.ToConstraints(options);
            var sources = options.MediaSources.Select(DlnaPlaybackAdapter.ToSnapshot).ToList();
            var mediaKind = kind == PlaybackMediaKind.Video ? MediaKind.Video : MediaKind.Audio;
            // PR113b: options.UserId now carries the real requesting user through from the calling
            // controller (PlaybackSessionsController/MediaInfoHelper) - previously always
            // Guid.Empty here, which meant every retained diagnostic's RequestContext.UserId was a
            // lie. Callers that never set it (pre-PR113b call sites, most test fixtures) still get
            // Guid.Empty, its default value, so this is not a behavioral change for them.
            var context = DlnaPlaybackAdapter.ToContext(options.ItemId, options.UserId, options.MediaSourceId, mediaKind, PlaybackEngine.EngineVersion);
            mappingStopwatch.Stop();

            return new PreparedShadowContext(kind, options, shadowOptions, mappingStopwatch.Elapsed, capabilities, constraints, sources, context, authoritative);
        }
#pragma warning disable CA1031 // Do not catch general exception types - shadow-mode safety requires it: v2 must never affect the live path.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            mappingStopwatch.Stop();
            var budgetExceeded = mappingStopwatch.Elapsed.TotalMilliseconds > shadowOptions.MaxExecutionMs;
            var summary = Metrics.RecordException(mappingStopwatch.Elapsed, budgetExceeded);

            _logger.LogWarning(
                ex,
                "Shadow v2 playback input mapping threw for item {ItemId} ({Kind}) before legacy planning ran; legacy plan is unaffected.",
                options.ItemId,
                kind);
            if (budgetExceeded)
            {
                _logger.LogWarning(
                    "Shadow v2 playback input mapping for item {ItemId} ({Kind}) exceeded the {BudgetMs}ms budget before throwing: took {ElapsedMs}ms.",
                    options.ItemId,
                    kind,
                    shadowOptions.MaxExecutionMs,
                    mappingStopwatch.Elapsed.TotalMilliseconds);
            }

            LogPeriodicSummaryIfAny(summary);
            return null;
        }
    }

    /// <summary>
    /// PR111e: the post-legacy half of the shadow run, invoked unconditionally after
    /// <see cref="_inner"/> has returned <paramref name="plan"/>. Does nothing when
    /// <paramref name="prepared"/> is <see langword="null"/> (shadow mode was disabled/sampled out,
    /// or the pre-legacy mapping already failed and was already logged/counted by
    /// <see cref="PrepareShadow"/>) - the zero-work guarantee when shadow mode isn't live for this
    /// call. Otherwise runs the v2 engine and the comparison against the pre-captured snapshot,
    /// swallowing every exception the v2 side might raise so the live path is never affected.
    /// </summary>
    private void CompleteShadow(PreparedShadowContext? prepared, PlaybackPlan? plan)
    {
        if (prepared is null)
        {
            return;
        }

        var postLegacyStopwatch = Stopwatch.StartNew();
        try
        {
            RunShadow(prepared, plan, postLegacyStopwatch);
        }
#pragma warning disable CA1031 // Do not catch general exception types - shadow-mode safety requires it: v2 must never affect the live path.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            postLegacyStopwatch.Stop();
            var totalElapsed = prepared.MappingElapsed + postLegacyStopwatch.Elapsed;
            var budgetExceeded = totalElapsed.TotalMilliseconds > prepared.ShadowOptions.MaxExecutionMs;
            var summary = Metrics.RecordException(totalElapsed, budgetExceeded);

            _logger.LogWarning(
                ex,
                "Shadow v2 playback decision threw for item {ItemId} ({Kind}) after legacy planning ran; legacy plan is unaffected.",
                prepared.Options.ItemId,
                prepared.Kind);
            if (budgetExceeded)
            {
                _logger.LogWarning(
                    "Shadow v2 playback decision for item {ItemId} ({Kind}) exceeded the {BudgetMs}ms budget before throwing: took {ElapsedMs}ms.",
                    prepared.Options.ItemId,
                    prepared.Kind,
                    prepared.ShadowOptions.MaxExecutionMs,
                    totalElapsed.TotalMilliseconds);
            }

            LogPeriodicSummaryIfAny(summary);
        }
    }

    private void RunShadow(PreparedShadowContext prepared, PlaybackPlan? plan, Stopwatch postLegacyStopwatch)
    {
        var engineStopwatch = Stopwatch.StartNew();
        var decision = _engine.Decide(prepared.Context, prepared.Capabilities, prepared.Sources, prepared.Constraints);
        engineStopwatch.Stop();

        // PR115a: authority is published immediately after the engine decides, strictly BEFORE any
        // observability work (projection, comparison, metrics, diagnostics retention) - a failure
        // anywhere in that machinery must be able to lose a diagnostic, never a canary session's
        // plan. TryBuild refusing (for example NotViable) still publishes, with a null plan: "v2
        // was authoritative here but produced nothing executable" is exactly what the PR115c live
        // path needs to see to fall back to legacy for this session.
        if (prepared.Authoritative)
        {
            _v2PlanStore.Publish(new V2PlanRecord(
                decision,
                PlaybackExecutionPlanBuilder.TryBuild(decision, out var executionPlan, out _) ? executionPlan : null,
                DateTimeOffset.UtcNow));
        }

        var comparisonStopwatch = Stopwatch.StartNew();
        var legacyVector = LegacyDecisionProjector.Project(plan);
        var v2Vector = V2DecisionProjector.Project(decision);
        var divergence = ShadowComparer.Compare(legacyVector, v2Vector);
        comparisonStopwatch.Stop();

        postLegacyStopwatch.Stop();

        // PR111e: the budget/metrics duration is the SUM of the measured phases (mapping, captured
        // pre-legacy by PrepareShadow, plus this post-legacy engine+comparison span) - never a single
        // stopwatch spanning the gap between them, which would silently fold legacy's own (unmeasured,
        // potentially slow) planning time into what is supposed to be a shadow-only cost.
        var totalElapsed = prepared.MappingElapsed + postLegacyStopwatch.Elapsed;
        var budgetExceeded = totalElapsed.TotalMilliseconds > prepared.ShadowOptions.MaxExecutionMs;
        var summary = Metrics.RecordExecution(divergence.Class, totalElapsed, budgetExceeded);

        if (budgetExceeded)
        {
            _logger.LogWarning(
                "Shadow v2 playback decision for item {ItemId} ({Kind}) exceeded the {BudgetMs}ms budget: mapping={MappingMs}ms engine={EngineMs}ms comparison={ComparisonMs}ms total={TotalMs}ms.",
                prepared.Options.ItemId,
                prepared.Kind,
                prepared.ShadowOptions.MaxExecutionMs,
                prepared.MappingElapsed.TotalMilliseconds,
                engineStopwatch.Elapsed.TotalMilliseconds,
                comparisonStopwatch.Elapsed.TotalMilliseconds,
                totalElapsed.TotalMilliseconds);
        }

        if (divergence.Class is DivergenceClass.PotentialRegression or DivergenceClass.Unexplained)
        {
            _logger.LogWarning(
                "Shadow v2 decision [{Class}] for item {ItemId} ({Kind}): {Summary}",
                divergence.Class,
                prepared.Options.ItemId,
                prepared.Kind,
                divergence.Summary.ToSingleLogLine());
        }

        // Equivalent / ExpectedImprovement / KnownV2Limitation are benign: no per-execution log
        // (that was the PR98 noise source). They are still counted above and surface via the
        // periodic aggregate summary below.
        LogPeriodicSummaryIfAny(summary);

        // PR113: publish for retention last, strictly after totalElapsed has already been computed and
        // every timing/logging concern above has run - the record allocation and AsyncLocal write
        // must never perturb the measured shadow duration that feeds Metrics.RecordExecution. A
        // shadow run that reached this point always has a record to offer, even one that exceeded
        // its time budget. PlaybackSessionManager (the only synchronous caller of Plan(), which this
        // call is nested inside) reads this back post-hoc once it has minted/known the real session
        // id; the no-op default store simply drops it.
        // Issue #75 (Option 1): the closed contract-mapping diagnostic, computed HERE and nowhere
        // else. Three properties follow from that placement, and all three are required:
        //   - it is behind the EXISTING shadow gate: RunShadow is only reached when the effective
        //     mode is not Legacy and this call was not sampled out, so it is off by default;
        //   - it is strictly AFTER the shadow publication point's timing/metrics work, so it cannot
        //     perturb the measured shadow duration, and inside CompleteShadow's catch-all, so it can
        //     never reach the live path;
        //   - a request rejected earlier (validation, unknown item, no viable plan) never gets here,
        //     so no diagnostic is ever created for one.
        // Returns null on the legacy MediaInfoHelper path, which supplies no ShadowCaptureInputs.
        // Issue #75 slice 75b: the structural scan the Tesserafin.Api resource filter captured into this
        // same ambient scope (behind the same shadow gate + sampling) rides through CapturedInputs
        // and is folded into the diagnostic here - null whenever the request was not scanned.
        var contractMapping = ContractMappingDiagnosticFactory.Create(
            _diagnosticsStore.CapturedInputs?.DeclaredCapabilities,
            prepared.Capabilities,
            _diagnosticsStore.CapturedInputs?.PayloadSizeBytes,
            _diagnosticsStore.CapturedInputs?.StructuralScan);

        _diagnosticsStore.Publish(new ShadowDiagnosticRecord(
            decision,
            legacyVector,
            divergence,
            prepared.Context,
            prepared.Capabilities,
            prepared.Sources,
            prepared.Constraints,
            prepared.Kind,
            DateTimeOffset.UtcNow,
            contractMapping));
    }

    private void LogPeriodicSummaryIfAny(ShadowMetricsSnapshot? summary)
    {
        if (summary is null)
        {
            return;
        }

        _logger.LogInformation("Shadow v2 playback metrics summary: {Summary}", summary.ToSummaryString());
    }

    /// <summary>
    /// PR111e: the v2 shadow inputs captured before <see cref="_inner"/> runs, plus the shadow
    /// options and elapsed mapping time this call was decided/measured against - carried from
    /// <see cref="PrepareShadow"/> to <see cref="CompleteShadow"/> across the legacy call in between.
    /// </summary>
    private sealed record PreparedShadowContext(
        PlaybackMediaKind Kind,
        MediaOptions Options,
        PlaybackShadowOptions ShadowOptions,
        TimeSpan MappingElapsed,
        ClientCapabilities Capabilities,
        PlaybackConstraints Constraints,
        IReadOnlyList<MediaSourceSnapshot> Sources,
        PlaybackRequestContext Context,
        bool Authoritative);
}
