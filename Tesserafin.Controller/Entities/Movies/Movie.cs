#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.Entities.Movies
{
    /// <summary>
    /// Class Movie.
    /// </summary>
    public class Movie : Video, IHasSpecialFeatures, IHasTrailers, IHasLookupInfo<MovieInfo>, ISupportsBoxSetGrouping
    {
        /// <inheritdoc />
        [JsonIgnore]
        public IReadOnlyList<Guid> SpecialFeatureIds => GetExtras()
            .Where(extra => extra.ExtraType is not null && extra is Video)
            .Select(extra => extra.Id)
            .ToArray();

        /// <inheritdoc />
        [JsonIgnore]
        public IReadOnlyList<BaseItem> LocalTrailers => GetExtras([Tesserafin.Model.Entities.ExtraType.Trailer]).ToArray();

        /// <summary>
        /// Gets or sets the name of the TMDb collection.
        /// </summary>
        /// <value>The name of the TMDb collection.</value>
        public string TmdbCollectionName { get; set; }

        [JsonIgnore]
        public string CollectionName
        {
            get => TmdbCollectionName;
            set => TmdbCollectionName = value;
        }

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            // hack for tv plugins
            if (SourceType == SourceType.Channel)
            {
                return 0;
            }

            return 2.0 / 3;
        }

        /// <inheritdoc />
        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Movie;
        }

        public MovieInfo GetLookupInfo()
        {
            var info = GetItemLookupInfo<MovieInfo>();

            if (!IsInMixedFolder)
            {
                var name = System.IO.Path.GetFileName(ContainingFolderPath);

                if (VideoType == VideoType.VideoFile || VideoType == VideoType.Iso)
                {
                    if (string.Equals(name, System.IO.Path.GetFileName(Path), StringComparison.OrdinalIgnoreCase))
                    {
                        // if the folder has the file extension, strip it
                        name = System.IO.Path.GetFileNameWithoutExtension(name);
                    }
                }

                info.Name = name;
            }

            return info;
        }

        /// <inheritdoc />
        public override bool BeforeMetadataRefresh(bool replaceAllMetadata, MetadataRefreshServices services)
        {
            var hasChanges = base.BeforeMetadataRefresh(replaceAllMetadata, services);

            if (!ProductionYear.HasValue)
            {
                var info = services.ItemNamingService.ParseName(Name);

                var yearInName = info.Year;

                if (yearInName.HasValue)
                {
                    ProductionYear = yearInName;
                    hasChanges = true;
                }
                else
                {
                    // Try to get the year from the folder name
                    if (!IsInMixedFolder)
                    {
                        info = services.ItemNamingService.ParseName(System.IO.Path.GetFileName(ContainingFolderPath));

                        yearInName = info.Year;

                        if (yearInName.HasValue)
                        {
                            ProductionYear = yearInName;
                            hasChanges = true;
                        }
                    }
                }
            }

            return hasChanges;
        }
    }
}
