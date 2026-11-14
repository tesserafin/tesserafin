using System;
using Tesserafin.Controller.Entities;
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
    }
}
