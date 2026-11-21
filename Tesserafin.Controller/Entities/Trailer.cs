#nullable disable

#pragma warning disable CA1819, CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Providers;

namespace Tesserafin.Controller.Entities
{
    /// <summary>
    /// Class Trailer.
    /// </summary>
    public class Trailer : Video, IHasLookupInfo<TrailerInfo>
    {
        public Trailer()
        {
            TrailerTypes = Array.Empty<TrailerType>();
        }

        public TrailerType[] TrailerTypes { get; set; }

        public override double GetDefaultPrimaryImageAspectRatio()
            => 2.0 / 3;

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Trailer;
        }

        public TrailerInfo GetLookupInfo()
        {
            var info = GetItemLookupInfo<TrailerInfo>();

            if (!IsInMixedFolder && IsFileProtocol)
            {
                info.Name = System.IO.Path.GetFileName(ContainingFolderPath);
            }

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
