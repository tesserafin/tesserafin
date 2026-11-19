#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Entities.TV
{
    /// <summary>
    /// Class Episode.
    /// </summary>
    public class Episode : Video, IHasTrailers, IHasLookupInfo<EpisodeInfo>, IHasSeries
    {
        /// <inheritdoc />
        [JsonIgnore]
        public IReadOnlyList<BaseItem> LocalTrailers => GetExtras([Tesserafin.Model.Entities.ExtraType.Trailer]).ToArray();

        /// <summary>
        /// Gets or sets the season in which it aired.
        /// </summary>
        /// <value>The aired season.</value>
        public int? AirsBeforeSeasonNumber { get; set; }

        public int? AirsAfterSeasonNumber { get; set; }

        public int? AirsBeforeEpisodeNumber { get; set; }

        /// <summary>
        /// Gets or sets the ending episode number for double episodes.
        /// </summary>
        /// <value>The index number.</value>
        public int? IndexNumberEnd { get; set; }

        [JsonIgnore]
        protected override bool SupportsOwnedItems => IsStacked || MediaSourceCount > 1;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => true;

        [JsonIgnore]
        public override bool SupportsPeople => true;

        [JsonIgnore]
        public int? AiredSeasonNumber => AirsAfterSeasonNumber ?? AirsBeforeSeasonNumber ?? ParentIndexNumber;

        [JsonIgnore]
        public override Folder LatestItemsIndexContainer => Series;

        [JsonIgnore]
        public override Guid DisplayParentId => SeasonId;

        [JsonIgnore]
        protected override bool EnableDefaultVideoUserDataKeys => false;

        /// <summary>
        /// Gets the Episode's Series Instance.
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
        public Season Season
        {
            get
            {
                var seasonId = SeasonId;
                if (seasonId.IsEmpty())
                {
                    seasonId = FindSeasonId();
                }

                return seasonId.IsEmpty() ? null : (LibraryManager.GetItemById(seasonId) as Season);
            }
        }

        [JsonIgnore]
        public bool IsInSeasonFolder => FindParent<Season>() is not null;

        [JsonIgnore]
        public string SeriesPresentationUniqueKey { get; set; }

        [JsonIgnore]
        public string SeriesName { get; set; }

        [JsonIgnore]
        public string SeasonName { get; set; }

        [JsonIgnore]
        public override bool SupportsRemoteImageDownloading
        {
            get
            {
                if (IsMissingEpisode)
                {
                    return false;
                }

                return true;
            }
        }

        [JsonIgnore]
        public bool IsMissingEpisode => LocationType == LocationType.Virtual;

        [JsonIgnore]
        public Guid SeasonId { get; set; }

        [JsonIgnore]
        public Guid SeriesId { get; set; }

        public string FindSeriesSortName()
        {
            var series = Series;
            return series is null ? SeriesName : series.SortName;
        }

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            // hack for tv plugins
            if (SourceType == SourceType.Channel)
            {
                return 0;
            }

            return 16.0 / 9;
        }

        /// <inheritdoc />
        public override string GetInheritedOriginalLanguage()
        {
            return OriginalLanguage ?? Series?.GetInheritedOriginalLanguage();
        }

        public override List<string> GetUserDataKeys()
        {
            var list = base.GetUserDataKeys();

            var series = Series;
            if (series is not null && ParentIndexNumber.HasValue && IndexNumber.HasValue)
            {
                var seriesUserDataKeys = series.GetUserDataKeys();
                var take = seriesUserDataKeys.Count;
                if (seriesUserDataKeys.Count > 1)
                {
                    take--;
                }

                var newList = seriesUserDataKeys.GetRange(0, take);
                var suffix = ParentIndexNumber.Value.ToString("000", CultureInfo.InvariantCulture) + IndexNumber.Value.ToString("000", CultureInfo.InvariantCulture);
                for (int i = 0; i < take; i++)
                {
                    newList[i] = newList[i] + suffix;
                }

                newList.AddRange(list);
                list = newList;
            }

            return list;
        }

        public string FindSeriesPresentationUniqueKey()
            => Series?.PresentationUniqueKey;

        public string FindSeasonName()
        {
            var season = Season;

            if (season is null)
            {
                if (ParentIndexNumber.HasValue)
                {
                    return "Season " + ParentIndexNumber.Value.ToString(CultureInfo.InvariantCulture);
                }

                return "Season Unknown";
            }

            return season.Name;
        }

        public string FindSeriesName()
        {
            var series = Series;
            return series is null ? SeriesName : series.Name;
        }

        public Guid FindSeasonId()
            => ResolveSeasonId(FindParent<Season>(), () => Series?.Children);

        /// <summary>Finds the parent season, or a numbered season for an episode stored in the series folder.</summary>
        /// <param name="lookup">The item identity lookup.</param>
        /// <param name="repository">The item repository.</param>
        /// <returns>The season ID, or <see cref="Guid.Empty"/> if no season matches.</returns>
        public Guid FindSeasonId(IItemLookupService lookup, IItemRepository repository)
            => ResolveSeasonId(FindParent<Season>(lookup), () => GetSeries(lookup)?.GetCatalogueChildren(repository, lookup));

        private Guid ResolveSeasonId(Season season, Func<IEnumerable<BaseItem>> getSeriesChildren)
        {
            if (season is null && ParentIndexNumber is int seasonNumber)
            {
                season = getSeriesChildren()?.OfType<Season>().FirstOrDefault(item => item.IndexNumber == seasonNumber);
            }

            return season?.Id ?? Guid.Empty;
        }

        /// <summary>
        /// Creates the name of the sort.
        /// </summary>
        /// <returns>System.String.</returns>
        protected override string CreateSortName()
        {
            return (ParentIndexNumber is not null ? ParentIndexNumber.Value.ToString("000 - ", CultureInfo.InvariantCulture) : string.Empty)
                    + (IndexNumber is not null ? IndexNumber.Value.ToString("0000 - ", CultureInfo.InvariantCulture) : string.Empty) + Name;
        }

        /// <summary>
        /// Determines whether [contains episode number] [the specified number].
        /// </summary>
        /// <param name="number">The number.</param>
        /// <returns><c>true</c> if [contains episode number] [the specified number]; otherwise, <c>false</c>.</returns>
        public bool ContainsEpisodeNumber(int number)
        {
            if (IndexNumber.HasValue)
            {
                if (IndexNumberEnd.HasValue)
                {
                    return number >= IndexNumber.Value && number <= IndexNumberEnd.Value;
                }

                return IndexNumber.Value == number;
            }

            return false;
        }

        public Guid FindSeriesId()
        {
            var series = FindParent<Series>();
            return series is null ? Guid.Empty : series.Id;
        }

        /// <summary>
        /// Gets the Episode's <see cref="Series"/> via the given lookup service, without touching the
        /// static <see cref="BaseItem.LibraryManager"/>. The parameterless <see cref="Series"/>
        /// property remains as a compatibility wrapper over the static path; new callers that hold an
        /// <see cref="IItemLookupService"/> should prefer this overload. Both the direct id resolution
        /// and the empty-id parent fallback (<see cref="FindSeriesId(IItemLookupService)"/>) go through
        /// <paramref name="lookup"/>, so this path is fully static-free.
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
            return series is null ? Guid.Empty : series.Id;
        }

        public override IEnumerable<Guid> GetAncestorIds()
        {
            var list = base.GetAncestorIds().ToList();

            var seasonId = SeasonId;

            if (!seasonId.IsEmpty() && !list.Contains(seasonId))
            {
                list.Add(seasonId);
            }

            return list;
        }

        public override IEnumerable<FileSystemMetadata> GetDeletePaths()
        {
            return new[]
            {
                new FileSystemMetadata
                {
                    FullName = Path,
                    IsDirectory = IsFolder
                }
            }.Concat(GetLocalMetadataFilesToDelete());
        }

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Series;
        }

        public EpisodeInfo GetLookupInfo()
        {
            var id = GetItemLookupInfo<EpisodeInfo>();

            var series = Series;

            if (series is not null)
            {
                id.SeriesProviderIds = series.ProviderIds;
                id.SeriesDisplayOrder = series.DisplayOrder;
            }

            if (Season is not null)
            {
                id.SeasonProviderIds = Season.ProviderIds;
            }

            id.IsMissingEpisode = IsMissingEpisode;
            id.IndexNumberEnd = IndexNumberEnd;

            return id;
        }
    }
}
