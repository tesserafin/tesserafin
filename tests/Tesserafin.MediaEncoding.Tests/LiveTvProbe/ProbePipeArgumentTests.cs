using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.LiveTvProbe;

/// <summary>
/// The arguments a piped probe is built with (#153-LTV-R1, LTV-R0 finding 1).
/// </summary>
/// <remarks>
/// #153-LTV-S0 hit the matching trap on the transcode side: ffmpeg resolves protocol options
/// against the INPUT's protocol, and <c>pipe:</c> has none, so a surviving <c>-user_agent</c> made
/// it refuse the whole invocation with "Option user_agent not found. Error opening input file
/// pipe:0." and exit 8 — the same exit code as the 401 it was meant to replace, from a different
/// cause. A piped probe must carry no HTTP option at all.
/// </remarks>
public sealed class ProbePipeArgumentTests
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

    [Fact]
    public void APipedProbe_CarriesNoHttpProtocolOption()
    {
        var arguments = Encoder().GetExtraArguments(Request(new FakeDirectStreamProvider()));

        Assert.DoesNotContain("-user_agent", arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control. Without a reader the request is an ordinary HTTP probe and the option is still
    /// forwarded, so the assertion above is about the pipe branch and not about the option having
    /// been dropped everywhere.
    /// </summary>
    [Fact]
    public void AnOrdinaryHttpProbe_StillForwardsIt()
    {
        var arguments = Encoder().GetExtraArguments(Request(null));

        Assert.Contains($"-user_agent \"{UserAgent}\"", arguments, StringComparison.Ordinal);
    }

    /// <summary>
    /// An RTSP transport option is protocol-specific in the same way and goes the same way.
    /// </summary>
    [Fact]
    public void APipedProbe_CarriesNoRtspTransportOption()
    {
        var request = Request(new FakeDirectStreamProvider());
        request.MediaSource.Protocol = MediaProtocol.Rtsp;

        Assert.DoesNotContain("-rtsp_transport", Encoder().GetExtraArguments(request), StringComparison.Ordinal);
    }

    private static MediaEncoder Encoder() => new(
        Mock.Of<ILogger<MediaEncoder>>(),
        Mock.Of<IServerConfigurationManager>(),
        Mock.Of<IFileSystem>(),
        Mock.Of<IBlurayExaminer>(),
        Mock.Of<ILocalizationManager>(),
        new ConfigurationBuilder().Build(),
        Mock.Of<IServerConfigurationManager>(),
        new FfmpegProcessRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<Tesserafin.MediaEncoding.Encoder.FfmpegProcessRunner>.Instance));

    private static MediaInfoRequest Request(IDirectStreamProvider? reader) => new()
    {
        MediaSource = new MediaSourceInfo
        {
            Path = "http://127.0.0.1:8096/LiveTv/LiveStreamFiles/abc/stream.ts",
            Protocol = MediaProtocol.Http,
            RequiredHttpHeaders = new Dictionary<string, string> { { "User-Agent", UserAgent } }
        },
        ExtractChapters = false,
        MediaType = DlnaProfileType.Video,
        DirectStreamReader = reader
    };

    private sealed class FakeDirectStreamProvider : IDirectStreamProvider
    {
        public Stream GetStream() => new MemoryStream();
    }
}
