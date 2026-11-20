using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Moq;
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
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Server.Core.Library;
using Xunit;
using LinkedChild = Tesserafin.Controller.Entities.LinkedChild;
using LinkedChildType = Tesserafin.Controller.Entities.LinkedChildType;

namespace Tesserafin.Server.Implementations.Tests.Library;

/// <summary>
/// #129 step 5: the rules the removed entity commands carried, now owned by
/// <see cref="ItemPlayedStateService"/>.
/// </summary>
public sealed class ItemPlayedStateServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _stored = new();
    private readonly List<(Guid User, Guid Item, UserItemData Data, UserDataSaveReason Reason)> _saved = new();
    private readonly List<(Guid User, Guid Item, UpdateUserItemDataDto Dto, UserDataSaveReason Reason)> _partialSaves = new();
    private readonly Mock<IItemQueryService> _queries = new();
    private readonly Mock<IItemAccessService> _access = new();
    private readonly Mock<IItemLookupService> _lookup = new();
    private readonly Mock<ILinkedChildrenService> _links = new();
    private readonly ServerConfiguration _configuration = new();
    private readonly User _user = new("alice", "default", "default");
    private readonly ItemPlayedStateService _service;

    public ItemPlayedStateServiceTests()
    {
        var userData = new Mock<IUserDataManager>();
        userData.Setup(m => m.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User u, BaseItem i) => _stored.TryGetValue((u.Id, i.Id), out var d) ? d : new UserItemData { Key = i.Id.ToString() });
        userData.Setup(m => m.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
            .Callback((User u, BaseItem i, UserItemData d, UserDataSaveReason r, CancellationToken _) => _saved.Add((u.Id, i.Id, d, r)));
        userData.Setup(m => m.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UpdateUserItemDataDto>(), It.IsAny<UserDataSaveReason>()))
            .Callback((User u, BaseItem i, UpdateUserItemDataDto d, UserDataSaveReason r) => _partialSaves.Add((u.Id, i.Id, d, r)));

        _links.Setup(m => m.GetLinkedChildrenIds(It.IsAny<Guid>(), It.IsAny<int?>())).Returns([]);

        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.SetupGet(m => m.Configuration).Returns(_configuration);

        _service = new ItemPlayedStateService(
            userData.Object,
            _queries.Object,
            _access.Object,
            _lookup.Object,
            _links.Object,
            Mock.Of<IChannelManager>(),
            configurationManager.Object,
            new FixedClock(),
            new ActiveRecordings());
    }

    [Fact]
    public void MarkPlayed_WithoutDate_CountsAtLeastOnePlay_ClearsPosition_StampsNow()
    {
        var film = new Movie { Id = Guid.NewGuid() };
        Store(film, new UserItemData { Key = "k", PlaybackPositionTicks = 42 });

        _service.MarkPlayed(_user, film, null);

        var (_, item, data, reason) = Assert.Single(_saved);
        Assert.Equal(film.Id, item);
        Assert.Equal(UserDataSaveReason.TogglePlayed, reason);
        Assert.True(data.Played);
        Assert.Equal(1, data.PlayCount);
        Assert.Equal(0, data.PlaybackPositionTicks);
        Assert.Equal(Now, data.LastPlayedDate);
    }

    [Fact]
    public void MarkPlayed_WithoutDate_KeepsCountAndExistingDate()
    {
        var film = new Movie { Id = Guid.NewGuid() };
        var earlier = new DateTime(2020, 5, 6, 0, 0, 0, DateTimeKind.Utc);
        Store(film, new UserItemData { Key = "k", PlayCount = 4, LastPlayedDate = earlier });

        _service.MarkPlayed(_user, film, null);

        var data = Assert.Single(_saved).Data;
        Assert.Equal(4, data.PlayCount);
        Assert.Equal(earlier, data.LastPlayedDate);
    }

    [Fact]
    public void MarkPlayed_WithDate_CountsOneMorePlay_RecordsTheDate()
    {
        var film = new Movie { Id = Guid.NewGuid() };
        var reported = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Store(film, new UserItemData { Key = "k", PlayCount = 2, LastPlayedDate = Now.AddDays(-1) });

        _service.MarkPlayed(_user, film, reported);

        var data = Assert.Single(_saved).Data;
        Assert.Equal(3, data.PlayCount);
        Assert.Equal(reported, data.LastPlayedDate);
    }

    [Fact]
    public void MarkUnplayed_ClearsCountPositionAndDate()
    {
        var film = new Movie { Id = Guid.NewGuid() };
        Store(film, new UserItemData { Key = "k", Played = true, PlayCount = 3, PlaybackPositionTicks = 9, LastPlayedDate = Now });

        _service.MarkUnplayed(_user, film);

        var (_, _, data, reason) = Assert.Single(_saved);
        Assert.Equal(UserDataSaveReason.TogglePlayed, reason);
        Assert.False(data.Played);
        Assert.Equal(0, data.PlayCount);
        Assert.Equal(0, data.PlaybackPositionTicks);
        Assert.Null(data.LastPlayedDate);
    }

    [Fact]
    public void Commands_WriteOnlyTheGivenUsersState()
    {
        var film = new Movie { Id = Guid.NewGuid() };
        var bob = new User("bob", "default", "default");

        _service.MarkPlayed(_user, film, null);
        _service.MarkUnplayed(bob, film);

        Assert.Equal([_user.Id, bob.Id], _saved.Select(s => s.User));
        Assert.True(_saved[0].Data.Played);
        Assert.False(_saved[1].Data.Played);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, false)]
    public void Folder_MarksItsLeavesUnderItself_NotItself(bool displayMissingEpisodes, bool? expectedIsVirtualItem)
    {
        _user.DisplayMissingEpisodes = displayMissingEpisodes;
        var folder = new Folder { Id = Guid.NewGuid() };
        var film = new Movie { Id = Guid.NewGuid() };
        InternalItemsQuery? asked = null;
        _queries.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback((InternalItemsQuery q) => asked = q)
            .Returns([film]);

        _service.MarkPlayed(_user, folder, null);

        Assert.NotNull(asked);
        Assert.Same(_user, asked.User);
        Assert.True(asked.Recursive);
        Assert.False(asked.IsFolder);
        Assert.Equal(folder.Id, asked.ParentId);
        Assert.Equal(expectedIsVirtualItem, asked.IsVirtualItem);
        Assert.Equal([film.Id], _saved.Select(s => s.Item));
    }

    [Fact]
    public void Folder_Unplayed_DoesNotFilterVirtualItems()
    {
        var folder = new Folder { Id = Guid.NewGuid() };
        InternalItemsQuery? asked = null;
        _queries.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback((InternalItemsQuery q) => asked = q)
            .Returns([]);

        _service.MarkUnplayed(_user, folder);

        Assert.Null(asked!.IsVirtualItem);
    }

    [Fact]
    public void Folder_Played_SkipsUnairedVirtualEpisodes_Unplayed_ResetsThem()
    {
        _user.DisplayMissingEpisodes = true;
        var folder = new Folder { Id = Guid.NewGuid() };
        var aired = new Episode { Id = Guid.NewGuid(), IsVirtualItem = true, PremiereDate = Now.AddYears(-1) };
        var unaired = new Episode { Id = Guid.NewGuid(), IsVirtualItem = true, PremiereDate = DateTime.UtcNow.AddYears(1) };
        var realFuture = new Episode { Id = Guid.NewGuid(), IsVirtualItem = false, PremiereDate = DateTime.UtcNow.AddYears(1) };
        _queries.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([aired, unaired, realFuture]);

        _service.MarkPlayed(_user, folder, null);
        Assert.Equal([aired.Id, realFuture.Id], _saved.Select(s => s.Item));

        _saved.Clear();
        _service.MarkUnplayed(_user, folder);
        Assert.Equal([aired.Id, unaired.Id, realFuture.Id], _saved.Select(s => s.Item));
    }

    [Fact]
    public void UserRoot_StandsForTheWholeLibrary()
    {
        InternalItemsQuery? asked = null;
        _queries.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback((InternalItemsQuery q) => asked = q)
            .Returns([]);

        _service.MarkPlayed(_user, new UserRootFolder { Id = Guid.NewGuid() }, null);

        Assert.True(asked!.ParentId.Equals(Guid.Empty));
    }

    [Fact]
    public void Series_MarksItsRealEpisodesBySeriesKey_WhateverTheUserDisplays()
    {
        _user.DisplayMissingEpisodes = true;
        var series = new Series { Id = Guid.NewGuid(), PresentationUniqueKey = "series-key" };
        var episode = new Episode { Id = Guid.NewGuid() };
        InternalItemsQuery? asked = null;
        _queries.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback((InternalItemsQuery q) => asked = q)
            .Returns([episode]);

        _service.MarkPlayed(_user, series, null);

        Assert.Equal("series-key", asked!.SeriesPresentationUniqueKey);
        Assert.False(asked.IsVirtualItem);
        Assert.False(asked.IsFolder);
        Assert.Equal(Guid.Empty, asked.ParentId);
        Assert.Equal([BaseItemKind.Episode, BaseItemKind.Season], asked.IncludeItemTypes);
        Assert.Equal([episode.Id], _saved.Select(s => s.Item));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Season_MarksItsOwnEpisodes(bool specialsWithinSeasons)
    {
        _configuration.DisplaySpecialsWithinSeasons = specialsWithinSeasons;
        var series = new Series { Id = Guid.NewGuid(), PresentationUniqueKey = "series-key" };
        var season = new Season { Id = Guid.NewGuid(), SeriesId = series.Id, IndexNumber = 1, PresentationUniqueKey = "season-key" };
        var mine = new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1 };
        var other = new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 2 };
        var missing = new Episode { Id = Guid.NewGuid(), ParentIndexNumber = 1, IsVirtualItem = true, PremiereDate = Now.AddYears(-1) };
        _lookup.Setup(m => m.GetItemById<Series>(series.Id)).Returns(series);
        InternalItemsQuery? asked = null;
        _queries.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback((InternalItemsQuery q) => asked = q)
            .Returns([mine, other, missing]);

        _service.MarkPlayed(_user, season, null);

        Assert.Equal(specialsWithinSeasons ? "series-key" : null, asked!.SeriesPresentationUniqueKey);
        Assert.Equal(specialsWithinSeasons ? null : "season-key", asked.AncestorWithPresentationUniqueKey);
        Assert.Equal([BaseItemKind.Episode], asked.IncludeItemTypes);

        // The user does not display missing episodes: only the real one of this season.
        Assert.Equal([mine.Id], _saved.Select(s => s.Item));
    }

    [Fact]
    public void Collection_MarksTheMembersTheUserMaySee()
    {
        var visible = new Movie { Id = Guid.NewGuid() };
        var withheld = Guid.NewGuid();
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren = [new LinkedChild { ItemId = visible.Id }, new LinkedChild { ItemId = withheld }],
        };
        _access.Setup(m => m.GetVisibleItemById<BaseItem>(visible.Id, _user)).Returns(visible);

        _service.MarkPlayed(_user, boxSet, null);

        Assert.Equal([visible.Id], _saved.Select(s => s.Item));
        _queries.Verify(m => m.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void Collection_MemberSeries_MarksItsEpisodes()
    {
        var series = new Series { Id = Guid.NewGuid(), PresentationUniqueKey = "series-key" };
        var episode = new Episode { Id = Guid.NewGuid() };
        var boxSet = new BoxSet { Id = Guid.NewGuid(), LinkedChildren = [new LinkedChild { ItemId = series.Id }] };
        _access.Setup(m => m.GetVisibleItemById<BaseItem>(series.Id, _user)).Returns(series);
        _queries.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([episode]);

        _service.MarkUnplayed(_user, boxSet);

        Assert.Equal([episode.Id], _saved.Select(s => s.Item));
    }

    [Fact]
    public void UserView_HasNoDescendantsToMark()
    {
        _service.MarkPlayed(_user, new UserView { Id = Guid.NewGuid() }, null);

        Assert.Empty(_saved);
        _queries.Verify(m => m.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void Played_MarksEveryOtherVersion_PositionCleared_CountKept()
    {
        var (primary, linked, local) = VersionGroup();

        _service.MarkPlayed(_user, primary, null);

        Assert.Equal([primary.Id], _saved.Select(s => s.Item));
        Assert.Equal(new[] { linked.Id, local.Id }.Order(), _partialSaves.Select(s => s.Item).Order());
        Assert.All(_partialSaves, s =>
        {
            Assert.Equal(UserDataSaveReason.TogglePlayed, s.Reason);
            Assert.True(s.Dto.Played);
            Assert.Equal(0, s.Dto.PlaybackPositionTicks);
            Assert.Null(s.Dto.PlayCount);
            Assert.Null(s.Dto.LastPlayedDate);
        });
    }

    [Fact]
    public void Played_FromAnAlternate_ReachesThePrimaryAndItsOtherVersions()
    {
        var (primary, linked, local) = VersionGroup();

        _service.MarkOtherVersionsPlayed(_user, linked);

        Assert.Equal(new[] { primary.Id, local.Id }.Order(), _partialSaves.Select(s => s.Item).Order());
    }

    [Fact]
    public void Unplayed_ResetsEveryVersion()
    {
        var (primary, linked, local) = VersionGroup();
        Store(linked, new UserItemData { Key = "l", Played = true, PlayCount = 3, PlaybackPositionTicks = 10, LastPlayedDate = Now });

        _service.MarkUnplayed(_user, primary);

        Assert.Equal(new[] { primary.Id, linked.Id, local.Id }.Order(), _saved.Select(s => s.Item).Order());
        Assert.All(_saved, s =>
        {
            Assert.False(s.Data.Played);
            Assert.Equal(0, s.Data.PlayCount);
            Assert.Equal(0, s.Data.PlaybackPositionTicks);
            Assert.Null(s.Data.LastPlayedDate);
        });
    }

    [Fact]
    public void SingleVersion_WritesNoOtherVersion()
    {
        var solo = new Movie { Id = Guid.NewGuid() };

        _service.MarkPlayed(_user, solo, null);

        Assert.Single(_saved);
        Assert.Empty(_partialSaves);
    }

    private (Movie Primary, Movie Linked, Movie Local) VersionGroup()
    {
        var primary = new Movie { Id = Guid.NewGuid(), LinkedAlternateVersions = [new LinkedChild()] };
        var linked = new Movie { Id = Guid.NewGuid(), PrimaryVersionId = primary.Id };
        var local = new Movie { Id = Guid.NewGuid() };
        foreach (var movie in new[] { primary, linked, local })
        {
            _lookup.Setup(m => m.GetItemById<Video>(movie.Id)).Returns(movie);
        }

        _links.Setup(m => m.GetLinkedChildrenIds(primary.Id, (int)LinkedChildType.LinkedAlternateVersion)).Returns([linked.Id]);
        _links.Setup(m => m.GetLinkedChildrenIds(primary.Id, (int)LinkedChildType.LocalAlternateVersion)).Returns([local.Id]);
        return (primary, linked, local);
    }

    private void Store(BaseItem item, UserItemData data) => _stored[(_user.Id, item.Id)] = data;

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
