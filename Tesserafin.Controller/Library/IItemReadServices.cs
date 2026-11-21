using System;
using System.Collections.Generic;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Configuration;
using LinkedChildType = Tesserafin.Controller.Entities.LinkedChildType;

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

    /// <summary>Gets a value indicating whether specials should appear in their aired seasons.</summary>
    bool DisplaySpecialsWithinSeasons { get; }

    /// <summary>Gets the configured defaults for metadata and name sorting.</summary>
    ItemMetadataDefaults MetadataDefaults { get; }

    /// <summary>Gets a value indicating whether channel names are included in Live TV user data keys.</summary>
    bool IncludeLiveTvChannelNameInUserDataKeys { get; }

    /// <summary>Gets the configured movie title prefix for the legacy guide provider.</summary>
    string? LiveTvMoviePrefix { get; }

    /// <summary>Looks up a catalogue item by its identity.</summary>
    /// <param name="id">The item id.</param>
    /// <returns>The item when it exists.</returns>
    BaseItem? GetItemById(Guid id);

    /// <summary>Gets the saved links of the requested version type.</summary>
    /// <param name="video">The video that owns the links.</param>
    /// <param name="versionType">The local or linked alternate version type.</param>
    /// <returns>The linked version ids.</returns>
    IReadOnlyList<Guid> GetVideoVersionIds(Video video, LinkedChildType versionType);

    /// <summary>Finds album artists by name.</summary>
    /// <param name="artistNames">The names to resolve.</param>
    /// <returns>The catalogue artists grouped by name.</returns>
    IReadOnlyDictionary<string, MusicArtist[]> FindArtists(IReadOnlyList<string> artistNames);

    /// <summary>Gets the libraries containing an item.</summary>
    /// <param name="item">The item.</param>
    /// <param name="candidates">A supplied set of libraries to check, or all libraries when absent.</param>
    /// <returns>The containing libraries.</returns>
    IReadOnlyList<Folder> GetCollectionFolders(BaseItem item, IEnumerable<Folder>? candidates = null);

    /// <summary>Gets the effective settings for an item.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The effective library settings.</returns>
    LibraryOptions GetLibraryOptions(BaseItem item);

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
