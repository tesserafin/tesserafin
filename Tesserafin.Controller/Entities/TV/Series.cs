#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Querying;
using MetadataProvider = Tesserafin.Model.Entities.MetadataProvider;

namespace Tesserafin.Controller.Entities.TV
{
    /// <summary>
    /// Class Series.
    /// </summary>
    public class Series : Folder, IHasTrailers, IHasDisplayOrder, IHasLookupInfo<SeriesInfo>, IMetadataContainer, ISupportsBoxSetGrouping
    {
        public Series()
        {
            AirDays = Array.Empty<DayOfWeek>();
        }

        public DayOfWeek[] AirDays { get; set; }

        public string AirTime { get; set; }

        [JsonIgnore]
        public override bool SupportsAddingToPlaylist => true;

        [JsonIgnore]
        public override bool IsPreSorted => true;

        [JsonIgnore]
        public override bool SupportsDateLastMediaAdded => true;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        [JsonIgnore]
        public override bool SupportsPeople => true;

        /// <inheritdoc />
        [JsonIgnore]
        public IReadOnlyList<BaseItem> LocalTrailers => GetExtras([Tesserafin.Model.Entities.ExtraType.Trailer]).ToArray();

        /// <summary>
        /// Gets or sets the display order.
        /// </summary>
        /// <remarks>
        /// Valid options are airdate, dvd or absolute.
        /// </remarks>
        public string DisplayOrder { get; set; }

        /// <summary>
        /// Gets or sets the status.
        /// </summary>
        /// <value>The status.</value>
        public SeriesStatus? Status { get; set; }

        public override bool SupportsRawQueryItems => false;

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            double value = 2;
            value /= 3;

            return value;
        }

        public override string CreatePresentationUniqueKey()
        {
            var key = GetGroupingKey(LibraryManager.GetLibraryOptions(this).EnableAutomaticSeriesGrouping);
            return key is null
                ? base.CreatePresentationUniqueKey()
                : FormatGroupingKey(key, GetPreferredMetadataLanguage(), LibraryManager.GetCollectionFolders(this));
        }

        /// <summary>Computes the series presentation key from the supplied catalogue and settings.</summary>
        /// <param name="options">The effective library options.</param>
        /// <param name="lookup">The lookup for parent language inheritance.</param>
        /// <param name="collections">The library membership resolver.</param>
        /// <param name="configuration">The server metadata defaults.</param>
        /// <returns>The presentation key.</returns>
        public string CreatePresentationUniqueKey(
            LibraryOptions options,
            IItemLookupService lookup,
            ICollectionFolderResolver collections,
            ServerConfiguration configuration)
        {
            var key = GetGroupingKey(options.EnableAutomaticSeriesGrouping);
            if (key is null)
            {
                return base.CreatePresentationUniqueKey();
            }

            var libraries = collections.GetCollectionFolders(this);
            var language = GetPreferredMetadataLanguage(
                GetParents(lookup),
                () => libraries,
                () => options.PreferredMetadataLanguage,
                () => configuration.PreferredMetadataLanguage);

            return FormatGroupingKey(key, language, libraries);
        }

        private string GetGroupingKey(bool enableAutomaticSeriesGrouping)
        {
            if (!enableAutomaticSeriesGrouping)
            {
                return null;
            }

            var keys = GetUserDataKeys();
            return keys.Count > 1 ? keys[0] : null;
        }

        private static string FormatGroupingKey(string key, string language, IReadOnlyList<Folder> libraries)
        {
            if (!string.IsNullOrEmpty(language))
            {
                key += "-" + language;
            }

            return libraries.Count == 0
                ? key
                : key + "-" + string.Join('-', libraries.Select(library => library.Id.ToString("N", CultureInfo.InvariantCulture)));
        }

        private static string GetUniqueSeriesKey(BaseItem series)
        {
            return series.GetPresentationUniqueKey();
        }

