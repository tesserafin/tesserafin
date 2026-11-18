#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Serialization;

namespace Tesserafin.Controller.Entities
{
    /// <summary>
    /// Specialized Folder class that points to a subset of the physical folders in the system.
    /// It is created from the user-specific folders within the system root.
    /// </summary>
    public class CollectionFolder : Folder, ICollectionFolder
    {
        private static readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
        private static readonly ConcurrentDictionary<string, LibraryOptions> _libraryOptions = new ConcurrentDictionary<string, LibraryOptions>();

        /// <summary>
        /// Initializes a new instance of the <see cref="CollectionFolder"/> class.
        /// </summary>
        public CollectionFolder()
        {
            PhysicalLocationsList = Array.Empty<string>();
            PhysicalFolderIds = Array.Empty<Guid>();
        }

        /// <summary>
        /// Event raised when library options are updated for any collection folder.
        /// </summary>
        public static event EventHandler<LibraryOptionsUpdatedEventArgs> LibraryOptionsUpdated;

        /// <summary>
        /// Gets the display preferences id.
        /// </summary>
        /// <remarks>
        /// Allow different display preferences for each collection folder.
        /// </remarks>
        /// <value>The display prefs id.</value>
        [JsonIgnore]
        public override Guid DisplayPreferencesId => Id;

        [JsonIgnore]
        public override string[] PhysicalLocations => PhysicalLocationsList;

        public string[] PhysicalLocationsList { get; set; }

        public Guid[] PhysicalFolderIds { get; set; }

        public static IXmlSerializer XmlSerializer { get; set; }

        public static IServerApplicationHost ApplicationHost { get; set; }

        [JsonIgnore]
        public override bool SupportsPlayedStatus => false;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        public CollectionType? CollectionType { get; set; }

        /// <summary>
        /// Gets or sets the item's children.
        /// </summary>
        /// <remarks>
        /// Our children are actually just references to the ones in the physical root...
        /// Setting to null propagates invalidation to physical folders since the getter
        /// reads from the physical folders and never uses the backing field.
        /// </remarks>
        /// <value>The actual children.</value>
        [JsonIgnore]
        public override IEnumerable<BaseItem> Children
        {
            get => GetPhysicalFolders(LibraryManager).SelectMany(folder => folder.Children);
            set
            {
                // The getter delegates to physical folders, so invalidate their caches.
                foreach (var folder in GetPhysicalFolders(LibraryManager))
                {
                    folder.Children = null;
                }
            }
        }

        [JsonIgnore]
        public override bool SupportsPeople => false;

        public override bool CanDelete()
        {
            return false;
        }

        public LibraryOptions GetLibraryOptions()
        {
            return GetLibraryOptions(Path);
        }

        public override bool IsVisible(User user, bool skipAllowedTagsCheck, ITagVisibilityService tagVisibility)
        {
            if (GetLibraryOptions().Enabled)
            {
                return base.IsVisible(user, skipAllowedTagsCheck, tagVisibility);
            }

            return false;
        }

        private static LibraryOptions LoadLibraryOptions(string path)
        {
            try
            {
                if (XmlSerializer.DeserializeFromFile(typeof(LibraryOptions), GetLibraryOptionsPath(path)) is not LibraryOptions result)
                {
                    return new LibraryOptions();
                }

                foreach (var mediaPath in result.PathInfos)
                {
                    if (!string.IsNullOrEmpty(mediaPath.Path))
                    {
                        mediaPath.Path = ApplicationHost.ExpandVirtualPath(mediaPath.Path);
                    }
                }

                return result;
            }
            catch (FileNotFoundException)
            {
                return new LibraryOptions();
            }
            catch (IOException)
            {
                return new LibraryOptions();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error loading library options");

                return new LibraryOptions();
            }
        }

        private static string GetLibraryOptionsPath(string path)
        {
            return System.IO.Path.Combine(path, "options.xml");
        }

        public void UpdateLibraryOptions(LibraryOptions options)
        {
            SaveLibraryOptions(Path, options);
        }

        public static LibraryOptions GetLibraryOptions(string path)
            => _libraryOptions.GetOrAdd(path, LoadLibraryOptions);

        public static void SaveLibraryOptions(string path, LibraryOptions options)
        {
            var clone = JsonSerializer.Deserialize<LibraryOptions>(JsonSerializer.SerializeToUtf8Bytes(options, _jsonOptions), _jsonOptions);
            foreach (var mediaPath in clone.PathInfos)
            {
                if (!string.IsNullOrEmpty(mediaPath.Path))
                {
                    mediaPath.Path = ApplicationHost.ReverseVirtualPath(mediaPath.Path);
                }
            }

            XmlSerializer.SerializeToFile(clone, GetLibraryOptionsPath(path));
            _libraryOptions[path] = options;

            LibraryOptionsUpdated?.Invoke(null, new LibraryOptionsUpdatedEventArgs(path, options));
        }

        public static void OnCollectionFolderChange()
            => _libraryOptions.Clear();

        public override bool IsSaveLocalMetadataEnabled()
        {
            return true;
        }

        protected override Task<bool> RefreshOwnedItemsAsync(IItemDeletionCommands deletions, IItemWriteCommands writes, MetadataRefreshOptions options, CancellationToken cancellationToken)
            => writes.RefreshCollectionFolderMembershipAsync(this, options.DirectoryService, cancellationToken);

        /// <summary>
        /// Compare our current children (presumably just read from the repo) with the current state of the file system and adjust for any changes
        /// ***Currently does not contain logic to maintain items that are unavailable in the file system***.
        /// </summary>
        /// <param name="deletions">Explicit deletion operations for this refresh.</param>
        /// <param name="writes">Explicit item write/resolution ownership.</param>
        /// <param name="images">Explicit image preparation and commit lifecycle.</param>
        /// <param name="resolution">Filesystem candidate resolution for the scan.</param>
        /// <param name="progress">The progress.</param>
        /// <param name="recursive">if set to <c>true</c> [recursive].</param>
        /// <param name="refreshChildMetadata">if set to <c>true</c> [refresh child metadata].</param>
        /// <param name="allowRemoveRoot">remove item even this folder is root.</param>
        /// <param name="refreshOptions">The refresh options.</param>
        /// <param name="directoryService">The directory service.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        protected override Task ValidateChildrenInternal(IItemDeletionCommands deletions, IItemWriteCommands writes, IItemImageService images, IItemResolutionService resolution, IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>Resolves the currently committed physical folder identities through a catalogue lookup.</summary>
        /// <param name="lookup">The item lookup used for physical folder resolution.</param>
        /// <returns>The available folders in membership order.</returns>
        public IEnumerable<Folder> GetPhysicalFolders(IItemLookupService lookup)
            => PhysicalFolderIds.Select(id => lookup.GetItemById(id)).OfType<Folder>();
    }
}
