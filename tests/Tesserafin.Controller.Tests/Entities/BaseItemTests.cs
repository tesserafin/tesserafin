using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.MediaSegments;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.LiveTv;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.Controller.Tests.Entities;

[Collection(BaseItemStaticStateFixture.Name)]
public class BaseItemTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("1", "0000000001")]
    [InlineData("t", "t")]
    [InlineData("test", "test")]
    [InlineData("test1", "test0000000001")]
    [InlineData("1test 2", "0000000001test 0000000002")]
    public void BaseItem_ModifySortChunks_Valid(string input, string expected)
        => Assert.Equal(expected, BaseItem.ModifySortChunks(input));

    [Theory]
    [InlineData("/Movies/Ted/Ted.mp4", "/Movies/Ted/Ted - Unrated Edition.mp4", "Ted", "Unrated Edition")]
    [InlineData("/Movies/Deadpool 2 (2018)/Deadpool 2 (2018).mkv", "/Movies/Deadpool 2 (2018)/Deadpool 2 (2018) - Super Duper Cut.mkv", "Deadpool 2 (2018)", "Super Duper Cut")]
    public void GetMediaSourceName_Valid(string primaryPath, string altPath, string name, string altName)
    {
        var video = new Video()
        {
            Path = primaryPath
        };

        var videoAlt = new Video()
        {
            Path = altPath,
        };

        Assert.Equal(name, video.GetMediaSourceName(video, MediaProtocol.File, static () => true));
        Assert.Equal(altName, video.GetMediaSourceName(videoAlt, MediaProtocol.File, static () => true));
    }

    [Theory]
    // Episode versions share a season folder; the common prefix (not the folder name) yields the label.
    // Both files carry a suffix (no bare base name), so the shared "- " must be stripped too.
    [InlineData(
        "Spider-Noir - S01E02 - Wo ist Flint - Greyscale",
        "Spider-Noir - S01E02 - Wo ist Flint - Colorized",
        "Greyscale",
        "Colorized")]
    // One version is the bare base name; the other is suffixed.
    [InlineData(
        "Spider-Noir - S01E02 - Wo ist Flint",
        "Spider-Noir - S01E02 - Wo ist Flint - Greyscale",
        "Spider-Noir - S01E02 - Wo ist Flint",
        "Greyscale")]
    // Suffixes share a leading word ("Grey"); the prefix must retreat to the separator, not split it.
    [InlineData(
        "Demo - S01E01 - Greyscale",
        "Demo - S01E01 - Greyish",
        "Greyscale",
        "Greyish")]
    // Underscore separator.
    [InlineData("Movie (2020)_4K", "Movie (2020)_1080p", "4K", "1080p")]
    // Dot separator.
    [InlineData("Movie (2020).UHD", "Movie (2020).1080p", "UHD", "1080p")]
    // Resolution variants that share leading digits must retreat to the separator, not yield "p"/"i".
    [InlineData("Movie - 1080p", "Movie - 1080i", "1080p", "1080i")]
    // A token shared by the descriptors but separated only by spaces (the resolution) must stay in the
    // label: retreat to the '-' delimiter, not the interior space, so the resolution is kept.
    [InlineData(
        "movie (2020) - 2160p Extended",
        "movie (2020) - 2160p Original",
        "2160p Extended",
        "2160p Original")]
    // Bracketed version labels: the opening bracket is kept in the label.
    [InlineData(
        "Blade Runner (1982) [Final Cut] [1080p HEVC AAC]",
        "Blade Runner (1982) [EE by ADM] [480p HEVC AAC]",
        "[Final Cut] [1080p HEVC AAC]",
        "[EE by ADM] [480p HEVC AAC]")]
    public void GetMediaSourceName_CommonPrefix_Valid(string primaryName, string altName, string expectedPrimary, string expectedAlt)
    {
        var primaryPath = "/Shows/Demo/Season 01/" + primaryName + ".mkv";
        var altPath = "/Shows/Demo/Season 01/" + altName + ".mkv";
        var commonPrefix = BaseItem.GetCommonVersionPrefix([primaryName, altName]);

        var video = new Video()
        {
            Path = primaryPath
        };

        var videoAlt = new Video()
        {
            Path = altPath,
        };

        Assert.Equal(expectedPrimary, video.GetMediaSourceName(video, MediaProtocol.File, static () => false, commonPrefix));
        Assert.Equal(expectedAlt, videoAlt.GetMediaSourceName(videoAlt, MediaProtocol.File, static () => false, commonPrefix));
    }

    [Fact]
    public void GetMediaSources_UsesSuppliedPathClassificationForRemoteNames()
    {
        var audio = new Audio { Id = Guid.NewGuid(), Path = "https://example.invalid/audio.mp3", Name = "Remote audio" };
        var sources = new Mock<IMediaSourceManager>();
        sources.Setup(manager => manager.GetPathProtocol(audio.Path)).Returns(MediaProtocol.Http);
        sources.Setup(manager => manager.GetMediaStreams(audio.Id)).Returns([]);
        sources.Setup(manager => manager.GetMediaAttachments(audio.Id)).Returns([]);
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>(MockBehavior.Strict);

        var source = Assert.Single(audio.GetMediaSources(false, sources.Object, Mock.Of<IMediaSegmentManager>(), CreateReadContext()));

        Assert.Equal(MediaProtocol.Http, source.Protocol);
        Assert.Equal("Remote audio", source.Name);
        Assert.Equal(audio.Path, source.Path);
        sources.Verify(manager => manager.GetPathProtocol(audio.Path), Times.Once);
    }

    [Fact]
    public void GetCatalogueVersions_FromAnyVersion_ReturnsEveryVersionOnce()
    {
        var (primary, alt1, alt2) = SetupVersionGroup();
        var context = CreateVersionGroupReadContext(primary, alt1, alt2);

        foreach (var source in new[] { primary, alt1, alt2 })
        {
            var versions = source.GetCatalogueVersions(context.Items, context.Links);

            Assert.Equal(3, versions.Count);
            Assert.Contains(primary, versions);
            Assert.Contains(alt1, versions);
            Assert.Contains(alt2, versions);
        }
    }

    private static (Video Primary, Video Alt1, Video Alt2) SetupVersionGroup()
    {
        var primary = new Video { Id = Guid.NewGuid(), Path = "/Movies/Movie/Movie.mkv" };
        var alt1 = new Video { Id = Guid.NewGuid(), Path = "/Movies/Movie/Movie - 1080p.mkv", PrimaryVersionId = primary.Id };
        var alt2 = new Video { Id = Guid.NewGuid(), Path = "/Movies/Movie/Movie - 4K.mkv", PrimaryVersionId = primary.Id };

        // 2160p primary, 1080p alternates: width is only the ordering tiebreaker, set so it would place
        // the primary first — letting the tests confirm the queried version's own source still wins.
        var widths = new Dictionary<Guid, int> { [primary.Id] = 3840, [alt1.Id] = 1920, [alt2.Id] = 1920 };
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(x => x.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
        mediaSourceManager.Setup(x => x.GetMediaStreams(It.IsAny<Guid>()))
            .Returns((Guid id) => new List<MediaStream> { new MediaStream { Type = MediaStreamType.Video, Width = widths.GetValueOrDefault(id) } });
        mediaSourceManager.Setup(x => x.GetMediaAttachments(It.IsAny<Guid>())).Returns(new List<MediaAttachment>());

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(x => x.GetLinkedAlternateVersions(It.IsAny<Video>())).Returns(Array.Empty<Video>());
        libraryManager.Setup(x => x.GetLocalAlternateVersionIds(primary)).Returns(new[] { alt1.Id, alt2.Id });
        libraryManager.Setup(x => x.GetLocalAlternateVersionIds(alt1)).Returns(Array.Empty<Guid>());
        libraryManager.Setup(x => x.GetLocalAlternateVersionIds(alt2)).Returns(Array.Empty<Guid>());
        libraryManager.Setup(x => x.GetItemById(alt1.Id)).Returns(alt1);
        libraryManager.Setup(x => x.GetItemById(alt2.Id)).Returns(alt2);
        libraryManager.Setup(x => x.GetItemById(primary.Id)).Returns(primary);

        var recordingsManager = new Mock<IRecordingsManager>();
        recordingsManager.Setup(x => x.GetActiveRecordingInfo(It.IsAny<string>())).Returns((ActiveRecordingInfo?)null);
        Video.RecordingsManager = recordingsManager.Object;

        BaseItem.MediaSourceManager = mediaSourceManager.Object;
        BaseItem.LibraryManager = libraryManager.Object;

        return (primary, alt1, alt2);
    }

    [Fact]
    public void GetMediaSources_DefaultsToTheQueriedVersionsOwnSource()
    {
        var (primary, alt1, alt2) = SetupVersionGroup();
        var segmentManager = Mock.Of<IMediaSegmentManager>();
        var context = CreateVersionGroupReadContext(primary, alt1, alt2);

        // Resuming the 1080p alternate must default to the 1080p source, not the higher-resolution
        // 2160p primary that the width ordering would otherwise place first.
        Assert.Equal(alt1.Id.ToString("N"), alt1.GetMediaSources(false, BaseItem.MediaSourceManager, segmentManager, context)[0].Id);

        // Opening the primary still defaults to the primary's own (here highest-resolution) source.
        Assert.Equal(primary.Id.ToString("N"), primary.GetMediaSources(false, BaseItem.MediaSourceManager, segmentManager, context)[0].Id);
    }

    [Fact]
    public void GetCatalogueMediaSourceIds_FromAnyVersion_HasNoDuplicates()
    {
        var (primary, alt1, alt2) = SetupVersionGroup();
        var context = CreateVersionGroupReadContext(primary, alt1, alt2);

        // Each version must surface exactly once, regardless of which member the list is built from.
        // Building from an alternate previously re-added that alternate as a "local alternate" of the
        // primary, producing a duplicate entry in the version dropdown.
        foreach (var source in new[] { primary, alt1, alt2 })
        {
            var ids = source.GetCatalogueMediaSourceIds(context.Items, context.Links).ToList();

            Assert.Equal(3, ids.Count);
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Contains(primary.Id, ids);
            Assert.Contains(alt1.Id, ids);
            Assert.Contains(alt2.Id, ids);
        }
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    public void IsCompleteMedia_UsesChannelTagsAndLiveRecordingSnapshot(bool channel, bool liveStream, bool recording, bool expected)
    {
        var video = new Video
        {
            ChannelId = channel ? Guid.NewGuid() : Guid.Empty,
            Path = "/Library/video.ts",
            Tags = liveStream ? ["liveSTREAM"] : []
        };
        var recordings = new ActiveRecordings();
        using var cancellation = new CancellationTokenSource();
        if (recording)
        {
            recordings["recording"] = new ActiveRecordingInfo
            {
                Path = video.Path,
                Timer = new TimerInfo { Status = RecordingStatus.InProgress },
                CancellationTokenSource = cancellation
            };
        }

        Assert.Equal(expected, video.IsCompleteMedia(recordings));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void CanDelete_ChannelItem_RequiresChannelCapabilityAndUserPermission(bool channelAllows, bool userAllows, bool expected)
    {
        var item = new Book { ChannelId = Guid.NewGuid() };
        var channels = new Mock<IChannelManager>(MockBehavior.Strict);
        channels.Setup(x => x.CanDelete(item)).Returns(channelAllows);
        var deletion = new ItemDeletionContext(
            Mock.Of<IFileSystem>(MockBehavior.Strict),
            new Lazy<IChannelManager>(() => channels.Object),
            new ActiveRecordings(),
            Mock.Of<ICollectionFolderResolver>(MockBehavior.Strict));
        var user = new User("u", "auth", "reset");
        user.SetPermission(PermissionKind.EnableContentDeletion, false);
        user.SetPreference(PreferenceKind.EnableContentDeletionFromFolders, [userAllows ? item.ChannelId : Guid.NewGuid()]);

        Assert.Equal(expected, item.CanDelete(user, deletion));
    }

    [Fact]
    public void CanDelete_ActiveRecording_IsDeniedBeforeReadingTheFileSystem()
    {
        var movie = new Movie { Path = "/Recordings/in-progress.ts" };
        using var cancellation = new CancellationTokenSource();
        var recordings = new ActiveRecordings
        {
            ["recording"] = new ActiveRecordingInfo
            {
                Path = movie.Path,
                Timer = new TimerInfo { Status = RecordingStatus.InProgress },
                CancellationTokenSource = cancellation
            }
        };
        var deletion = new ItemDeletionContext(
            Mock.Of<IFileSystem>(MockBehavior.Strict),
            new Lazy<IChannelManager>(() => throw new InvalidOperationException("Unexpected channel manager access.")),
            recordings,
            Mock.Of<ICollectionFolderResolver>(MockBehavior.Strict));

        Assert.False(movie.CanDelete(deletion));
    }

    [Fact]
    public void GetEnableMediaSourceDisplay_NonChannelItem_ReturnsTrueWithoutCallingChannelManager()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        var channelManager = new Lazy<IChannelManager>(() => throw new InvalidOperationException("Unexpected channel manager access."));

        // Video.SourceType (touched via BaseItem.GetEnableMediaSourceDisplay) calls
        // Video.IsActiveRecording(), which reads the static Video.RecordingsManager. That static is
        // only ever assigned in production at startup (see ApplicationHost), so it must be set
        // explicitly here rather than relying on another test in this collection having left a value
        // behind — see BaseItemStaticStateFixture for why leaking statics between tests is fragile.
        var recordingsManager = new Mock<IRecordingsManager>();
        recordingsManager.Setup(x => x.GetActiveRecordingInfo(It.IsAny<string>())).Returns((ActiveRecordingInfo?)null);
        Video.RecordingsManager = recordingsManager.Object;

        Assert.True(movie.GetEnableMediaSourceDisplay(channelManager));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetEnableMediaSourceDisplay_ChannelItem_DelegatesToChannelManager(bool channelManagerResult)
    {
        var channelItem = new Movie { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid() };
        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(x => x.EnableMediaSourceDisplay(channelItem)).Returns(channelManagerResult);

        // Video.SourceType (touched via BaseItem.GetEnableMediaSourceDisplay) calls
        // Video.IsActiveRecording(), which reads the static Video.RecordingsManager. That static is
        // only ever assigned in production at startup (see ApplicationHost), so it must be set
        // explicitly here rather than relying on another test in this collection having left a value
        // behind — see BaseItemStaticStateFixture for why leaking statics between tests is fragile.
        var recordingsManager = new Mock<IRecordingsManager>();
        recordingsManager.Setup(x => x.GetActiveRecordingInfo(It.IsAny<string>())).Returns((ActiveRecordingInfo?)null);
        Video.RecordingsManager = recordingsManager.Object;

        Assert.Equal(channelManagerResult, channelItem.GetEnableMediaSourceDisplay(new Lazy<IChannelManager>(() => channelManager.Object)));
    }

    [Fact]
    public void GetMediaSources_ChannelItem_WithStaticSources_ReturnsThemWithoutFallingBackToVersions()
    {
        var channelItem = new Movie { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid() };

        var staticSource = new MediaSourceInfo { Id = Guid.NewGuid().ToString("N") };
        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(x => x.GetStaticMediaSources(channelItem, It.IsAny<CancellationToken>()))
            .Returns([staticSource]);

        var mediaSourceManager = new Mock<IMediaSourceManager>(MockBehavior.Strict);
        var segmentManager = new Mock<IMediaSegmentManager>(MockBehavior.Strict);

        var result = channelItem.GetMediaSources(false, mediaSourceManager.Object, segmentManager.Object, CreateReadContext(channelManager.Object));

        Assert.Single(result);
        Assert.Equal(staticSource.Id, result[0].Id);
    }

    [Fact]
    public void GetMediaSources_ChannelItem_WithNoStaticSources_FallsBackToOwnVersion()
    {
        var channelItem = new Movie { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid(), Path = "/Channels/Live/Live.mkv" };

        var channelManager = new Mock<IChannelManager>();
        channelManager.Setup(x => x.GetStaticMediaSources(channelItem, It.IsAny<CancellationToken>()))
            .Returns(Array.Empty<MediaSourceInfo>());

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(x => x.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
        mediaSourceManager.Setup(x => x.GetMediaStreams(It.IsAny<Guid>())).Returns(new List<MediaStream>());
        mediaSourceManager.Setup(x => x.GetMediaAttachments(It.IsAny<Guid>())).Returns(new List<MediaAttachment>());

        var segmentManager = new Mock<IMediaSegmentManager>();
        segmentManager.Setup(x => x.IsTypeSupported(It.IsAny<BaseItem>())).Returns(false);

        var result = channelItem.GetMediaSources(false, mediaSourceManager.Object, segmentManager.Object, CreateReadContext(channelManager.Object));

        Assert.Single(result);
        Assert.Equal(channelItem.Id.ToString("N"), result[0].Id);
    }

    private static MediaSourceReadContext CreateVersionGroupReadContext(Video primary, Video alt1, Video alt2)
    {
        var lookup = new Mock<IItemLookupService>(MockBehavior.Strict);
        foreach (var video in new[] { primary, alt1, alt2 })
        {
            lookup.Setup(items => items.GetItemById(video.Id)).Returns(video);
        }

        var links = new Mock<ILinkedChildrenService>();
        links.Setup(x => x.GetLinkedChildrenIds(It.IsAny<Guid>(), It.IsAny<int?>())).Returns([]);
        links.Setup(x => x.GetLinkedChildrenIds(primary.Id, (int)Tesserafin.Controller.Entities.LinkedChildType.LocalAlternateVersion)).Returns([alt1.Id, alt2.Id]);
        return CreateReadContext(lookup: lookup.Object, links: links.Object);
    }

    private static MediaSourceReadContext CreateReadContext(IChannelManager? channels = null, IItemLookupService? lookup = null, ILinkedChildrenService? links = null)
        => new(
            lookup ?? Mock.Of<IItemLookupService>(MockBehavior.Strict),
            links ?? Mock.Of<ILinkedChildrenService>(service => service.GetLinkedChildrenIds(It.IsAny<Guid>(), It.IsAny<int?>()) == Array.Empty<Guid>()),
            new Lazy<IChannelManager>(() => channels ?? throw new InvalidOperationException("Unexpected channel manager access.")),
            new ActiveRecordings(),
            static path => path);

    [Fact]
    public void GetMediaSources_ActiveRecordingUsesTheCatalogueAndReturnsAPlaceholder()
    {
        var video = new Video { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid(), Path = "/Recordings/in-progress.ts" };
        using var recordingCancellation = new CancellationTokenSource();
        var context = CreateReadContext();
        context.Recordings["recording"] = new ActiveRecordingInfo
        {
            Path = video.Path,
            Timer = new TimerInfo { Status = RecordingStatus.InProgress },
            CancellationTokenSource = recordingCancellation
        };
        var sources = new Mock<IMediaSourceManager>();
        sources.Setup(manager => manager.GetMediaStreams(video.Id)).Returns([]);
        sources.Setup(manager => manager.GetMediaAttachments(video.Id)).Returns([]);

        var source = Assert.Single(video.GetMediaSources(false, sources.Object, Mock.Of<IMediaSegmentManager>(), context));

        Assert.Equal(video.Id.ToString("N"), source.Id);
        Assert.Equal(MediaSourceType.Placeholder, source.Type);
    }
}
