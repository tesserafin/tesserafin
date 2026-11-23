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
using Tesserafin.Model.Dto;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Providers.TV;
using Xunit;
using EmbeddedInfo = Tesserafin.Model.MediaInfo.MediaInfo;

namespace Tesserafin.Providers.Tests.TV;

/// <summary>
/// The embedded mp4 episode tags are read by the episode metadata service through injected services,
/// not by the entity through the former <c>Episode.MediaEncoder</c> static.
/// </summary>
/// <remarks>The refresh pipeline still reads <c>BaseItem.LibraryManager</c>; the test swaps it inside the
/// collection that already serializes that static and restores it.</remarks>
[Collection("LibraryManagerTests")]
public sealed class EpisodeEmbeddedInfoProbeTests
{
    private static readonly LibraryOptions _enabled = new() { EnableEmbeddedEpisodeInfos = true };

    private readonly Mock<IMediaEncoder> _encoder = new(MockBehavior.Strict);
    private readonly Mock<IMediaSourceManager> _sources = new();
    private readonly ProbeService _service;

    public EpisodeEmbeddedInfoProbeTests()
    {
        _sources.Setup(s => s.GetStaticMediaSources(It.IsAny<BaseItem>(), false, null)).Returns([new MediaSourceInfo { Path = "/tv/e.mp4" }]);
        _service = new ProbeService(_encoder.Object, _sources.Object);
    }

    [Fact]
    public async Task Mp4Episode_TakesTheEmbeddedNumbersAndShowName()
    {
        var episode = Mp4Episode();
        _encoder.Setup(e => e.GetMediaInfo(It.Is<MediaInfoRequest>(r => r.MediaSource.Path == "/tv/e.mp4"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddedInfo { ParentIndexNumber = 2, IndexNumber = 7, ShowName = "Show" });

        Assert.True(await _service.Probe(episode, _enabled, CancellationToken.None));
        Assert.Equal(2, episode.ParentIndexNumber);
        Assert.Equal(7, episode.IndexNumber);
        Assert.Equal("Show", episode.SeriesName);
    }

    [Fact]
    public async Task SameValues_ReportNoChange()
    {
        var episode = Mp4Episode();
        episode.ParentIndexNumber = 2;
        episode.IndexNumber = 7;
        episode.SeriesName = "Show";
        _encoder.Setup(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddedInfo { ParentIndexNumber = 2, IndexNumber = 7, ShowName = "Show" });

        Assert.False(await _service.Probe(episode, _enabled, CancellationToken.None));
    }

    [Fact]
    public async Task ProbeFailure_LeavesTheEpisodeUnchanged()
    {
        var episode = Mp4Episode();
        episode.IndexNumber = 1;
        _encoder.Setup(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffprobe failed"));

        Assert.False(await _service.Probe(episode, _enabled, CancellationToken.None));
        Assert.Equal(1, episode.IndexNumber);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _encoder.Setup(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.Probe(Mp4Episode(), _enabled, cancellation.Token));
    }

    [Theory]
    [InlineData("mkv", true, false)]
    [InlineData("mp4", false, false)]
    [InlineData("mp4", true, true)]
    public async Task OtherContainer_DisabledOption_OrLockedEpisode_IsNotProbed(string container, bool enabled, bool locked)
    {
        var episode = Mp4Episode();
        episode.Container = container;
        episode.IsLocked = locked;

        // The strict encoder mock throws if it is called.
        Assert.False(await _service.Probe(episode, new LibraryOptions { EnableEmbeddedEpisodeInfos = enabled }, CancellationToken.None));
    }

    [Fact]
    public async Task RefreshMetadata_RunsTheProbeBeforeTheEntityHook()
    {
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        var itemOptions = Mock.Of<ILibraryItemOptions>(o => o.GetLibraryOptions(It.IsAny<BaseItem>()) == _enabled);
        var naming = new Mock<IItemNamingService>();
        var episode = Mp4Episode();
        _encoder.Setup(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddedInfo { IndexNumber = 7 });

        // The path parser sees the probed number: it only fills what is still missing.
        naming.Setup(n => n.FillMissingEpisodeNumbersFromPath(episode, false))
            .Returns(() =>
            {
                Assert.Equal(7, episode.IndexNumber);
                return false;
            });

        var fileSystem = Mock.Of<IFileSystem>(f => f.GetFileSystemInfo(It.IsAny<string>()) == new FileSystemMetadata { Exists = false });
        var service = new EpisodeMetadataService(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemPeopleCommands>(),
            configuration.Object,
            NullLogger<EpisodeMetadataService>.Instance,
            Mock.Of<IProviderManager>(),
            fileSystem,
            itemOptions,
            naming.Object,
            Mock.Of<IExternalDataManager>(),
            Mock.Of<IItemRepository>(),
            _encoder.Object,
            _sources.Object,
            Mock.Of<ILocalizationManager>(),
            Mock.Of<IItemLookupService>(),
            Mock.Of<ICollectionFolderResolver>());

        await service.RefreshMetadata(
            episode,
            new MetadataRefreshOptions(Mock.Of<IDirectoryService>()) { ImageRefreshMode = MetadataRefreshMode.ValidationOnly },
            CancellationToken.None);

        _encoder.Verify(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        naming.Verify(n => n.FillMissingEpisodeNumbersFromPath(episode, false), Times.Once);
        Assert.Equal(7, episode.IndexNumber);
    }

    private static Episode Mp4Episode() => new() { Id = Guid.NewGuid(), Path = "/tv/e.mp4", Container = "mp4" };

    private sealed class ProbeService : EpisodeMetadataService
    {
        public ProbeService(IMediaEncoder encoder, IMediaSourceManager sources)
            : base(
                Mock.Of<IItemWriteCommands>(),
                Mock.Of<IItemPeopleCommands>(),
                Mock.Of<IServerConfigurationManager>(),
                NullLogger<EpisodeMetadataService>.Instance,
                Mock.Of<IProviderManager>(),
                Mock.Of<IFileSystem>(),
                Mock.Of<ILibraryItemOptions>(),
                Mock.Of<IItemNamingService>(),
                Mock.Of<IExternalDataManager>(),
                Mock.Of<IItemRepository>(),
                encoder,
                sources,
                Mock.Of<ILocalizationManager>(),
                Mock.Of<IItemLookupService>(),
                Mock.Of<ICollectionFolderResolver>())
        {
        }

        public Task<bool> Probe(Episode episode, LibraryOptions options, CancellationToken cancellationToken)
            => BeforeMetadataRefreshAsync(episode, options, new MetadataRefreshOptions(Mock.Of<IDirectoryService>()), cancellationToken);
    }
}
