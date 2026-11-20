using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Music;
using Xunit;

namespace Tesserafin.Providers.Tests.Music;

public sealed class ArtistMetadataServiceTests
{
    [Fact]
    public void FolderArtist_UsesCatalogueDescendantsAndEachTracksLocale()
    {
        var artist = new MusicArtist { Id = Guid.NewGuid(), ParentId = Guid.NewGuid() };
        var album = new MusicAlbum { Id = Guid.NewGuid(), ParentId = artist.Id };
        var track = new Audio { Id = Guid.NewGuid(), Name = "Album track", ParentId = album.Id };
        var linked = new Audio { Id = Guid.NewGuid(), Name = "Linked track", PreferredMetadataLanguage = "ja" };
        var video = new MusicVideo { Id = Guid.NewGuid(), ParentId = artist.Id };
        artist.LinkedChildren = [new LinkedChild { ItemId = track.Id }, new LinkedChild { ItemId = linked.Id }];

        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(artist.Id)))).Returns([album, video]);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(album.Id)))).Returns([track]);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ItemIds.Length == 2))).Returns([linked, track]);

        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(album.Id)).Returns(album);
        lookup.Setup(l => l.GetItemById(artist.Id)).Returns(artist);
        lookup.Setup(l => l.GetItemById(artist.ParentId)).Returns(new Folder { Id = artist.ParentId, PreferredMetadataLanguage = "fr" });

        var collections = new Mock<ICollectionFolderResolver>(MockBehavior.Strict);
        collections.Setup(c => c.GetCollectionFolders(artist)).Returns([]);
        collections.Setup(c => c.GetCollectionFolders(track)).Returns([]);
        collections.Setup(c => c.GetCollectionFolders(linked)).Returns([]);
        var options = new Mock<ILibraryItemOptions>(MockBehavior.Strict);
        options.Setup(o => o.GetLibraryOptions(track)).Returns(new LibraryOptions { MetadataCountryCode = "BE" });
        options.Setup(o => o.GetLibraryOptions(linked)).Returns(new LibraryOptions { MetadataCountryCode = "JP" });
        var service = new ExposedArtistMetadataService(repository.Object, lookup.Object, collections.Object, options.Object);

        var info = service.ReadLookupInfo(artist, new LibraryOptions { MetadataCountryCode = "FR" });

        Assert.Equal(("fr", "FR"), (info.MetadataLanguage, info.MetadataCountryCode));
        Assert.Collection(
            info.SongInfos,
            song => Assert.Equal(("Album track", "fr", "BE"), (song.Name, song.MetadataLanguage, song.MetadataCountryCode)),
            song => Assert.Equal(("Linked track", "ja", "JP"), (song.Name, song.MetadataLanguage, song.MetadataCountryCode)));
        Assert.Equal<BaseItem>([track, video, linked], service.ReadGenreSources(artist));
    }

    [Fact]
    public void NamedArtist_UsesArtistFilteredCatalogueQueryForGenreSources()
    {
        var artist = new MusicArtist { Id = Guid.NewGuid(), PreferredMetadataLanguage = "nl", PreferredMetadataCountryCode = "BE" };
        var track = new Audio { Id = Guid.NewGuid() };
        BaseItemKind[] itemTypes = [BaseItemKind.Audio, BaseItemKind.MusicVideo, BaseItemKind.MusicAlbum];
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.Recursive
            && q.IsFolder == false
            && q.ParentId.Equals(Guid.Empty)
            && q.User == null
            && q.ArtistIds.Length == 1
            && q.ArtistIds[0].Equals(artist.Id)
            && q.IncludeItemTypes.SequenceEqual(itemTypes)))).Returns([track]);
        var service = new ExposedArtistMetadataService(
            repository.Object,
            Mock.Of<IItemLookupService>(MockBehavior.Strict),
            Mock.Of<ICollectionFolderResolver>(MockBehavior.Strict),
            Mock.Of<ILibraryItemOptions>(MockBehavior.Strict));

        Assert.Same(track, Assert.Single(service.ReadGenreSources(artist)));
        Assert.Empty(service.ReadLookupInfo(artist, new LibraryOptions()).SongInfos);
    }

    private sealed class ExposedArtistMetadataService(
        IItemRepository repository,
        IItemLookupService lookup,
        ICollectionFolderResolver collections,
        ILibraryItemOptions itemOptions)
        : ArtistMetadataService(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemPeopleCommands>(),
            Mock.Of<IServerConfigurationManager>(),
            NullLogger<ArtistMetadataService>.Instance,
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            itemOptions,
            Mock.Of<IItemNamingService>(),
            Mock.Of<IExternalDataManager>(),
            repository,
            lookup,
            collections,
            new LinkedChildResolver(
                repository,
                lookup,
                Mock.Of<IItemPathLookupService>(MockBehavior.Strict),
                Mock.Of<IFileSystem>(MockBehavior.Strict),
                NullLogger<LinkedChildResolver>.Instance))
    {
        public IReadOnlyList<BaseItem> ReadGenreSources(MusicArtist artist) => GetChildrenForMetadataUpdates(artist);

        public ArtistInfo ReadLookupInfo(MusicArtist artist, LibraryOptions options) => GetLookupInfo(artist, options);
    }
}
