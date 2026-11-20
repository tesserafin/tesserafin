using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using LinkedChildType = Tesserafin.Controller.Entities.LinkedChildType;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// The "mark played / unplayed" commands, moved out of the entities (#129 step 5).
    /// </summary>
    /// <remarks>
    /// Every rule here was carried over from the removed <c>BaseItem</c>, <c>Folder</c>,
    /// <c>BoxSet</c> and <c>Video</c> methods; the selection of descendants reproduces what the
    /// folder's own recursive query used to return for the same user, through injected ports
    /// instead of the static <c>BaseItem.LibraryManager</c>/<c>UserDataManager</c> locators.
    /// </remarks>
    public sealed class ItemPlayedStateService : IItemPlayedStateService
    {
        private readonly IUserDataManager _userDataManager;
        private readonly IItemQueryService _itemQueryService;
        private readonly IItemAccessService _itemAccessService;
        private readonly IItemLookupService _itemLookupService;
        private readonly ILinkedChildrenService _linkedChildrenService;
        private readonly IChannelManager _channelManager;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly TimeProvider _timeProvider;
        private readonly ActiveRecordings _recordings;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemPlayedStateService"/> class.
        /// </summary>
        /// <param name="userDataManager">Reads and persists the per-user state.</param>
        /// <param name="itemQueryService">Selects the descendants of a folder for a user.</param>
        /// <param name="itemAccessService">Resolves collection members through the user's visibility rules.</param>
        /// <param name="itemLookupService">Resolves a season's series and a video's versions by id.</param>
        /// <param name="linkedChildrenService">Reads the alternate-version links of a video.</param>
        /// <param name="channelManager">Selects the items of a channel folder.</param>
        /// <param name="configurationManager">Whether specials are listed within seasons.</param>
        /// <param name="timeProvider">The clock for a mark without a date.</param>
        /// <param name="recordings">The recordings in progress.</param>
        public ItemPlayedStateService(
            IUserDataManager userDataManager,
            IItemQueryService itemQueryService,
            IItemAccessService itemAccessService,
            IItemLookupService itemLookupService,
            ILinkedChildrenService linkedChildrenService,
            IChannelManager channelManager,
            IServerConfigurationManager configurationManager,
            TimeProvider timeProvider,
            ActiveRecordings recordings)
        {
            _recordings = recordings;
            _userDataManager = userDataManager;
            _itemQueryService = itemQueryService;
            _itemAccessService = itemAccessService;
            _itemLookupService = itemLookupService;
            _linkedChildrenService = linkedChildrenService;
            _channelManager = channelManager;
            _configurationManager = configurationManager;
            _timeProvider = timeProvider;
        }

        /// <inheritdoc />
        public void MarkPlayed(User user, BaseItem item, DateTime? datePlayed)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(item);

            foreach (var target in SelectTargets(user, item, played: true))
            {
                var data = _userDataManager.GetUserData(user, target) ?? new UserItemData { Key = target.GetUserDataKeys(_recordings)[0] };
                PlayedStateTransitions.MarkPlayed(data, datePlayed, _timeProvider.GetUtcNow().UtcDateTime);
                _userDataManager.SaveUserData(user, target, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);

                if (target is Video video)
                {
                    MarkOtherVersionsPlayed(user, video);
                }
            }
        }

        /// <inheritdoc />
        public void MarkUnplayed(User user, BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(item);

            foreach (var target in SelectTargets(user, item, played: false))
            {
                // Unplayed always clears the position, on this item and on every other version.
                ResetToUnplayed(user, target);

                if (target is Video video)
                {
                    foreach (var version in GetOtherVersions(video))
                    {
                        ResetToUnplayed(user, version);
                    }
                }
            }
        }

        /// <inheritdoc />
        public void MarkOtherVersionsPlayed(User user, Video video)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(video);

            foreach (var version in GetOtherVersions(video))
            {
                // This SaveUserData overload writes only the fields set on the DTO: the play count
                // and the last played date of the other versions are kept.
                _userDataManager.SaveUserData(
                    user,
                    version,
                    new UpdateUserItemDataDto { Played = true, PlaybackPositionTicks = 0 },
                    UserDataSaveReason.TogglePlayed);
            }
        }

        private void ResetToUnplayed(User user, BaseItem target)
        {
            var data = _userDataManager.GetUserData(user, target);
            if (data is null)
            {
                return;
            }

            PlayedStateTransitions.MarkUnplayed(data);
            _userDataManager.SaveUserData(user, target, data, UserDataSaveReason.TogglePlayed, CancellationToken.None);
        }

        /// <summary>
        /// The items whose own state the command writes: the item itself unless it is a folder.
        /// </summary>
        private IEnumerable<BaseItem> SelectTargets(User user, BaseItem item, bool played)
        {
            if (item is not Folder folder)
            {
                return [item];
            }

            // A collection with members marks them, each by its own rule (a member series marks
            // its episodes). Without members, a collection is a plain folder: a legacy on-disk
            // box set marks its physical descendants, any other one has none.
            if (folder is BoxSet boxSet && boxSet.SourceType != SourceType.Channel && boxSet.LinkedChildren.Length > 0)
            {
                return GetVisibleMembers(user, boxSet).SelectMany(member => SelectTargets(user, member, played));
            }

            var leaves = GetPlayableDescendants(user, folder, played);

            // Marking played skips unaired virtual episodes; marking unplayed resets them as well.
            return played
                ? leaves.Where(leaf => !(leaf.IsVirtualItem && leaf is Episode { IsUnaired: true }))
                : leaves;
        }

        private IEnumerable<BaseItem> GetVisibleMembers(User user, BoxSet boxSet)
        {
            foreach (var link in boxSet.LinkedChildren)
            {
                if (link.ItemId is not { } id || id.Equals(Guid.Empty))
                {
                    continue;
                }

                var member = _itemAccessService.GetVisibleItemById<BaseItem>(id, user);
                if (member is not null)
                {
                    yield return member;
                }
            }
        }

        private IReadOnlyList<BaseItem> GetPlayableDescendants(User user, Folder folder, bool played)
        {
            // A user who does not display missing episodes only marks real items played; marking
            // unplayed has never applied that filter.
            bool? isVirtualItem = played && !user.DisplayMissingEpisodes ? false : null;

            if (folder.SourceType == SourceType.Channel)
            {
                var channelQuery = new InternalItemsQuery
                {
                    User = user,
                    Recursive = true,
                    IsFolder = false,
                    IsVirtualItem = isVirtualItem,
                    EnableTotalRecordCount = false,
                    Parent = folder,
                    ChannelIds = [folder.ChannelId],
                };

                try
                {
                    return _channelManager.GetChannelItemsInternal(channelQuery, new Progress<double>(), CancellationToken.None)
                        .GetAwaiter()
                        .GetResult()
                        .Items;
                }
                catch (Exception)
                {
                    // The channel logs its own failures; an unreachable channel marks nothing.
                    return [];
                }
            }

            switch (folder)
            {
                case Series series:
                    // A series always marks its real episodes, whatever the user displays.
                    return _itemQueryService.GetItemList(new InternalItemsQuery
                    {
                        User = user,
                        Recursive = true,
                        IsFolder = false,
                        IsVirtualItem = false,
                        EnableTotalRecordCount = false,
                        SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(),
                        IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Season],
                    });

                case Season season:
                    return GetSeasonEpisodes(user, season, isVirtualItem);

                case UserView:
                    // A user view is a computed listing, not a container in the library: it has
                    // no descendants of its own to mark.
                    return [];

                default:
                    var query = new InternalItemsQuery
                    {
                        User = user,
                        Recursive = true,
                        IsFolder = false,
                        IsVirtualItem = isVirtualItem,
                        EnableTotalRecordCount = false,
                    };

                    // The user's root and the aggregate root stand for the whole library.
                    if (folder is not UserRootFolder and not AggregateFolder)
                    {
                        query.Parent = folder;
                    }

                    return _itemQueryService.GetItemList(query);
            }
        }

        private IReadOnlyList<BaseItem> GetSeasonEpisodes(User user, Season season, bool? isVirtualItem)
        {
            var seriesId = season.SeriesId.Equals(Guid.Empty) ? season.ParentId : season.SeriesId;
            var series = _itemLookupService.GetItemById<Series>(seriesId);
            if (series is null)
            {
                return [];
            }

            // Specials listed within seasons are found from the series; otherwise the season's
            // own key narrows the query to it.
            var specialsWithinSeasons = _configurationManager.Configuration.DisplaySpecialsWithinSeasons;
            var episodes = _itemQueryService.GetItemList(new InternalItemsQuery(user)
            {
                AncestorWithPresentationUniqueKey = specialsWithinSeasons ? null : season.GetPresentationUniqueKey(),
                SeriesPresentationUniqueKey = specialsWithinSeasons ? series.GetPresentationUniqueKey() : null,
                IncludeItemTypes = [BaseItemKind.Episode],
            });

            return Series.FilterEpisodesBySeason(episodes, season, specialsWithinSeasons)
                .Where(episode => isVirtualItem is null || episode.IsVirtualItem == isVirtualItem.Value)
                .ToList();
        }

        /// <summary>
        /// Every other version of <paramref name="video"/>: its linked versions; when it is itself
        /// a version, its primary and the primary's linked versions; and the local (file-based)
        /// versions of all of them.
        /// </summary>
        private IReadOnlyList<Video> GetOtherVersions(Video video)
        {
            // Same short cut as the removed Video.PropagatePlayedState: a video that is neither a
            // version nor carries linked versions can only have local ones.
            if (video.PrimaryVersionId is null && video.LinkedAlternateVersions.Length == 0)
            {
                return GetVersions(video.Id, LinkedChildType.LocalAlternateVersion)
                    .Where(v => !v.Id.Equals(video.Id))
                    .DistinctBy(v => v.Id)
                    .ToList();
            }

            var primary = video.PrimaryVersionId is { } primaryId
                ? _itemLookupService.GetItemById<Video>(primaryId)
                : null;

            var grouped = new List<Video> { video };
            grouped.AddRange(GetVersions(video.Id, LinkedChildType.LinkedAlternateVersion));
            if (primary is not null)
            {
                grouped.Add(primary);
                grouped.AddRange(GetVersions(primary.Id, LinkedChildType.LinkedAlternateVersion));
            }

            var local = grouped
                .SelectMany(v => GetVersions(v.Id, LinkedChildType.LocalAlternateVersion))
                .ToList();

            return grouped
                .Concat(local)
                .DistinctBy(v => v.Id)
                .Where(v => !v.Id.Equals(video.Id))
                .ToList();
        }

        private IEnumerable<Video> GetVersions(Guid videoId, LinkedChildType type)
            => _linkedChildrenService.GetLinkedChildrenIds(videoId, (int)type)
                .Select(id => _itemLookupService.GetItemById<Video>(id))
                .OfType<Video>();
    }
}
