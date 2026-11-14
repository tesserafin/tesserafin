using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;

namespace Tesserafin.Controller.Sorting;

/// <summary>
/// Provides item sorting/comparer operations.
/// </summary>
public interface IItemSortService
{
    /// <summary>
    /// Registers the available item comparers.
    /// </summary>
    /// <param name="itemComparers">The item comparers.</param>
    void AddParts(IEnumerable<IBaseItemComparer> itemComparers);

    /// <summary>
    /// Sorts the specified items.
    /// </summary>
    /// <param name="items">The items.</param>
    /// <param name="user">The user.</param>
    /// <param name="sortBy">The sort by.</param>
    /// <param name="sortOrder">The sort order.</param>
    /// <returns>IEnumerable{BaseItem}.</returns>
    IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder);

    /// <summary>
    /// Sorts the specified items.
    /// </summary>
    /// <param name="items">The items.</param>
    /// <param name="user">The user.</param>
    /// <param name="orderBy">The list of (sort by, sort order) pairs.</param>
    /// <returns>IEnumerable{BaseItem}.</returns>
    IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<(ItemSortBy OrderBy, SortOrder SortOrder)> orderBy);
}
