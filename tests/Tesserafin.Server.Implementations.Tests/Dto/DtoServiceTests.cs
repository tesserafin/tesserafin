using System;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Common;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Chapters;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Trickplay;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Dto;
using Tesserafin.Server.Implementations.Tests.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Dto;

public class DtoServiceTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IItemPeopleService> _itemPeopleServiceMock;
    private readonly DtoService _dtoService;

    public DtoServiceTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _itemPeopleServiceMock = new Mock<IItemPeopleService>();

        var imageProcessor = new Mock<IImageProcessor>();
        // Deterministic tag derived from the image so each item gets a distinct, assertable tag.
        imageProcessor
            .Setup(x => x.GetImageCacheTag(It.IsAny<BaseItem>(), It.IsAny<ItemImageInfo>()))
            .Returns((BaseItem _, ItemImageInfo image) => "tag:" + image.Path);

        var appHost = new Mock<IApplicationHost>();
        appHost.Setup(x => x.SystemId).Returns("test-server");

        // Video.SourceType probes the active-recording manager; provide one so it doesn't NRE.
        Video.RecordingsManager = new Mock<IRecordingsManager>().Object;

        _dtoService = new DtoService(
            NullLogger<DtoService>.Instance,
            Mock.Of<INamedItemCatalog>(),
            _libraryManagerMock.Object,
            _itemPeopleServiceMock.Object,
            new Mock<IUserDataManager>().Object,
            imageProcessor.Object,
            new Mock<IProviderManager>().Object,
            new Mock<IRecordingsManager>().Object,
            appHost.Object,
            new Mock<IMediaSourceManager>().Object,
            new Lazy<ILiveTvManager>(() => new Mock<ILiveTvManager>().Object),
            new Mock<ITrickplayManager>().Object,
            new Mock<IChapterManager>().Object,
            new Mock<IItemCountService>().Object,
            Mock.Of<IVideoVersionCatalog>(),
            new Lazy<IChannelManager>(() => throw new InvalidOperationException("Unexpected channel manager access.")),
            LibraryWriteFixture.CreateDeletionContext(Mock.Of<IFileSystem>(MockBehavior.Strict)),
            Mock.Of<IStandaloneVisibilityService>(MockBehavior.Strict),
            Mock.Of<IItemExtrasService>(MockBehavior.Strict),
            Mock.Of<IFileSystem>(MockBehavior.Strict));

        // Episode.Series / Episode.Season resolve through the static BaseItem.LibraryManager.
        BaseItem.LibraryManager = _libraryManagerMock.Object;
    }

    [Fact]
    public void GetBaseItemDto_PreferEpisodeParentPoster_PrefersSeasonPosterOverEpisodeAndSeries()
    {
        var (episode, season, series) = BuildEpisode(seasonHasPoster: true);
        var options = new DtoOptions(false) { PreferEpisodeParentPoster = true };

        var dto = _dtoService.GetBaseItemDto(episode, options);

        // The episode's own 16:9 primary is dropped in favor of the season's portrait poster.
        Assert.False(dto.ImageTags is not null && dto.ImageTags.ContainsKey(ImageType.Primary));
        Assert.Null(dto.SeriesPrimaryImageTag);
        Assert.Equal(season.Id, dto.ParentPrimaryImageItemId);
        Assert.Equal("tag:" + season.GetImageInfo(ImageType.Primary, 0)!.Path, dto.ParentPrimaryImageTag);
        // Aspect ratio follows the (portrait) poster, not the episode's 16:9 image.
        Assert.Equal(season.GetDefaultPrimaryImageAspectRatio(), dto.PrimaryImageAspectRatio);
    }

    [Fact]
    public void GetBaseItemDto_PreferEpisodeParentPoster_FallsBackToSeriesWhenSeasonHasNoPoster()
    {
        var (episode, _, series) = BuildEpisode(seasonHasPoster: false);
        var options = new DtoOptions(false) { PreferEpisodeParentPoster = true };

        var dto = _dtoService.GetBaseItemDto(episode, options);

        Assert.False(dto.ImageTags is not null && dto.ImageTags.ContainsKey(ImageType.Primary));
        Assert.Null(dto.SeriesPrimaryImageTag);
        Assert.Equal(series.Id, dto.ParentPrimaryImageItemId);
        Assert.Equal("tag:" + series.GetImageInfo(ImageType.Primary, 0)!.Path, dto.ParentPrimaryImageTag);
    }

    [Fact]
    public void GetBaseItemDto_WithoutPreferEpisodeParentPoster_KeepsEpisodePrimary()
    {
        var (episode, _, _) = BuildEpisode(seasonHasPoster: true);
        var options = new DtoOptions(false);

        var dto = _dtoService.GetBaseItemDto(episode, options);

        // Default behavior: the episode keeps its own primary and exposes the series poster as a tag.
        Assert.NotNull(dto.ImageTags);
        Assert.True(dto.ImageTags.ContainsKey(ImageType.Primary));
        Assert.NotNull(dto.SeriesPrimaryImageTag);
        Assert.Null(dto.ParentPrimaryImageItemId);
    }

    private (Episode Episode, Season Season, Series Series) BuildEpisode(bool seasonHasPoster)
    {
        // Non-local (http) paths keep aspect-ratio resolution off the image processor and on the
        // item's default ratio, which is portrait (2/3) for Season/Series and 16:9 for Episode.
        var series = new Series { Id = Guid.NewGuid(), Name = "Series" };
        series.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = "http://test/series.jpg" }, 0);

        var season = new Season { Id = Guid.NewGuid(), Name = "Season", SeriesId = series.Id };
        if (seasonHasPoster)
        {
            season.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = "http://test/season.jpg" }, 0);
        }

        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Episode",
            SeasonId = season.Id,
            SeriesId = series.Id
        };
        episode.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = "http://test/episode.jpg" }, 0);

        _libraryManagerMock.Setup(x => x.GetItemById(season.Id)).Returns(season);
        _libraryManagerMock.Setup(x => x.GetItemById(series.Id)).Returns(series);

        return (episode, season, series);
    }
}
