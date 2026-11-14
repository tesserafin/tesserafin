using System.Collections.Generic;
using System.Threading;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;

namespace Tesserafin.Controller.LiveTv
{
    /// <summary>
    /// Narrow leaf port covering the two <c>ILiveTvManager</c> members actually exercised by
    /// user-views (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §5, PR108): which users have Live
    /// TV enabled, and the shared Live TV folder view.
    /// </summary>
    /// <remarks>
    /// Introduced in PR108 as one of the two "external catalog" leaves (alongside
    /// <c>IChannelCatalog</c>) that let <c>IUserViewManager</c>/the future <c>IUserViewCatalog</c>
    /// (PR109) stop depending on <c>ILiveTvManager</c>. See the concrete
    /// <c>LiveTvPresenceProvider</c> implementation (Tesserafin.Server.Core) for the RFC invariant I1
    /// proof (no eager construction-graph edge to <c>ILiveTvManager</c>, <c>IChannelManager</c>,
    /// <c>ILibraryManager</c> or <c>IUserViewManager</c>) - notably, <see cref="GetLiveTvFolder"/>
    /// goes through <c>IUserViewFactory.GetNamedView</c> (PR106b), never
    /// <c>_libraryManager.GetNamedView</c> directly, and <see cref="GetEnabledUsers"/>'s service-count
    /// dependency is <c>Lazy</c>-wrapped rather than a direct <c>IEnumerable&lt;ILiveTvService&gt;</c>
    /// injection - see the concrete class's remarks for why, and for an unresolved I2-relevant finding
    /// this PR reports but does not settle. <c>ILiveTvManager</c> keeps its own
    /// <c>GetEnabledUsers</c>/<c>GetInternalLiveTvFolder</c> members for its own consumers; this
    /// port does not replace it.
    /// </remarks>
    public interface ILiveTvPresenceProvider
    {
        /// <summary>
        /// Gets the users who have Live TV access enabled, identical in behavior to
        /// <c>ILiveTvManager.GetEnabledUsers</c> (including its <c>IsLiveTvEnabled</c> check: the
        /// user must have the <c>EnableLiveTvAccess</c> permission, and either more than one Live TV
        /// service must be registered or at least one tuner host must be configured).
        /// </summary>
        /// <returns>The enabled users.</returns>
        IEnumerable<User> GetEnabledUsers();

        /// <summary>
        /// Gets the shared Live TV named view, identical in behavior to
        /// <c>ILiveTvManager.GetInternalLiveTvFolder</c> - same localized name, same view type
        /// (<c>CollectionType.livetv</c>), same resulting item (backed by
        /// <c>IUserViewFactory.GetNamedView</c> instead of <c>ILibraryManager.GetNamedView</c>,
        /// proven equivalent in PR106b).
        /// </summary>
        /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>The Live TV folder.</returns>
        Folder GetLiveTvFolder(CancellationToken cancellationToken);
    }
}
