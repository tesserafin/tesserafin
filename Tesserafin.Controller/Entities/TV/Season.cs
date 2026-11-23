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
using Tesserafin.Controller.Persistence;
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

                return GetRelatedItem(seriesId) as Series;
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
            => IndexNumber is int number ? CreatePresentationUniqueKey(Series, number) : base.CreatePresentationUniqueKey();

        /// <summary>Creates the presentation key using the supplied lookup for the owning series.</summary>
        /// <param name="lookup">The item identity lookup.</param>
        /// <returns>The presentation key.</returns>
        public string CreatePresentationUniqueKey(IItemLookupService lookup)
            => IndexNumber is int number ? CreatePresentationUniqueKey(GetSeries(lookup), number) : base.CreatePresentationUniqueKey();

        private string CreatePresentationUniqueKey(Series series, int number)
            => series is null ? base.CreatePresentationUniqueKey() : series.PresentationUniqueKey + "-" + number.ToString("000", CultureInfo.InvariantCulture);

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

            var items = UserViewBuilder.Filter(GetEpisodes(user, query.DtoOptions, true, query.Listing), user, query, GetListingServices(query));

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

            var items = UserViewBuilder.Filter(GetEpisodes(user, query.DtoOptions, true, query.Listing), user, query, GetListingServices(query));

            return PostFilterAndSort(items, query, collectionManager, itemSortService);
        }

        /// <summary>
        /// Gets the episodes.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="options">The options to use.</param>
        /// <param name="shouldIncludeMissingEpisodes">If missing episodes should be included.</param>
        /// <param name="listing">The listing services for this request.</param>
        /// <returns>Set of episodes.</returns>
        public List<BaseItem> GetEpisodes(User user, DtoOptions options, bool shouldIncludeMissingEpisodes, IItemListingServices listing)
        {
            return GetEpisodes(Series, user, options, shouldIncludeMissingEpisodes, listing);
        }

        /// <summary>Reads grouped season episodes, including missing entries, for a background metadata refresh.</summary>
        /// <param name="options">The item data to load.</param>
        /// <param name="repository">The item repository.</param>
        /// <param name="lookup">The lookup used to resolve the owning series.</param>
        /// <param name="listing">Sort and channel services for episode listing.</param>
        /// <returns>The season episodes in display order.</returns>
        public IReadOnlyList<BaseItem> GetCatalogueEpisodes(
            DtoOptions options,
            IItemRepository repository,
            IItemLookupService lookup,
            IItemListingServices listing)
            => GetSeries(lookup)?.GetCatalogueSeasonEpisodes(this, options, repository, listing) ?? [];

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

        public override List<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query)
        {
            return GetEpisodes(user, new DtoOptions(true), true, query.Listing);
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
        /// Gets the owning series through the supplied lookup, falling back to the parent chain
        /// when the series id is missing.
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
        /// Finds the owning series id by walking the parent chain via the given lookup service.
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
            => CreateLookupInfo(Series, null);

        /// <summary>Builds provider lookup information using the supplied lookup for the owning series.</summary>
        /// <param name="lookup">The item identity lookup.</param>
        /// <param name="locale">The resolved metadata language and country.</param>
        /// <returns>The lookup information.</returns>
        public SeasonInfo GetLookupInfo(IItemLookupService lookup, (string Language, string CountryCode) locale)
            => CreateLookupInfo(GetSeries(lookup), locale);

        private SeasonInfo CreateLookupInfo(Series series, (string Language, string CountryCode)? locale)
        {
            var id = GetItemLookupInfo<SeasonInfo>(locale);

            if (series is not null)
            {
                id.SeriesProviderIds = series.ProviderIds;
                id.SeriesDisplayOrder = series.DisplayOrder;
            }

            return id;
        }

        /// <summary>Prepares the season name and infers a missing season number from its path.</summary>
        /// <param name="replaceAllMetadata">Whether to replace all metadata.</param>
        /// <param name="services">The metadata refresh services.</param>
        /// <param name="lookup">The catalogue lookup for the season folder's parent.</param>
        /// <returns>Whether the preparation changed the season.</returns>
        public bool BeforeMetadataRefresh(bool replaceAllMetadata, MetadataRefreshServices services, IItemLookupService lookup)
        {
            var hasChanges = this.BeforeMetadataRefresh(replaceAllMetadata, services);
            if (IndexNumber.HasValue || string.IsNullOrEmpty(Path))
            {
                return hasChanges;
            }

            var parentPath = GetParent(lookup)?.ContainingFolderPath;
            IndexNumber = services.ItemNamingService.GetSeasonNumberFromPath(Path, parentPath);
            return hasChanges || IndexNumber.HasValue;
        }
    }
}
