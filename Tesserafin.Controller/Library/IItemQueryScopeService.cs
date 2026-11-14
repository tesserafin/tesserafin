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
    /// Introduced in PR85b by lifting the scoping helpers out of <c>LibraryManager</c>
    /// (<c>SetTopParentIdsOrAncestors</c>, <c>AddUserToQuery</c>) into a cycle-free leaf service.
    /// This exists in preparation for PR86, which will let <c>IItemQueryService</c> build global
    /// queries without depending on <see cref="ILibraryManager"/>. <c>LibraryManager</c> keeps its
    /// own copies of the same logic during the transition (temporary duplication; see
    /// <c>docs/pr85b-item-query-scope-service.md</c>) - this interface is not yet consumed by
    /// <c>LibraryManager</c> itself, to avoid re-forming the DI cycle this extraction exists to
    /// avoid.
    /// </remarks>
    public interface IItemQueryScopeService
    {
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
