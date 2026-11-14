using System;
using System.Collections.Generic;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Playback.Contract.Diagnostics;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Playback.Shadow;

/// <summary>
/// Everything a shadow run (<see cref="ShadowPlaybackSessionPlanner"/>, PR98) computed for a single
/// planning call, retained so the admin diagnostics surface (docs/pr92-design-playback-api-and-diagnostics.md
/// §4.3, PR113) can serve a filtered detail without re-running anything. Captured only when shadow
/// mode actually ran (enabled and not sampled out) - the common case (shadow off by default) never
/// allocates or retains one of these.
/// </summary>
/// <param name="Decision">The v2 engine's decision for this call.</param>
/// <param name="LegacyVector">
/// The legacy plan projected into the shared comparison vocabulary. Retained (rather than just
/// <see cref="Divergence"/>) because it is the only source for the method/reason detail the
/// diagnostic's <c>Comparison</c> exposes - <see cref="ShadowDivergence"/> only carries whether/how
/// the two sides differ, not the legacy side's own values.
/// </param>
/// <param name="Divergence">The classified legacy-vs-v2 comparison.</param>
/// <param name="Context">The request context the v2 engine was given.</param>
/// <param name="Capabilities">The client capabilities the v2 engine was given.</param>
/// <param name="Sources">The media source snapshots the v2 engine considered.</param>
/// <param name="Constraints">The playback constraints the v2 engine was given.</param>
/// <param name="Kind">Whether this was an audio or video planning call.</param>
/// <param name="CapturedAt">When this record was produced.</param>
/// <param name="ContractMapping">
/// Issue #75 (Option 1): the structurally closed diagnostic of what the request lost on its way
/// through the capability mapping, or <see langword="null"/> when there was nothing to compare -
/// which is the case for every caller that is not the v2 request contract (the legacy
/// <c>MediaInfoHelper</c> path declares no domain <see cref="ClientCapabilities"/>), and for every
/// request rejected before this record is published. Trailing and optional so every pre-#75 call
/// site keeps compiling. Unlike <see cref="Capabilities"/> - which intentionally echoes the client's
/// declaration and is catalogued as such in issue #80 - NOTHING inside this member can carry a
/// client-supplied value: see <c>Tesserafin.Playback.Contract.Diagnostics.ContractMappingDiagnostic</c>.
/// </param>
public sealed record ShadowDiagnosticRecord(
    PlaybackDecision Decision,
    DecisionVector LegacyVector,
    ShadowDivergence Divergence,
    PlaybackRequestContext Context,
    ClientCapabilities Capabilities,
    IReadOnlyList<MediaSourceSnapshot> Sources,
    PlaybackConstraints Constraints,
    PlaybackMediaKind Kind,
    DateTimeOffset CapturedAt,
    ContractMappingDiagnostic? ContractMapping = null);
