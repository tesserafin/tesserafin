using System;
using System.Collections.Generic;
using System.Linq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Extensions;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="ICollectionFolderResolver"/>, built on the id
    /// lookup and the user root folder only.
    /// </summary>
    /// <remarks>
    /// Walks the item up to its top-level ancestor - by parent, or by owner for a parentless owned
    /// item such as an extra - stopping below an <see cref="AggregateFolder"/>, then returns the
    /// libraries whose path or one of whose physical locations equals that ancestor's path, ignoring
    /// case. Moved unchanged from <c>LibraryManager</c> (DEBT-SERVER-2).
    /// </remarks>
    internal sealed class CollectionFolderResolver : ICollectionFolderResolver
    {
        private readonly IItemLookupService _itemLookupService;
        private readonly IUserRootFolderProvider _userRootFolderProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="CollectionFolderResolver"/> class.
        /// </summary>
        /// <param name="itemLookupService">The cached id lookup (parents and owners).</param>
        /// <param name="userRootFolderProvider">The user root folder (its children are the libraries).</param>
        public CollectionFolderResolver(IItemLookupService itemLookupService, IUserRootFolderProvider userRootFolderProvider)
        {
            _itemLookupService = itemLookupService;
            _userRootFolderProvider = userRootFolderProvider;
        }

        /// <inheritdoc />
        public List<Folder> GetCollectionFolders(BaseItem item)
        {
            return GetCollectionFolders(item, GetLibraries(_userRootFolderProvider.GetUserRootFolder(), _itemLookupService).OfType<Folder>());
        }

        /// <summary>
        /// Gets the root's children, resolving a <see cref="UserRootFolder"/>'s through the given lookup
        /// rather than the static <c>BaseItem.LibraryManager</c> (DEBT-SERVER-4).
        /// </summary>
        /// <param name="root">The user root folder.</param>
        /// <param name="lookup">The id lookup.</param>
        /// <returns>The libraries.</returns>
        internal static IEnumerable<BaseItem> GetLibraries(Folder root, IItemLookupService lookup)
            => root is UserRootFolder userRoot ? userRoot.GetLibraries(lookup) : root.Children;

        /// <inheritdoc />
        public List<Folder> GetCollectionFolders(BaseItem item, IEnumerable<Folder> allUserRootChildren)
        {
            while (item is not null)
            {
                var parent = item.GetParent(_itemLookupService);

                if (parent is AggregateFolder)
                {
                    break;
                }

                if (parent is null)
                {
                    var owner = item.GetOwner(_itemLookupService);

                    if (owner is null)
                    {
                        break;
                    }

                    item = owner;
                }
                else
                {
                    item = parent;
                }
            }

            if (item is null)
            {
                return [];
            }

            return allUserRootChildren
                .Where(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase) || i.PhysicalLocations.Contains(item.Path.AsSpan(), StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}
