using System;
using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// The parents, library membership and libraries that the standalone visibility check reads, over
    /// explicit dependencies, on top of the tags of <see cref="ITagVisibilityService"/>.
    /// </summary>
    /// <remarks>
    /// Extracted from the entities (DEBT-SERVER-4): <c>IsVisibleStandaloneInternal</c> walks the parents
    /// and reads the libraries, and the parental rating walks the display parents and reads the country
    /// code of the parents and libraries, through the static <c>BaseItem.LibraryManager</c>. Passed as the
    /// <see cref="ITagVisibilityService"/> of <see cref="BaseItem.IsVisibleStandalone(User, ITagVisibilityService)"/>,
    /// it supplies those data instead, while the rules stay the entities' own. Implementations must not
    /// depend on <see cref="ILibraryManager"/>, directly or deferred.
    /// </remarks>
    public interface IStandaloneVisibilityService : ITagVisibilityService
    {
        /// <summary>
        /// Gets the item's parents, nearest first.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>The parents; empty for a parentless item.</returns>
        IReadOnlyList<BaseItem> GetParents(BaseItem item);

        /// <summary>
        /// Gets the item's display parent, the one its ratings are inherited from.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>The item with <see cref="BaseItem.DisplayParentId"/>; <c>null</c> without one.</returns>
        BaseItem? GetDisplayParent(BaseItem item);

        /// <summary>
        /// Gets the libraries the item belongs to.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>The member libraries, in root order; empty when none.</returns>
        IReadOnlyList<Folder> GetCollectionFolders(BaseItem item);

        /// <summary>
        /// Gets the ids of every library, the user root folder's children.
        /// </summary>
        /// <returns>The library ids, in root order.</returns>
        IReadOnlyList<Guid> GetLibraryIds();

        /// <summary>
        /// Gets the options of <paramref name="item"/> when it is a library, else of its first library.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>The options; default options when the item belongs to no library.</returns>
        LibraryOptions GetLibraryOptions(BaseItem item);
    }
}
