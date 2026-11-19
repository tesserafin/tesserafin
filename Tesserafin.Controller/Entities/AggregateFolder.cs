#nullable disable

#pragma warning disable CA1819, CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Extensions;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Entities
{
    /// <summary>
    /// Specialized folder that can have items added to it's children by external entities.
    /// Used for our RootFolder so plugins can add items.
    /// </summary>
    public class AggregateFolder : Folder
    {
        private readonly Lock _childIdsLock = new();

        /// <summary>
        /// The _virtual children.
        /// </summary>
        private readonly ConcurrentBag<BaseItem> _virtualChildren = new ConcurrentBag<BaseItem>();
        private bool _requiresRefresh;
        private Guid[] _childrenIds = null;

        public AggregateFolder()
        {
            PhysicalLocationsList = Array.Empty<string>();
        }

        /// <summary>
        /// Gets the virtual children.
        /// </summary>
        /// <value>The virtual children.</value>
        public ConcurrentBag<BaseItem> VirtualChildren => _virtualChildren;

        [JsonIgnore]
        public override bool IsPhysicalRoot => true;

        [JsonIgnore]
        public override bool SupportsPlayedStatus => false;

        [JsonIgnore]
        public override string[] PhysicalLocations => PhysicalLocationsList;

        public string[] PhysicalLocationsList { get; set; }

        public override bool CanDelete()
        {
            return false;
        }

        protected override FileSystemMetadata[] GetFileSystemChildren(IDirectoryService directoryService)
        {
            return ReadLibraryDirectory(directoryService, true).Entries
                ?? throw new DirectoryNotFoundException($"Cannot read library directory {ContainingFolderPath}.");
        }

        protected override IReadOnlyList<BaseItem> LoadChildren()
        {
            lock (_childIdsLock)
            {
                if (_childrenIds is null || _childrenIds.Length == 0)
                {
                    var list = base.LoadChildren();
                    _childrenIds = list.Select(i => i.Id).ToArray();
                    return list;
                }

                return _childrenIds.Select(LibraryManager.GetItemById).Where(i => i is not null).ToList();
            }
        }

        private void ClearCache()
        {
            lock (_childIdsLock)
            {
                _childrenIds = null;
            }
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

            return changed;
        }

        public override bool BeforeMetadataRefresh(bool replaceAllMetadata, IItemNamingService itemNamingService)
        {
            ClearCache();

            var changed = base.BeforeMetadataRefresh(replaceAllMetadata, itemNamingService) || _requiresRefresh;
            _requiresRefresh = false;
            return changed;
        }

        private (FileSystemMetadata[] Entries, string[] PhysicalLocations) ReadLibraryDirectory(IDirectoryService directoryService, bool setPhysicalLocations)
        {
            ClearCache();

            var directory = FileSystem.GetDirectoryInfo(ContainingFolderPath);
            var discovery = FileData.ReadLibraryDirectory(directory, directoryService, FileSystem, CollectionFolder.ApplicationHost, Logger, physicalRoot: true);
            _requiresRefresh = _requiresRefresh || !discovery.PhysicalLocations.SequenceEqual(PhysicalLocations);
            if (setPhysicalLocations)
            {
                PhysicalLocationsList = discovery.PhysicalLocations;
            }

            return discovery;
        }

        protected override IEnumerable<BaseItem> GetNonCachedChildren(IItemResolutionService resolution, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            return base.GetNonCachedChildren(resolution, directoryService, cancellationToken).Concat(_virtualChildren);
        }

        protected override async Task ValidateChildrenInternal(LibraryScanContext context, IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            ClearCache();

            await base.ValidateChildrenInternal(context, progress, recursive, refreshChildMetadata, allowRemoveRoot, refreshOptions, directoryService, cancellationToken)
                .ConfigureAwait(false);

            ClearCache();
        }

        /// <summary>
        /// Adds the virtual child.
        /// </summary>
        /// <param name="child">The child.</param>
        /// <exception cref="ArgumentNullException">Throws if child is null.</exception>
        public void AddVirtualChild(BaseItem child)
        {
            ArgumentNullException.ThrowIfNull(child);

            _virtualChildren.Add(child);
        }

        /// <summary>
        /// Finds the virtual child.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>BaseItem.</returns>
        /// <exception cref="ArgumentNullException">The id is empty.</exception>
        public BaseItem FindVirtualChild(Guid id)
        {
            if (id.IsEmpty())
            {
                throw new ArgumentNullException(nameof(id));
            }

            foreach (var child in _virtualChildren)
            {
                if (child.Id.Equals(id))
                {
                    return child;
                }
            }

            return null;
        }
    }
}
