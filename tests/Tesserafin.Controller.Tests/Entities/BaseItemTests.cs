using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.MediaSegments;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.Controller.Tests.Entities;

[Collection(BaseItemStaticStateFixture.Name)]
public class BaseItemTests
{
    [Theory]
    [InlineData(null, false, LocationType.Virtual)]
    [InlineData(null, true, LocationType.Remote)]
    [InlineData("/library/film.mkv", false, LocationType.FileSystem)]
    [InlineData("file:///library/film.mkv", false, LocationType.FileSystem)]
    [InlineData("https://example.test/film.mkv", false, LocationType.Remote)]
    public void LocationType_ClassifiesThePathWithoutUsingTheFileSystem(string? path, bool isChannel, LocationType expected)
    {
        var item = new Movie { Path = path!, ChannelId = isChannel ? Guid.NewGuid() : Guid.Empty };

        Assert.Equal(expected, item.LocationType);
    }

    [Fact]
    public void RequiresRefresh_ReadsModificationTimeFromSuppliedFileSystem()
    {
        var item = new Movie { Path = "/media/film.mkv", DateModified = DateTime.UnixEpoch };
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(fs => fs.GetFileSystemInfo(item.Path))
            .Returns(new FileSystemMetadata { Exists = true, LastWriteTimeUtc = item.DateModified.AddMinutes(1) });

        var services = new MetadataRefreshServices(
            fileSystem.Object,
            Mock.Of<IServerApplicationPaths>(),
            Mock.Of<IItemNamingService>(),
            NullLogger.Instance);
        Assert.True(item.RequiresRefresh(services));
    }

    [Theory]
    [InlineData("/library/music.flac", true, "/share/music.flac")]
    [InlineData("/library/music.flac", false, "/library/music.flac")]
    [InlineData("https://example.test/music.flac", true, "https://example.test/music.flac")]
    public void GetMediaSources_MapsOnlyLocalPathsWhenRequested(string path, bool substitute, string expected)
    {
        var item = new Audio { Id = Guid.NewGuid(), Path = path };
        var sources = new Mock<IMediaSourceManager>();
        sources.Setup(manager => manager.MapFilePath("/library/music.flac")).Returns("/share/music.flac");

        var result = item.GetMediaSources(substitute, sources.Object, Mock.Of<IMediaSegmentManager>(), Mock.Of<IChannelManager>());

        Assert.Equal(expected, Assert.Single(result).Path);
        sources.Verify(manager => manager.MapFilePath(It.IsAny<string>()), expected == "/share/music.flac" ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task RefreshMetadata_UsesSuppliedProviderManager()
    {
        var item = new Movie();
        var options = new MetadataRefreshOptions(Mock.Of<IDirectoryService>());
        var providers = new Mock<IProviderManager>(MockBehavior.Strict);
        var cancellationToken = TestContext.Current.CancellationToken;
        providers.Setup(manager => manager.RefreshSingleItem(item, options, cancellationToken))
            .ReturnsAsync(ItemUpdateType.MetadataImport);

        var result = await item.RefreshMetadata(
            Mock.Of<IItemDeletionCommands>(),
            Mock.Of<IItemWriteCommands>(),
            providers.Object,
            options,
            cancellationToken);

        Assert.Equal(ItemUpdateType.MetadataImport, result);
        providers.Verify(manager => manager.RefreshSingleItem(item, options, cancellationToken), Times.Once);
    }

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

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(x => x.GetLocalAlternateVersionIds(It.IsAny<Video>()))
                .Returns([Guid.Empty]);
        BaseItem.LibraryManager = libraryManager.Object;

        Assert.Equal(name, video.GetMediaSourceName(video));
        Assert.Equal(altName, video.GetMediaSourceName(videoAlt));
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

        var libraryManager = new Mock<ILibraryManager>();
        // No local alternate versions: these are linked (separate items), so the folder fallback is unavailable.
        libraryManager.Setup(x => x.GetLocalAlternateVersionIds(It.IsAny<Video>()))
                .Returns(Array.Empty<Guid>());
        BaseItem.LibraryManager = libraryManager.Object;

        Assert.Equal(expectedPrimary, video.GetMediaSourceName(video, commonPrefix));
        Assert.Equal(expectedAlt, videoAlt.GetMediaSourceName(videoAlt, commonPrefix));
    }

    [Fact]
    public void GetAlternateVersion_ReturnsMatchingLocalVersion()
    {
        var (primary, alt1, alt2, _) = SetupVersionGroup();

        Assert.Same(alt1, primary.GetAlternateVersion(alt1.Id));
        Assert.Same(alt2, primary.GetAlternateVersion(alt2.Id));
        Assert.Same(primary, primary.GetAlternateVersion(primary.Id));
        Assert.Null(primary.GetAlternateVersion(Guid.NewGuid()));
    }

    [Fact]
    public void GetAllVersions_FromAnyVersion_ReturnsEveryVersionOnce()
    {
        var (primary, alt1, alt2, _) = SetupVersionGroup();

        foreach (var source in new[] { primary, alt1, alt2 })
        {
            var versions = source.GetAllVersions();

            Assert.Equal(3, versions.Count);
            Assert.Contains(versions, v => v.Id.Equals(primary.Id));
            Assert.Contains(versions, v => v.Id.Equals(alt1.Id));
            Assert.Contains(versions, v => v.Id.Equals(alt2.Id));
        }
    }

    private static (Video Primary, Video Alt1, Video Alt2, IMediaSourceManager MediaSources) SetupVersionGroup()
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

        BaseItem.LibraryManager = libraryManager.Object;

        return (primary, alt1, alt2, mediaSourceManager.Object);
    }

