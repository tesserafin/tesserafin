using Tesserafin.Playback.Decision;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// What a client declared on <c>POST</c>/<c>PUT Playback/Sessions</c> for a library video or audio track, handed to
/// <see cref="LocalMediaPlanner"/> as the engine's own inputs.
/// </summary>
/// <param name="Capabilities">The declared client capabilities.</param>
/// <param name="Constraints">The declared playback constraints.</param>
/// <param name="LiveStreamId">
/// For a Live TV channel or a recording still being written, the stream this session opened: the
/// one infinite source the planner may decide on. <see langword="null"/> for a library item.
/// </param>
public sealed record LocalMediaRequest(ClientCapabilities Capabilities, PlaybackConstraints Constraints, string? LiveStreamId = null)
{
    /// <summary>
    /// Gets or sets why the planner refused this request, once it has: read by the caller to say so in
    /// its answer. <see cref="LocalMediaRefusal.None"/> while nothing was refused.
    /// </summary>
    public LocalMediaRefusal Refusal { get; set; }
}
