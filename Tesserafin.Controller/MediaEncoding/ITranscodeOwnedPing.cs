using System;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Keeps alive the transcoding jobs of a play session that the caller is found to own, and no others.
/// </summary>
/// <remarks>
/// A separate interface for the reason <see cref="ITranscodeOwnedStop"/> is one: adding members
/// to a published interface breaks every implementation of it.
///
/// <see cref="ITranscodeManager.PingTranscodingJob"/> selects by play session id, which is the
/// client's to send. A caller who names another user's play session id must not keep that user's
/// transcode running past its idle timeout, or change whether it counts as paused.
/// </remarks>
public interface ITranscodeOwnedPing
{
    /// <summary>
    /// Pings every job of <paramref name="playSessionId"/> that <paramref name="isCallers"/> accepts.
    /// </summary>
    /// <param name="playSessionId">The play session id the caller named.</param>
    /// <param name="isUserPaused">Whether the caller reports the playback as paused, or null to leave it.</param>
    /// <param name="isCallers">Whether a job is the caller's to ping. Asked once per job.</param>
    void PingTranscodingJobs(string playSessionId, bool? isUserPaused, Func<TranscodingJob, bool> isCallers);
}
