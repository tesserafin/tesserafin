using System;
using System.Collections.Generic;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Playback.Contract.Diagnostics;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Shadow;

namespace Tesserafin.Api.Models.PlaybackSessionDtos;

/// <summary>
/// The richer, admin-only diagnostic projection of a tracked playback session
/// (docs/pr92-design-playback-api-and-diagnostics.md §4.3, PR113): every
/// <see cref="PlaybackSessionResponse"/> field, plus the request context, client capabilities,
/// source snapshot, full reasoning tree, and legacy/v2 comparison retained from a shadow run - when
/// one was retained at all.
/// </summary>
/// <remarks>
/// Filtering rule (§4.3, tested in PR113): never a file path, transcoding URL, session token, or API
/// key. Every field here is either a primitive, or drawn from the <see cref="Tesserafin.Playback.Decision"/>
/// vocabulary (PR91) or the shadow comparison vocabulary (<see cref="DivergenceClass"/>, PR98) - never
/// <c>Tesserafin.Model.Dlna.MediaSourceInfo</c>, <c>StreamInfo</c>, <c>DeviceProfile</c>, or the internal
/// <c>Tesserafin.Controller.MediaEncoding.PlaybackSession</c>/<c>PlaybackSessionRequest</c> records. See
/// <see cref="PlaybackDiagnosticDetailMapper"/> for the derivation.
/// </remarks>
/// <param name="Id">The session identifier.</param>
/// <param name="Kind">Whether this is an audio or video session.</param>
/// <param name="DecisionVersion">See <see cref="PlaybackSessionResponse.DecisionVersion"/>.</param>
/// <param name="Method">The chosen playback method.</param>
/// <param name="Output">The shape of the output the client will receive.</param>
/// <param name="SelectedStreams">The streams selected for playback.</param>
/// <param name="Transforms">The pipeline transforms this decision implies.</param>
/// <param name="Reasons">A flat summary of the reason codes behind this decision.</param>
/// <param name="CreatedAt">When the session was first created.</param>
/// <param name="UpdatedAt">When the session was last created or replaced.</param>
/// <param name="RequestContext">
/// The request context a retained shadow run captured, or <see langword="null"/> when no
/// diagnostic was retained for this session (shadow mode off is the common case: it defaults to
/// disabled, per <c>ShadowPlaybackSessionPlanner</c> (removed, tesserafin#129 step 7)).
/// <see cref="PlaybackRequestContext.UserId"/> (PR113b) is the real requesting user when the
/// calling controller populated <c>MediaOptions.UserId</c>, or <see cref="Guid.Empty"/> for a
/// caller that predates PR113b and never set it.
/// </param>
/// <param name="Capabilities">The client capabilities a retained shadow run captured, or <see langword="null"/> if none was retained.</param>
/// <param name="SourceSnapshot">
/// The media source characteristics (codecs/streams/protocol) a retained shadow run captured, or
/// <see langword="null"/> if none was retained. Never a file path or URL -
/// <see cref="MediaSourceSnapshot"/> carries none by construction.
/// </param>
/// <param name="Reasoning">The v2 engine's full structured reasoning tree, or <see langword="null"/> if no diagnostic was retained.</param>
/// <param name="Comparison">The legacy-vs-v2 shadow comparison, or <see langword="null"/> if no diagnostic was retained.</param>
/// <param name="Timeline">
/// The lifecycle timeline for this session: <c>Created</c>/<c>Updated</c> from the session record,
/// plus (PR113b) any of <c>FfmpegStarted</c>/<c>PlaybackStarted</c>/<c>PlaybackStopped</c> that was
/// actually observed for it, each stamped with its real, received-at timestamp - never a
/// fabricated or approximated one. A stage that was never observed is simply absent, not defaulted.
/// </param>
/// <param name="LiveWiring">
/// PR115c: whether the live streaming path (<c>MediaInfoHelper.SetDeviceSpecificData</c>) actually
/// served this session from the v2 execution plan, or why it fell back to legacy - or
/// <see langword="null"/> when no live-wiring decision has been retained for this session yet
/// (request not yet made, or the session predates PR115c's diagnostics store). Independent of
/// <see cref="Comparison"/>: a session can have a shadow comparison retained without ever having
/// been through the live-wiring decision, and vice versa.
/// </param>
/// <param name="PlaybackAttemptId">
/// Issue #43: the opaque, client-supplied attempt id recorded on this session, or
/// <see langword="null"/> when the client sent none. This is the field that lets an operator pull up
/// every session — and therefore every retry — belonging to ONE playback attempt, which neither
/// <see cref="Id"/> (per session) nor <see cref="DiagnosticTimelineEntry.RequestId"/> (per request,
/// issue #42) can do. Opaque: displayed verbatim, never parsed.
/// </param>
/// <param name="ContractMapping">
/// Issue #75 (Option 1): what the client's DECLARED capabilities lost on their way through the
/// request mapping, in a strictly closed server-owned vocabulary - or <see langword="null"/> when no
/// shadow diagnostic was retained (shadow mode is off by default), or when the session was planned
/// by a caller that declares no domain capabilities (the legacy path). Additive and nullable: a
/// client that ignores this member sees no change.
/// <para>
/// This member, unlike <see cref="Capabilities"/> and <see cref="PlaybackAttemptId"/> (both of which
/// intentionally echo client-supplied data and are catalogued as such in issue #80), cannot carry a
/// client-supplied value at all: its entire transitive type closure is enums, booleans and integers.
/// See <see cref="ContractMappingDiagnostic"/> for the closure guarantee and for an explicit account
/// of what Option 1 does NOT observe - unknown members above all.
/// </para>
/// </param>
public sealed record PlaybackDiagnosticDetail(
    Guid Id,
    MediaKind Kind,
    int DecisionVersion,
    PlaybackMethod Method,
    OutputSpec Output,
    SelectedStreams SelectedStreams,
    IReadOnlyList<TransformKind> Transforms,
    IReadOnlyList<ReasonCode> Reasons,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PlaybackRequestContext? RequestContext,
    ClientCapabilities? Capabilities,
    IReadOnlyList<MediaSourceSnapshot>? SourceSnapshot,
    ReasonNode? Reasoning,
    DiagnosticComparison? Comparison,
    IReadOnlyList<DiagnosticTimelineEntry> Timeline,
    PlaybackLiveWiringOutcome? LiveWiring = null,
    string? PlaybackAttemptId = null,
    ContractMappingDiagnostic? ContractMapping = null);
