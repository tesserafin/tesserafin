#nullable disable

#pragma warning disable CA1724, CS1591

using System;
using System.Text.Json.Serialization;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Entities
{
    [Tesserafin.Common.RequiresSourceSerialisation]
    public class AudioBook : Audio.Audio, IHasSeries, IHasLookupInfo<SongInfo>
    {
        [JsonIgnore]
        public override bool SupportsPositionTicksResume => true;

        [JsonIgnore]
        public override bool SupportsPlayedStatus => true;

        [JsonIgnore]
        public string SeriesPresentationUniqueKey { get; set; }

        [JsonIgnore]
        public string SeriesName { get; set; }

        [JsonIgnore]
        public Guid SeriesId { get; set; }

        public string FindSeriesSortName()
        {
            return SeriesName;
        }

        public string FindSeriesName()
        {
            return SeriesName;
        }

        public string FindSeriesPresentationUniqueKey()
        {
            return SeriesPresentationUniqueKey;
        }

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            return 0;
        }

        public Guid FindSeriesId()
        {
            return SeriesId;
        }

        public override bool CanDownload(IFileSystem fileSystem)
        {
            return MediaPathProtocol.IsLocalFile(Path, fileSystem);
        }

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Book;
        }
    }
}
