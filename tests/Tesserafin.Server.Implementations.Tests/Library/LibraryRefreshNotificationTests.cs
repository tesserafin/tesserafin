using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Session;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Events;
using Tesserafin.Model.Session;
using Tesserafin.Server.Core.EntryPoints;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class LibraryRefreshNotificationTests
{
    [Fact]
    public async Task ProgressMessages_AverageOnlyActivePhysicalFolders_AndClearTheLibraryOnCompletion()
    {
        var root = new AggregateFolder { Id = Guid.NewGuid(), IsRoot = true };
        var active = new Folder { Id = Guid.NewGuid(), ParentId = root.Id };
        var other = new Folder { Id = Guid.NewGuid(), ParentId = root.Id };
        var idle = new Folder { Id = Guid.NewGuid(), ParentId = root.Id };
        var wrongType = new Video { Id = Guid.NewGuid() };
        var missing = Guid.NewGuid();
        var library = new CollectionFolder
        {
            Id = Guid.NewGuid(),
            PhysicalFolderIds = [active.Id, missing, other.Id, wrongType.Id, idle.Id]
        };
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        foreach (var item in new BaseItem[] { root, active, other, idle, wrongType })
        {
            lookup.Setup(l => l.GetItemById(item.Id)).Returns(item);
        }

        lookup.Setup(l => l.GetItemById(missing)).Returns((BaseItem?)null);
        var collections = new Mock<ICollectionFolderResolver>(MockBehavior.Strict);
        collections.Setup(c => c.GetCollectionFolders(active)).Returns([library]);
        var refreshProgress = new Mock<ILibraryRefreshProgress>();
        refreshProgress.SetupSequence(p => p.GetRefreshProgress(active.Id)).Returns(40).Returns((double?)null);
        refreshProgress.SetupSequence(p => p.GetRefreshProgress(other.Id)).Returns(85).Returns((double?)null);
        var messages = new List<(string Item, string Progress)>();
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.SendMessageToAdminSessions(SessionMessageType.RefreshProgress, It.IsAny<Dictionary<string, string>>(), CancellationToken.None))
            .Callback((SessionMessageType _, Dictionary<string, string> message, CancellationToken _) => messages.Add((message["ItemId"], message["Progress"])))
            .Returns(Task.CompletedTask);
        using var notifier = new LibraryChangedNotifier(
            collections.Object,
            Mock.Of<IUserRootFolderProvider>(),
            lookup.Object,
            Mock.Of<IItemChangeEvents>(),
            Mock.Of<IServerConfigurationManager>(),
            sessions.Object,
            Mock.Of<IUserManager>(),
            NullLogger<LibraryChangedNotifier>.Instance,
            refreshProgress.Object,
            Mock.Of<IItemSortService>(),
            Mock.Of<IStandaloneVisibilityService>(MockBehavior.Strict));

        var token = TestContext.Current.CancellationToken;
        await notifier.StartAsync(token);
        try
        {
            refreshProgress.Raise(p => p.RefreshProgress += null, new GenericEventArgs<Tuple<BaseItem, double>>(Tuple.Create<BaseItem, double>(active, 40)));
            refreshProgress.Raise(p => p.RefreshCompleted += null, new GenericEventArgs<BaseItem>(active));
        }
        finally
        {
            await notifier.StopAsync(token);
        }

        Assert.Equal(
            new[] { (active.Id.ToString("N"), "40"), (library.Id.ToString("N"), "62.5"), (active.Id.ToString("N"), "100"), (library.Id.ToString("N"), "0") },
            messages);
        refreshProgress.Verify(p => p.GetRefreshProgress(missing), Times.Never);
        refreshProgress.Verify(p => p.GetRefreshProgress(wrongType.Id), Times.Never);
    }
}
