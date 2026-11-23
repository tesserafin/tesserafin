using System;
using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Narrow boundary for resolving an item by id while applying a specific user's visibility
    /// rules (parental/tag restrictions, blocked tags, etc.). Item *lookup* (finding the item) and
    /// *access control* (deciding whether this user is allowed to see it) are separate concerns:
    /// this interface owns the latter and depends on <see cref="IItemLookupService"/> for the
    /// former.
    /// </summary>
    /// <remarks>
    /// Introduced in PR77 by splitting the user-aware overload out of <see cref="IItemLookupService"/>,
    /// which is now a strictly unauthenticated lookup port. The <c>User</c> parameter is
    /// intentionally non-nullable: a null user does not mean "skip visibility filtering", it means
    /// there is no user to check visibility against - callers with an optional user should route a
    /// null user to a plain <see cref="IItemLookupService.GetItemById{T}(Guid)"/> call instead of
    /// calling this service (see <see cref="ILibraryManager.GetItemById{T}(Guid, User)"/> for the
    /// reference implementation of that routing).
    /// </remarks>
    public interface IItemAccessService
    {
        /// <summary>
        /// Gets the item by id, as T, if it exists and is visible to <paramref name="user"/>.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <param name="user">The user to validate visibility against.</param>
        /// <typeparam name="T">The type of item.</typeparam>
        /// <returns>The item if found and visible to <paramref name="user"/>; otherwise <c>null</c>.</returns>
        T? GetVisibleItemById<T>(Guid id, User user)
            where T : BaseItem;

        /// <summary>
        /// Gets a value indicating whether <paramref name="user"/> may see <paramref name="item"/> itself
        /// (tags and parental rating), through the visibility services rather than the entity statics.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="user">The user.</param>
        /// <returns><c>true</c> when the item is visible.</returns>
        bool IsVisible(BaseItem item, User user);

        /// <summary>
        /// Gets a value indicating whether <paramref name="user"/> may see <paramref name="item"/> on its own,
        /// including its parents and libraries, through the visibility services rather than the entity statics.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="user">The user.</param>
        /// <returns><c>true</c> when the item is visible standalone.</returns>
        bool IsVisibleStandalone(BaseItem item, User user);

        /// <summary>
        /// Gets the children of <paramref name="folder"/> that <paramref name="user"/> may see, read through the
        /// visibility services.
        /// </summary>
        /// <param name="folder">The folder.</param>
        /// <param name="user">The user.</param>
        /// <param name="itemSortService">The sort service the folder's own ordering uses.</param>
        /// <returns>The visible children.</returns>
        IReadOnlyList<BaseItem> GetChildren(Folder folder, User user, IItemSortService itemSortService);
    }
}
