using System;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.LiveTv;
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
    public void LiveTvSettings_ReadTheCurrentCatalogueConfiguration()
    {
        var settings = new ServerConfiguration();
        var liveTv = new LiveTvOptions
        {
            ListingProviders = [new ListingsProviderInfo(), new ListingsProviderInfo { MoviePrefix = "Movie:" }]
        };
        var configuration = new Mock<IServerConfigurationManager>(MockBehavior.Strict);
        configuration.SetupGet(manager => manager.Configuration).Returns(settings);
        configuration.Setup(manager => manager.GetConfiguration("livetv")).Returns(liveTv);
        var services = CreateReads(Mock.Of<IItemRepository>(), configuration.Object);

        Assert.False(services.IncludeLiveTvChannelNameInUserDataKeys);
        Assert.Equal("Movie:", services.LiveTvMoviePrefix);

        settings.DisableLiveTvChannelUserDataName = false;
        Assert.True(services.IncludeLiveTvChannelNameInUserDataKeys);
    }

    [Fact]
    public void GetChildren_QueriesTheRepositoryForTheFolder_Ungrouped()
    {
        var folder = new Folder { Id = Guid.NewGuid() };
        var child = new Movie { Id = Guid.NewGuid() };
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(folder.Id) && !q.GroupByPresentationUniqueKey)))
            .Returns([child]);

        var children = CreateReads(repository.Object).GetChildren(folder);

        Assert.Same(child, Assert.Single(children));
    }

    [Fact]
    public void IsPlayed_AsksTheRepositoryRecursively()
    {
        var user = new User("u", "auth", "reset") { Id = Guid.NewGuid() };
        var folderId = Guid.NewGuid();
        var repository = Mock.Of<IItemRepository>(r => r.GetIsPlayed(user, folderId, true) == true);

        Assert.True(CreateReads(repository).IsPlayed(user, folderId));
    }

    [Fact]
    public void IsActiveRecording_NoRecordingInProgress_IsFalse()
        => Assert.False(CreateReads(Mock.Of<IItemRepository>()).IsActiveRecording("/rec/a.ts"));

    private static IItemReadServices CreateReads(IItemRepository repository, IServerConfigurationManager? configuration = null)
        => new ItemReadServices(
            new(() => repository),
            new ActiveRecordings(),
            new(),
            configuration ?? Mock.Of<IServerConfigurationManager>(),
            new(),
            new(),
            new(),
            new());
}
