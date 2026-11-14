using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Tag inheritance and the tag filter of user visibility, over explicit dependencies.
    /// </summary>
    /// <remarks>
    /// Extracted from the entities (DEBT-SERVER-3): <see cref="BaseItem.GetInheritedTags()"/> reads
    /// parents and member libraries through the static <c>BaseItem.LibraryManager</c>. A caller holding
    /// this service passes it down <see cref="BaseItem.IsVisibleStandalone(User, ITagVisibilityService)"/>
    /// and <see cref="BaseItem.IsVisible(User, bool, ITagVisibilityService)"/>, so every entity override
    /// keeps its own rules while the tags come from here. Implementations must not depend on
    /// <see cref="ILibraryManager"/>, directly or deferred.
    /// </remarks>
    public interface ITagVisibilityService
    {
        /// <summary>
        /// Gets the item's own tags, then each parent's, nearest first, then each member library's,
        /// deduplicated ignoring case (the first spelling is kept).
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>The inherited tags.</returns>
        IReadOnlyList<string> GetInheritedTags(BaseItem item);

        /// <summary>
        /// Applies the user's blocked and allowed tags to the item's inherited tags.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="user">The user.</param>
        /// <param name="skipAllowedTagsCheck">Whether to ignore the user's allowed tags.</param>
        /// <returns><c>false</c> when a blocked tag matches, or when allowed tags apply and none matches.</returns>
        bool IsVisibleViaTags(BaseItem item, User user, bool skipAllowedTagsCheck);
    }
}
