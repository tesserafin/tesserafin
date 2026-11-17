#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Playlists;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using MusicAlbum = Tesserafin.Controller.Entities.Audio.MusicAlbum;

namespace Tesserafin.Server.Core.Library
{
    public class MusicManager : IMusicManager
    {
        private readonly INamedItemCatalog _namedItems;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemSortService _itemSortService;

        public MusicManager(INamedItemCatalog namedItems, ILibraryManager libraryManager, IItemSortService itemSortService)
        {
            _namedItems = namedItems;
            _libraryManager = libraryManager;
            _itemSortService = itemSortService;
        }

        public IReadOnlyList<BaseItem> GetInstantMixFromSong(Audio item, User? user, DtoOptions dtoOptions)
        {
            var instantMixItems = GetInstantMixFromGenres(item.Genres, user, dtoOptions);

            return [item, .. instantMixItems.Where(i => !i.Id.Equals(item.Id))];
        }

        /// <inheritdoc />
        public IReadOnlyList<BaseItem> GetInstantMixFromArtist(MusicArtist artist, User? user, DtoOptions dtoOptions)
        {
            return GetInstantMixFromGenres(artist.Genres, user, dtoOptions);
        }

        public IReadOnlyList<BaseItem> GetInstantMixFromAlbum(MusicAlbum item, User? user, DtoOptions dtoOptions)
        {
            return GetInstantMixFromGenres(item.Genres, user, dtoOptions);
        }

        public IReadOnlyList<BaseItem> GetInstantMixFromFolder(Folder item, User? user, DtoOptions dtoOptions)
        {
            var genres = item
                .GetRecursiveChildren(
                    user,
                    new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = [BaseItemKind.Audio],
                        DtoOptions = dtoOptions
                    },
                    out _,
                    _itemSortService)
                .Cast<Audio>()
                .SelectMany(i => i.Genres)
                .Concat(item.Genres)
                .DistinctNames();

            return GetInstantMixFromGenres(genres, user, dtoOptions);
        }

        public IReadOnlyList<BaseItem> GetInstantMixFromPlaylist(Playlist item, User? user, DtoOptions dtoOptions)
        {
            return GetInstantMixFromGenres(item.Genres, user, dtoOptions);
        }

        public IReadOnlyList<BaseItem> GetInstantMixFromGenres(IEnumerable<string> genres, User? user, DtoOptions dtoOptions)
        {
            var genreIds = genres.DistinctNames().Select(i =>
            {
                try
                {
                    return _namedItems.GetId<MusicGenre>(i);
                }
                catch
                {
                    return Guid.Empty;
                }
            }).Where(i => !i.IsEmpty()).ToArray();

            return GetInstantMixFromGenreIds(genreIds, user, dtoOptions);
        }

        public IReadOnlyList<BaseItem> GetInstantMixFromGenreIds(Guid[] genreIds, User? user, DtoOptions dtoOptions)
        {
            return _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [BaseItemKind.Audio],
                GenreIds = genreIds,
                Limit = 200,
                OrderBy = [(ItemSortBy.Random, SortOrder.Ascending)],
                DtoOptions = dtoOptions
            });
        }

        public IReadOnlyList<BaseItem> GetInstantMixFromItem(BaseItem item, User? user, DtoOptions dtoOptions)
        {
            if (item is MusicGenre)
            {
                return GetInstantMixFromGenreIds([item.Id], user, dtoOptions);
            }

            if (item is Playlist playlist)
            {
                return GetInstantMixFromPlaylist(playlist, user, dtoOptions);
            }

            if (item is MusicAlbum album)
            {
                return GetInstantMixFromAlbum(album, user, dtoOptions);
            }

            if (item is MusicArtist artist)
            {
                return GetInstantMixFromArtist(artist, user, dtoOptions);
            }

            if (item is Audio song)
            {
                return GetInstantMixFromSong(song, user, dtoOptions);
            }

            if (item is Folder folder)
            {
                return GetInstantMixFromFolder(folder, user, dtoOptions);
            }

            return new List<BaseItem>();
        }
    }
}
