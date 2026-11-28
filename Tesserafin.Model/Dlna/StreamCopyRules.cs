using System;

namespace Tesserafin.Model.Dlna;

/// <summary>
/// Stream-copy rules that the decision (the v2 engine) and the execution
/// (<c>EncodingHelper.CanStreamCopyVideo</c>) have to agree on (#374).
/// </summary>
public static class StreamCopyRules
{
    /// <summary>
    /// Determines whether a video stream is H.264 in Annex B form inside AVI, which cannot be copied
    /// into another container: AVI stores no presentation timestamps, so a stream with B-frames
    /// makes the muxer abort ("first pts and dts value must be set").
    /// </summary>
    /// <param name="inputContainer">The source container.</param>
    /// <param name="videoCodec">The source video codec.</param>
    /// <param name="isAvc">Whether the source video stream is length-prefixed AVC.</param>
    /// <returns><see langword="true"/> if the video has to be re-encoded to leave this container.</returns>
    public static bool IsAnnexBH264InAvi(string? inputContainer, string? videoCodec, bool? isAvc)
        => string.Equals(inputContainer, "avi", StringComparison.OrdinalIgnoreCase)
           && string.Equals(videoCodec, "h264", StringComparison.OrdinalIgnoreCase)
           && !(isAvc ?? false);
}
