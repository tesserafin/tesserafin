using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// The path search a box set's legacy linked children take, on top of <see cref="IBoxSetVisibilityService"/>.
    /// </summary>
    /// <remarks>
    /// A linked child without an id, or whose id finds nothing, is searched by its stored path.
    /// The scope passed to <see cref="BoxSet"/>'s visibility check supplies the path search.
    /// Implementations must not depend on <see cref="ILibraryManager"/>, directly or deferred.
    /// </remarks>
    public interface ILinkedChildVisibilityService : IBoxSetVisibilityService
    {
        /// <summary>
        /// Finds the newest item at a path without a folder filter.
        /// </summary>
        /// <param name="path">The absolute path.</param>
        /// <returns>The newest item at that path; <c>null</c> when none.</returns>
        BaseItem? FindByPath(string path);
    }
}
