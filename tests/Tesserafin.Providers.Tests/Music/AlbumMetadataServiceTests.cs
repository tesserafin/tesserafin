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
using Tesserafin.Model.Globalization;
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
            PreferredMetadataLanguage = "fr",
            PreferredMetadataCountryCode = "BE",
            LinkedChildren = [new LinkedChild { ItemId = second.Id }, new LinkedChild { ItemId = linked.Id }]
        };

        first.ParentId = album.Id;
        first.PreferredMetadataLanguage = null;
        first.PreferredMetadataCountryCode = null;
        linked.PreferredMetadataLanguage = null;
        linked.PreferredMetadataCountryCode = null;
        var linkedLibrary = new CollectionFolder { PreferredMetadataLanguage = "ja" };
        var collections = new Mock<ICollectionFolderResolver>(MockBehavior.Strict);
        collections.Setup(c => c.GetCollectionFolders(linked)).Returns([linkedLibrary]);
        var linkedOptions = new LibraryOptions { MetadataCountryCode = "JP" };
        var itemOptions = new Mock<ILibraryItemOptions>(MockBehavior.Strict);
        itemOptions.Setup(o => o.GetLibraryOptions(linked)).Returns(linkedOptions);

        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(album.Id)))).Returns([first, disc]);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(disc.Id)))).Returns([second]);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds.Length == 2))).Returns([linked, second]);

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(parent.Id)).Returns(parent);
        lookup.Setup(l => l.GetItemById(album.Id)).Returns(album);

        var artists = new Mock<ILinkedChildrenService>(MockBehavior.Strict);
        artists.Setup(a => a.FindArtists(It.Is<IReadOnlyList<string>>(names => names.Count == 1 && names[0] == "Artist from tags")))
            .Returns(new Dictionary<string, MusicArtist[]> { ["Artist from tags"] = [namedArtist, physicalArtist] });
        var resolver = new LinkedChildResolver(
            repository.Object,
            lookup.Object,
            Mock.Of<IItemPathLookupService>(MockBehavior.Strict),
            Mock.Of<IFileSystem>(MockBehavior.Strict),
            NullLogger<LinkedChildResolver>.Instance);
        var service = new ExposedAlbumMetadataService(
            repository.Object,
            lookup.Object,
            resolver,
            artists.Object,
            collections.Object,
            itemOptions.Object);

        Assert.Equal<BaseItem>([first, second, linked], service.ReadTracks(album));

        var info = service.ReadLookupInfo(album);

        Assert.Equal("Album from tags", info.Name);
        Assert.Equal(album.AlbumArtists, info.AlbumArtists);
        Assert.Equal(("fr", "BE"), (info.MetadataLanguage, info.MetadataCountryCode));
        Assert.Equal(hasParentArtist ? "parent" : "physical", info.ArtistProviderIds["MusicBrainzArtist"]);
        Assert.Collection(
            info.SongInfos,
            song => Assert.Equal(("First", "fr", "BE"), (song.Name, song.MetadataLanguage, song.MetadataCountryCode)),
            song => Assert.Equal(("Second", "en", "US"), (song.Name, song.MetadataLanguage, song.MetadataCountryCode)),
            song => Assert.Equal(("Linked", "ja", "JP"), (song.Name, song.MetadataLanguage, song.MetadataCountryCode)));
        collections.Verify(c => c.GetCollectionFolders(linked), Times.Once());
        itemOptions.Verify(o => o.GetLibraryOptions(linked), Times.Once());
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
        ILinkedChildrenService artists,
        ICollectionFolderResolver collections,
        ILibraryItemOptions itemOptions)
        : AlbumMetadataService(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemPeopleCommands>(),
            Mock.Of<IServerConfigurationManager>(),
            NullLogger<AlbumMetadataService>.Instance,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            itemOptions,
            Mock.Of<IItemNamingService>(),
            Mock.Of<IExternalDataManager>(),
            repository,
            Mock.Of<ILocalizationManager>(),
            lookup,
            links,
            artists,
            collections)
    {
        public IReadOnlyList<BaseItem> ReadTracks(MusicAlbum album) => GetChildrenForMetadataUpdates(album);

        public AlbumInfo ReadLookupInfo(MusicAlbum album) => GetLookupInfo(album, new LibraryOptions());
    }
}
