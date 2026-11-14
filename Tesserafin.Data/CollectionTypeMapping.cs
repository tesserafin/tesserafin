using Tesserafin.Data.Enums;

namespace Tesserafin.Data;

/// <summary>Maps library collection types to their content kinds.</summary>
public static class CollectionTypeMapping
{
    /// <summary>
    /// Gets the BaseItemKind values associated with the specified CollectionType.
    /// </summary>
    /// <param name="collectionType">The collection type to map to BaseItemKind values.</param>
    /// <returns>An array of BaseItemKind values that correspond to the collection type.</returns>
    public static BaseItemKind[] GetItemKinds(CollectionType? collectionType)
    {
        switch (collectionType)
        {
            case CollectionType.movies:
                return [BaseItemKind.Movie];
            case CollectionType.tvshows:
                return [BaseItemKind.Series];
            case CollectionType.music:
                return [BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist];
            case CollectionType.musicvideos:
                return [BaseItemKind.MusicVideo];
            case CollectionType.books:
                return [BaseItemKind.Book, BaseItemKind.AudioBook];
            case CollectionType.boxsets:
                return [BaseItemKind.BoxSet];
            case CollectionType.homevideos:
            case CollectionType.photos:
                return [BaseItemKind.Video, BaseItemKind.Photo];
            case CollectionType.folders:
                return [];
            default:
                return [BaseItemKind.Video, BaseItemKind.Audio, BaseItemKind.Photo, BaseItemKind.Movie, BaseItemKind.Series];
        }
    }
}
