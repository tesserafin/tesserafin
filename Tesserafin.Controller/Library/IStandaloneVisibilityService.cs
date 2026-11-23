using System;
using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;

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
    /// <see cref="ITagVisibilityService"/> of <see cref="BaseItem.IsVisibleStandalone(User, IStandaloneVisibilityService)"/>,
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

        /// <summary>
        /// Gets the score of a rating in a country, as <see cref="Tesserafin.Model.Globalization.ILocalizationManager.GetRatingScore"/> does.
        /// </summary>
        /// <param name="rating">The rating.</param>
        /// <param name="countryCode">The country code; <c>null</c> for the server's.</param>
        /// <returns>The score; <c>null</c> when the rating is not recognised.</returns>
        ParentalRatingScore? GetRatingScore(string rating, string? countryCode);

        /// <summary>
        /// Gets the server's metadata country code, the last fallback of an item's.
        /// </summary>
        /// <returns>The country code.</returns>
        string GetServerMetadataCountryCode();

        /// <summary>
        /// Gets an item by id: a library, a box set's linked child, or a channel.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <returns>The item; <c>null</c> when none has that id.</returns>
        BaseItem? GetItemById(Guid id);

        /// <summary>
        /// Finds the item at a path, as <see cref="IItemPathLookupService.FindByPath"/> does with no folder filter.
        /// </summary>
        /// <param name="path">The absolute path.</param>
        /// <returns>The newest item at that path; <c>null</c> when none.</returns>
        BaseItem? FindByPath(string path);

        /// <summary>
        /// Gets a value indicating whether a recording is in progress at a path.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns><c>true</c> if a recording is in progress at that path.</returns>
        bool IsActiveRecording(string path);
    }
}
