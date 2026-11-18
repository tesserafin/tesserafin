using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Providers.MediaInfo;
using Xunit;
using ProbeMediaInfo = Tesserafin.Model.MediaInfo.MediaInfo;

namespace Tesserafin.Providers.Tests.MediaInfo;

public class FFProbeVideoInfoTests
{
    private readonly FFProbeVideoInfo _fFProbeVideoInfo;

    public FFProbeVideoInfoTests()
    {
        var serverConfiguration = new ServerConfiguration()
        {
            DummyChapterDuration = (int)TimeSpan.FromMinutes(5).TotalSeconds
        };
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(c => c.Configuration)
            .Returns(serverConfiguration);

        IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject(serverConfig);
        _fFProbeVideoInfo = fixture.Create<FFProbeVideoInfo>();
    }

    [Theory]
    [InlineData(EmbeddedSubtitleOptions.AllowAll, true, true, true)]
    [InlineData(EmbeddedSubtitleOptions.AllowText, false, true, false)]
    [InlineData(EmbeddedSubtitleOptions.AllowImage, true, false, true)]
    [InlineData(EmbeddedSubtitleOptions.AllowNone, false, false, false)]
    public async Task ProbeVideo_UsesOneLibraryPolicyForTitleAndPersistedSubtitles(EmbeddedSubtitleOptions subtitles, bool embeddedTitle, bool expectText, bool expectImage)
    {
        var video = new Video
        {
            Id = Guid.NewGuid(),
            Name = "File title",
            Path = Path.Combine(Path.GetTempPath(), "probe-policy", "source.mkv")
        };
        var token = TestContext.Current.CancellationToken;
        var itemOptions = new Mock<ILibraryItemOptions>(MockBehavior.Strict);
        itemOptions.Setup(o => o.GetLibraryOptions(video)).Returns(new LibraryOptions
        {
            AllowEmbeddedSubtitles = subtitles,
            EnableEmbeddedTitles = embeddedTitle
        });
        var encoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        encoder.Setup(e => e.GetMediaInfo(It.Is<MediaInfoRequest>(request => request.MediaSource.Path == video.Path), token))
            .ReturnsAsync(new ProbeMediaInfo
            {
                Name = "Embedded title",
                MediaStreams =
                [
                    new MediaStream { Type = MediaStreamType.Video, Codec = "h264" },
                    new MediaStream { Type = MediaStreamType.Subtitle, Codec = "subrip" },
                    new MediaStream { Type = MediaStreamType.Subtitle, Codec = "hdmv_pgs_subtitle" }
                ]
            });
        var streams = new Mock<IMediaStreamRepository>();
        IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject(itemOptions.Object);
        fixture.Inject(encoder.Object);
        fixture.Inject(streams.Object);
        fixture.Inject(Mock.Of<IFileSystem>());
        var prober = fixture.Create<FFProbeVideoInfo>();
        var refresh = new MetadataRefreshOptions(Mock.Of<IDirectoryService>(MockBehavior.Strict)) { MetadataRefreshMode = MetadataRefreshMode.None };

        await prober.ProbeVideo(video, refresh, token);

        Assert.Equal(embeddedTitle ? "Embedded title" : "File title", video.Name);
        Assert.Equal(expectText || expectImage, video.HasSubtitles);
        streams.Verify(r => r.SaveMediaStreams(video.Id, It.Is<IReadOnlyList<MediaStream>>(saved =>
            saved.Count == 1 + (expectText ? 1 : 0) + (expectImage ? 1 : 0)
            && saved.Any(stream => stream.Type == MediaStreamType.Video)
            && saved.Any(stream => stream.Type == MediaStreamType.Subtitle && stream.IsTextSubtitleStream) == expectText
            && saved.Any(stream => stream.Type == MediaStreamType.Subtitle && !stream.IsTextSubtitleStream) == expectImage), token), Times.Once);
        itemOptions.Verify(o => o.GetLibraryOptions(video), Times.Once);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void CreateDummyChapters_InvalidRuntime_ThrowsArgumentException(long? runtime)
    {
        Assert.Throws<ArgumentException>(
            () => _fFProbeVideoInfo.CreateDummyChapters(new Video()
            {
                RunTimeTicks = runtime
            }));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0L, 0)]
    [InlineData(1L, 1)]
    [InlineData(TimeSpan.TicksPerMinute * 3, 1)]
    [InlineData(TimeSpan.TicksPerMinute * 5, 1)]
    [InlineData((TimeSpan.TicksPerMinute * 5) + 1, 1)]
    [InlineData(TimeSpan.TicksPerMinute * 50, 10)]
    public void CreateDummyChapters_ValidRuntime_CorrectChaptersCount(long? runtime, int chaptersCount)
    {
        var chapters = _fFProbeVideoInfo.CreateDummyChapters(new Video()
        {
            RunTimeTicks = runtime
        });

        Assert.Equal(chaptersCount, chapters.Length);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(TimeSpan.TicksPerMinute * 3)]
    [InlineData(TimeSpan.TicksPerMinute * 5)]
    [InlineData((TimeSpan.TicksPerMinute * 5) + 1)]
    [InlineData((TimeSpan.TicksPerMinute * 50) + 1)]
    public void CreateDummyChapters_PositiveRuntime_NoChapterBeyondRuntime(long runtime)
    {
        var chapters = _fFProbeVideoInfo.CreateDummyChapters(new Video()
        {
            RunTimeTicks = runtime
        });

        Assert.All(chapters, chapter => Assert.True(chapter.StartPositionTicks < runtime));
    }
}
