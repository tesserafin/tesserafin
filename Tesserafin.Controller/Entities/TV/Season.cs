#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using Tesserafin.Common;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Querying;

namespace Tesserafin.Controller.Entities.TV
{
    /// <summary>
    /// Class Season.
    /// </summary>
    [RequiresSourceSerialisation]
    public class Season : Folder, IHasSeries, IHasLookupInfo<SeasonInfo>
    {
        [JsonIgnore]
        public override bool SupportsAddingToPlaylist => true;

        [JsonIgnore]
        public override bool IsPreSorted => true;

        [JsonIgnore]
        public override bool SupportsDateLastMediaAdded => false;

        [JsonIgnore]
        public override bool SupportsPeople => true;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => true;

        [JsonIgnore]
        public override Guid DisplayParentId => SeriesId;

        /// <summary>
        /// Gets this Episode's Series Instance.
        /// </summary>
        /// <value>The series.</value>
        [JsonIgnore]
        public Series Series
        {
            get
            {
                var seriesId = SeriesId;
                if (seriesId.IsEmpty())
                {
                    seriesId = FindSeriesId();
                }

                return seriesId.IsEmpty() ? null : (LibraryManager.GetItemById(seriesId) as Series);
            }
        }

        [JsonIgnore]
        public string SeriesPath
        {
            get
            {
                var series = Series;

                if (series is not null)
                {
                    return series.Path;
                }

                return System.IO.Path.GetDirectoryName(Path);
            }
        }

        [JsonIgnore]
        public string SeriesPresentationUniqueKey { get; set; }

        [JsonIgnore]
        public string SeriesName { get; set; }

        [JsonIgnore]
        public Guid SeriesId { get; set; }

        // Only unsafe when query.User is set (the base routing is used as-is for query.User is
        // null, cf. the base.GetItemsInternal call in GetItemsInternal below) - kept false
        // unconditionally anyway.
        public override bool SupportsRawQueryItems => false;

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            double value = 2;
            value /= 3;

            return value;
        }

        public string FindSeriesSortName()
        {
            var series = Series;
            return series is null ? SeriesName : series.SortName;
        }

        public override List<string> GetUserDataKeys()
        {
            var list = base.GetUserDataKeys();

            var series = Series;
            if (series is not null)
            {
                var newList = series.GetUserDataKeys();
                var suffix = (IndexNumber ?? 0).ToString("000", CultureInfo.InvariantCulture);
                for (int i = 0; i < newList.Count; i++)
                {
                    newList[i] = newList[i] + suffix;
                }

                newList.AddRange(list);
                list = newList;
            }

            return list;
        }

        public override int GetChildCount(User user, IStandaloneVisibilityService scope, IItemListingServices listing)
        {
            var result = GetChildren(user, true, new InternalItemsQuery(user) { Visibility = scope, Listing = listing }).Count;

            return result;
        }

        /// <inheritdoc />
        public override string GetInheritedOriginalLanguage()
        {
            return OriginalLanguage ?? Series?.GetInheritedOriginalLanguage();
        }

        public override string CreatePresentationUniqueKey()
        {
            if (IndexNumber.HasValue)
            {
                var series = Series;
                if (series is not null)
                {
                    return series.PresentationUniqueKey + "-" + IndexNumber.Value.ToString("000", CultureInfo.InvariantCulture);
                }
            }

            return base.CreatePresentationUniqueKey();
        }

