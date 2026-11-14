using Tesserafin.Data;
using Tesserafin.Data.Enums;
using Xunit;

namespace Tesserafin.Model.Tests;

public sealed class CollectionTypeMappingTests
{
    public static TheoryData<CollectionType?, BaseItemKind[]> Mappings => new()
    {
        { CollectionType.movies, [BaseItemKind.Movie] },
        { CollectionType.tvshows, [BaseItemKind.Series] },
        { CollectionType.music, [BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist] },
        { CollectionType.musicvideos, [BaseItemKind.MusicVideo] },
        { CollectionType.books, [BaseItemKind.Book, BaseItemKind.AudioBook] },
        { CollectionType.boxsets, [BaseItemKind.BoxSet] },
        { CollectionType.homevideos, [BaseItemKind.Video, BaseItemKind.Photo] },
        { CollectionType.photos, [BaseItemKind.Video, BaseItemKind.Photo] },
        { CollectionType.folders, [] },
        { null, [BaseItemKind.Video, BaseItemKind.Audio, BaseItemKind.Photo, BaseItemKind.Movie, BaseItemKind.Series] },
        { CollectionType.unknown, [BaseItemKind.Video, BaseItemKind.Audio, BaseItemKind.Photo, BaseItemKind.Movie, BaseItemKind.Series] },
        { CollectionType.playlists, [BaseItemKind.Video, BaseItemKind.Audio, BaseItemKind.Photo, BaseItemKind.Movie, BaseItemKind.Series] },
        { (CollectionType)999, [BaseItemKind.Video, BaseItemKind.Audio, BaseItemKind.Photo, BaseItemKind.Movie, BaseItemKind.Series] },
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void Collections_HaveTheirExpectedContentKinds(CollectionType? collectionType, BaseItemKind[] expected)
        => Assert.Equal(expected, CollectionTypeMapping.GetItemKinds(collectionType));
}
