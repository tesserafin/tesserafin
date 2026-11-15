using System;
using System.IO;
using System.Threading;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Playlists;

namespace Tesserafin.Server.Core.Library;

/// <summary>Constructs the application-owned root; media resolvers do not select its type or identity.</summary>
internal sealed class LibraryRootFolderProvider(
    IServerConfigurationManager configuration,
    IFileSystem fileSystem,
    IItemLookupService lookup,
    IItemStore store,
    IItemPersistenceService persistence) : ILibraryRootFolderProvider
{
    private readonly Lock _gate = new();
    private AggregateFolder? _root;

    public AggregateFolder GetRootFolder()
    {
        lock (_gate)
        {
            return _root ??= CreateRoot();
        }
    }

    private AggregateFolder CreateRoot()
    {
        var path = configuration.ApplicationPaths.RootFolderPath;
        Directory.CreateDirectory(path);
        var id = store.GetNewItemId(path, typeof(AggregateFolder));
        var root = lookup.GetItemById(id) as AggregateFolder;
        var saveRoot = root is null || !string.Equals(root.Path, path, StringComparison.Ordinal);
        if (root is null)
        {
            var directory = fileSystem.GetDirectoryInfo(path);
            root = new AggregateFolder
            {
                Id = id,
                IsRoot = true,
                Name = directory.Name,
                Path = path,
                DateCreated = configuration.GetMetadataConfiguration().UseFileCreationTimeForDateAdded && directory.CreationTimeUtc != DateTime.MinValue ? directory.CreationTimeUtc : DateTime.UtcNow,
                DateModified = directory.LastWriteTimeUtc,
                IsLocked = path.Contains("[dontfetchmeta]", StringComparison.OrdinalIgnoreCase)
            };
        }

        root.Path = path;
        var playlistPath = Path.Combine(configuration.ApplicationPaths.DataPath, "playlists");
        var info = Directory.CreateDirectory(playlistPath);
        var playlistId = store.GetNewItemId(playlistPath, typeof(PlaylistsFolder));
        var playlist = lookup.GetItemById(playlistId) as PlaylistsFolder;
        if (playlist is null || !string.Equals(playlist.Path, playlistPath, StringComparison.OrdinalIgnoreCase))
        {
            playlist = new PlaylistsFolder
            {
                Id = playlistId,
                Path = playlistPath,
                DateCreated = info.CreationTimeUtc,
                DateModified = info.LastWriteTimeUtc
            };
        }

        if (saveRoot || !playlist.ParentId.Equals(root.Id))
        {
            // Application-owned containers have no external metadata to export or media update
            // event to emit. Persist their relationship directly, without re-entering item management.
            playlist.ParentId = root.Id;
            root.DateLastSaved = DateTime.UtcNow;
            playlist.DateLastSaved = root.DateLastSaved;
            persistence.SaveItems([root, playlist], CancellationToken.None);
            store.RegisterItem(root);
        }

        root.AddVirtualChild(playlist);
        store.RegisterItem(playlist);
        return root;
    }
}
