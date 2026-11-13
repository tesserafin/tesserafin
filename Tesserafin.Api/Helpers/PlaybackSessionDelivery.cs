using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Playback.Execution;

namespace Tesserafin.Api.Helpers;

/// <summary>
/// A delivery decision and, when granted, what is served.
/// </summary>
/// <param name="Outcome">The decision.</param>
/// <param name="Session">The session, when granted.</param>
/// <param name="Options">The options the session was planned with, when granted.</param>
/// <param name="Plan">The retained execution plan, when granted.</param>
/// <param name="Source">The local-file source the plan selected, when granted.</param>
public sealed record PlaybackSessionDelivery(
    PlaybackSessionDeliveryOutcome Outcome,
    PlaybackSession? Session = null,
    MediaOptions? Options = null,
    PlaybackExecutionPlan? Plan = null,
    MediaSourceInfo? Source = null)
{
    /// <summary>Gets the refusal for anything not servable under the names the caller sent.</summary>
    public static PlaybackSessionDelivery NotFound { get; } = new(PlaybackSessionDeliveryOutcome.NotFound);

    /// <summary>Gets the refusal for a session that is not the caller's.</summary>
    public static PlaybackSessionDelivery Forbidden { get; } = new(PlaybackSessionDeliveryOutcome.Forbidden);
}