    [Fact]
    public void GetMediaSources_DefaultsToTheQueriedVersionsOwnSource()
    {
        var (primary, alt1, _, mediaSources) = SetupVersionGroup();

        // Resuming the 1080p alternate must default to the 1080p source, not the higher-resolution
        // 2160p primary that the width ordering would otherwise place first.
        Assert.Equal(alt1.Id.ToString("N"), alt1.GetMediaSources(false, mediaSources, Mock.Of<IMediaSegmentManager>(), Mock.Of<IChannelManager>())[0].Id);

        // Opening the primary still defaults to the primary's own (here highest-resolution) source.
        Assert.Equal(primary.Id.ToString("N"), primary.GetMediaSources(false, mediaSources, Mock.Of<IMediaSegmentManager>(), Mock.Of<IChannelManager>())[0].Id);
    }

    [Fact]
    public void GetAllItemsForMediaSources_FromAnyVersion_HasNoDuplicates()
    {
        var (primary, alt1, alt2, _) = SetupVersionGroup();

        var method = typeof(Video).GetMethod("GetAllItemsForMediaSources", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        // Each version must surface exactly once, regardless of which member the list is built from.
        // Building from an alternate previously re-added that alternate as a "local alternate" of the
        // primary, producing a duplicate entry in the version dropdown.
        foreach (var source in new[] { primary, alt1, alt2 })
        {
            var items = (IEnumerable<(BaseItem Item, MediaSourceType MediaSourceType)>)method!.Invoke(source, null)!;
            var ids = items.Select(i => i.Item.Id).ToList();

            Assert.Equal(3, ids.Count);
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Contains(primary.Id, ids);
            Assert.Contains(alt1.Id, ids);
            Assert.Contains(alt2.Id, ids);
        }
    }

    [Fact]
    public void GetEnableMediaSourceDisplay_NonChannelItem_ReturnsTrueWithoutCallingChannelManager()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        var channelManager = new Mock<IChannelManager>(MockBehavior.Strict);

        // Video.SourceType (touched via BaseItem.GetEnableMediaSourceDisplay) calls
        // Video.IsActiveRecording(), which reads the static Video.RecordingsManager. That static is
        // only ever assigned in production at startup (see ApplicationHost), so it must be set
        // explicitly here rather than relying on another test in this collection having left a value
        // behind — see BaseItemStaticStateFixture for why leaking statics between tests is fragile.
        var recordingsManager = new Mock<IRecordingsManager>();
        recordingsManager.Setup(x => x.GetActiveRecordingInfo(It.IsAny<string>())).Returns((ActiveRecordingInfo?)null);

        Assert.True(movie.GetEnableMediaSourceDisplay(channelManager.Object));
        channelManager.Verify(x => x.EnableMediaSourceDisplay(It.IsAny<BaseItem>()), Times.Never);
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

        Assert.Equal(channelManagerResult, channelItem.GetEnableMediaSourceDisplay(channelManager.Object));
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

        // Video.SourceType reads the static Video.RecordingsManager; set it here, as above.

        var result = channelItem.GetMediaSources(false, mediaSourceManager.Object, segmentManager.Object, channelManager.Object);

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

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(x => x.GetLinkedAlternateVersions(It.IsAny<Video>())).Returns(Array.Empty<Video>());
        libraryManager.Setup(x => x.GetLocalAlternateVersionIds(It.IsAny<Video>())).Returns(Array.Empty<Guid>());
        BaseItem.LibraryManager = libraryManager.Object;

        // Video.SourceType (touched via BaseItem.GetMediaSources' own-version fallback) calls
        // Video.IsActiveRecording(), which reads the static Video.RecordingsManager. That static is
        // only ever assigned in production at startup (see ApplicationHost), so it must be set
        // explicitly here rather than relying on another test in this collection having left a value
        // behind — see BaseItemStaticStateFixture for why leaking statics between tests is fragile.
        var recordingsManager = new Mock<IRecordingsManager>();
        recordingsManager.Setup(x => x.GetActiveRecordingInfo(It.IsAny<string>())).Returns((ActiveRecordingInfo?)null);

        var result = channelItem.GetMediaSources(false, mediaSourceManager.Object, segmentManager.Object, channelManager.Object);

        Assert.Single(result);
        Assert.Equal(channelItem.Id.ToString("N"), result[0].Id);
    }
}
