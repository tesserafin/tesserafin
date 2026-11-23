using System.Threading.Tasks;
using Tesserafin.Model.Channels;
using Tesserafin.Model.Querying;

namespace Tesserafin.Controller.Channels
{
    /// <summary>
    /// Narrow leaf port covering the raw (entity, non-DTO) channel catalog subset of
    /// <see cref="IChannelManager"/> actually exercised by user-views (RFC
    /// <c>docs/rfc-di-query-user-views-v2.md</c> §4, PR108). Mirrors
    /// <c>IChannelManager.GetChannelsInternalAsync</c> under a simplified name - same query type,
    /// same <see cref="QueryResult{Channel}"/> return type.
    /// </summary>
    /// <remarks>
    /// Introduced in PR108 as one of the two "external catalog" leaves (alongside
    /// <c>ILiveTvPresenceProvider</c>) that let <c>IUserViewManager</c>/the future
    /// <c>IUserViewCatalog</c> (PR109) stop depending on <c>IChannelManager</c>. See the concrete
    /// <c>ChannelCatalog</c> implementation (Tesserafin.Server.Core) for the RFC invariant I1 proof (no
    /// eager construction-graph edge to <see cref="IChannelManager"/>, <c>ILibraryManager</c>,
    /// <c>IUserViewManager</c> or <c>ILiveTvManager</c>) and for the documented behavioral subset
    /// this leaf actually covers (lookup-only; on-the-fly channel materialization stays
    /// <c>ChannelManager</c>'s responsibility because it uses <c>IProviderManager</c> to refresh new items).
    /// <c>IChannelManager</c> keeps <c>GetChannelsInternalAsync</c> for its own consumers (the DTO
    /// variant <c>GetChannelsAsync</c>, refresh, delete); this port does not replace it.
    /// </remarks>
    public interface IChannelCatalog
    {
        /// <summary>
        /// Gets the raw (entity) channels matching <paramref name="query"/>, identical in behavior
        /// to <c>IChannelManager.GetChannelsInternalAsync</c> for the subset actually exercised by
        /// user-views (RFC §4): user-scoped visibility/enablement filtering and paging. See the
        /// concrete implementation's remarks for the two query options it does not support
        /// (<c>IsFavorite</c>, <c>RefreshLatestChannelItems</c>) - both unset by every known caller.
        /// </summary>
        /// <param name="query">The channel query.</param>
        /// <returns>The matching channels.</returns>
        Task<QueryResult<Channel>> GetChannelsAsync(ChannelQuery query);
    }
}
