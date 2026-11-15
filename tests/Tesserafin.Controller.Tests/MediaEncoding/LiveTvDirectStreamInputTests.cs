using System;
using System.Collections.Generic;
using System.IO;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Streaming;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Xunit;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// Which input ffmpeg is given for a Live TV stream.
/// </summary>
/// <remarks>
/// A live stream's <c>MediaSource.Path</c> is the <c>[Authorize]</c>d
/// <c>/LiveTv/LiveStreamFiles/{id}/stream.ts</c> URL. ffmpeg is a child process with no session,
/// no api key and no user, so pointing it at that URL means an anonymous fetch, a 401, and exit
/// code 8. When the server already holds the stream open it must hand the bytes over instead.
/// </remarks>
public class LiveTvDirectStreamInputTests
{
    private const string LiveStreamUrl = "http://192.168.0.234:8096/LiveTv/LiveStreamFiles/2db848437faf4a19b756d9d7425c4b50/stream.ts";
    private const string InputPathArgumentMarker = "file:\"/media/movie.mkv\"";

    [Fact]
    public void GetInputArgument_LiveStreamWithProvider_ReadsStdinAndNeverTheAuthorizedUrl()
    {
        var state = BuildLiveState(withProvider: true);

        var args = CreateHelper().GetInputArgument(state, new EncodingOptions(), null).ArgumentLine;

        Assert.Contains(" -i pipe:0", args, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveStreamFiles", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetInputArgument_LiveStreamWithProvider_CarriesNoCredentialOfAnyKind()
    {
        var state = BuildLiveState(withProvider: true);

        var args = CreateHelper().GetInputArgument(state, new EncodingOptions(), null).ArgumentLine;

        foreach (var forbidden in new[] { "api_key", "apikey", "X-Emby-Token", "X-MediaBrowser-Token", "Authorization", "token", "playSessionId" })
        {
            Assert.DoesNotContain(forbidden, args, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GetInputArgument_NoProvider_IsTheHistoricalInputPathUnchanged()
    {
        // The non-live path must be untouched: whatever GetInputPathArgument produces, prefixed
        // by " -i ", is exactly what it produced before stdin selection existed.
        var state = BuildLiveState(withProvider: false);

        var args = CreateHelper().GetInputArgument(state, new EncodingOptions(), null).ArgumentLine;

        Assert.Contains(" -i " + InputPathArgumentMarker, args, StringComparison.Ordinal);
        Assert.DoesNotContain("pipe:0", args, StringComparison.Ordinal);
    }

    [Fact]
    public void GetInputArgument_PlainFileEncodingJob_IsTheHistoricalInputPathUnchanged()
    {
        // Not a StreamState at all - the shape every non-streaming caller (image extraction,
        // keyframe scans, recordings) passes. It can never take the stdin branch.
        var state = new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            MediaSource = new MediaSourceInfo
            {
                Container = "mkv",
                Path = "/media/movie.mkv",
                Protocol = MediaProtocol.File,
                MediaStreams = new List<MediaStream>(),
            },
            BaseRequest = new VideoRequestDto(),
            IsVideoRequest = true,
            IsInputVideo = true,
        };

        var args = CreateHelper().GetInputArgument(state, new EncodingOptions(), null).ArgumentLine;

        Assert.Contains(" -i " + InputPathArgumentMarker, args, StringComparison.Ordinal);
        Assert.DoesNotContain("pipe:0", args, StringComparison.Ordinal);
    }

    [Fact]
    public void GetInputModifier_LiveStreamWithProvider_DropsEveryHttpProtocolOption()
    {
        // ffmpeg resolves -user_agent and -referer against the input's protocol, and pipe: has
        // none. Leaving them on makes it refuse the whole invocation:
        //   "Option user_agent not found. Error opening input file pipe:0."
        // and exit 8 - the same exit code as the 401 this branch is fixing, from a different cause.
        var state = BuildLiveState(withProvider: true);
        state.RemoteHttpHeaders["User-Agent"] = "Mozilla/5.0";
        state.RemoteHttpHeaders["Referer"] = "http://example.invalid/";

        var modifier = CreateHelper().GetInputModifier(state, new EncodingOptions(), null);

        Assert.DoesNotContain("-user_agent", modifier, StringComparison.Ordinal);
        Assert.DoesNotContain("-referer", modifier, StringComparison.Ordinal);
    }

    [Fact]
    public void GetInputModifier_NoProvider_KeepsTheHttpProtocolOptions()
    {
        var state = BuildLiveState(withProvider: false);
        state.RemoteHttpHeaders["User-Agent"] = "Mozilla/5.0";
        state.RemoteHttpHeaders["Referer"] = "http://example.invalid/";

        var modifier = CreateHelper().GetInputModifier(state, new EncodingOptions(), null);

        Assert.Contains("-user_agent", modifier, StringComparison.Ordinal);
        Assert.Contains("-referer", modifier, StringComparison.Ordinal);
    }

    private static StreamState BuildLiveState(bool withProvider)
    {
        var video = new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" };
        var audio = new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac" };

        var state = new StreamState(Mock.Of<IMediaSourceManager>(), TranscodingJobType.Hls, Mock.Of<ITranscodeManager>())
        {
            MediaSource = new MediaSourceInfo
            {
                Container = "ts",
                Path = LiveStreamUrl,
                Protocol = MediaProtocol.Http,
                IsInfiniteStream = true,
                MediaStreams = new List<MediaStream> { video, audio },
            },
            VideoStream = video,
            AudioStream = audio,
            Request = new VideoRequestDto { LiveStreamId = "d9e42c41f05e258d81c9ffb819efa158" },
            IsVideoRequest = true,
            IsInputVideo = true,
            MediaPath = LiveStreamUrl,
        };

        if (withProvider)
        {
            state.DirectStreamProvider = Mock.Of<IDirectStreamProvider>();
        }

        return state;
    }

    private static EncodingHelper CreateHelper()
    {
        var appPaths = Mock.Of<IApplicationPaths>();
        var mediaEncoder = new Mock<IMediaEncoder>();
        mediaEncoder
            .Setup(x => x.GetInputPathArgument(It.IsAny<EncodingJobInfo>()))
            .Returns(InputPathArgumentMarker);

        return new EncodingHelper(
            appPaths,
            mediaEncoder.Object,
            Mock.Of<ISubtitleEncoder>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<IConfigurationManager>(),
            Mock.Of<IPathManager>());
    }
}