        /// <summary>
        /// Creates the name of the sort.
        /// </summary>
        /// <returns>System.String.</returns>
        protected override string CreateSortName()
        {
            return IndexNumber is not null ? IndexNumber.Value.ToString("0000", CultureInfo.InvariantCulture) : Name;
        }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            if (SourceType == SourceType.Channel)
            {
                try
                {
                    query.Parent = this;
                    query.ChannelIds = new[] { ChannelId };
                    return channelManager.GetChannelItemsInternal(query, new Progress<double>(), CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    // Already logged at lower levels
                    return new QueryResult<BaseItem>();
                }
            }

            if (query.User is null)
            {
                return base.GetItemsInternal(query, channelManager, collectionManager, userViewManager, tvSeriesManager);
            }

            var user = query.User;

            var items = UserViewBuilder.Filter(GetEpisodes(user, query.DtoOptions, true), user, query, UserDataManager, LibraryManager);

            return PostFilterAndSort(items, query, collectionManager);
        }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager, IItemSortService itemSortService)
        {
            if (SourceType == SourceType.Channel)
            {
                try
                {
                    query.Parent = this;
                    query.ChannelIds = new[] { ChannelId };
                    return channelManager.GetChannelItemsInternal(query, new Progress<double>(), CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    // Already logged at lower levels
                    return new QueryResult<BaseItem>();
                }
            }

            if (query.User is null)
            {
                return base.GetItemsInternal(query, channelManager, collectionManager, userViewManager, tvSeriesManager, itemSortService);
            }

            var user = query.User;

            var items = UserViewBuilder.Filter(GetEpisodes(user, query.DtoOptions, true, query.Listing), user, query, UserDataManager, LibraryManager);

            return PostFilterAndSort(items, query, collectionManager, itemSortService);
        }

        /// <summary>
        /// Gets the episodes.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="options">The options to use.</param>
        /// <param name="shouldIncludeMissingEpisodes">If missing episodes should be included.</param>
        /// <returns>Set of episodes.</returns>
        public List<BaseItem> GetEpisodes(User user, DtoOptions options, bool shouldIncludeMissingEpisodes)
        {
            return GetEpisodes(user, options, shouldIncludeMissingEpisodes, null);
        }

        /// <summary>
        /// Gets the episodes.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="options">The options to use.</param>
        /// <param name="shouldIncludeMissingEpisodes">If missing episodes should be included.</param>
        /// <param name="listing">Listing services (sort, channels); the sort falls back to the obsolete static <c>LibraryManager.Sort</c> facade when <c>null</c> (see docs/major-rewrite-plan-v13.md § PR50/N).</param>
        /// <returns>Set of episodes.</returns>
        public List<BaseItem> GetEpisodes(User user, DtoOptions options, bool shouldIncludeMissingEpisodes, IItemListingServices listing)
        {
            return GetEpisodes(Series, user, options, shouldIncludeMissingEpisodes, listing);
        }

        public List<BaseItem> GetEpisodes(Series series, User user, DtoOptions options, bool shouldIncludeMissingEpisodes)
        {
            return GetEpisodes(series, user, options, shouldIncludeMissingEpisodes, null);
        }

        public List<BaseItem> GetEpisodes(Series series, User user, DtoOptions options, bool shouldIncludeMissingEpisodes, IItemListingServices listing)
        {
            return GetEpisodes(series, user, null, options, shouldIncludeMissingEpisodes, listing);
        }

        public List<BaseItem> GetEpisodes(Series series, User user, IEnumerable<Episode> allSeriesEpisodes, DtoOptions options, bool shouldIncludeMissingEpisodes)
        {
            return GetEpisodes(series, user, allSeriesEpisodes, options, shouldIncludeMissingEpisodes, null);
        }

        public List<BaseItem> GetEpisodes(Series series, User user, IEnumerable<Episode> allSeriesEpisodes, DtoOptions options, bool shouldIncludeMissingEpisodes, IItemListingServices listing)
        {
            if (series is null)
            {
                return [];
            }

            return series.GetSeasonEpisodes(this, user, allSeriesEpisodes, options, shouldIncludeMissingEpisodes, listing);
        }

        public List<BaseItem> GetEpisodes()
        {
            // No IItemSortService to give here (no DI-resolved caller reaches this no-arg
            // overload): falls back to the obsolete static LibraryManager.Sort facade via
            // Series.GetSeasonEpisodes, same as Season.GetChildren below. See PR50/N.
            return GetEpisodes(Series, null, null, new DtoOptions(true), true);
        }

        public override List<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query)
        {
            return GetEpisodes(user, new DtoOptions(true), true);
        }

        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query, IItemSortService itemSortService)
        {
            return GetEpisodes(user, new DtoOptions(true), true, query.Listing);
        }

        protected override bool GetBlockUnratedValue(User user)
        {
            // Don't block. Let either the entire series rating or episode rating determine it
            return false;
        }

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Series;
        }

        public string FindSeriesPresentationUniqueKey()
        {
            var series = Series;
            return series is null ? null : series.PresentationUniqueKey;
        }

        public string FindSeriesName()
        {
            var series = Series;
            return series is null ? SeriesName : series.Name;
        }

        public Guid FindSeriesId()
        {
            var series = FindParent<Series>();
            return series?.Id ?? Guid.Empty;
        }

        /// <summary>
        /// Gets this season's owning series, resolving every hop through the given lookup service
        /// instead of the static <see cref="BaseItem.LibraryManager"/>. The <see cref="Series"/>
        /// property remains as a compatibility wrapper over the static path; new callers that hold
        /// an <see cref="IItemLookupService"/> should prefer this overload. Both the direct id
        /// resolution and the empty-id parent fallback (<see cref="FindSeriesId(IItemLookupService)"/>)
        /// go through <paramref name="lookup"/>, so this path is fully static-free.
        /// </summary>
        /// <param name="lookup">The lookup service.</param>
        /// <returns>The series, or <c>null</c> if none can be resolved.</returns>
        public Series GetSeries(IItemLookupService lookup)
        {
            ArgumentNullException.ThrowIfNull(lookup);

            var seriesId = SeriesId;
            if (seriesId.IsEmpty())
            {
                seriesId = FindSeriesId(lookup);
            }

            return seriesId.IsEmpty() ? null : (lookup.GetItemById(seriesId) as Series);
        }

        /// <summary>
        /// Finds the owning series id by walking the parent chain via the given lookup service,
        /// without touching the static <see cref="BaseItem.LibraryManager"/>. Used by
        /// <see cref="GetSeries(IItemLookupService)"/> as its empty-id fallback.
        /// </summary>
        /// <param name="lookup">The lookup service.</param>
        /// <returns>The series id, or <see cref="Guid.Empty"/> if no series ancestor exists.</returns>
        public Guid FindSeriesId(IItemLookupService lookup)
        {
            var series = FindParent<Series>(lookup);
            return series?.Id ?? Guid.Empty;
        }

        /// <summary>
        /// Gets the lookup information.
        /// </summary>
        /// <returns>SeasonInfo.</returns>
        public SeasonInfo GetLookupInfo()
        {
            var id = GetItemLookupInfo<SeasonInfo>();

            var series = Series;

            if (series is not null)
            {
                id.SeriesProviderIds = series.ProviderIds;
                id.SeriesDisplayOrder = series.DisplayOrder;
            }

            return id;
        }

        /// <summary>
        /// This is called before any metadata refresh and returns true or false indicating if changes were made.
        /// </summary>
        /// <param name="replaceAllMetadata"><c>true</c> to replace metadata, <c>false</c> to not.</param>
        /// <param name="itemNamingService">Instance of the <see cref="IItemNamingService"/> interface.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        public override bool BeforeMetadataRefresh(bool replaceAllMetadata, IItemNamingService itemNamingService)
        {
            var hasChanges = base.BeforeMetadataRefresh(replaceAllMetadata, itemNamingService);

            if (!IndexNumber.HasValue && !string.IsNullOrEmpty(Path))
            {
                // Legacy parentId -> parentPath lookup stays on the BaseItem.LibraryManager
                // static (GetParent(), out of scope for this migration); only the naming
                // computation itself moves to the injected service (PR34/PR35 boundary).
                var parentPath = GetParent()?.ContainingFolderPath;
                IndexNumber ??= itemNamingService.GetSeasonNumberFromPath(Path, parentPath);

                // If a change was made record it
                if (IndexNumber.HasValue)
                {
                    hasChanges = true;
                }
            }

            return hasChanges;
        }
    }
}
