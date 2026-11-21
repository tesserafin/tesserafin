#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.IO;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Entities.Audio
{
    /// <summary>
    /// Class MusicGenre.
    /// </summary>
    [Tesserafin.Common.RequiresSourceSerialisation]
    public class MusicGenre : BaseItem, IItemByName
    {
        [JsonIgnore]
        public override bool SupportsAddingToPlaylist => true;

        [JsonIgnore]
        public override bool SupportsAncestors => false;

        [JsonIgnore]
        public override bool IsDisplayedAsFolder => true;

        /// <summary>
        /// Gets the folder containing the item.
        /// If the item is a folder, it returns the folder itself.
        /// </summary>
        /// <value>The containing folder path.</value>
        [JsonIgnore]
        public override string ContainingFolderPath => Path;

        [JsonIgnore]
        public override bool SupportsPeople => false;

        public override List<string> GetUserDataKeys()
        {
            var list = base.GetUserDataKeys();

            list.Insert(0, GetType().Name + "-" + (Name ?? string.Empty).RemoveDiacritics());
            return list;
        }

        public override string CreatePresentationUniqueKey()
        {
            return GetUserDataKeys()[0];
        }

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            return 1;
        }

        public override bool CanDelete(IChannelManager channels)
        {
            return false;
        }

        public override bool IsSaveLocalMetadataEnabled()
        {
            return true;
        }

        public IReadOnlyList<BaseItem> GetTaggedItems(InternalItemsQuery query)
        {
            query.GenreIds = new[] { Id };
            query.IncludeItemTypes = new[] { BaseItemKind.MusicVideo, BaseItemKind.Audio, BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist };

            return LibraryManager.GetItemList(query);
        }

        public static string GetPath(string name, IFileSystem fileSystem, string root, bool normalizeName = true)
        {
            // Trim the period at the end because windows will have a hard time with that
            var validName = normalizeName ?
                fileSystem.GetValidFilename(name).Trim().TrimEnd('.') :
                name;

            return SafeDirectoryLeafName.CombineWithRoot(root, validName, nameof(name));
        }

        private string GetRebasedPath(IFileSystem fileSystem, IServerApplicationPaths applicationPaths)
        {
            return GetPath(System.IO.Path.GetFileName(Path), fileSystem, applicationPaths.MusicGenrePath, normalizeName: false);
        }

        public override bool RequiresRefresh(IFileSystem fileSystem, IServerApplicationPaths applicationPaths)
        {
            var newPath = GetRebasedPath(fileSystem, applicationPaths);
            if (!string.Equals(Path, newPath, StringComparison.Ordinal))
            {
                Logger.LogDebug("{0} path has changed from {1} to {2}", GetType().Name, Path, newPath);
                return true;
            }

            return base.RequiresRefresh(fileSystem, applicationPaths);
        }

        /// <summary>
        /// This is called before any metadata refresh and returns true or false indicating if changes were made.
        /// </summary>
        /// <param name="replaceAllMetadata">Option to replace metadata.</param>
        /// <param name="itemNamingService">Instance of the <see cref="IItemNamingService"/> interface.</param>
        /// <param name="fileSystem">The filesystem used to resolve item paths.</param>
        /// <param name="applicationPaths">The configured storage paths.</param>
        /// <returns>True if metadata changed.</returns>
        public override bool BeforeMetadataRefresh(bool replaceAllMetadata, IItemNamingService itemNamingService, IFileSystem fileSystem, IServerApplicationPaths applicationPaths)
        {
            var hasChanges = base.BeforeMetadataRefresh(replaceAllMetadata, itemNamingService, fileSystem, applicationPaths);

            var newPath = GetRebasedPath(fileSystem, applicationPaths);
            if (!string.Equals(Path, newPath, StringComparison.Ordinal))
            {
                Path = newPath;
                hasChanges = true;
            }

            return hasChanges;
        }
    }
}
