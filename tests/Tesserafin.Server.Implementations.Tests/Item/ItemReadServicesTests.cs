using System;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Entities;
using Tesserafin.Server.Implementations.Item;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Item;

/// <summary>
/// The catalogue reads an entity performs on itself, in place of the former BaseItem.ItemRepository and
/// Video.RecordingsManager statics.
/// </summary>
public sealed class ItemReadServicesTests
{
    [Fact]
    public void GetChildren_QueriesTheRepositoryForTheFolder_Ungrouped()
    {
        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(folder.Id) && !q.GroupByPresentationUniqueKey)))
            .Returns([child]);

        var children = new ItemReadServices(repository.Object, new ActiveRecordings(), new(), Mock.Of<Tesserafin.Controller.Configuration.IServerConfigurationManager>()).GetChildren(folder);

        Assert.Same(child, Assert.Single(children));
    }

    [Fact]
    public void IsPlayed_AsksTheRepositoryRecursively()
    {
        var user = new User("u", "auth", "reset") { Id = Guid.NewGuid() };
        var folderId = Guid.NewGuid();
        var repository = Mock.Of<IItemRepository>(r => r.GetIsPlayed(user, folderId, true) == true);

        Assert.True(new ItemReadServices(repository, new ActiveRecordings(), new(), Mock.Of<Tesserafin.Controller.Configuration.IServerConfigurationManager>()).IsPlayed(user, folderId));
    }

    [Fact]
    public void IsActiveRecording_NoRecordingInProgress_IsFalse()
        => Assert.False(new ItemReadServices(Mock.Of<IItemRepository>(), new ActiveRecordings(), new(), Mock.Of<Tesserafin.Controller.Configuration.IServerConfigurationManager>()).IsActiveRecording("/rec/a.ts"));
}
