#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Querying;

namespace Tesserafin.Server.Core.Library
{
    public class ItemQueryService : IItemQueryService
    {
        private readonly IItemListingServices _listing;
        private readonly IItemCatalogueQuery _catalogue;
        private readonly IChannelManager _channelManager;
        private readonly ICollectionManager _collectionManager;
        private readonly IUserViewManager _userViewManager;
        private readonly ITVSeriesManager _tvSeriesManager;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IItemSortService _itemSortService;
        private readonly IStandaloneVisibilityService _visibility;

        public ItemQueryService(
            IChannelManager channelManager,
            ICollectionManager collectionManager,
            IUserViewManager userViewManager,
            ITVSeriesManager tvSeriesManager,
            IServerConfigurationManager configurationManager,
            IItemSortService itemSortService,
            IStandaloneVisibilityService visibility,
            IItemListingServices listing,
            IItemCatalogueQuery catalogue)
        {
            _listing = listing;
            _catalogue = catalogue;
            _visibility = visibility;
            _channelManager = channelManager;
            _collectionManager = collectionManager;
            _userViewManager = userViewManager;
            _tvSeriesManager = tvSeriesManager;
            _configurationManager = configurationManager;
            _itemSortService = itemSortService;
        }

        public QueryResult<BaseItem> GetItems(Folder folder, InternalItemsQuery query)
        {
            query.Visibility ??= _visibility;
            query.Listing ??= _listing;

            if (!CanUseRawQueryItemsFastPath(folder, query))
            {
                return folder.GetItems(query, _channelManager, _collectionManager, _userViewManager, _tvSeriesManager, _itemSortService);
            }

            return PostFilterAndSort(folder, folder.GetRawQueryItems(query), query);
        }

        public IReadOnlyList<BaseItem> GetItemList(Folder folder, InternalItemsQuery query)
        {
            query.Visibility ??= _visibility;
            query.Listing ??= _listing;

            if (!CanUseRawQueryItemsFastPath(folder, query))
            {
                return folder.GetItemList(query, _channelManager, _collectionManager, _userViewManager, _tvSeriesManager, _itemSortService);
            }

            // Mirrors Folder.GetItemList's own unconditional mutation before it delegates further.
            query.EnableTotalRecordCount = false;
            return PostFilterAndSort(folder, folder.GetRawQueryItems(query), query).Items;
        }

        public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query)
        {
            PrepareListing(query);
            return _catalogue.GetItemList(query);
        }

        public QueryResult<BaseItem> GetItems(InternalItemsQuery query)
        {
            PrepareListing(query);
            return _catalogue.GetItems(query);
        }

        public int GetCount(InternalItemsQuery query)
        {
            PrepareListing(query);
            return _catalogue.GetCount(query);
        }

        private void PrepareListing(InternalItemsQuery query)
        {
            query.Visibility ??= _visibility;
            query.Listing ??= _listing;
        }

        // GetRawQueryItems + PostFilterAndSort is only equivalent to the full GetItems/GetItemList
        // pipeline when none of the branches that precede or replace it in Folder are in play:
        // folder.SupportsRawQueryItems (PR25) rules out the 6 known GetItemsInternal overrides that
        // build items differently; query.ItemIds mirrors Folder.GetItems/GetItemList's own early
        // return (LibraryManager.GetItemsResult/GetItemList, never reaches GetItemsInternal at all);
        // SourceType.Channel and query.Recursive mirror GetItemsInternal's own first two checks.
        private static bool CanUseRawQueryItemsFastPath(Folder folder, InternalItemsQuery query)
            => folder.SupportsRawQueryItems
                && query.ItemIds.Length == 0
                && folder.SourceType != SourceType.Channel
                && !query.Recursive;

        // Relocation of Folder.PostFilterAndSort (and its private helpers) per the major rewrite
        // plan's point 5. Folder keeps its own copy for legacy callers, while ItemQueryService owns
        // injected dependencies for the migrated path.
        internal QueryResult<BaseItem> PostFilterAndSort(BaseItem queryParent, IEnumerable<BaseItem> items, InternalItemsQuery query)
        {
            var user = query.User;

            if (user is not null)
            {
                items = CollapseBoxSetItemsIfNeeded(items, query, queryParent, user);
                items = ApplyNameFilter(items, query);
            }

            var filteredItems = items as IReadOnlyList<BaseItem> ?? items.ToList();
            var result = UserViewBuilder.SortAndPage(filteredItems, null, query, _itemSortService);

            if (query.EnableTotalRecordCount)
            {
                result.TotalRecordCount = filteredItems.Count;
            }

            return result;
        }

