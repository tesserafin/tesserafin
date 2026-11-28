using System;
using Microsoft.AspNetCore.Http;

namespace Tesserafin.Api.Auth.HlsJobOwnership;

/// <summary>
/// The single authority every HLS resource derived from a transcoding job goes through
/// (#153-LTV-R3).
/// </summary>
/// <remarks>
/// THE ORDER IS THE CONTRACT. Each member resolves ONLY the job's metadata, builds the canonical
/// binding, and authorizes the caller. It opens nothing and it returns no path. The route then
/// resolves and opens the resource from the binding it was handed. A refusal therefore cannot have
/// touched a file, which is the property the mission asks to be proven rather than asserted.
///
/// General authentication is not enough and is not consulted as if it were: reaching an action
/// under <c>Policies.MediaDelivery</c> only proves the caller is *some* authenticated principal.
/// This decides whether they are *this job's* principal.
/// </remarks>
public interface IHlsJobOwnershipAuthorizer
{
    /// <summary>
    /// Authorizes a caller against the job that owns a playlist identifier.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="playlistId">The playlist identifier the route named.</param>
    /// <returns>The decision, carrying the binding it was made against.</returns>
    HlsJobOwnershipDecision AuthorizeByPlaylistId(HttpContext context, string playlistId);

    /// <summary>
    /// Authorizes a caller against the job that writes a resolved output path.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="outputPath">The playlist path the controller resolved for this request.</param>
    /// <returns>The decision, carrying the binding it was made against.</returns>
    HlsJobOwnershipDecision AuthorizeByOutputPath(HttpContext context, string outputPath);

    /// <summary>
    /// Authorizes a caller against the progressive job that writes a resolved output file (#293).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="outputPath">The output file the route resolved for this request.</param>
    /// <returns>The decision, carrying the binding it was made against.</returns>
    HlsJobOwnershipDecision AuthorizeProgressiveByOutputPath(HttpContext context, string outputPath);

    /// <summary>
    /// Answers whether a caller owns a job, given the job's recorded owner.
    /// </summary>
    /// <remarks>
    /// For the control plane rather than the data plane: <c>DELETE Videos/ActiveEncodings</c> acts
    /// on a job's LIFETIME, not on its bytes, so it resolves no binding and opens nothing — but it
    /// selects jobs by caller-named ids, so without this any authenticated caller could end
    /// anyone's transcode. The identity comparison is the same one the byte routes use; only the
    /// resource differs.
    /// </remarks>
    /// <param name="context">The request.</param>
    /// <param name="ownerUserId">The job's recorded owner.</param>
    /// <param name="ownerDeviceId">The job's recorded owning device.</param>
    /// <returns>Whether the caller is that owner.</returns>
    bool OwnsJob(HttpContext context, Guid ownerUserId, string? ownerDeviceId);
}
