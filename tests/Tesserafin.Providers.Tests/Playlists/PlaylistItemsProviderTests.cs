using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Playlists;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Playlists;
using Tesserafin.Providers.Tests.Plugins;
using Xunit;

namespace Tesserafin.Providers.Tests.Playlists;

public sealed class PlaylistItemsProviderTests
{
    [Fact]
    public async Task GetMetadata_PreservesTrackOrderAndLooksUpOnlyExistingFilesInEligibleLibraries()
    {
        using var directory = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var mediaRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "music")).FullName;
        var ignoredRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "ignored")).FullName;
        var first = new Audio { Id = Guid.NewGuid(), Path = Path.Combine(mediaRoot, "first.mp3") };
        var second = new Audio { Id = Guid.NewGuid(), Path = Path.Combine(mediaRoot, "second.mp3") };
        var unindexed = Path.Combine(mediaRoot, "unindexed.mp3");
        var missing = Path.Combine(mediaRoot, "missing.mp3");
        var ignored = Path.Combine(ignoredRoot, "ignored.mp3");
        var playlistPath = Path.Combine(directory.Path, "tracks.m3u");

        await Task.WhenAll(
            File.WriteAllTextAsync(first.Path, string.Empty, token),
            File.WriteAllTextAsync(second.Path, string.Empty, token),
            File.WriteAllTextAsync(unindexed, string.Empty, token),
            File.WriteAllTextAsync(ignored, string.Empty, token));
        string[] entries = [second.Path, ignored, first.Path, missing, unindexed, second.Path];
        await File.WriteAllLinesAsync(playlistPath, entries.Select(p => Path.GetRelativePath(directory.Path, p)).Prepend("#EXTM3U"), token);

        var userRoot = new Mock<UserRootFolder>();
        userRoot.SetupGet(f => f.Children).Returns(
        [
            new CollectionFolder { CollectionType = CollectionType.music, PhysicalLocationsList = [mediaRoot] },
            new CollectionFolder { CollectionType = CollectionType.playlists, PhysicalLocationsList = [ignoredRoot] }
        ]);
        var roots = Mock.Of<IUserRootFolderProvider>(r => r.GetUserRootFolder() == userRoot.Object);
        var catalogue = new Mock<IItemPathLookupService>(MockBehavior.Strict);
        catalogue.Setup(c => c.FindByPath(first.Path, null)).Returns(first);
        catalogue.Setup(c => c.FindByPath(second.Path, null)).Returns(second);
        catalogue.Setup(c => c.FindByPath(unindexed, null)).Returns((BaseItem?)null);
        var files = new Mock<IFileSystem>(MockBehavior.Strict);
        files.Setup(f => f.MakeAbsolutePath(directory.Path, It.IsAny<string>()))
            .Returns((string folder, string path) => Path.GetFullPath(Path.Combine(folder, path)));
        var provider = new PlaylistItemsProvider(NullLogger<PlaylistItemsProvider>.Instance, roots, catalogue.Object, files.Object);

        var metadata = await provider.GetMetadata(new ItemInfo(new Playlist { Path = playlistPath }), Mock.Of<IDirectoryService>(), token);

        Assert.True(metadata.HasMetadata);
        Guid?[] expected = [second.Id, first.Id, second.Id];
        Assert.Equal(expected, metadata.Item!.LinkedChildren.Select(c => c.ItemId));
        Assert.All(metadata.Item.LinkedChildren, child => Assert.Equal(LinkedChildType.Manual, child.Type));
        catalogue.Verify(c => c.FindByPath(first.Path, null), Times.Once);
        catalogue.Verify(c => c.FindByPath(second.Path, null), Times.Exactly(2));
        catalogue.Verify(c => c.FindByPath(unindexed, null), Times.Once);
        catalogue.VerifyNoOtherCalls();
    }
}
