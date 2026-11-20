using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Controller;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Naming.Common;
using Tesserafin.Providers.MediaInfo;
using Xunit;

namespace Tesserafin.Providers.Tests.MediaInfo;

public class AudioResolverTests
{
    private readonly AudioResolver _audioResolver;

    public AudioResolverTests()
    {
        // build resolver to test with
        var localizationManager = Mock.Of<ILocalizationManager>();

        var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        mediaEncoder.Setup(me => me.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .Returns<MediaInfoRequest, CancellationToken>((_, _) => Task.FromResult(new Tesserafin.Model.MediaInfo.MediaInfo
            {
                MediaStreams = new List<MediaStream>
                {
                    new()
                    {
                        Type = MediaStreamType.Audio
                    }
                }
            }));

        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(fs => fs.DirectoryExists(It.IsRegex(MediaInfoResolverTests.VideoDirectoryRegex)))
            .Returns(true);
        fileSystem.Setup(fs => fs.DirectoryExists(It.IsRegex(MediaInfoResolverTests.MetadataDirectoryRegex)))
            .Returns(true);

        _audioResolver = new AudioResolver(Mock.Of<ILogger<AudioResolver>>(), localizationManager, mediaEncoder.Object, fileSystem.Object, Mock.Of<IServerApplicationPaths>(paths => paths.InternalMetadataPath == MediaInfoResolverTests.MetadataRoot), new NamingOptions());
    }

    [Theory]
    [InlineData("My.Video.srt", false, false)]
    [InlineData("My.Video.mp3", false, true)]
    [InlineData("My.Video.srt", true, false)]
    [InlineData("My.Video.mp3", true, true)]
    public async Task GetExternalStreams_MixedFilenames_PicksAudio(string file, bool metadataDirectory, bool matches)
    {
        var video = new Movie
        {
            Path = MediaInfoResolverTests.VideoDirectoryPath + "/My.Video.mkv"
        };

        var directoryService = MediaInfoResolverTests.GetDirectoryServiceForExternalFile(file, metadataDirectory);
        var streams = await _audioResolver.GetExternalStreamsAsync(video, 0, directoryService, false, CancellationToken.None);

        if (matches)
        {
            Assert.Single(streams);
            var actual = streams[0];
            Assert.Equal(MediaStreamType.Audio, actual.Type);
        }
        else
        {
            Assert.Empty(streams);
        }
    }
}
