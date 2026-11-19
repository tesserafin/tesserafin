#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Library;
using Tesserafin.Model.Querying;

namespace Tesserafin.Controller.Entities
{
    /// <summary>
    /// Special class used for User Roots.  Children contain actual ones defined for this user
    /// PLUS the virtual folders from the physical root (added by plug-ins).
    /// </summary>
    public class UserRootFolder : Folder
    {
        private readonly Lock _childIdsLock = new();
        private List<Guid> _childrenIds = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserRootFolder"/> class.
        /// </summary>
        public UserRootFolder()
        {
            IsRoot = true;
        }

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        [JsonIgnore]
        public override bool SupportsPlayedStatus => false;

        [JsonIgnore]
        protected override bool SupportsShortcutChildren => true;

        [JsonIgnore]
        public override bool IsPreSorted => true;

        public override bool SupportsRawQueryItems => false;

        // Keep only the IDs so a library edit is visible without waiting for the next root scan.
        /// <inheritdoc />
        public override IEnumerable<BaseItem> GetCatalogueChildren(IItemRepository repository, IItemLookupService lookup)
            => LoadChildren(repository, lookup);

        private void ClearCache()
        {
            lock (_childIdsLock)
            {
                _childrenIds = null;
            }
        }

        /// <summary>
        /// Gets the libraries as <see cref="Children"/> does, resolving them through the given lookup
        /// instead of the static <see cref="BaseItem.LibraryManager"/> (DEBT-SERVER-4).
        /// </summary>
        /// <param name="lookup">The id lookup.</param>
        /// <returns>The libraries.</returns>
        public IReadOnlyList<BaseItem> GetLibraries(IItemLookupService lookup)
        {
            ArgumentNullException.ThrowIfNull(lookup);

            return LoadChildren(ItemRepository, lookup);
        }

        protected override IReadOnlyList<BaseItem> LoadChildren(IItemRepository repository, IItemLookupService lookup)
        {
            lock (_childIdsLock)
            {
                if (_childrenIds is null)
                {
                    var list = base.LoadChildren(repository, lookup);
                    _childrenIds = list.Select(i => i.Id).ToList();
                    return list;
                }

                return _childrenIds.Select(lookup.GetItemById).Where(i => i is not null).ToList();
            }
        }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            // The user root holds no items of its own - a plain listing returns the user's
            // views. But a request carrying any filter is a search across the libraries, so
            // resolve it through the recursive query path even when Recursive wasn't set;
            // otherwise the filters would be silently dropped. Recursive is set so the
            // downstream query (ancestor/top-parent scoping) treats it as a recursive search.
            if (query.Recursive || query.HasFilters)
            {
                query.Recursive = true;
                return QueryRecursive(query);
            }

            var result = userViewManager.GetUserViews(new UserViewQuery
            {
                User = query.User,
                PresetViews = query.PresetViews
            });

#pragma warning disable CS0618
            return UserViewBuilder.SortAndPage(result, null, query, LibraryManager);
#pragma warning restore CS0618
        }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager, IItemSortService itemSortService)
        {
            if (query.Recursive || query.HasFilters)
            {
                query.Recursive = true;
                return QueryRecursive(query);
            }

            var result = userViewManager.GetUserViews(new UserViewQuery
            {
                User = query.User,
                PresetViews = query.PresetViews
            });

            return UserViewBuilder.SortAndPage(result, null, query, itemSortService);
        }

        public override int GetChildCount(User user, IStandaloneVisibilityService scope, IItemListingServices listing)
        {
            return GetChildren(user, true, new InternalItemsQuery(user) { Visibility = scope, Listing = listing }).Count;
        }

        protected override IEnumerable<BaseItem> GetEligibleChildrenForRecursiveChildren(User user, InternalItemsQuery query)
        {
            var list = base.GetEligibleChildrenForRecursiveChildren(user, query).ToList();
            list.AddRange(LibraryManager.RootFolder.VirtualChildren);

            return list;
        }

        public override bool BeforeMetadataRefresh(bool replaceAllMetadata, IItemNamingService itemNamingService)
        {
            ClearCache();
            var hasChanges = base.BeforeMetadataRefresh(replaceAllMetadata, itemNamingService);

            if (string.Equals("default", Name, StringComparison.OrdinalIgnoreCase))
            {
                Name = "Media Folders";
                hasChanges = true;
            }

            return hasChanges;
        }

        protected override IEnumerable<BaseItem> GetNonCachedChildren(IItemResolutionService resolution, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            ClearCache();

            return base.GetNonCachedChildren(resolution, directoryService, cancellationToken);
        }

        protected override async Task ValidateChildrenInternal(IFolderValidationServices services, IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            ClearCache();

            await base.ValidateChildrenInternal(services, progress, recursive, refreshChildMetadata, allowRemoveRoot, refreshOptions, directoryService, cancellationToken)
                .ConfigureAwait(false);

            ClearCache();

            // Not the best way to handle this, but it solves an issue
            // CollectionFolders aren't always getting saved after changes
            // This means that grabbing the item by Id may end up returning the old one
            // Fix is in two places - make sure the folder gets saved
            // And here to remedy it for affected users.
            // In theory this can be removed eventually.
            foreach (var item in GetCatalogueChildren(services.Repository, services.Lookup))
            {
                services.Store.RegisterItem(item);
            }
        }
    }
}
