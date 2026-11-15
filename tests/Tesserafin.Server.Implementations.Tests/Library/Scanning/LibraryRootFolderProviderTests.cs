using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;
using Tesserafin.Server.Core.Playlists;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library.Scanning;

public sealed class LibraryRootFolderProviderTests
{
    [Fact]
    public async Task FreshRoot_IsTypedAndSharedAcrossConcurrentReaders_AndLinksPlaylistOnce()
    {
        using var context = new Context();
        var roots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(context.Provider.GetRootFolder, TestContext.Current.CancellationToken)));
        Assert.All(roots, root => Assert.Same(roots[0], root));
        var root = roots[0];
        Assert.Equal(context.RootId, root.Id);
        Assert.True(root.IsRoot);
        Assert.Equal(context.Path, root.Path);
        var playlist = Assert.IsType<PlaylistsFolder>(context.Items[context.PlaylistId]);
        Assert.Equal(root.Id, playlist.ParentId);
        Assert.Same(playlist, root.FindVirtualChild(context.PlaylistId));
        context.Persistence.Verify(p => p.SaveItems(It.Is<IReadOnlyList<BaseItem>>(items => items.Count == 2 && items[0] == root && items[1] == playlist), CancellationToken.None), Times.Once);
        context.Store.Verify(s => s.RegisterItem(playlist), Times.Once);
    }

    [Fact]
    public void PersistedRootAndPlaylist_AreReusedWithUpdatedRootPath()
    {
        using var context = new Context();
        var root = new AggregateFolder { Id = context.RootId, Path = "old-program-data" };
        var playlist = new PlaylistsFolder { Id = context.PlaylistId, Path = System.IO.Path.Combine(context.Path, "playlists"), ParentId = root.Id };
        context.Items[root.Id] = root;
        context.Items[playlist.Id] = playlist;
        Assert.Same(root, context.Provider.GetRootFolder());
        Assert.Equal(context.Path, root.Path);
        Assert.Same(playlist, root.FindVirtualChild(playlist.Id));
        context.Persistence.Verify(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public void MissingRoot_WithAlreadyLinkedPlaylist_StillPersistsTheRoot()
    {
        using var context = new Context();
        context.Items[context.PlaylistId] = new PlaylistsFolder { Id = context.PlaylistId, Path = System.IO.Path.Combine(context.Path, "playlists"), ParentId = context.RootId };
        var root = context.Provider.GetRootFolder();
        Assert.Same(root, context.Items[context.RootId]);
        context.Persistence.Verify(p => p.SaveItems(It.Is<IReadOnlyList<BaseItem>>(items => items.Contains(root)), CancellationToken.None), Times.Once);
    }

    [Fact]
    public void FailedConstruction_DoesNotCacheAnIncompleteRoot()
    {
        using var context = new Context();
        var failed = true;
        context.Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<BaseItem> items, CancellationToken _) =>
            {
                if (failed)
                {
                    failed = false;
                    throw new IOException("root persistence failed");
                }

                foreach (var item in items)
                {
                    context.Items[item.Id] = item;
                }
            });
        Assert.Throws<IOException>(() => context.Provider.GetRootFolder());
        var root = context.Provider.GetRootFolder();
        Assert.Equal(context.RootId, root.Id);
        Assert.NotNull(root.FindVirtualChild(context.PlaylistId));
        context.Store.Verify(s => s.RegisterItem(It.IsAny<PlaylistsFolder>()), Times.Once);
    }

    private sealed class Context : IDisposable
    {
        public Context()
        {
            var config = new Mock<IServerConfigurationManager>();
            config.Setup(c => c.ApplicationPaths.RootFolderPath).Returns(Path);
            config.Setup(c => c.ApplicationPaths.DataPath).Returns(Path);
            config.Setup(c => c.GetConfiguration("metadata")).Returns(new MetadataConfiguration());
            var fileSystem = new Mock<IFileSystem>();
            fileSystem.Setup(f => f.GetDirectoryInfo(Path)).Returns(new FileSystemMetadata { FullName = Path, Name = "root", IsDirectory = true, Exists = true, LastWriteTimeUtc = DateTime.UtcNow });
            Store.Setup(s => s.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns((string _, Type type) => type == typeof(AggregateFolder) ? RootId : PlaylistId);
            var lookup = new Mock<IItemLookupService>();
            lookup.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => Items.GetValueOrDefault(id));
            Persistence.Setup(p => p.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>())).Callback((IReadOnlyList<BaseItem> items, CancellationToken _) =>
            {
                foreach (var item in items)
                {
                    Items[item.Id] = item;
                }
            });
            Provider = new LibraryRootFolderProvider(config.Object, fileSystem.Object, lookup.Object, Store.Object, Persistence.Object);
        }

        public string Path { get; } = Directory.CreateTempSubdirectory("tesserafin-scan-root-").FullName;

        public Guid RootId { get; } = Guid.NewGuid();

        public Guid PlaylistId { get; } = Guid.NewGuid();

        public Dictionary<Guid, BaseItem> Items { get; } = [];

        public Mock<IItemPersistenceService> Persistence { get; } = new();

        public Mock<IItemStore> Store { get; } = new();

        public LibraryRootFolderProvider Provider { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
