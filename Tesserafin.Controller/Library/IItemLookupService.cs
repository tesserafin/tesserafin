using System;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Narrow, read-only boundary for looking up items by id. Implementations are expected to be
    /// cache-aware (repeated lookups for the same id should not necessarily hit the underlying
    /// storage). This interface is deliberately unauthenticated - it has no notion of a user or of
    /// visibility; see <see cref="IItemAccessService"/> for the user-aware boundary that layers on
    /// top of it.
    /// </summary>
    /// <remarks>
    /// This interface intentionally excludes mutation (create/register/delete), querying/listing,
    /// and collection folder access. Path lookups use <see cref="IItemPathLookupService"/>.
    /// The user-aware <c>GetItemById&lt;T&gt;(Guid, User)</c> and
    /// <c>GetItemById&lt;T&gt;(Guid, Guid)</c> overloads are also excluded (moved to
    /// <see cref="IItemAccessService"/> in PR77, for the <c>User</c> overload; the <c>Guid</c>
    /// user-id overload was never part of this interface); callers needing either should depend on
    /// <see cref="ILibraryManager"/> or <see cref="IItemAccessService"/> directly.
    /// </remarks>
    public interface IItemLookupService
    {
        /// <summary>
        /// Gets the item by id.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>BaseItem.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="id"/> is <c>null</c>.</exception>
        BaseItem? GetItemById(Guid id);

        /// <summary>
        /// Gets the item by id, as T.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <typeparam name="T">The type of item.</typeparam>
        /// <returns>The item.</returns>
        T? GetItemById<T>(Guid id)
            where T : BaseItem;
    }
}
