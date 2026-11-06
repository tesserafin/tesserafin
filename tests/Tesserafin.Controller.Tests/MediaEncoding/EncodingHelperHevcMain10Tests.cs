using System;
using System.Collections.Generic;
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

public class EncodingHelperHevcMain10Tests
{
    [Theory]
    [InlineData("main10", null, true, "libx265", "main10")]
    [InlineData("Main 10", 10, true, "libx265", "main10")]
    [InlineData("main10,main", 12, true, "libx265", "main10")]
    [InlineData("main10", 8, true, "libx265", "main")]
    [InlineData("main10", 10, false, "libx265", "main")]
    [InlineData(null, 10, true, "libx265", "main")]
    [InlineData("main", 10, true, "libx265", "main")]
    [InlineData("main,main10", 10, true, "libx265", "main")]
    [InlineData("mainstillpicture", 10, true, "libx265", "main")]
    [InlineData("main10", 10, true, "hevc_vaapi", "main")]
    [InlineData("main10", 10, true, "hevc_nvenc", "main")]
    [InlineData("main10", 10, true, "hevc_qsv", "main")]
    public void HevcProfileAndSoftwareFormat_RespectRequestAndRuntime(
        string? profile, int? maxDepth, bool probed, string encoder, string expected)
    {
        var helper = CreateHelper(probed);
        var state = State(profile, maxDepth);
        Assert.Equal(expected, helper.GetHevcOutputProfile(state, encoder));
        var quality = helper.GetVideoQualityParam(state, encoder, Options(), EncoderPreset.ultrafast);
        Assert.Contains(" -profile:v:0 " + expected, quality, StringComparison.Ordinal);

        if (encoder == "libx265")
        {
            var (filters, _, _) = helper.GetSwVidFilterChain(state, Options(), encoder);
            Assert.Contains("format=" + (expected == "main10" ? "yuv420p10le" : "yuv420p"), filters);
        }
    }

    [Fact]
    public void CodecQualifiedOptions_AreHonored()
    {
        var state = State(null, null);
        state.BaseRequest.StreamOptions["hevc-profile"] = "main10";
        state.BaseRequest.StreamOptions["hevc-videobitdepth"] = "10";
        Assert.Equal("main10", CreateHelper(true).GetHevcOutputProfile(state, "libx265"));
        state.BaseRequest.MaxVideoBitDepth = 8;
        Assert.Equal("main", CreateHelper(true).GetHevcOutputProfile(state, "libx265"));
    }

    [Fact]
    public void ExistingEncoderImplementation_WithoutOptionalCapability_RemainsMain()
    {
        var helper = CreateHelper(null);
        Assert.Equal("main", helper.GetHevcOutputProfile(State("main10", 10), "libx265"));
    }

    [Fact]
    public void GraphicalSubtitleOverlay_ExplicitlyKeepsTenBitSamples()
    {
        var state = State("main10", 10);
        state.SubtitleStream = new MediaStream { Index = 1, Type = MediaStreamType.Subtitle, Codec = "hdmv_pgs_subtitle", Width = 320, Height = 180 };
        state.SubtitleDeliveryMethod = SubtitleDeliveryMethod.Encode;
        var (_, _, overlays) = CreateHelper(true).GetSwVidFilterChain(state, Options(), "libx265");
        Assert.Contains("overlay=format=yuv420p10:eof_action=pass:repeatlast=0", overlays);
    }

    [Fact]
    public void DolbyVisionReshaping_DoesNotClaimTenBitPreservation()
    {
        var state = State("main10", 10);
        state.VideoStream.DvProfile = 5;
        state.VideoStream.CodecTag = "dvhe";
        Assert.Equal("main", CreateHelper(true).GetHevcOutputProfile(state, "libx265"));
    }

    internal static EncodingOptions Options() => new() { HardwareAccelerationType = HardwareAccelerationType.none, EncoderPreset = EncoderPreset.ultrafast };

    internal static EncodingJobInfo State(string? profile, int? maxDepth)
    {
        var video = new MediaStream
        {
            Index = 0,
            Type = MediaStreamType.Video,
            Codec = "hevc",
            Width = 320,
            Height = 180,
            BitDepth = 10,
            PixelFormat = "yuv420p10le",
            ColorTransfer = "bt709",
            ColorPrimaries = "bt709",
            ColorSpace = "bt709",
        };
        return new EncodingJobInfo(TranscodingJobType.Hls)
        {
            MediaSource = new MediaSourceInfo { Id = "test", Container = "mkv", MediaStreams = new List<MediaStream> { video }, VideoType = VideoType.VideoFile },
            VideoStream = video,
            VideoType = VideoType.VideoFile,
            IsVideoRequest = true,
            IsInputVideo = true,
            OutputVideoCodec = "hevc",
            OutputContainer = "mp4",
            OutputVideoBitrate = 1000000,
            BaseRequest = new VideoRequestDto { VideoCodec = "hevc", Profile = profile, MaxVideoBitDepth = maxDepth },
        };
    }

    internal static EncodingHelper CreateHelper(bool? probed)
    {
        var encoder = new Mock<IMediaEncoder>();
        if (probed.HasValue)
        {
            encoder.As<ISoftwareVideoEncoderCapabilities>().Setup(x => x.SupportsHevcMain10).Returns(probed.Value);
        }

        return new EncodingHelper(
            Mock.Of<IApplicationPaths>(),
            encoder.Object,
            Mock.Of<ISubtitleEncoder>(),
            Mock.Of<IConfiguration>(),
            Mock.Of<IConfigurationManager>(),
            Mock.Of<IPathManager>());
    }
}
