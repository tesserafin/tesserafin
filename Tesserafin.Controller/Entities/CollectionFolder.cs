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
                foreach (var folder in GetPhysicalFolders())
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

        protected override Task<bool> RefreshOwnedItemsAsync(IItemDeletionCommands deletions, IItemWriteCommands writes, MetadataRefreshOptions options, CancellationToken cancellationToken)
            => writes.RefreshCollectionFolderMembershipAsync(this, options.DirectoryService, cancellationToken);

        public override double? GetRefreshProgress(IProviderManager providerManager)
        {
            var folders = GetPhysicalFolders().ToList();
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
            return GetPhysicalFolders().SelectMany(c => c.Children);
        }

        public IEnumerable<Folder> GetPhysicalFolders()
            => PhysicalFolderIds.Select(id => LibraryManager.GetItemById(id)).OfType<Folder>();
    }
}
