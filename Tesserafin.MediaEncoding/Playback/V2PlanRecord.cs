using System;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Execution;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// The authoritative v2 outcome retained for a canary/v2 session (PR115a): the decision the engine
/// made and, when the decision was executable, the execution plan built from it. A record is only
/// ever published for a session the effective <c>PlaybackEngineMode</c> (removed, tesserafin#129 step 7)
/// made v2 authoritative for (canary cohort member, or full v2 mode) - its presence in
/// <see cref="IV2PlanStore"/> IS the statement of authority. This is deliberately independent of
/// <c>ShadowDiagnosticRecord</c>/<c>IShadowDiagnosticsStore</c>: diagnostics are an
/// observability projection that may be disabled, sampled out, or evicted without ever affecting
/// which engine owns a live session's plan.
/// </summary>
/// <param name="Decision">The v2 decision the engine made for this session's planning call.</param>
/// <param name="ExecutionPlan">
/// The execution plan built from <paramref name="Decision"/>, or <see langword="null"/> when
/// <see cref="PlaybackExecutionPlanBuilder"/> refused it (for example a <c>NotViable</c> decision).
/// A retained record with a <see langword="null"/> plan means "v2 was authoritative for this
/// session but produced nothing executable" - the PR115c live path falls back to legacy for it.
/// </param>
/// <param name="CapturedAt">When the decision was made.</param>
public sealed record V2PlanRecord(
    PlaybackDecision Decision,
    PlaybackExecutionPlan? ExecutionPlan,
    DateTimeOffset CapturedAt);
