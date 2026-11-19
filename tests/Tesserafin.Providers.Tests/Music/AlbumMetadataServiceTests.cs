using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Music;
using Xunit;

namespace Tesserafin.Providers.Tests.Music;

public sealed class AlbumMetadataServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RefreshReads_UseCatalogueTracksAndSelectTheArtist(bool hasParentArtist)
    {
        var parent = new MusicArtist { Id = Guid.NewGuid() };
        var namedArtist = new MusicArtist { Id = Guid.NewGuid() };
        var physicalArtist = new MusicArtist { Id = Guid.NewGuid(), ParentId = Guid.NewGuid() };
        parent.ProviderIds["MusicBrainzArtist"] = "parent";
        namedArtist.ProviderIds["MusicBrainzArtist"] = "named";
        physicalArtist.ProviderIds["MusicBrainzArtist"] = "physical";

        var disc = new Folder { Id = Guid.NewGuid() };
        var first = Track("First", string.Empty);
        var second = Track("Second", "Album from tags");
        var linked = Track("Linked", "Linked album");
        var album = new MusicAlbum
        {
            Id = Guid.NewGuid(),
            ParentId = hasParentArtist ? parent.Id : Guid.Empty,
            Name = "Album folder",
            AlbumArtists = ["Artist from tags"],
            PreferredMetadataLanguage = "en",
            PreferredMetadataCountryCode = "US",
            LinkedChildren = [new LinkedChild { ItemId = second.Id }, new LinkedChild { ItemId = linked.Id }]
        };

        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(album.Id)))).Returns([first, disc]);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(disc.Id)))).Returns([second]);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds.Length == 2))).Returns([linked, second]);

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(parent.Id)).Returns(parent);

        var artists = new Mock<ILinkedChildrenService>(MockBehavior.Strict);
        artists.Setup(a => a.FindArtists(It.Is<IReadOnlyList<string>>(names => names.Count == 1 && names[0] == "Artist from tags")))
            .Returns(new Dictionary<string, MusicArtist[]> { ["Artist from tags"] = [namedArtist, physicalArtist] });
        var resolver = new LinkedChildResolver(
            repository.Object,
            lookup.Object,
            Mock.Of<IItemPathLookupService>(MockBehavior.Strict),
            Mock.Of<IFileSystem>(MockBehavior.Strict),
            NullLogger<LinkedChildResolver>.Instance);
        var service = new ExposedAlbumMetadataService(repository.Object, lookup.Object, resolver, artists.Object);

        Assert.Equal<BaseItem>([first, second, linked], service.ReadTracks(album));

        var info = service.ReadLookupInfo(album);

        Assert.Equal("Album from tags", info.Name);
        Assert.Equal(album.AlbumArtists, info.AlbumArtists);
        Assert.Equal(hasParentArtist ? "parent" : "physical", info.ArtistProviderIds["MusicBrainzArtist"]);
        Assert.Collection(
            info.SongInfos,
            song => Assert.Equal("First", song.Name),
            song => Assert.Equal("Second", song.Name),
            song => Assert.Equal("Linked", song.Name));
        artists.Verify(a => a.FindArtists(It.IsAny<IReadOnlyList<string>>()), hasParentArtist ? Times.Never() : Times.Once());
    }

    private static Audio Track(string name, string album)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Album = album,
            PreferredMetadataLanguage = "en",
            PreferredMetadataCountryCode = "US"
        };

    private sealed class ExposedAlbumMetadataService(
        IItemRepository repository,
        IItemLookupService lookup,
        LinkedChildResolver links,
        ILinkedChildrenService artists)
        : AlbumMetadataService(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemPeopleCommands>(),
            Mock.Of<IServerConfigurationManager>(),
            NullLogger<AlbumMetadataService>.Instance,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            Mock.Of<ILibraryItemOptions>(),
            Mock.Of<IItemNamingService>(),
            Mock.Of<IExternalDataManager>(),
            repository,
            lookup,
            links,
            artists)
    {
        public IReadOnlyList<BaseItem> ReadTracks(MusicAlbum album) => GetChildrenForMetadataUpdates(album);

        public AlbumInfo ReadLookupInfo(MusicAlbum album) => GetLookupInfo(album, new LibraryOptions());
    }
}
