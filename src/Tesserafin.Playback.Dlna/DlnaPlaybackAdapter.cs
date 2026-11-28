using System;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Playback.Dlna;

/// <summary>
/// The engine's inputs from server state: a media source projected by
/// <see cref="MediaSourceSnapshotMapper"/>, plus a constructor for
/// <see cref="PlaybackRequestContext"/>, which has no legacy equivalent to adapt from.
/// </summary>
public static class DlnaPlaybackAdapter
{
    /// <summary>
    /// Projects a legacy <see cref="MediaSourceInfo"/> into a frozen <see cref="MediaSourceSnapshot"/>.
    /// </summary>
    /// <param name="source">The legacy media source to project.</param>
    /// <returns>The equivalent domain snapshot.</returns>
    public static MediaSourceSnapshot ToSnapshot(MediaSourceInfo source) => MediaSourceSnapshotMapper.ToSnapshot(source);

    /// <summary>
    /// Builds a new <see cref="PlaybackRequestContext"/> for a fresh playback decision request.
    /// There is no legacy equivalent of this type to adapt from: it is constructed directly from
    /// the request's identifying values.
    /// </summary>
    /// <param name="itemId">The requested media item.</param>
    /// <param name="userId">The requesting user.</param>
    /// <param name="mediaSourceId">The specific alternate source version requested, or <see langword="null"/> to let the engine choose.</param>
    /// <param name="kind">Whether audio or video playback is being requested.</param>
    /// <param name="engineVersion">The version of the decision engine that will produce the decision for this request.</param>
    /// <returns>A new request context, stamped with a fresh <see cref="PlaybackRequestContext.RequestId"/> and the current time.</returns>
    public static PlaybackRequestContext ToContext(Guid itemId, Guid userId, string? mediaSourceId, MediaKind kind, int engineVersion) =>
        new(Guid.NewGuid(), itemId, mediaSourceId, userId, kind, DateTimeOffset.UtcNow, engineVersion);
}
