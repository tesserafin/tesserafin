using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Decides how a media item will be played back (direct play, direct stream, or
/// transcode) for a given device, without touching session or transcoding-job
/// lifecycle state.
/// </summary>
public interface IPlaybackSessionPlanner
{
    /// <summary>
    /// Plans an audio playback session.
    /// </summary>
    /// <param name="options">The media options to plan against.</param>
    /// <returns>The resulting <see cref="PlaybackPlan"/>, or <c>null</c> if no viable stream exists.</returns>
    PlaybackPlan? PlanAudio(MediaOptions options);

    /// <summary>
    /// Plans a video playback session.
    /// </summary>
    /// <param name="options">The media options to plan against.</param>
    /// <returns>The resulting <see cref="PlaybackPlan"/>, or <c>null</c> if no viable stream exists.</returns>
    PlaybackPlan? PlanVideo(MediaOptions options);
}

/// <summary>
/// The result of a playback planning decision.
/// </summary>
/// <param name="PlayMethod">The chosen play method (direct play, direct stream, or transcode).</param>
/// <param name="TranscodeReasons">The reasons a transcode was required, if any.</param>
public sealed record PlaybackPlan(PlayMethod PlayMethod, TranscodeReason TranscodeReasons);