        private static IEnumerable<BaseItem> ApplyNameFilter(IEnumerable<BaseItem> items, InternalItemsQuery query)
        {
            if (!string.IsNullOrWhiteSpace(query.NameStartsWith))
            {
                items = items.Where(i => i.SortName.StartsWith(query.NameStartsWith, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query.NameStartsWithOrGreater))
            {
                items = items.Where(i => string.Compare(i.SortName, query.NameStartsWithOrGreater, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (!string.IsNullOrWhiteSpace(query.NameLessThan))
            {
                items = items.Where(i => string.Compare(i.SortName, query.NameLessThan, StringComparison.OrdinalIgnoreCase) < 0);
            }

            return items;
        }

        private IEnumerable<BaseItem> CollapseBoxSetItemsIfNeeded(
            IEnumerable<BaseItem> items,
            InternalItemsQuery query,
            BaseItem queryParent,
            User user)
        {
            ArgumentNullException.ThrowIfNull(items);

            if (!CollapseBoxSetItems(query, queryParent, user))
            {
                return items;
            }

            var config = _configurationManager.Configuration;

            bool collapseMovies = config.EnableGroupingMoviesIntoCollections;
            bool collapseSeries = config.EnableGroupingShowsIntoCollections;

            if (user is null || (collapseMovies && collapseSeries))
            {
                return _collectionManager.CollapseItemsWithinBoxSets(items, user);
            }

            if (!collapseMovies && !collapseSeries)
            {
                return items;
            }

            var collapsibleItems = new List<BaseItem>();
            var remainingItems = new List<BaseItem>();

            foreach (var item in items)
            {
                if ((collapseMovies && item is Movie) || (collapseSeries && item is Series))
                {
                    collapsibleItems.Add(item);
                }
                else
                {
                    remainingItems.Add(item);
                }
            }

            if (collapsibleItems.Count == 0)
            {
                return remainingItems;
            }

            var collapsedItems = _collectionManager.CollapseItemsWithinBoxSets(collapsibleItems, user);

            return collapsedItems.Concat(remainingItems);
        }

        private bool CollapseBoxSetItems(
            InternalItemsQuery query,
            BaseItem queryParent,
            User user)
        {
            // Could end up stuck in a loop like this
            if (queryParent is BoxSet)
            {
                return false;
            }

            if (queryParent is Season)
            {
                return false;
            }

            if (queryParent is MusicAlbum)
            {
                return false;
            }

            if (queryParent is MusicArtist)
            {
                return false;
            }

            var param = query.CollapseBoxSetItems;
            if (param.HasValue)
            {
                return param.Value && AllowBoxSetCollapsing(query);
            }

            var config = _configurationManager.Configuration;

            bool queryHasMovies = query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Contains(BaseItemKind.Movie);
            bool queryHasSeries = query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Contains(BaseItemKind.Series);

            bool collapseMovies = config.EnableGroupingMoviesIntoCollections;
            bool collapseSeries = config.EnableGroupingShowsIntoCollections;

            if (user is not null)
            {
                bool canCollapse = (queryHasMovies && collapseMovies) || (queryHasSeries && collapseSeries);
                return canCollapse && AllowBoxSetCollapsing(query);
            }

            return (queryHasMovies || queryHasSeries) && AllowBoxSetCollapsing(query);
        }

        private static bool AllowBoxSetCollapsing(InternalItemsQuery request)
        {
            if (request.IsFavorite.HasValue)
            {
                return false;
            }

            if (request.IsFavoriteOrLiked.HasValue)
            {
                return false;
            }

            if (request.IsLiked.HasValue)
            {
                return false;
            }

            if (request.IsPlayed.HasValue)
            {
                return false;
            }

            if (request.IsResumable.HasValue)
            {
                return false;
            }

            if (request.IsFolder.HasValue)
            {
                return false;
            }

            if (request.Genres.Count > 0)
            {
                return false;
            }

            if (request.GenreIds.Count > 0)
            {
                return false;
            }

            if (request.HasImdbId.HasValue)
            {
                return false;
            }

            if (request.HasOfficialRating.HasValue)
            {
                return false;
            }

            if (request.HasOverview.HasValue)
            {
                return false;
            }

            if (request.HasParentalRating.HasValue)
            {
                return false;
            }

            if (request.HasSpecialFeature.HasValue)
            {
                return false;
            }

            if (request.HasSubtitles.HasValue)
            {
                return false;
            }

            if (request.HasThemeSong.HasValue)
            {
                return false;
            }

            if (request.HasThemeVideo.HasValue)
            {
                return false;
            }

            if (request.HasTmdbId.HasValue)
            {
                return false;
            }

            if (request.HasTrailer.HasValue)
            {
                return false;
            }

            if (request.ImageTypes.Length > 0)
            {
                return false;
            }

            if (request.Is3D.HasValue)
            {
                return false;
            }

            if (request.Is4K.HasValue)
            {
                return false;
            }

            if (request.IsHD.HasValue)
            {
                return false;
            }

            if (request.IsLocked.HasValue)
            {
                return false;
            }

            if (request.IsPlaceHolder.HasValue)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(request.Person))
            {
                return false;
            }

            if (request.PersonIds.Length > 0)
            {
                return false;
            }

            if (request.ItemIds.Length > 0)
            {
                return false;
            }

            if (request.StudioIds.Length > 0)
            {
                return false;
            }

            if (request.VideoTypes.Length > 0)
            {
                return false;
            }

            if (request.Years.Length > 0)
            {
                return false;
            }

            if (request.Tags.Length > 0)
            {
                return false;
            }

            if (request.OfficialRatings.Length > 0)
            {
                return false;
            }

            if (request.MinIndexNumber.HasValue)
            {
                return false;
            }

            if (request.OrderBy.Any(o =>
                o.OrderBy == ItemSortBy.CommunityRating ||
                o.OrderBy == ItemSortBy.CriticRating ||
                o.OrderBy == ItemSortBy.Runtime))
            {
                return false;
            }

            return true;
        }
    }
}
