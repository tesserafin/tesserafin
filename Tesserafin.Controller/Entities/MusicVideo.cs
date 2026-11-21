#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;

namespace Tesserafin.Controller.Entities
{
    public class MusicVideo : Video, IHasArtist, IHasMusicGenres, IHasLookupInfo<MusicVideoInfo>
    {
        public MusicVideo()
        {
            Artists = Array.Empty<string>();
        }

        /// <inheritdoc />
        [JsonIgnore]
        public IReadOnlyList<string> Artists { get; set; }

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Music;
        }

        public MusicVideoInfo GetLookupInfo()
        {
            var info = GetItemLookupInfo<MusicVideoInfo>();

            info.Artists = Artists;

            return info;
        }

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
