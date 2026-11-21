using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Playlists;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class LinkedChildResolverTests
{
    [Fact]
    public void FolderGetLinkedChildren_UsesTheAttachedResolver()
    {
        var target = new Folder { Id = Guid.NewGuid() };
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.ItemIds.Length == 1 && q.ItemIds[0].Equals(target.Id)))).Returns([target]);
        var resolver = new LinkedChildResolver(
            repository.Object,
            Mock.Of<IItemLookupService>(MockBehavior.Strict),
            Mock.Of<IItemPathLookupService>(MockBehavior.Strict),
            NullLogger<LinkedChildResolver>.Instance);
        var folder = new Folder
        {
            LinkedChildren = [LinkedChild.Create(target), LinkedChild.Create(target)],
            ReadServices = Mock.Of<IItemReadServices>(reads => reads.LinkedChildren == resolver)
        };

        Assert.Equal(new[] { target, target }, folder.GetLinkedChildren());
    }

    [Fact]
    public void Resolve_BatchesIdsButKeepsLinkOrderAndRecoversSavedPaths()
    {
        var first = new Folder { Id = Guid.NewGuid() };
        var second = new Folder { Id = Guid.NewGuid() };
        var recovered = new Folder { Id = Guid.NewGuid() };
        var staleId = Guid.NewGuid();
        var playlistPath = Path.Combine(Path.GetTempPath(), "playlists", "test.m3u");
        var resolvedPath = Path.Combine(Path.GetDirectoryName(playlistPath)!, "recovered.mkv");
        var firstLink = LinkedChild.Create(first);
        var secondLink = LinkedChild.Create(second);
        var repeated = LinkedChild.Create(first);
#pragma warning disable CS0618 // Exercise previously saved path links.
        var movedLink = new LinkedChild { ItemId = staleId, Path = "recovered.mkv" };
        var missing = new LinkedChild { Path = "missing.mkv" };
#pragma warning restore CS0618
        var folder = new Playlist
        {
            Path = playlistPath,
            LinkedChildren = [secondLink, movedLink, firstLink, missing, repeated]
        };

        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        var expectedIds = new[] { second.Id, staleId, first.Id };
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds.SequenceEqual(expectedIds))))
            .Returns(new BaseItem[] { first, second });
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(staleId)).Returns((BaseItem?)null);
        var paths = new Mock<IItemPathLookupService>(MockBehavior.Strict);
        paths.Setup(p => p.FindByPath(resolvedPath, null)).Returns(recovered);
        var missingPath = Path.Combine(Path.GetDirectoryName(playlistPath)!, "missing.mkv");
        paths.Setup(p => p.FindByPath(missingPath, null)).Returns((BaseItem?)null);
        var resolver = new LinkedChildResolver(repository.Object, lookup.Object, paths.Object, NullLogger<LinkedChildResolver>.Instance);

        var items = resolver.Resolve(folder);

        Assert.Equal(new[] { second, recovered, first, first }, items.Select(entry => entry.Item));
        Assert.Equal(new[] { secondLink, movedLink, firstLink, repeated }, items.Select(entry => entry.Link));
        Assert.Equal(recovered.Id, movedLink.ItemId);
        Assert.Equal(Guid.Empty, missing.ItemId);
        repository.Verify(r => r.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Once);
        lookup.Verify(l => l.GetItemById(staleId), Times.Once);
    }
}
