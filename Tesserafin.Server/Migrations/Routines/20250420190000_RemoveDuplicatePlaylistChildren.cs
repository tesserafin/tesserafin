using System;
using System.Linq;
using System.Threading;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Playlists;
using Tesserafin.Data.Enums;

namespace Tesserafin.Server.Migrations.Routines;

/// <summary>
/// Remove duplicate playlist entries.
/// </summary>
#pragma warning disable CS0618 // Type or member is obsolete
[TesserafinMigration("2025-04-20T19:00:00", nameof(RemoveDuplicatePlaylistChildren), "96C156A2-7A13-4B3B-A8B8-FB80C94D20C0")]
internal class RemoveDuplicatePlaylistChildren : IMigrationRoutine
#pragma warning restore CS0618 // Type or member is obsolete
{
    private readonly IItemWriteCommands _writes;
    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;

    public RemoveDuplicatePlaylistChildren(
        IItemWriteCommands writes,
        ILibraryManager libraryManager,
        IPlaylistManager playlistManager)
    {
        _writes = writes;
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
    }

    /// <inheritdoc/>
    public void Perform()
    {
        var playlists = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Playlist]
        })
        .Cast<Playlist>()
        .Where(p => !p.OpenAccess || !p.OwnerUserId.Equals(Guid.Empty))
        .ToArray();

        if (playlists.Length > 0)
        {
            foreach (var playlist in playlists)
            {
                var linkedChildren = playlist.LinkedChildren;
                if (linkedChildren.Length > 0)
                {
                    var newLinkedChildren = linkedChildren
                        .Where(c => c.ItemId is null || c.ItemId.Value.Equals(Guid.Empty))
                        .Concat(linkedChildren
                            .Where(c => c.ItemId.HasValue && !c.ItemId.Value.Equals(Guid.Empty))
                            .DistinctBy(c => c.ItemId))
                        .ToArray();
                    playlist.LinkedChildren = newLinkedChildren;
                    playlist.UpdateToRepositoryAsync(_writes, ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
                    _playlistManager.SavePlaylistFile(playlist);
                }
            }
        }
    }
}
