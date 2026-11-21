using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Model.Querying;

namespace Tesserafin.Controller.Library;

/// <summary>Queries the catalogue with the requesting user's library scope and item access restrictions.</summary>
public interface IItemCatalogueQuery
{
    /// <summary>Gets a page of catalogue items matching the query.</summary>
    /// <param name="query">The catalogue query.</param>
    /// <returns>The page and its available count.</returns>
    QueryResult<BaseItem> GetItems(InternalItemsQuery query);

    /// <summary>Gets the catalogue items matching the query.</summary>
    /// <param name="query">The catalogue query.</param>
    /// <returns>The matching items.</returns>
    IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query);

    /// <summary>Counts catalogue items matching the query.</summary>
    /// <param name="query">The catalogue query.</param>
    /// <returns>The number of matching items.</returns>
    int GetCount(InternalItemsQuery query);
}
