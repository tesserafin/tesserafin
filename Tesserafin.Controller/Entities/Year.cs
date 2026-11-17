#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.IO;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Library;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Entities
{
    /// <summary>
    /// Class Year.
    /// </summary>
    [Tesserafin.Common.RequiresSourceSerialisation]
    public class Year : BaseItem, IItemByName
    {
        [JsonIgnore]
        public override bool SupportsAncestors => false;

        [JsonIgnore]
        public override bool SupportsPeople => false;

        /// <summary>
        /// Gets the folder containing the item.
        /// If the item is a folder, it returns the folder itself.
        /// </summary>
        /// <value>The containing folder path.</value>
        [JsonIgnore]
        public override string ContainingFolderPath => Path;

        public override bool CanDelete(IChannelManager channels)
        {
            return false;
        }

        public override List<string> GetUserDataKeys()
        {
            var list = base.GetUserDataKeys();

            list.Insert(0, "Year-" + Name);
            return list;
        }

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            double value = 2;
            value /= 3;

            return value;
        }

        public override bool IsSaveLocalMetadataEnabled()
        {
            return true;
        }

        public IReadOnlyList<BaseItem> GetTaggedItems(InternalItemsQuery query)
        {
            if (!int.TryParse(Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                return new List<BaseItem>();
            }

            query.Years = new[] { year };

            return LibraryManager.GetItemList(query);
        }

        public int? GetYearValue()
        {
            if (int.TryParse(Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                return year;
            }

            return null;
        }

        public static string GetPath(string name)
        {
            return GetPath(name, true);
        }

        public static string GetPath(string name, bool normalizeName)
            => GetPath(name, FileSystem, ConfigurationManager.ApplicationPaths.YearPath, normalizeName);

        public static string GetPath(string name, IFileSystem fileSystem, string root, bool normalizeName = true)
        {
            // Trim the period at the end because windows will have a hard time with that
            var validName = normalizeName ?
                fileSystem.GetValidFilename(name).Trim().TrimEnd('.') :
                name;

            return SafeDirectoryLeafName.CombineWithRoot(root, validName, nameof(name));
        }

        private string GetRebasedPath()
        {
            return GetPath(System.IO.Path.GetFileName(Path), false);
        }

        public override bool RequiresRefresh()
        {
            var newPath = GetRebasedPath();
            if (!string.Equals(Path, newPath, StringComparison.Ordinal))
            {
                Logger.LogDebug("{0} path has changed from {1} to {2}", GetType().Name, Path, newPath);
                return true;
            }

            return base.RequiresRefresh();
        }

        /// <summary>
        /// This is called before any metadata refresh and returns true if changes were made.
        /// </summary>
        /// <param name="replaceAllMetadata">Whether to replace all metadata.</param>
        /// <param name="itemNamingService">Instance of the <see cref="IItemNamingService"/> interface.</param>
        /// <returns>true if the item has change, else false.</returns>
        public override bool BeforeMetadataRefresh(bool replaceAllMetadata, IItemNamingService itemNamingService)
        {
            var hasChanges = base.BeforeMetadataRefresh(replaceAllMetadata, itemNamingService);

            var newPath = GetRebasedPath();
            if (!string.Equals(Path, newPath, StringComparison.Ordinal))
            {
                Path = newPath;
                hasChanges = true;
            }

            return hasChanges;
        }
    }
}
