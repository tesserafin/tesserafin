using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Session;
using Tesserafin.Data.Entities;
using Tesserafin.Server.Core.SyncPlay;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.SyncPlay;

public class GroupTests
{
    public GroupTests()
    {
        var mockLogger = new Mock<ILogger<Tesserafin.Server.Core.SyncPlay.Group>>();
        MockLoggerFactory = new Mock<ILoggerFactory>();
        MockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);

        MockUserManager = new Mock<IUserManager>();
        MockSessionManager = new Mock<ISessionManager>();
        MockItemLookupService = new Mock<IItemLookupService>();
        MockItemAccessService = new Mock<IItemAccessService>();

        // Guard: this mock item throws if visibility is ever checked directly on the item
        // instead of being decided by IItemAccessService. If HasAccessToQueue regresses to
        // calling BaseItem.IsVisibleStandalone directly, this test suite must fail loudly.
        MockItem = new Mock<BaseItem>();
        MockItem.Setup(i => i.IsVisibleStandalone(It.IsAny<User>()))
            .Throws(new InvalidOperationException(
                "BaseItem.IsVisibleStandalone should not be called directly; visibility must be decided by IItemAccessService."));
    }

    private Mock<ILoggerFactory> MockLoggerFactory { get; }

    private Mock<IUserManager> MockUserManager { get; }

    private Mock<ISessionManager> MockSessionManager { get; }

    private Mock<IItemLookupService> MockItemLookupService { get; }

    private Mock<IItemAccessService> MockItemAccessService { get; }

    private Mock<BaseItem> MockItem { get; }

    private Tesserafin.Server.Core.SyncPlay.Group CreateGroup()
    {
        return new Tesserafin.Server.Core.SyncPlay.Group(
            MockLoggerFactory.Object,
            MockUserManager.Object,
            MockSessionManager.Object,
            MockItemLookupService.Object,
            MockItemAccessService.Object);
    }

    [Fact]
    public void HasAccessToPlayQueue_ReturnsTrue_WhenItemsAreVisible()
    {
        MockItemAccessService
            .Setup(m => m.GetVisibleItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>()))
            .Returns(MockItem.Object);

        var group = CreateGroup();
        var itemId = Guid.NewGuid();
        var playlist = new List<Guid> { itemId };
        group.PlayQueue.Reset();
        group.PlayQueue.SetPlaylist(playlist);

        Assert.Single(group.PlayQueue.GetPlaylist());
        Assert.Equal(itemId, group.PlayQueue.GetPlaylist()[0].ItemId);

        var user = new User("test-user", "auth-provider", "pwdreset-provider");
        var result = group.HasAccessToPlayQueue(user);

        Assert.True(result);
        MockItemAccessService.Verify(
            m => m.GetVisibleItemById<BaseItem>(itemId, user),
            Times.Once);
    }

    [Fact]
    public void HasAccessToPlayQueue_ReturnsFalse_WhenAccessServiceReturnsNullForInvisibleItem()
    {
        MockItemAccessService
            .Setup(m => m.GetVisibleItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>()))
            .Returns((BaseItem?)null);

        var group = CreateGroup();
        var itemId = Guid.NewGuid();
        var playlist = new List<Guid> { itemId };
        group.PlayQueue.Reset();
        group.PlayQueue.SetPlaylist(playlist);

        Assert.Single(group.PlayQueue.GetPlaylist());
        Assert.Equal(itemId, group.PlayQueue.GetPlaylist()[0].ItemId);

        var user = new User("test-user", "auth-provider", "pwdreset-provider");
        var result = group.HasAccessToPlayQueue(user);

        Assert.False(result);
        MockItemAccessService.Verify(
            m => m.GetVisibleItemById<BaseItem>(itemId, user),
            Times.Once);
    }

    [Fact]
    public void HasAccessToPlayQueue_ReturnsFalse_WhenAccessServiceReturnsNullForAbsentItem()
    {
        // Same signal from the access service (null) covers both "item does not exist" and
        // "item exists but is not visible to the user" - HasAccessToQueue must treat both as
        // no access, without querying item existence separately.
        MockItemAccessService
            .Setup(m => m.GetVisibleItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>()))
            .Returns((BaseItem?)null);

        var group = CreateGroup();
        var itemId = Guid.NewGuid();
        var playlist = new List<Guid> { itemId };
        group.PlayQueue.Reset();
        group.PlayQueue.SetPlaylist(playlist);

        var user = new User("test-user", "auth-provider", "pwdreset-provider");
        var result = group.HasAccessToPlayQueue(user);

        Assert.False(result);
        MockItemAccessService.Verify(
            m => m.GetVisibleItemById<BaseItem>(itemId, user),
            Times.Once);
    }
}
