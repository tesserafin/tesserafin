namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Resolves the authoritative owner of a legacy HLS playlist's segment files (#153-LTV-R1).
/// </summary>
/// <remarks>
/// Deliberately a separate interface rather than three more members on
/// <see cref="ITranscodeManager"/>: the segment route needs exactly this and nothing else, and a
/// narrow interface is what lets the route be tested without standing up a transcode manager.
///
/// EVERY MEMBER RESOLVES A JOB, NEVER A PATH (#153-LTV-R3). Each one takes something the caller
/// named, uses it only to SELECT a job out of the server's own active-job list, and then returns
/// what that job knows. None of them combines a caller-supplied string with a directory to make a
/// file name; that is the route's job, and the route may only do it against the canonical root the
/// returned binding carries.
/// </remarks>
public interface IHlsSegmentBindingRegistry
{
    /// <summary>
    /// Returns the binding for a playlist identifier, or <see langword="null"/> if no active job
    /// owns it.
    /// </summary>
    /// <param name="playlistId">The playlist identifier the route named.</param>
    /// <returns>The binding, or <see langword="null"/>.</returns>
    HlsSegmentBinding? ResolveByPlaylistId(string playlistId);

    /// <summary>
    /// Returns the binding of the job that writes a given output path, or <see langword="null"/>
    /// if no active job does (#153-LTV-R3).
    /// </summary>
    /// <remarks>
    /// For <c>DynamicHlsController</c>, whose routes name no job either: it derives its output
    /// path as <c>MD5(mediaPath-userAgent-deviceId-playSessionId)</c>, where the device and the
    /// play session are query parameters and the user agent is a request header. A second
    /// authenticated caller replaying another caller's url with their <c>User-Agent</c> therefore
    /// arrives at the same path. Handing that path here answers the only question that matters —
    /// whose job writes it — so the route can compare the caller against a job instead of against
    /// the url that reached it.
    /// </remarks>
    /// <param name="outputPath">The playlist path the controller resolved for the request.</param>
    /// <returns>The binding, or <see langword="null"/>.</returns>
    HlsSegmentBinding? ResolveByOutputPath(string outputPath);
}
