using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Entities;
using Tesserafin.Extensions;
using Tesserafin.Model.Channels;
using Tesserafin.Model.Querying;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="IChannelCatalog"/> - the raw channel catalog
    /// subset of <c>ChannelManager.GetChannelsInternalAsync</c> actually exercised by user-views,
    /// ported off <c>IChannelManager</c> with behavior preserved exactly for that subset (RFC
    /// <c>docs/rfc-di-query-user-views-v2.md</c> §4, §8, §9, PR108).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Depends only on <see cref="IEnumerable{IChannel}"/> (the injected channel plugins, exactly as
    /// <c>ChannelManager</c> already receives them), <see cref="IItemLookupService"/> (item lookup),
    /// <see cref="IItemStore"/> (deterministic id generation via <c>GetNewItemId</c>, PR106a) and
    /// <see cref="IUserManager"/> (optional user-scoped filtering). None of these reference
    /// <c>ILibraryManager</c>, <c>IUserViewManager</c>, <c>IChannelManager</c>, <c>ILiveTvManager</c>
    /// or <c>IDtoService</c> - this satisfies RFC invariant I1 (eager construction graph) for this
    /// leaf.
    /// </para>
    /// <para>
    /// <b>Lookup-only, by design (RFC finding, PR108)</b>: <c>ChannelManager</c>'s
    /// <c>GetAllChannelEntitiesAsync</c> (which <c>GetChannelsInternalAsync</c> calls) does not just
    /// look items up - for a channel plugin with no matching library item yet, it falls back to a
    /// private async <c>GetChannel(IChannel, CancellationToken)</c> that creates the item and then
    /// unconditionally calls <c>item.RefreshMetadata(...)</c>, which reaches the static
    /// <c>BaseItem.ProviderManager</c> -&gt; <c>IProviderManager</c> -&gt; <c>ILibraryManager</c> (RFC
    /// §2's <c>ProviderManager.cs:117</c> edge). That is exactly the kind of SCC-reaching runtime
    /// edge PR108 exists to keep out of this leaf, so it is deliberately <b>not</b> ported here:
    /// materialization stays <c>ChannelManager</c>'s job (its own <c>GetChannel</c>/<c>RefreshChannels</c>,
    /// the latter run periodically by <c>RefreshChannelsScheduledTask</c>). In steady state this
    /// scheduled task has already materialized every plugin channel as a library item by the time
    /// user-views run, so <see cref="GetChannelsAsync"/> and <c>ChannelManager.GetChannelsInternalAsync</c>
    /// return the same set; a channel plugin installed but not yet refreshed once is the one case
    /// where they would differ - this leaf silently omits such a channel from its result (see
    /// <see cref="GetAllChannelEntities"/>) rather than reaching into the SCC to materialize it.
    /// </para>
    /// <para>
    /// <b>Two <see cref="ChannelQuery"/> options are intentionally unsupported</b>, both because no
    /// known caller sets them (<c>UserViewManager.GetUserViews</c> only ever passes
    /// <c>new ChannelQuery { UserId = user.Id }</c>) and because supporting them would require
    /// dependencies outside PR108's authorized set: <see cref="ChannelQuery.IsFavorite"/> needs
    /// <c>IUserDataManager</c>, and <see cref="ChannelQuery.RefreshLatestChannelItems"/> needs the
    /// same channel-refresh machinery as the materialization path above. Both throw
    /// <see cref="NotSupportedException"/> rather than silently ignoring the option.
    /// </para>
    /// </remarks>
    internal sealed class ChannelCatalog : IChannelCatalog
    {
        private readonly IStandaloneVisibilityService _visibility;
        private readonly IChannel[] _channels;
        private readonly IItemLookupService _itemLookupService;
        private readonly IItemStore _itemStore;
        private readonly IUserManager _userManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelCatalog"/> class.
        /// </summary>
        /// <param name="channels">The channel plugins (injected directly, as <c>ChannelManager</c> already does).</param>
        /// <param name="itemLookupService">The item lookup service (read side; see PR75/PR76).</param>
        /// <param name="itemStore">The item store leaf (deterministic id generation; see PR106a).</param>
        /// <param name="userManager">The user manager (optional user-scoped filtering).</param>
        /// <param name="visibility">Decides item visibility through the services, never the entity statics.</param>
        public ChannelCatalog(
            IEnumerable<IChannel> channels,
            IItemLookupService itemLookupService,
            IItemStore itemStore,
            IUserManager userManager,
            IStandaloneVisibilityService visibility)
        {
            _visibility = visibility;
            _channels = channels.ToArray();
            _itemLookupService = itemLookupService;
            _itemStore = itemStore;
            _userManager = userManager;
        }

        /// <inheritdoc />
        public Task<QueryResult<Channel>> GetChannelsAsync(ChannelQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);

            if (query.IsFavorite.HasValue)
            {
                throw new NotSupportedException(
                    $"{nameof(ChannelCatalog)} does not support {nameof(ChannelQuery.IsFavorite)} - it has no {nameof(IUserDataManager)} dependency (RFC PR108, not part of the exercised subset).");
            }

            if (query.RefreshLatestChannelItems)
            {
                throw new NotSupportedException(
                    $"{nameof(ChannelCatalog)} does not support {nameof(ChannelQuery.RefreshLatestChannelItems)} - channel item refresh is out of scope for this lookup-only leaf (RFC PR108).");
            }

            var user = query.UserId.IsEmpty()
                ? null
                : _userManager.GetUserById(query.UserId);

            var channels = GetAllChannelEntities()
                .OrderBy(i => i.SortName)
                .ToList();

            if (query.IsRecordingsFolder.HasValue)
            {
                var val = query.IsRecordingsFolder.Value;
                channels = channels.Where(i =>
                {
                    try
                    {
                        return (GetChannelProvider(i) is IHasFolderAttributes hasAttributes
                            && hasAttributes.Attributes.Contains("Recordings", StringComparison.OrdinalIgnoreCase)) == val;
                    }
                    catch
                    {
                        return false;
                    }
                }).ToList();
            }

            if (query.SupportsLatestItems.HasValue)
            {
                var val = query.SupportsLatestItems.Value;
                channels = channels.Where(i =>
                {
                    try
                    {
                        return GetChannelProvider(i) is ISupportsLatestMedia == val;
                    }
                    catch
                    {
                        return false;
                    }
                }).ToList();
            }

            if (query.SupportsMediaDeletion.HasValue)
            {
                var val = query.SupportsMediaDeletion.Value;
                channels = channels.Where(i =>
                {
                    try
                    {
                        return GetChannelProvider(i) is ISupportsDelete == val;
                    }
                    catch
                    {
                        return false;
                    }
                }).ToList();
            }

            if (user is not null)
            {
                var userId = user.Id.ToString("N", CultureInfo.InvariantCulture);
                channels = channels.Where(i =>
                {
                    if (!i.IsVisible(user, false, _visibility))
                    {
                        return false;
                    }

                    try
                    {
                        return GetChannelProvider(i).IsEnabledFor(userId);
                    }
                    catch
                    {
                        return false;
                    }
                }).ToList();
            }

            var all = channels;
            var totalCount = all.Count;

            int startIndex = query.StartIndex ?? 0;
            int count = (query.Limit ?? 0) > 0 ? Math.Min(query.Limit.GetValueOrDefault(), totalCount - startIndex) : totalCount - startIndex;
            all = all.GetRange(startIndex, count);

            return Task.FromResult(new QueryResult<Channel>(
                query.StartIndex,
                totalCount,
                all));
        }

        /// <summary>
        /// Gets the channel plugins in the same order <c>ChannelManager.GetAllChannels</c> uses.
        /// </summary>
        private IEnumerable<IChannel> GetAllChannels()
        {
            return _channels.OrderBy(i => i.Name);
        }

        /// <summary>
        /// Looks up the already-materialized library item for each channel plugin, mirroring
        /// <c>ChannelManager.GetAllChannelEntitiesAsync</c>'s lookup half exactly
        /// (<c>GetInternalChannelId</c> + item lookup by id) but silently omitting a plugin with no
        /// matching item yet instead of materializing it (see type-level remarks: lookup-only by
        /// design, materialization stays <c>ChannelManager</c>'s job).
        /// </summary>
        private IEnumerable<Channel> GetAllChannelEntities()
        {
            foreach (var channel in GetAllChannels())
            {
                var id = GetInternalChannelId(channel.Name);
                var item = _itemLookupService.GetItemById<Channel>(id);

                if (item is not null)
                {
                    yield return item;
                }
            }
        }

        /// <summary>
        /// Computes the deterministic library item id for a channel plugin, identical to
        /// <c>ChannelManager.GetInternalChannelId</c>.
        /// </summary>
        private Guid GetInternalChannelId(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            return _itemStore.GetNewItemId("Channel " + name, typeof(Channel));
        }

        /// <summary>
        /// Resolves the channel plugin backing a materialized <see cref="Channel"/> item, identical
        /// to <c>ChannelManager.GetChannelProvider</c>.
        /// </summary>
        private IChannel GetChannelProvider(Channel channel)
        {
            ArgumentNullException.ThrowIfNull(channel);

            var result = GetAllChannels()
                .FirstOrDefault(i => GetInternalChannelId(i.Name).Equals(channel.ChannelId) || string.Equals(i.Name, channel.Name, StringComparison.OrdinalIgnoreCase));

            if (result is null)
            {
                throw new ResourceNotFoundException("No channel provider found for channel " + channel.Name);
            }

            return result;
        }
    }
}
