using System;
using System.Collections.Generic;
using System.Linq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;

namespace Tesserafin.Server.Core.Sorting;

/// <summary>
/// Provides item sorting/comparer operations.
/// </summary>
public class ItemSortService : IItemSortService
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemSortService"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    public ItemSortService(IUserManager userManager, IUserDataManager userDataManager)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <summary>
    /// Gets or sets the comparers.
    /// </summary>
    /// <value>The comparers.</value>
    private IBaseItemComparer[] Comparers { get; set; } = [];

    /// <inheritdoc/>
    public void AddParts(IEnumerable<IBaseItemComparer> itemComparers)
    {
        Comparers = itemComparers.ToArray();
    }

    /// <inheritdoc/>
    public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder)
    {
        IOrderedEnumerable<BaseItem>? orderedItems = null;

        foreach (var orderBy in sortBy.Select(o => GetComparer(o, user)).Where(c => c is not null))
        {
            if (orderBy is RandomComparer)
            {
                var randomItems = items.ToArray();
                Random.Shared.Shuffle(randomItems);
                items = randomItems;
                // Items are no longer ordered at this point, so set orderedItems back to null
                orderedItems = null;
            }
            else if (orderedItems is null)
            {
                orderedItems = sortOrder == SortOrder.Descending
                    ? items.OrderByDescending(i => i, orderBy)
                    : items.OrderBy(i => i, orderBy);
            }
            else
            {
                orderedItems = sortOrder == SortOrder.Descending
                    ? orderedItems!.ThenByDescending(i => i, orderBy)
                    : orderedItems!.ThenBy(i => i, orderBy); // orderedItems is set during the first iteration
            }
        }

        return orderedItems ?? items;
    }

    /// <inheritdoc/>
    public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<(ItemSortBy OrderBy, SortOrder SortOrder)> orderBy)
    {
        IOrderedEnumerable<BaseItem>? orderedItems = null;

        foreach (var (name, sortOrder) in orderBy)
        {
            var comparer = GetComparer(name, user);
            if (comparer is null)
            {
                continue;
            }

            if (comparer is RandomComparer)
            {
                var randomItems = items.ToArray();
                Random.Shared.Shuffle(randomItems);
                items = randomItems;
                // Items are no longer ordered at this point, so set orderedItems back to null
                orderedItems = null;
            }
            else if (orderedItems is null)
            {
                orderedItems = sortOrder == SortOrder.Descending
                    ? items.OrderByDescending(i => i, comparer)
                    : items.OrderBy(i => i, comparer);
            }
            else
            {
                orderedItems = sortOrder == SortOrder.Descending
                    ? orderedItems!.ThenByDescending(i => i, comparer)
                    : orderedItems!.ThenBy(i => i, comparer); // orderedItems is set during the first iteration
            }
        }

        return orderedItems ?? items;
    }

    /// <summary>
    /// Gets the comparer.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="user">The user.</param>
    /// <returns>IBaseItemComparer.</returns>
    private IBaseItemComparer? GetComparer(ItemSortBy name, User? user)
    {
        var comparer = Comparers.FirstOrDefault(c => name == c.Type);

        // If it requires a user, create a new one, and assign the user
        if (comparer is IUserBaseItemComparer)
        {
            var userComparer = (IUserBaseItemComparer)Activator.CreateInstance(comparer.GetType())!; // only null for Nullable<T> instances

            userComparer.User = user;
            userComparer.UserManager = _userManager;
            userComparer.UserDataManager = _userDataManager;

            return userComparer;
        }

        return comparer;
    }
}
