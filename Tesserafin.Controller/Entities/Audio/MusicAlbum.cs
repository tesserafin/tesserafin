#nullable disable

#pragma warning disable CA1721, CA1826, CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Entities;
using MetadataProvider = Tesserafin.Model.Entities.MetadataProvider;

namespace Tesserafin.Controller.Entities.Audio
{
    /// <summary>
    /// Class MusicAlbum.
    /// </summary>
    [Tesserafin.Common.RequiresSourceSerialisation]
    public class MusicAlbum : Folder, IHasAlbumArtist, IHasArtist, IHasMusicGenres, IHasLookupInfo<AlbumInfo>, IMetadataContainer
    {
        public MusicAlbum()
        {
            Artists = Array.Empty<string>();
            AlbumArtists = Array.Empty<string>();
        }

        /// <inheritdoc />
        public IReadOnlyList<string> AlbumArtists { get; set; }

        /// <inheritdoc />
        public IReadOnlyList<string> Artists { get; set; }

        [JsonIgnore]
        public override bool SupportsAddingToPlaylist => true;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => true;

        [JsonIgnore]
        public MusicArtist MusicArtist => GetMusicArtist();

        [JsonIgnore]
        public override bool SupportsPlayedStatus => false;

        [JsonIgnore]
        public override bool SupportsCumulativeRunTimeTicks => true;

        [JsonIgnore]
        public string AlbumArtist => AlbumArtists.FirstOrDefault();

        [JsonIgnore]
        public override bool SupportsPeople => true;

        /// <summary>
        /// Gets the tracks.
        /// </summary>
        /// <value>The tracks.</value>
        [JsonIgnore]
        public IEnumerable<Audio> Tracks => GetRecursiveChildren(i => i is Audio).Cast<Audio>();

        public IReadOnlyList<BaseItem> GetCatalogueTracks(IItemRepository repository, IItemLookupService lookup, LinkedChildResolver links)
            => GetRecursiveCatalogueChildren(repository, lookup, links, static item => item is Audio);

        public MusicArtist GetMusicArtist()
            => FindMusicArtist(GetParents(), name => LibraryManager.GetArtists([name]));

        protected override IEnumerable<BaseItem> GetEligibleChildrenForRecursiveChildren(User user, InternalItemsQuery query)
        {
            return Tracks;
        }

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            return 1;
        }

        public override List<string> GetUserDataKeys()
        {
            var list = base.GetUserDataKeys();

            var albumArtist = AlbumArtist;
            if (!string.IsNullOrEmpty(albumArtist))
            {
                list.Insert(0, albumArtist + "-" + Name);
            }

            var id = this.GetProviderId(MetadataProvider.MusicBrainzAlbum);

            if (!string.IsNullOrEmpty(id))
            {
                list.Insert(0, "MusicAlbum-Musicbrainz-" + id);
            }

            id = this.GetProviderId(MetadataProvider.MusicBrainzReleaseGroup);

            if (!string.IsNullOrEmpty(id))
            {
                list.Insert(0, "MusicAlbum-MusicBrainzReleaseGroup-" + id);
            }

            return list;
        }

        protected override bool GetBlockUnratedValue(User user)
        {
            return user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Contains(UnratedItem.Music);
        }

        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Music;
        }

        public AlbumInfo GetLookupInfo()
            => BuildLookupInfo(GetMusicArtist(), Tracks);

        public AlbumInfo GetLookupInfo(IItemRepository repository, IItemLookupService lookup, LinkedChildResolver links, ILinkedChildrenService artists)
            => BuildLookupInfo(
                FindMusicArtist(GetParents(lookup), name => artists.FindArtists([name])),
                GetCatalogueTracks(repository, lookup, links).Cast<Audio>());

        public async Task RefreshAllMetadata(IItemDeletionCommands deletions, IItemWriteCommands writes, IProviderManager providers, IItemImageService images, MetadataRefreshOptions refreshOptions, IProgress<double> progress, CancellationToken cancellationToken)
        {
            var items = GetRecursiveCatalogueChildren(context.Repository, context.Lookup, context.Links);

            var totalItems = items.Count;
            var numComplete = 0;

            var childUpdateType = ItemUpdateType.None;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var updateType = await item.RefreshMetadata(deletions, writes, providers, refreshOptions, cancellationToken).ConfigureAwait(false);
                childUpdateType = childUpdateType | updateType;

                numComplete++;
                double percent = numComplete;
                percent /= totalItems;
                progress.Report(percent * 95);
            }

            var parentRefreshOptions = refreshOptions;
            if (childUpdateType > ItemUpdateType.None)
            {
                parentRefreshOptions = new MetadataRefreshOptions(refreshOptions)
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                };
            }

            // Refresh current item
            await RefreshMetadata(deletions, writes, providers, parentRefreshOptions, cancellationToken).ConfigureAwait(false);

            if (!refreshOptions.IsAutomated)
            {
                await RefreshArtists(deletions, writes, providers, refreshOptions, cancellationToken).ConfigureAwait(false);
            }
        }

        private MusicArtist FindMusicArtist(IEnumerable<BaseItem> parents, Func<string, IReadOnlyDictionary<string, MusicArtist[]>> findArtists)
        {
            foreach (var parent in parents)
            {
                if (parent is MusicArtist artist)
                {
                    return artist;
                }
            }

            var name = AlbumArtist;
            if (!string.IsNullOrEmpty(name))
            {
                return findArtists(name).TryGetValue(name, out var artists)
                    ? artists.OrderBy(artist => artist.IsAccessedByName ? 1 : 0).ThenBy(artist => artist.Id).FirstOrDefault()
                    : null;
            }

            return null;
        }

        private AlbumInfo BuildLookupInfo(MusicArtist artist, IEnumerable<Audio> tracks)
        {
            var id = GetItemLookupInfo<AlbumInfo>();

            id.AlbumArtists = AlbumArtists;

            if (artist is not null)
            {
                id.ArtistProviderIds = artist.ProviderIds;
            }

            id.SongInfos = tracks
                .Select(i => i.GetLookupInfo())
                .ToList();

            var album = id.SongInfos
                .Select(i => i.Album)
                .FirstOrDefault(i => !string.IsNullOrEmpty(i));

            if (!string.IsNullOrEmpty(album))
            {
                id.Name = album;
            }

            return id;
        }

        private async Task RefreshArtists(IItemDeletionCommands deletions, IItemWriteCommands writes, IProviderManager providers, MetadataRefreshOptions refreshOptions, CancellationToken cancellationToken)
        {
            foreach (var i in this.GetAllArtists())
            {
                // This should not be necessary but we're seeing some cases of it
                if (string.IsNullOrEmpty(i))
                {
                    continue;
                }

                var artist = writes.GetOrCreateNamedItem<MusicArtist>(i, cancellationToken);

                if (!artist.IsAccessedByName)
                {
                    continue;
                }

                await artist.RefreshMetadata(deletions, writes, providers, refreshOptions, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
