using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Narrow boundary for scoping an <see cref="InternalItemsQuery"/> to a set of parents or to a
    /// user's visible top-level views, ahead of the query reaching the item repository. This is the
    /// "which top parents/ancestors is this query allowed to touch" concern, split out from query
    /// *orchestration* (building and dispatching the final repository call), which remains the
    /// responsibility of the query service/<see cref="ILibraryManager"/>.
    /// </summary>
    /// <remarks>
    /// The single parent/user scoping policy used by both the query service and manager.
    /// It depends on explicit leaf ports and does not hold a manager or deferred query dependency.
    /// Query-specific playlist/collection refusal filtering remains with manager orchestration.
    /// </remarks>
    public interface IItemQueryScopeService
    {
        /// <summary>
        /// Converts named-metadata ancestor filters to physical library scopes when all ancestors
        /// are libraries or user views. Unknown ancestors are rejected; inaccessible or empty scopes
        /// use a sentinel rather than broadening to every library. Ordinary ancestors remain intact.
        /// </summary>
        /// <param name="query">The query whose ancestor/top-parent filters are mutated.</param>
        void SetTopParentOrAncestorIds(InternalItemsQuery query);

        /// <summary>
        /// Scopes <paramref name="query"/> to <paramref name="parents"/>, choosing the most
        /// efficient available filter: top-parent ids when every parent is a collection/user view,
        /// linked-child ids for a single Playlist/BoxSet parent, or ancestor ids otherwise. Clears
        /// <see cref="InternalItemsQuery.Parent"/> once scoping has been applied. An empty resolved
        /// scope is replaced with a fresh sentinel id so the query never silently falls back to
        /// scanning every library.
        /// </summary>
        /// <param name="query">The query to scope. Mutated in place.</param>
        /// <param name="parents">The parent items to scope the query to.</param>
        void SetTopParentIdsOrAncestors(InternalItemsQuery query, IReadOnlyCollection<BaseItem> parents);

        /// <summary>
        /// Scopes <paramref name="query"/> to <paramref name="user"/>'s visible top-level views when
        /// the query does not already carry any other scoping (ancestor/parent/channel/top-parent
        /// ids, presentation keys, item/owner ids). Also sets <paramref name="query"/>'s user if it
        /// does not already have one. No-op when the query is already scoped.
        /// </summary>
        /// <param name="query">The query to scope. Mutated in place.</param>
        /// <param name="user">The user whose visible views scope the query.</param>
        /// <param name="allowExternalContent">
        /// Whether external content (e.g. channels) should be included when resolving the user's
        /// views. Defaults to <c>true</c>.
        /// </param>
        void AddUserToQuery(InternalItemsQuery query, User user, bool allowExternalContent = true);
    }
}