        public override int GetChildCount(User user)
        {
            var seriesKey = GetUniqueSeriesKey(this);

            var result = LibraryManager.GetCount(new InternalItemsQuery(user)
            {
                AncestorWithPresentationUniqueKey = null,
                SeriesPresentationUniqueKey = seriesKey,
                IncludeItemTypes = new[] { BaseItemKind.Season },
                IsVirtualItem = false,
                Limit = 0,
                DtoOptions = new DtoOptions(false)
                {
                    EnableImages = false
                }
            });

            return result;
        }

        public override int GetRecursiveChildCount(User user)
        {
            var seriesKey = GetUniqueSeriesKey(this);

            var query = new InternalItemsQuery(user)
            {
                AncestorWithPresentationUniqueKey = null,
                SeriesPresentationUniqueKey = seriesKey,
                DtoOptions = new DtoOptions(false)
                {
                    EnableImages = false
                }
            };

            if (query.IncludeItemTypes.Length == 0)
            {
                query.IncludeItemTypes = new[] { BaseItemKind.Episode };
            }

            query.IsVirtualItem = false;
            query.Limit = 0;
            var totalRecordCount = LibraryManager.GetCount(query);

            return totalRecordCount;
        }

        /// <summary>
        /// Gets the user data key.
        /// </summary>
        /// <returns>System.String.</returns>
        public override List<string> GetUserDataKeys()
        {
            var list = base.GetUserDataKeys();

            if (this.TryGetProviderId(MetadataProvider.Imdb, out var key))
            {
                list.Insert(0, key);
            }

            if (this.TryGetProviderId(MetadataProvider.Tvdb, out key))
            {
                list.Insert(0, key);
            }

            if (this.TryGetProviderId(MetadataProvider.Custom, out key))
            {
                list.Insert(0, key);
            }

            return list;
        }

        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query)
        {
            return GetSeasons(user, new DtoOptions(true));
        }

        public IReadOnlyList<BaseItem> GetSeasons(User user, DtoOptions options)
        {
            var query = new InternalItemsQuery(user)
            {
                DtoOptions = options
            };

            SetSeasonQueryOptions(query, user);

            return LibraryManager.GetItemList(query);
        }

        private void SetSeasonQueryOptions(InternalItemsQuery query, User user)
        {
            var seriesKey = GetUniqueSeriesKey(this);

            query.AncestorWithPresentationUniqueKey = null;
            query.SeriesPresentationUniqueKey = seriesKey;
            query.IncludeItemTypes = new[] { BaseItemKind.Season };
            query.OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) };

            if (user is not null && !user.DisplayMissingEpisodes)
            {
                query.IsMissing = false;
            }
        }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            var user = query.User;

            if (SourceType == SourceType.Channel)
            {
                try
                {
                    query.Parent = this;
                    query.ChannelIds = [ChannelId];
                    return channelManager.GetChannelItemsInternal(query, new Progress<double>(), CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    // Already logged at lower levels
                    return new QueryResult<BaseItem>();
                }
            }

            if (query.Recursive)
            {
                var seriesKey = GetUniqueSeriesKey(this);

                query.AncestorWithPresentationUniqueKey = null;
                query.SeriesPresentationUniqueKey = seriesKey;
                if (query.OrderBy.Count == 0)
                {
                    query.OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) };
                }

                if (query.IncludeItemTypes.Length == 0)
                {
                    query.IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Season };
                }

                query.IsVirtualItem = false;
                return LibraryManager.GetItemsResult(query);
            }

            SetSeasonQueryOptions(query, user);

            return LibraryManager.GetItemsResult(query);
        }

        public IEnumerable<BaseItem> GetEpisodes(User user, DtoOptions options, bool shouldIncludeMissingEpisodes)
        {
            return GetEpisodes(user, options, shouldIncludeMissingEpisodes, null);
        }

        public IEnumerable<BaseItem> GetEpisodes(User user, DtoOptions options, bool shouldIncludeMissingEpisodes, IItemSortService itemSortService)
            => ReadEpisodes(user, options, shouldIncludeMissingEpisodes, itemSortService, static query => LibraryManager.GetItemList(query));

        /// <summary>Reads grouped episodes, including missing entries, for a background metadata refresh.</summary>
        /// <param name="options">The item data to load.</param>
        /// <param name="repository">The item repository.</param>
        /// <param name="itemSortService">The episode sort service.</param>
        /// <returns>The grouped episodes in display order.</returns>
        public IEnumerable<BaseItem> GetCatalogueEpisodes(DtoOptions options, IItemRepository repository, IItemSortService itemSortService)
            => ReadEpisodes(null, options, true, itemSortService, repository.GetItemList);

        private IEnumerable<BaseItem> ReadEpisodes(
            User user,
            DtoOptions options,
            bool shouldIncludeMissingEpisodes,
            IItemSortService itemSortService,
            Func<InternalItemsQuery, IReadOnlyList<BaseItem>> readItems)
        {
            var seriesKey = GetUniqueSeriesKey(this);

            var query = new InternalItemsQuery(user)
            {
                AncestorWithPresentationUniqueKey = null,
                SeriesPresentationUniqueKey = seriesKey,
                IncludeItemTypes = new[] { BaseItemKind.Episode, BaseItemKind.Season },
                OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
                DtoOptions = options,
            };

            if (!shouldIncludeMissingEpisodes)
            {
                query.IsMissing = false;
            }

            var allItems = readItems(query);

            var allSeriesEpisodes = allItems.OfType<Episode>().ToList();

            var allEpisodes = allItems.OfType<Season>()
                .SelectMany(i => i.GetEpisodes(this, user, allSeriesEpisodes, options, shouldIncludeMissingEpisodes, itemSortService))
                .Reverse();

            // Specials could appear twice based on above - once in season 0, once in the aired season
            // This depends on settings for that series
            // When this happens, remove the duplicate from season 0

            return allEpisodes.DistinctBy(i => i.Id).Reverse();
        }

        public async Task RefreshAllMetadata(LibraryScanContext context, MetadataRefreshOptions refreshOptions, IProgress<double> progress, CancellationToken cancellationToken)
        {
            InvalidateCatalogueChildren(context.Lookup);
            // Refresh bottom up, seasons and episodes first, then the series
            var items = GetRecursiveCatalogueChildren(context.Repository, context.Lookup, context.Links);

            var totalItems = items.Count;
            var numComplete = 0;

            // Refresh seasons
            foreach (var item in items)
            {
                if (item is not Season)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (refreshOptions.RefreshItem(item))
                {
                    await item.RefreshMetadata(context.Metadata.Value, context.Deletions, context.Writes, refreshOptions, cancellationToken).ConfigureAwait(false);
                }

                numComplete++;
                double percent = numComplete;
                percent /= totalItems;
                progress.Report(percent * 100);
            }

            // Refresh episodes and other children
            foreach (var item in items)
            {
                if (item is Season)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                bool skipItem = item is Episode episode
                    && refreshOptions.MetadataRefreshMode != MetadataRefreshMode.FullRefresh
                    && !refreshOptions.ReplaceAllMetadata
                    && episode.IsMissingEpisode
                    && episode.LocationType == LocationType.Virtual
                    && episode.PremiereDate.HasValue
                    && (DateTime.UtcNow - episode.PremiereDate.Value).TotalDays > 30;

                if (!skipItem)
                {
                    if (refreshOptions.RefreshItem(item))
                    {
                        await item.RefreshMetadata(context.Metadata.Value, context.Deletions, context.Writes, refreshOptions, cancellationToken).ConfigureAwait(false);
                    }
                }

                numComplete++;
                double percent = numComplete;
                percent /= totalItems;
                progress.Report(percent * 100);
            }

            refreshOptions = new MetadataRefreshOptions(refreshOptions);
            await context.Metadata.Value.RefreshSingleItem(this, refreshOptions, cancellationToken).ConfigureAwait(false);
        }

        public List<BaseItem> GetSeasonEpisodes(Season parentSeason, User user, DtoOptions options, bool shouldIncludeMissingEpisodes)
        {
            return GetSeasonEpisodes(parentSeason, user, options, shouldIncludeMissingEpisodes, null);
        }

        public List<BaseItem> GetSeasonEpisodes(Season parentSeason, User user, DtoOptions options, bool shouldIncludeMissingEpisodes, IItemSortService itemSortService)
            => ReadSeasonEpisodes(parentSeason, user, options, shouldIncludeMissingEpisodes, itemSortService, static query => LibraryManager.GetItemList(query));

        internal List<BaseItem> GetCatalogueSeasonEpisodes(Season parentSeason, DtoOptions options, IItemRepository repository, IItemSortService itemSortService)
            => ReadSeasonEpisodes(parentSeason, null, options, true, itemSortService, repository.GetItemList);

        private List<BaseItem> ReadSeasonEpisodes(
            Season parentSeason,
            User user,
            DtoOptions options,
            bool shouldIncludeMissingEpisodes,
            IItemSortService itemSortService,
            Func<InternalItemsQuery, IReadOnlyList<BaseItem>> readItems)
        {
            var queryFromSeries = ConfigurationManager.Configuration.DisplaySpecialsWithinSeasons;

            // add optimization when this setting is not enabled
            var seriesKey = queryFromSeries ?
                GetUniqueSeriesKey(this) :
                GetUniqueSeriesKey(parentSeason);

            var query = new InternalItemsQuery(user)
            {
                AncestorWithPresentationUniqueKey = queryFromSeries ? null : seriesKey,
                SeriesPresentationUniqueKey = queryFromSeries ? seriesKey : null,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
                DtoOptions = options
            };

            if (!shouldIncludeMissingEpisodes)
            {
                query.IsMissing = false;
            }

            IReadOnlyList<BaseItem> allItems;
            if (SourceType == SourceType.Channel)
            {
                try
                {
                    query.Parent = parentSeason;
                    query.ChannelIds = [ChannelId];
                    allItems = [.. ChannelManager.GetChannelItemsInternal(query, new Progress<double>(), CancellationToken.None).GetAwaiter().GetResult().Items];
                }
                catch
                {
                    // Already logged at lower levels
                    return [];
                }
            }
            else
            {
                allItems = readItems(query);
            }

            return GetSeasonEpisodes(parentSeason, user, allItems, options, shouldIncludeMissingEpisodes, itemSortService);
        }

        public List<BaseItem> GetSeasonEpisodes(Season parentSeason, User user, IEnumerable<BaseItem> allSeriesEpisodes, DtoOptions options, bool shouldIncludeMissingEpisodes)
        {
            return GetSeasonEpisodes(parentSeason, user, allSeriesEpisodes, options, shouldIncludeMissingEpisodes, null);
        }

        public List<BaseItem> GetSeasonEpisodes(Season parentSeason, User user, IEnumerable<BaseItem> allSeriesEpisodes, DtoOptions options, bool shouldIncludeMissingEpisodes, IItemSortService itemSortService)
        {
            if (allSeriesEpisodes is null)
            {
                return GetSeasonEpisodes(parentSeason, user, options, shouldIncludeMissingEpisodes, itemSortService);
            }

            var episodes = FilterEpisodesBySeason(allSeriesEpisodes, parentSeason, ConfigurationManager.Configuration.DisplaySpecialsWithinSeasons);

            var sortBy = (parentSeason.IndexNumber ?? -1) == 0 ? ItemSortBy.SortName : ItemSortBy.AiredEpisodeOrder;

            // Partial threading (PR50/N): callers that can supply an IItemSortService use it
            // directly; callers that cannot (e.g. Season.GetChildren, the virtual GetChildren
            // contract has no service to give) keep falling back to the obsolete static facade.
            // The pragma below therefore survives this tranche on purpose -- see
            // docs/major-rewrite-plan-v13.md § PR49/N and § PR50/N.
            if (itemSortService is not null)
            {
                return itemSortService.Sort(episodes, user, new[] { sortBy }, SortOrder.Ascending).ToList();
            }

#pragma warning disable CS0618 // static LibraryManager.Sort facade left in place pending Folder.GetChildren/Series.GetEpisodes cascade, see docs/major-rewrite-plan-v13.md § PR49/N
            return LibraryManager.Sort(episodes, user, new[] { sortBy }, SortOrder.Ascending).ToList();
#pragma warning restore CS0618
        }

        /// <summary>
        /// Filters the episodes by season.
        /// </summary>
        /// <param name="episodes">The episodes.</param>
        /// <param name="parentSeason">The season.</param>
        /// <param name="includeSpecials"><c>true</c> to include special, <c>false</c> to not.</param>
        /// <returns>The set of episodes.</returns>
        public static IEnumerable<BaseItem> FilterEpisodesBySeason(IEnumerable<BaseItem> episodes, Season parentSeason, bool includeSpecials)
        {
            var seasonNumber = parentSeason.IndexNumber;
            var seasonPresentationKey = GetUniqueSeriesKey(parentSeason);

            var supportSpecialsInSeason = includeSpecials && seasonNumber.HasValue && seasonNumber.Value != 0;

            return episodes.Where(episode =>
            {
                var episodeItem = (Episode)episode;

                var currentSeasonNumber = supportSpecialsInSeason ? episodeItem.AiredSeasonNumber : episode.ParentIndexNumber;
                if (currentSeasonNumber.HasValue && seasonNumber.HasValue && currentSeasonNumber.Value == seasonNumber.Value)
                {
                    return true;
                }

                if (!currentSeasonNumber.HasValue && !seasonNumber.HasValue && parentSeason.LocationType == LocationType.Virtual)
                {
                    return episodeItem.Season is null or { LocationType: LocationType.Virtual };
                }

                var season = episodeItem.Season;
                return season is not null && string.Equals(GetUniqueSeriesKey(season), seasonPresentationKey, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>
        /// Filters the episodes by season.
        /// </summary>
        /// <param name="episodes">The episodes.</param>
        /// <param name="seasonNumber">The season.</param>
        /// <param name="includeSpecials"><c>true</c> to include special, <c>false</c> to not.</param>
        /// <returns>The set of episodes.</returns>
        public static IEnumerable<Episode> FilterEpisodesBySeason(IEnumerable<Episode> episodes, int seasonNumber, bool includeSpecials)
        {
            if (!includeSpecials || seasonNumber < 1)
            {
                return episodes.Where(i => (i.ParentIndexNumber ?? -1) == seasonNumber);
            }

            return episodes.Where(i =>
            {
                var episode = i;

                if (episode is not null)
                {
                    var currentSeasonNumber = episode.AiredSeasonNumber;

                    return currentSeasonNumber.HasValue && currentSeasonNumber.Value == seasonNumber;
                }

                return false;
            });
        }

        protected override bool GetBlockUnratedValue(User user)
        {
            return user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Contains(UnratedItem.Series);
        }

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Series;
        }

        public SeriesInfo GetLookupInfo()
        {
            var info = GetItemLookupInfo<SeriesInfo>();

            return info;
        }

        public override bool BeforeMetadataRefresh(bool replaceAllMetadata, IItemNamingService itemNamingService)
        {
            var hasChanges = base.BeforeMetadataRefresh(replaceAllMetadata, itemNamingService);

            if (!ProductionYear.HasValue)
            {
                var info = itemNamingService.ParseName(Name);

                var yearInName = info.Year;

                if (yearInName.HasValue)
                {
                    ProductionYear = yearInName;
                    hasChanges = true;
                }
            }

            return hasChanges;
        }
    }
}
