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
using Tesserafin.Controller.Persistence;
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
        /// reads from the physical folders and never uses the backing field.
        /// </remarks>
        /// <value>The actual children.</value>
        [JsonIgnore]
        public override IEnumerable<BaseItem> Children
        {
            get => GetCatalogueChildren(ItemRepository, LibraryManager);
            set => InvalidateCatalogueChildren(LibraryManager);
        }

        /// <inheritdoc />
        public override IEnumerable<BaseItem> GetCatalogueChildren(IItemRepository repository, IItemLookupService lookup)
            => GetPhysicalFolders(lookup).SelectMany(folder => folder.GetCatalogueChildren(repository, lookup));

        /// <inheritdoc />
        public override void InvalidateCatalogueChildren(IItemLookupService lookup)
        {
            foreach (var folder in GetPhysicalFolders(lookup))
            {
                folder.InvalidateCatalogueChildren(lookup);
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

        /// <summary>Resolves the currently committed physical folder identities through a catalogue lookup.</summary>
        /// <param name="lookup">The item lookup used for physical folder resolution.</param>
        /// <returns>The available folders in membership order.</returns>
        public IEnumerable<Folder> GetPhysicalFolders(IItemLookupService lookup)
            => PhysicalFolderIds.Select(id => lookup.GetItemById(id)).OfType<Folder>();
    }
}
