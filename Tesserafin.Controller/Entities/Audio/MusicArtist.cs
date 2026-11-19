#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using MetadataProvider = Tesserafin.Model.Entities.MetadataProvider;

namespace Tesserafin.Controller.Entities.Audio
{
    /// <summary>
    /// Class MusicArtist.
    /// </summary>
    [Tesserafin.Common.RequiresSourceSerialisation]
    public class MusicArtist : Folder, IItemByName, IHasMusicGenres, IHasDualAccess, IHasLookupInfo<ArtistInfo>
    {
        [JsonIgnore]
        public bool IsAccessedByName => ParentId.IsEmpty();

        [JsonIgnore]
        public override bool IsFolder => !IsAccessedByName;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        [JsonIgnore]
        public override bool SupportsCumulativeRunTimeTicks => true;

        [JsonIgnore]
        public override bool IsDisplayedAsFolder => true;

        [JsonIgnore]
        public override bool SupportsAddingToPlaylist => true;

        [JsonIgnore]
        public override bool SupportsPlayedStatus => false;

        /// <summary>
        /// Gets the folder containing the item.
        /// If the item is a folder, it returns the folder itself.
        /// </summary>
        /// <value>The containing folder path.</value>
        [JsonIgnore]
        public override string ContainingFolderPath => Path;

        [JsonIgnore]
        public override IEnumerable<BaseItem> Children
        {
            get
            {
                if (IsAccessedByName)
                {
                    return Enumerable.Empty<BaseItem>();
                }

                return base.Children;
            }
        }

        [JsonIgnore]
        public override bool SupportsPeople => false;

        public static string GetPath(string name)
        {
            return GetPath(name, true);
        }

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            return 1;
        }

        public override bool CanDelete()
        {
            return !IsAccessedByName;
        }

        public IReadOnlyList<BaseItem> GetTaggedItems(InternalItemsQuery query)
        {
            if (query.IncludeItemTypes.Length == 0)
            {
                query.IncludeItemTypes = new[] { BaseItemKind.Audio, BaseItemKind.MusicVideo, BaseItemKind.MusicAlbum };
                query.ArtistIds = new[] { Id };
            }

            return LibraryManager.GetItemList(query);
        }

        public override int GetChildCount(User user)
        {
            return IsAccessedByName ? 0 : base.GetChildCount(user);
        }

        public override bool IsSaveLocalMetadataEnabled()
        {
            if (IsAccessedByName)
            {
                return true;
            }

            return base.IsSaveLocalMetadataEnabled();
        }

        protected override async Task ValidateChildrenInternal(IItemDeletionCommands deletions, IItemWriteCommands writes, IItemImageService images, IItemResolutionService resolution, ILibraryRefreshProgress refreshProgress, IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            if (IsAccessedByName)
            {
                // Should never get in here anyway
                return;
            }

            await base.ValidateChildrenInternal(deletions, writes, images, resolution, refreshProgress, progress, recursive, refreshChildMetadata, false, refreshOptions, directoryService, cancellationToken).ConfigureAwait(false);
        }

        public override List<string> GetUserDataKeys()
        {
            var list = base.GetUserDataKeys();

            list.InsertRange(0, GetUserDataKeys(this));
            return list;
        }

        /// <summary>
        /// Gets the user data key.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>System.String.</returns>
        private static List<string> GetUserDataKeys(MusicArtist item)
        {
            var list = new List<string>();
            if (item.TryGetProviderId(MetadataProvider.MusicBrainzArtist, out var externalId))
            {
                list.Add("Artist-Musicbrainz-" + externalId);
            }

            list.Add("Artist-" + (item.Name ?? string.Empty).RemoveDiacritics());
            return list;
        }

        public override string CreatePresentationUniqueKey()
        {
            return "Artist-" + (Name ?? string.Empty).RemoveDiacritics();
        }

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Music;
        }

        public ArtistInfo GetLookupInfo()
        {
            var info = GetItemLookupInfo<ArtistInfo>();

            info.SongInfos = GetRecursiveChildren(i => i is Audio)
                .Cast<Audio>()
                .Select(i => i.GetLookupInfo())
                .ToList();

            return info;
        }

        public static string GetPath(string name, bool normalizeName)
            => GetPath(name, FileSystem, ConfigurationManager.ApplicationPaths.ArtistsPath, normalizeName);

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
            if (IsAccessedByName)
            {
                var newPath = GetRebasedPath();
                if (!string.Equals(Path, newPath, StringComparison.Ordinal))
                {
                    Logger.LogDebug("{0} path has changed from {1} to {2}", GetType().Name, Path, newPath);
                    return true;
                }
            }

            return base.RequiresRefresh();
        }

        /// <summary>
        /// This is called before any metadata refresh and returns true or false indicating if changes were made.
        /// </summary>
        /// <param name="replaceAllMetadata">Option to replace metadata.</param>
        /// <param name="itemNamingService">Instance of the <see cref="IItemNamingService"/> interface.</param>
        /// <returns>True if metadata changed.</returns>
        public override bool BeforeMetadataRefresh(bool replaceAllMetadata, IItemNamingService itemNamingService)
        {
            var hasChanges = base.BeforeMetadataRefresh(replaceAllMetadata, itemNamingService);

            if (IsAccessedByName)
            {
                var newPath = GetRebasedPath();
                if (!string.Equals(Path, newPath, StringComparison.Ordinal))
                {
                    Path = newPath;
                    hasChanges = true;
                }
            }

            return hasChanges;
        }
    }
}
