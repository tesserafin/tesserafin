#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LibraryTaskScheduler;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Entities
{
    /// <summary>
    /// Specialized Folder class that points to a subset of the physical folders in the system.
    /// It is created from the user-specific folders within the system root.
    /// </summary>
    public class CollectionFolder : Folder, ICollectionFolder
    {
        private bool _requiresRefresh;

        /// <summary>
        /// Initializes a new instance of the <see cref="CollectionFolder"/> class.
        /// </summary>
        public CollectionFolder()
        {
            PhysicalLocationsList = Array.Empty<string>();
            PhysicalFolderIds = Array.Empty<Guid>();
        }

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
        /// always delegates to <see cref="GetActualChildren"/> and never reads the backing field.
        /// </remarks>
        /// <value>The actual children.</value>
        [JsonIgnore]
        public override IEnumerable<BaseItem> Children
        {
            get => GetActualChildren();
            set
            {
                // The getter delegates to physical folders, so invalidate their caches.
                foreach (var folder in GetPhysicalFolders(true))
                {
                    folder.Children = null;
                }
            }
        }

        [JsonIgnore]
        public override bool SupportsPeople => false;

        public override bool CanDelete(IChannelManager channels)
        {
            return false;
        }

        public override bool IsVisible(User user, bool skipAllowedTagsCheck, IStandaloneVisibilityService tagVisibility)
        {
            var options = tagVisibility.GetLibraryOptions(this);
            if (options.Enabled)
            {
                return base.IsVisible(user, skipAllowedTagsCheck, tagVisibility);
            }

            return false;
        }

        public override bool IsSaveLocalMetadataEnabled()
        {
            return true;
        }

        protected override FileSystemMetadata[] GetFileSystemChildren(IDirectoryService directoryService)
        {
            return ReadLibraryDirectory(directoryService, true).Entries
                ?? throw new DirectoryNotFoundException($"Cannot read library directory {ContainingFolderPath}.");
        }

        public override bool RequiresRefresh()
        {
            var changed = base.RequiresRefresh() || _requiresRefresh;

            if (!changed)
            {
                var locations = PhysicalLocations;

                var newLocations = ReadLibraryDirectory(new DirectoryService(FileSystem), false).PhysicalLocations;

                if (!locations.SequenceEqual(newLocations))
                {
                    changed = true;
                }
            }

            if (!changed)
            {
                var folderIds = PhysicalFolderIds;

                var newFolderIds = GetPhysicalFolders(false).Select(i => i.Id).ToList();

                if (!folderIds.SequenceEqual(newFolderIds))
                {
                    changed = true;
                }
            }

            return changed;
        }

        public override bool BeforeMetadataRefresh(bool replaceAllMetadata, IItemNamingService itemNamingService)
        {
            var changed = base.BeforeMetadataRefresh(replaceAllMetadata, itemNamingService) || _requiresRefresh;
            _requiresRefresh = false;
            return changed;
        }

        public override double? GetRefreshProgress(IProviderManager providerManager)
        {
            var folders = GetPhysicalFolders(true).ToList();
            double totalProgresses = 0;
            var foldersWithProgress = 0;

            foreach (var folder in folders)
            {
                var progress = providerManager.GetRefreshProgress(folder.Id);
                if (progress.HasValue)
                {
                    totalProgresses += progress.Value;
                    foldersWithProgress++;
                }
            }

            if (foldersWithProgress == 0)
            {
                return null;
            }

            return totalProgresses / foldersWithProgress;
        }

        protected override Task<bool> RefreshLinkedChildrenAsync(IItemWriteCommands writes, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(RefreshLinkedChildrenInternal(true));
        }

        private bool RefreshLinkedChildrenInternal(bool setFolders)
        {
            var physicalFolders = GetPhysicalFolders(false)
                .ToList();

            var linkedChildren = physicalFolders
                .SelectMany(c => c.LinkedChildren)
                .ToList();

            var changed = !linkedChildren.SequenceEqual(LinkedChildren, new LinkedChildComparer(FileSystem));

            LinkedChildren = linkedChildren.ToArray();

            var folderIds = PhysicalFolderIds;
            var newFolderIds = physicalFolders.Select(i => i.Id).ToArray();

            if (!folderIds.SequenceEqual(newFolderIds))
            {
                changed = true;
                if (setFolders)
                {
                    PhysicalFolderIds = newFolderIds;
                }
            }

            return changed;
        }

        private (FileSystemMetadata[] Entries, string[] PhysicalLocations) ReadLibraryDirectory(IDirectoryService directoryService, bool setPhysicalLocations)
        {
            var directory = FileSystem.GetDirectoryInfo(ContainingFolderPath);
            var discovery = FileData.ReadLibraryDirectory(directory, directoryService, FileSystem, ConfigurationManager.ApplicationPaths, Logger, physicalRoot: false);
            _requiresRefresh = _requiresRefresh || !discovery.PhysicalLocations.SequenceEqual(PhysicalLocations);
            if (setPhysicalLocations)
            {
                PhysicalLocationsList = discovery.PhysicalLocations;
            }

            return discovery;
        }

        /// <summary>
        /// Compare our current children (presumably just read from the repo) with the current state of the file system and adjust for any changes
        /// ***Currently does not contain logic to maintain items that are unavailable in the file system***.
        /// </summary>
        /// <param name="services">Deletion, write, image, scheduling and channel services for this validation.</param>
        /// <param name="progress">The progress.</param>
        /// <param name="recursive">if set to <c>true</c> [recursive].</param>
        /// <param name="refreshChildMetadata">if set to <c>true</c> [refresh child metadata].</param>
        /// <param name="allowRemoveRoot">remove item even this folder is root.</param>
        /// <param name="refreshOptions">The refresh options.</param>
        /// <param name="directoryService">The directory service.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        protected override Task ValidateChildrenInternal(IFolderValidationServices services, IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public IEnumerable<BaseItem> GetActualChildren()
        {
            return GetPhysicalFolders(true).SelectMany(c => c.Children);
        }

        public IEnumerable<Folder> GetPhysicalFolders()
        {
            return GetPhysicalFolders(true);
        }

        private IEnumerable<Folder> GetPhysicalFolders(bool enableCache)
        {
            if (enableCache)
            {
                return PhysicalFolderIds.Select(i => LibraryManager.GetItemById(i)).OfType<Folder>();
            }

            var rootChildren = LibraryManager.RootFolder.Children
                .OfType<Folder>()
                .ToList();

            return PhysicalLocations
                    .Where(i => !FileSystem.AreEqual(i, Path))
                    .SelectMany(i => GetPhysicalParents(i, rootChildren))
                    .DistinctBy(x => x.Id);
        }

        private IEnumerable<Folder> GetPhysicalParents(string path, List<Folder> rootChildren)
        {
            var result = rootChildren
                .Where(i => FileSystem.AreEqual(i.Path, path))
                .ToList();

            if (result.Count == 0)
            {
                if (LibraryManager.FindByPath(path, true) is Folder folder)
                {
                    result.Add(folder);
                }
            }

            return result;
        }
    }
}
