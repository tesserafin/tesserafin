using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Subtitles;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Model.Providers;
using Tesserafin.Naming.Common;
using Tesserafin.Providers.Lyric;
using Tesserafin.Providers.MediaInfo;
using Tesserafin.Providers.Subtitles;
using Xunit;

namespace Tesserafin.Providers.Tests.MediaInfo;

public sealed class SidecarStoragePolicyTests
{
    private const string SubtitleText = "1\n00:00:00,000 --> 00:00:01,000\nA line\n";
    private const string LyricText = "First line\nSecond line";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadSubtitle_UsesTheLibraryDestination(bool besideMedia)
    {
        using var storage = new Storage();
        var video = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(storage.MediaDirectory, "film.mkv") };
        var itemOptions = new Mock<ILibraryItemOptions>(MockBehavior.Strict);
        itemOptions.Setup(o => o.GetLibraryOptions(video)).Returns(new LibraryOptions { SaveSubtitlesWithMedia = besideMedia });
        var manager = CreateSubtitleManager(storage, itemOptions.Object);

        await manager.UploadSubtitle(video, Subtitle());

        await storage.AssertSidecarAsync(video, "film.eng.srt", besideMedia, SubtitleText);
        itemOptions.Verify(o => o.GetLibraryOptions(video), Times.Once);
    }

    [Fact]
    public async Task AutomatedSubtitle_UsesTheSearchPolicyForTheSavedFile()
    {
        using var storage = new Storage();
        var video = new CompleteMovie { Id = Guid.NewGuid(), Path = Path.Combine(storage.MediaDirectory, "film.mkv") };
        var token = TestContext.Current.CancellationToken;
        var settings = new LibraryOptions
        {
            SubtitleDownloadLanguages = ["eng"],
            SaveSubtitlesWithMedia = false,
            DisabledSubtitleFetchers = ["Blocked"],
            SubtitleFetcherOrder = ["Preferred"]
        };
        var itemOptions = new Mock<ILibraryItemOptions>(MockBehavior.Strict);
        var blocked = Provider("Blocked");
        var preferred = Provider("Preferred");
        preferred.Setup(p => p.Search(It.Is<SubtitleSearchRequest>(r => r.Language == "eng" && r.IsAutomated), token))
            .ReturnsAsync(new[] { new RemoteSubtitleInfo { Id = "match", ProviderName = "Preferred" } });
        preferred.Setup(p => p.GetSubtitles("match", token)).ReturnsAsync(Subtitle);
        var manager = CreateSubtitleManager(storage, itemOptions.Object, blocked.Object, preferred.Object);
        var downloader = new SubtitleDownloader(NullLogger.Instance, manager);

        var downloaded = await downloader.DownloadSubtitles(video, [], settings, true, token);

        Assert.Equal("eng", Assert.Single(downloaded));
        await storage.AssertSidecarAsync(video, "film.eng.srt", false, SubtitleText);
        blocked.Verify(p => p.Search(It.IsAny<SubtitleSearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        itemOptions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lyrics_UseLibraryOptionsForUploadsAndKeepExplicitOptionsForEmbeddedText(bool besideMedia)
    {
        using var storage = new Storage();
        var upload = new Audio { Id = Guid.NewGuid(), Path = Path.Combine(storage.MediaDirectory, "upload.mp3") };
        var embedded = new Audio { Id = Guid.NewGuid(), Path = Path.Combine(storage.MediaDirectory, "embedded.mp3") };
        var itemOptions = new Mock<ILibraryItemOptions>(MockBehavior.Strict);
        itemOptions.Setup(o => o.GetLibraryOptions(upload)).Returns(new LibraryOptions { SaveLyricsWithMedia = besideMedia });
        var manager = new LyricManager(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemDeletionCommands>(),
            new Lazy<IItemMetadataDispatcher>(() => Mock.Of<IItemMetadataDispatcher>(MockBehavior.Strict)),
            itemOptions.Object,
            storage.ApplicationPaths,
            NullLogger<LyricManager>.Instance,
            Mock.Of<IFileSystem>(),
            Mock.Of<ILibraryMonitor>(),
            Mock.Of<IMediaSourceManager>(),
            [],
            [new TxtLyricParser()]);

        using var text = new MemoryStream(Encoding.UTF8.GetBytes(LyricText));
        Assert.NotNull(await manager.SaveLyricAsync(upload, "lrc", text));
        Assert.NotNull(await manager.SaveLyricAsync(embedded, new LibraryOptions { SaveLyricsWithMedia = !besideMedia }, "lrc", LyricText));

        await storage.AssertSidecarAsync(upload, "upload.lrc", besideMedia, LyricText);
        await storage.AssertSidecarAsync(embedded, "embedded.lrc", !besideMedia, LyricText);
        itemOptions.Verify(o => o.GetLibraryOptions(upload), Times.Once);
        itemOptions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeletingLyrics_RefreshesTheItemThroughTheInjectedDispatcher()
    {
        var audio = new Audio { Id = Guid.NewGuid() };
        const string lyricsPath = "song.lrc";
        var mediaSources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
        mediaSources.Setup(m => m.GetMediaStreams(It.Is<MediaStreamQuery>(q => q.ItemId.Equals(audio.Id) && q.Type == MediaStreamType.Lyric)))
            .Returns([new MediaStream { Path = lyricsPath }]);
        var sequence = new MockSequence();
        var files = new Mock<IFileSystem>(MockBehavior.Strict);
        files.InSequence(sequence).Setup(f => f.DeleteFile(lyricsPath));
        var dispatcher = new Mock<IItemMetadataDispatcher>(MockBehavior.Strict);
        dispatcher.InSequence(sequence).Setup(d => d.RefreshSingleItem(
            audio,
            It.Is<MetadataRefreshOptions>(options => options.MetadataRefreshMode == MetadataRefreshMode.Default),
            CancellationToken.None)).ReturnsAsync(ItemUpdateType.None);
        var metadata = new Lazy<IItemMetadataDispatcher>(() => dispatcher.Object);
        var manager = new LyricManager(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemDeletionCommands>(),
            metadata,
            Mock.Of<ILibraryItemOptions>(),
            Mock.Of<IServerApplicationPaths>(),
            NullLogger<LyricManager>.Instance,
            files.Object,
            Mock.Of<ILibraryMonitor>(),
            mediaSources.Object,
            [],
            []);

        Assert.False(metadata.IsValueCreated);
        await manager.DeleteLyricsAsync(audio);

        files.Verify(f => f.DeleteFile(lyricsPath), Times.Once);
        dispatcher.Verify(
            d => d.RefreshSingleItem(audio, It.IsAny<MetadataRefreshOptions>(), CancellationToken.None),
            Times.Once);
    }

    private static SubtitleManager CreateSubtitleManager(Storage storage, ILibraryItemOptions itemOptions, params ISubtitleProvider[] providers)
        => new(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemDeletionCommands>(),
            new Lazy<IItemMetadataDispatcher>(() => Mock.Of<IItemMetadataDispatcher>(MockBehavior.Strict)),
            itemOptions,
            storage.ApplicationPaths,
            NullLogger<SubtitleManager>.Instance,
            Mock.Of<IFileSystem>(),
            Mock.Of<ILibraryMonitor>(),
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<ILocalizationManager>(),
            providers,
            new NamingOptions());

    private static Mock<ISubtitleProvider> Provider(string name)
    {
        var provider = new Mock<ISubtitleProvider>(MockBehavior.Strict);
        provider.SetupGet(p => p.Name).Returns(name);
        provider.SetupGet(p => p.SupportedMediaTypes).Returns([VideoContentType.Movie]);
        return provider;
    }

    private static SubtitleResponse Subtitle()
        => new() { Language = "eng", Format = "srt", Stream = new MemoryStream(Encoding.UTF8.GetBytes(SubtitleText)) };

    private sealed class CompleteMovie : Movie
    {
        protected override bool IsActiveRecording() => false;
    }

    private sealed class Storage : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tesserafin-sidecar-policy-");

        public Storage()
        {
            MediaDirectory = _directory.CreateSubdirectory("media").FullName;
            MetadataRoot = _directory.CreateSubdirectory("metadata").FullName;
            ApplicationPaths = Mock.Of<IServerApplicationPaths>(p => p.InternalMetadataPath == MetadataRoot);
        }

        public string MediaDirectory { get; }

        public string MetadataRoot { get; }

        public IServerApplicationPaths ApplicationPaths { get; }

        public async Task AssertSidecarAsync(BaseItem item, string fileName, bool besideMedia, string expected)
        {
            var media = Path.Combine(MediaDirectory, fileName);
            var metadata = Path.Combine(item.GetInternalMetadataPath(MetadataRoot), fileName);
            Assert.Equal(expected, await File.ReadAllTextAsync(besideMedia ? media : metadata, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(besideMedia ? metadata : media));
        }

        public void Dispose() => _directory.Delete(true);
    }
}
