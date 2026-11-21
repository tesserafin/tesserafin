#nullable disable

using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Model.Querying;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Owns the dependencies (<see cref="Tesserafin.Controller.Channels.IChannelManager"/>,
    /// <see cref="Tesserafin.Controller.Collections.ICollectionManager"/>, <see cref="IUserViewManager"/>,
    /// <see cref="Tesserafin.Controller.TV.ITVSeriesManager"/>) that <c>Folder.GetItems</c> and
    /// <c>Folder.GetItemList</c> currently require every caller to thread through by hand.
    /// First step of moving item-querying logic out of <see cref="BaseItem"/>/<see cref="Folder"/>
    /// and into a dedicated application service, per the point 4 trajectory correction (major rewrite
    /// plan, "Revue externe post-PR14").
    /// </summary>
    public interface IItemQueryService : IItemCatalogueQuery
    {
        /// <summary>
        /// Gets the child items of <paramref name="folder"/> matching <paramref name="query"/>.
        /// </summary>
        /// <param name="folder">Folder to query.</param>
        /// <param name="query">Query to use.</param>
        /// <returns>Query result.</returns>
        QueryResult<BaseItem> GetItems(Folder folder, InternalItemsQuery query);

        /// <summary>
        /// Gets the child items of <paramref name="folder"/> matching <paramref name="query"/>, as a flat list.
        /// </summary>
        /// <param name="folder">Folder to query.</param>
        /// <param name="query">Query to use.</param>
        /// <returns>Matching items.</returns>
        IReadOnlyList<BaseItem> GetItemList(Folder folder, InternalItemsQuery query);
    }
}
