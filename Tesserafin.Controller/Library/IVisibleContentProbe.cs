using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Narrow port answering one question for <see cref="IUserViewCatalog"/>: does a playlists or
    /// boxsets library hold at least one item the user may see? Such a library only lists items it
    /// references, so an empty or fully forbidden one is left out of the user's views.
    /// </summary>
    /// <remarks>
    /// Replaces the catalog's former <c>Lazy&lt;IItemQueryService&gt;</c> probe (DEBT-SERVER-1).
    /// Implementations must not depend on <see cref="ILibraryManager"/>, <see cref="IUserViewManager"/>,
    /// <c>IChannelManager</c> or <see cref="IItemQueryService"/>, directly or deferred: the catalog sits
    /// below all four in the construction graph.
    /// </remarks>
    public interface IVisibleContentProbe
    {
        /// <summary>
        /// Gets a value indicating whether <paramref name="library"/> holds an item visible to
        /// <paramref name="user"/>.
        /// </summary>
        /// <param name="library">A top-level playlists or boxsets library folder.</param>
        /// <param name="user">The user whose visibility rules apply.</param>
        /// <returns><c>true</c> if at least one child or linked child is visible to the user.</returns>
        bool HasVisibleContent(Folder library, User user);
    }
}
