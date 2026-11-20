using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Lyrics;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Subtitles;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Model.Lyrics;
using Tesserafin.Model.Providers;
using Tesserafin.Providers.Lyric;
using Tesserafin.Providers.MediaInfo;
using Xunit;

namespace Tesserafin.Providers.Tests.MediaInfo;

public sealed class ScheduledSidecarDownloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Subtitles_FilterEachLanguageAndProcessEachVideoOnce(bool skipMatchingTracks)
    {
        var token = TestContext.Current.CancellationToken;
        var folder = new Folder { Id = Guid.NewGuid() };
        var disabledFolder = new Folder { Id = Guid.NewGuid() };
        var movie = new CompleteMovie { Id = Guid.NewGuid(), Path = "film.mkv" };
        string[] languages = ["eng", "fra"];
        var options = new Mock<ILibraryItemOptions>(MockBehavior.Strict);
        options.Setup(o => o.GetLibraryOptions(folder)).Returns(new LibraryOptions
        {
            SubtitleDownloadLanguages = languages,
            SkipSubtitlesIfAudioTrackMatches = skipMatchingTracks,
            SkipSubtitlesIfEmbeddedSubtitlesPresent = skipMatchingTracks
        });
        options.Setup(o => o.GetLibraryOptions(disabledFolder)).Returns(new LibraryOptions
        {
            SubtitleDownloadLanguages = languages,
            DisabledSubtitleFetchers = ["provider"]
        });
        options.Setup(o => o.GetLibraryOptions(movie)).Returns(new LibraryOptions
        {
            SubtitleDownloadLanguages = languages,
            SubtitleFetcherOrder = ["Preferred"]
        });
        var selectedLanguages = new List<string>();
        var queries = new Mock<IItemQueryService>(MockBehavior.Strict);
        queries.Setup(q => q.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            Assert.Equal(folder.Id, query.ParentId);
            Assert.True(query.Recursive);
            Assert.False(query.IsVirtualItem);
            Assert.True(query.IncludeOwnedItems);
            Assert.Equal([BaseItemKind.Episode, BaseItemKind.Movie], query.IncludeItemTypes);
            Assert.Equal(MediaType.Video, Assert.Single(query.MediaTypes));
            Assert.Equal(SourceType.Library, Assert.Single(query.SourceTypes));
            var language = skipMatchingTracks ? query.HasNoSubtitleTrackWithLanguage : query.HasNoExternalSubtitleTrackWithLanguage;
            Assert.NotNull(language);
            Assert.Equal(skipMatchingTracks ? language : null, query.HasNoAudioTrackWithLanguage);
            Assert.Null(skipMatchingTracks ? query.HasNoExternalSubtitleTrackWithLanguage : query.HasNoSubtitleTrackWithLanguage);
            selectedLanguages.Add(language);
            return new BaseItem[] { movie };
        });
        var subtitles = new Mock<ISubtitleManager>(MockBehavior.Strict);
        subtitles.Setup(s => s.SearchSubtitles(It.IsAny<SubtitleSearchRequest>(), token)).ReturnsAsync(Array.Empty<RemoteSubtitleInfo>());
        var mediaSources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
        mediaSources.Setup(s => s.GetMediaStreams(It.Is<MediaStreamQuery>(q => q.ItemId.Equals(movie.Id)))).Returns([]);
        var task = new SubtitleScheduledTask(
            Mock.Of<IItemWriteCommands>(),
            Mock.Of<IItemDeletionCommands>(),
            Mock.Of<IItemMetadataDispatcher>(MockBehavior.Strict),
            Mock.Of<IFileSystem>(),
            PhysicalRoot(folder, disabledFolder),
            options.Object,
            queries.Object,
            subtitles.Object,
            NullLogger<SubtitleScheduledTask>.Instance,
            Mock.Of<ILocalizationManager>(),
            [Mock.Of<ISubtitleProvider>(p => p.Name == "Provider")],
            mediaSources.Object,
            new ActiveRecordings());

        await task.ExecuteAsync(Mock.Of<IProgress<double>>(), token);

        Assert.Equal(languages, selectedLanguages);
        options.Verify(o => o.GetLibraryOptions(movie), Times.Once);
        foreach (var language in languages)
        {
            subtitles.Verify(
                s => s.SearchSubtitles(
                    It.Is<SubtitleSearchRequest>(r => r.IsAutomated && r.Language == language && r.SubtitleFetcherOrder[0] == "Preferred"),
                    token),
                Times.Once);
        }
    }

    [Fact]
    public async Task Lyrics_PageThroughLibraryAudioAndUseItsDownloadPolicy()
    {
        var token = TestContext.Current.CancellationToken;
        var folder = new Folder { Id = Guid.NewGuid() };
        var song = new Audio { Id = Guid.NewGuid(), Name = "Song", Path = "song.mp3" };
        var songsWithLyrics = Enumerable.Range(0, 100).Select(_ => new Audio { Id = Guid.NewGuid() }).ToArray();
        var settings = new LibraryOptions { DisabledLyricFetchers = ["Blocked"], LyricFetcherOrder = ["Preferred"] };
        var options = new Mock<ILibraryItemOptions>(MockBehavior.Strict);
        options.Setup(o => o.GetLibraryOptions(folder)).Returns(settings);
        var count = new Mock<IItemCountService>(MockBehavior.Strict);
        count.Setup(c => c.GetCount(It.Is<InternalItemsQuery>(q => q.ParentId.Equals(Guid.Empty) && q.Recursive
            && q.IsVirtualItem == false && q.IncludeItemTypes.SequenceEqual(new[] { BaseItemKind.Audio })
            && q.MediaTypes.SequenceEqual(new[] { MediaType.Audio }) && q.SourceTypes.SequenceEqual(new[] { SourceType.Library }))))
            .Returns(101);
        var pageOffsets = new List<int>();
        var queries = new Mock<IItemQueryService>(MockBehavior.Strict);
        queries.Setup(q => q.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            Assert.Equal(folder.Id, query.ParentId);
            Assert.True(query.Recursive);
            Assert.False(query.IsVirtualItem);
            Assert.Equal(BaseItemKind.Audio, Assert.Single(query.IncludeItemTypes));
            Assert.Equal(MediaType.Audio, Assert.Single(query.MediaTypes));
            Assert.Equal(SourceType.Library, Assert.Single(query.SourceTypes));
            Assert.Equal(100, query.Limit);
            pageOffsets.Add(query.StartIndex!.Value);
            return query.StartIndex switch
            {
                0 => songsWithLyrics,
                100 => new BaseItem[] { song },
                _ => Array.Empty<BaseItem>()
            };
        });
        var mediaSources = new Mock<IMediaSourceManager>(MockBehavior.Strict);
        mediaSources.Setup(s => s.GetMediaStreams(It.IsAny<MediaStreamQuery>()))
            .Returns((MediaStreamQuery q) => q.ItemId.Equals(song.Id) ? [] : [new MediaStream { Type = MediaStreamType.Lyric }]);
        var match = new RemoteLyricInfoDto { Id = "match", ProviderName = "Preferred", Lyrics = new LyricDto() };
        var lyrics = new Mock<ILyricManager>(MockBehavior.Strict);
        lyrics.Setup(l => l.SearchLyricsAsync(It.IsAny<LyricSearchRequest>(), token)).ReturnsAsync(new[] { match });
        lyrics.Setup(l => l.DownloadLyricsAsync(song, settings, match.Id, token)).ReturnsAsync(match.Lyrics);
        var task = new LyricScheduledTask(
            PhysicalRoot(folder),
            options.Object,
            queries.Object,
            count.Object,
            lyrics.Object,
            NullLogger<LyricScheduledTask>.Instance,
            Mock.Of<ILocalizationManager>(),
            mediaSources.Object);
        var progress = new Mock<IProgress<double>>();

        await task.ExecuteAsync(progress.Object, token);

        Assert.Equal([0, 100, 200], pageOffsets);
        lyrics.Verify(
            l => l.SearchLyricsAsync(
                It.Is<LyricSearchRequest>(r => r.IsAutomated && r.MediaPath == song.Path
                    && r.DisabledLyricFetchers == settings.DisabledLyricFetchers && r.LyricFetcherOrder == settings.LyricFetcherOrder),
                token),
            Times.Once);
        lyrics.Verify(l => l.DownloadLyricsAsync(song, settings, match.Id, token), Times.Once);
        lyrics.VerifyNoOtherCalls();
        progress.Verify(p => p.Report(100d / 101), Times.Once);
        progress.Verify(p => p.Report(100), Times.Exactly(2));
    }

    private static ILibraryRootFolderProvider PhysicalRoot(params BaseItem[] children)
    {
        var root = new Mock<AggregateFolder>();
        root.SetupGet(r => r.Children).Returns(children);
        return Mock.Of<ILibraryRootFolderProvider>(r => r.GetRootFolder() == root.Object);
    }

    private sealed class CompleteMovie : Movie
    {
        protected override bool IsActiveRecording() => false;
    }
}
