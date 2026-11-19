using System;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Naming.Common;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public class ItemNamingServiceTests
{
    private static ItemNamingService CreateService(IItemLookupService? lookup = null, IMediaSourceManager? mediaSources = null)
        => new(
            new NamingOptions(),
            lookup ?? Mock.Of<IItemLookupService>(MockBehavior.Strict),
            Mock.Of<IItemRepository>(MockBehavior.Strict),
            new Lazy<IMediaSourceManager>(() => mediaSources ?? Mock.Of<IMediaSourceManager>(MockBehavior.Strict)));

    [Fact]
    public void ParseName_CleansNameAndYear()
    {
        var service = CreateService();

        var result = service.ParseName("The Movie (2020)");

        Assert.Equal("The Movie", result.Name);
        Assert.Equal(2020, result.Year);
    }

    [Fact]
    public void GetSeasonNumberFromPath_UsesParentPathContext()
    {
        var service = CreateService();

        var result = service.GetSeasonNumberFromPath("/media/Shows/Example/Season 02", "/media/Shows/Example");

        Assert.Equal(2, result);
    }

    [Fact]
    public void FillMissingEpisodeNumbersFromPath_PopulatesSeasonAndEpisodeNumbers()
    {
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(x => x.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
        var service = CreateService(mediaSources: mediaSourceManager.Object);
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Path = "/media/Shows/Example/S01E02.mkv",
            VideoType = VideoType.VideoFile
        };

        var result = service.FillMissingEpisodeNumbersFromPath(episode, false);

        Assert.True(result);
        Assert.Equal(1, episode.ParentIndexNumber);
        Assert.Equal(2, episode.IndexNumber);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FillMissingEpisodeNumbersFromPath_ReadsTheParentFolderWhenTheFileCannotBeParsed(bool parentExists)
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "/media/Foo/Foo-S01E01" };
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Path = "/media/Foo/Foo-S01E01/feature.unknown",
            ParentId = folder.Id,
            VideoType = VideoType.VideoFile
        };
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(folder.Id)).Returns(parentExists ? folder : null);
        var mediaSources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
        mediaSources.Setup(s => s.GetPathProtocol(episode.Path)).Returns(MediaProtocol.File);
        var service = CreateService(lookup.Object, mediaSources.Object);

        Assert.Equal(parentExists, service.FillMissingEpisodeNumbersFromPath(episode, false));
        Assert.Equal(parentExists ? 1 : null, episode.ParentIndexNumber);
        Assert.Equal(parentExists ? 1 : null, episode.IndexNumber);
    }
}
