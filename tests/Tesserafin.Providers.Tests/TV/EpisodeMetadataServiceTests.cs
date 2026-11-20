using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Providers.Manager;
using Tesserafin.Providers.TV;
using Xunit;
using ProbedMediaInfo = Tesserafin.Model.MediaInfo.MediaInfo;

namespace Tesserafin.Providers.Tests.TV;

public class EpisodeMetadataServiceTests
{
    private readonly TestEpisodeMetadataService _service = new();

    [Fact]
    public async Task BeforeRefresh_ReadsEmbeddedEpisodeInfoBeforeTheFilenameFallback()
    {
        var sources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
        var source = new MediaSourceInfo { Path = "episode.mp4" };
        var episode = CreateMp4Episode();
        sources.Setup(manager => manager.GetStaticMediaSources(episode.Object, false, null)).Returns([source]);

        var token = TestContext.Current.CancellationToken;
        var encoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        encoder.Setup(e => e.GetMediaInfo(
            It.Is<MediaInfoRequest>(r => ReferenceEquals(r.MediaSource, source) && r.MediaType == DlnaProfileType.Video),
            token)).ReturnsAsync(new ProbedMediaInfo { ParentIndexNumber = 2, IndexNumber = 3, ShowName = "Show" });

        (int? Season, int? Number, string? Show) embeddedAtFallback = default;
        var naming = new Mock<IItemNamingService>(MockBehavior.Strict);
        naming.Setup(n => n.FillMissingEpisodeNumbersFromPath(episode.Object, false))
            .Callback(() => embeddedAtFallback = (episode.Object.ParentIndexNumber, episode.Object.IndexNumber, episode.Object.SeriesName))
            .Returns(false);

        var service = new TestEpisodeMetadataService(encoder.Object, naming.Object, sources.Object);
        await service.PrepareAsync(episode.Object, token);

        Assert.Equal(2, embeddedAtFallback.Season);
        Assert.Equal(3, embeddedAtFallback.Number);
        Assert.Equal("Show", embeddedAtFallback.Show);
    }

    [Fact]
    public async Task BeforeRefresh_CancellationStopsTheProbeAndFilenameFallback()
    {
        var encoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        var naming = new Mock<IItemNamingService>(MockBehavior.Strict);
        var service = new TestEpisodeMetadataService(encoder.Object, naming.Object);
        var cancellationToken = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(CreateMp4Episode().Object, cancellationToken).AsTask());

