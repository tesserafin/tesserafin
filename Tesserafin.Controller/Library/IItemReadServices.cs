using System;
using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>
/// The catalogue reads an entity performs on itself: a folder's persisted children, a folder's played state and
/// whether a path is being recorded.
/// </summary>
/// <remarks>
/// Attached to each item by the catalogue when it loads or saves the item (<see cref="BaseItem.ReadServices"/>),
/// in place of the former <c>BaseItem.ItemRepository</c> and <c>Video.RecordingsManager</c> statics.
/// </remarks>
public interface IItemReadServices
{
    /// <summary>Gets the resolver for saved links in the catalogue.</summary>
    LinkedChildResolver LinkedChildren { get; }

    /// <summary>Checks whether a path is inside the server-managed data directory.</summary>
    /// <param name="path">The catalogue item path.</param>
    /// <returns>Whether this item is managed by the server.</returns>
    bool IsManagedLibraryPath(string path);

    /// <summary>
    /// Gets the persisted direct children of <paramref name="folder"/>.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The children.</returns>
    IReadOnlyList<BaseItem> GetChildren(Folder folder);

    /// <summary>
    /// Gets a value indicating whether <paramref name="user"/> has played every playable item below a folder.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="folderId">The folder id.</param>
    /// <returns><c>true</c> when played.</returns>
    bool IsPlayed(User user, Guid folderId);

    /// <summary>
    /// Gets a value indicating whether a recording is in progress at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The media path.</param>
    /// <returns><c>true</c> when it is being recorded.</returns>
    bool IsActiveRecording(string path);
}
