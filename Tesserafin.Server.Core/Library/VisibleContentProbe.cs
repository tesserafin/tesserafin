using System.Collections.Generic;
using System.Linq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Playlists;
using Tesserafin.Data.Entities;
using Tesserafin.Extensions;
using Tesserafin.Server.Core.Playlists;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="IVisibleContentProbe"/>, built on the item
    /// repository, the id lookup and the standalone visibility service only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reproduces what <c>ItemQueryService.GetItemList(library, new InternalItemsQuery(user))
    /// .Any(item =&gt; item.IsVisible(user))</c> computed for the two library types that can reach it
    /// (the only <see cref="ICollectionFolder"/> implementations): an item is a candidate when it is a
    /// child of the library's physical folders (<see cref="CollectionFolder"/>), or a
    /// <see cref="Playlist"/> child of the <see cref="PlaylistsFolder"/>, or a linked child of either.
    /// The historical name and post-filters were no-ops for that query, and box-set collapsing only
    /// ever swaps a visible item for a visible box set, so neither can change the answer.
    /// </para>
    /// <para>
    /// Candidates are enumerated lazily and the first one the user may see ends the search; an item
    /// the user may not see never does.
    /// </para>
    /// <para>
    /// Fails closed: an unknown library type, a physical folder or linked child that no longer
    /// resolves, or a path-only linked child (which only shortcut-capable folders produce, and
    /// neither library type is one) contributes nothing.
    /// </para>
    /// </remarks>
    internal sealed class VisibleContentProbe : IVisibleContentProbe
    {
        private readonly IStandaloneVisibilityService _visibility;
        private readonly IItemLookupService _itemLookupService;
        private readonly IItemRepository _itemRepository;

        /// <summary>
        /// Initializes a new instance of the <see cref="VisibleContentProbe"/> class.
        /// </summary>
        /// <param name="itemLookupService">The cached id lookup.</param>
        /// <param name="itemRepository">The item repository (child ids by parent).</param>
        /// <param name="visibility">Decides item visibility through the services, never the entity statics.</param>
        public VisibleContentProbe(IItemLookupService itemLookupService, IItemRepository itemRepository, IStandaloneVisibilityService visibility)
        {
            _visibility = visibility;
            _itemLookupService = itemLookupService;
            _itemRepository = itemRepository;
        }

        /// <inheritdoc />
        public bool HasVisibleContent(Folder library, User user)
        {
            IEnumerable<BaseItem> children = library switch
            {
                CollectionFolder collectionFolder => collectionFolder.GetPhysicalFolders(_itemLookupService)
                    .SelectMany(GetChildren),
                PlaylistsFolder => GetChildren(library).OfType<Playlist>(),
                _ => []
            };

            return children
                .Concat(GetLinkedChildren(library))
                .Any(item => item.IsVisible(user, false, _visibility));
        }

        private IEnumerable<BaseItem> GetChildren(Folder folder)
        {
            return _itemRepository
                .GetItemIdsList(new InternalItemsQuery { Parent = folder, GroupByPresentationUniqueKey = false })
                .Select(_itemLookupService.GetItemById)
                .OfType<BaseItem>();
        }

        private IEnumerable<BaseItem> GetLinkedChildren(Folder library)
        {
            return library.LinkedChildren
                .Select(linkedChild => linkedChild.ItemId)
                .Where(id => id.HasValue && !id.Value.IsEmpty())
                .Select(id => _itemLookupService.GetItemById(id!.Value))
                .OfType<BaseItem>();
        }
    }
}