        encoder.VerifyNoOtherCalls();
        naming.VerifyNoOtherCalls();
    }

    [Fact]
    public void MergeData_ProviderSeasonOverridesPathDerivedSeason()
    {
        var source = new MetadataResult<Episode>
        {
            Item = new Episode
            {
                ParentIndexNumber = 2
            }
        };

        var target = new MetadataResult<Episode>
        {
            Item = new Episode
            {
                ParentIndexNumber = 1
            }
        };

        _service.Merge(source, target, replaceData: false, mergeMetadataSettings: true);

        Assert.Equal(2, target.Item.ParentIndexNumber);
    }

    [Fact]
    public void MergeData_BackfillExistingMetadata_DoesNotOverrideProviderSeason()
    {
        var existingMetadata = new MetadataResult<Episode>
        {
            Item = new Episode
            {
                ParentIndexNumber = 1
            }
        };

        var temp = new MetadataResult<Episode>
        {
            Item = new Episode
            {
                ParentIndexNumber = 2
            }
        };

        _service.Merge(existingMetadata, temp, replaceData: false, mergeMetadataSettings: false);

        Assert.Equal(2, temp.Item.ParentIndexNumber);
    }

    [Fact]
    public void MergeData_MissingProviderSeasonKeepsExistingSeason()
    {
        var source = new MetadataResult<Episode>
        {
            Item = new Episode()
        };

        var target = new MetadataResult<Episode>
        {
            Item = new Episode
            {
                ParentIndexNumber = 1
            }
        };

        _service.Merge(source, target, replaceData: false, mergeMetadataSettings: true);

        Assert.Equal(1, target.Item.ParentIndexNumber);
    }

    [Fact]
    public void BeforeSave_RebindsAMovedEpisodeAndLooksUpProvidersFromTheNewHierarchy()
    {
        var previousSeries = new Series { Id = Guid.NewGuid(), Name = "Previous series" };
        var previousSeason = new Season { Id = Guid.NewGuid(), Name = "Previous season" };
        var series = new Series { Id = Guid.NewGuid(), Name = "Series", PresentationUniqueKey = "series-key" };
        series.ProviderIds["Tvdb"] = "series-provider";
        var season = new Season { Id = Guid.NewGuid(), IndexNumber = 2 };
        season.ProviderIds["Tvdb"] = "season-provider";
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            ParentId = series.Id,
            SeriesId = previousSeries.Id,
            SeasonId = previousSeason.Id,
            ParentIndexNumber = 2,
            PreferredMetadataLanguage = "en",
            PreferredMetadataCountryCode = "US"
        };
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        lookup.Setup(l => l.GetItemById(previousSeries.Id)).Returns(previousSeries);
        lookup.Setup(l => l.GetItemById(previousSeason.Id)).Returns(previousSeason);
        lookup.Setup(l => l.GetItemById(series.Id)).Returns(series);
        lookup.Setup(l => l.GetItemById(season.Id)).Returns(season);
        var repository = new Mock<IItemRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(series.Id)))).Returns([season]);
        var service = new TestEpisodeMetadataService(lookup: lookup.Object, repository: repository.Object);

        Assert.True(service.PrepareSave(episode).HasFlag(ItemUpdateType.MetadataImport));
        Assert.Equal(previousSeries.Name, episode.SeriesName);
        Assert.Equal(previousSeason.Name, episode.SeasonName);
        Assert.Equal(series.Id, episode.SeriesId);
        Assert.Equal(season.Id, episode.SeasonId);
        Assert.Equal(series.PresentationUniqueKey, episode.SeriesPresentationUniqueKey);

        var info = service.ReadLookupInfo(episode);
        Assert.Equal("series-provider", info.SeriesProviderIds["Tvdb"]);
        Assert.Equal("season-provider", info.SeasonProviderIds["Tvdb"]);
    }

    private static Mock<Episode> CreateMp4Episode()
    {
        var episode = new Mock<Episode> { CallBase = true };
        episode.SetupGet(e => e.SourceType).Returns(SourceType.Library);
        episode.Object.Name = "Episode";
        episode.Object.Container = "MP4";
        return episode;
    }

    private sealed class TestEpisodeMetadataService : EpisodeMetadataService
    {
        public TestEpisodeMetadataService(
            IMediaEncoder? encoder = null,
            IItemNamingService? naming = null,
            IMediaSourceManager? sources = null,
            IItemLookupService? lookup = null,
            IItemRepository? repository = null)
            : base(
                Mock.Of<IItemWriteCommands>(),
                Mock.Of<IItemPeopleCommands>(),
                Mock.Of<IServerConfigurationManager>(),
                NullLogger<EpisodeMetadataService>.Instance,
                Mock.Of<IProviderManager>(),
                Mock.Of<IFileSystem>(),
                Mock.Of<ILibraryItemOptions>(),
                naming ?? Mock.Of<IItemNamingService>(),
                Mock.Of<IExternalDataManager>(),
                repository ?? Mock.Of<IItemRepository>(),
                encoder ?? Mock.Of<IMediaEncoder>(),
                sources ?? Mock.Of<IMediaSourceManager>(),
                lookup ?? Mock.Of<IItemLookupService>(),
                Mock.Of<ICollectionFolderResolver>(MockBehavior.Strict),
                new ActiveRecordings())
        {
        }

        public ValueTask<bool> PrepareAsync(Episode episode, CancellationToken cancellationToken)
            => BeforeMetadataRefreshAsync(episode, new LibraryOptions { EnableEmbeddedEpisodeInfos = true }, false, cancellationToken);

        public ItemUpdateType PrepareSave(Episode episode) => BeforeSaveInternal(episode, false, ItemUpdateType.None);

        public EpisodeInfo ReadLookupInfo(Episode episode) => GetLookupInfo(episode, new LibraryOptions());

        public void Merge(MetadataResult<Episode> source, MetadataResult<Episode> target, bool replaceData, bool mergeMetadataSettings)
        {
            MergeData(source, target, Array.Empty<MetadataField>(), replaceData, mergeMetadataSettings);
        }
    }
}
