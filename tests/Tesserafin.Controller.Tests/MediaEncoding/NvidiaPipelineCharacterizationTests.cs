using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Entities;
using Xunit;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace Tesserafin.Controller.Tests.MediaEncoding;

public sealed class NvidiaPipelineCharacterizationTests
{
    public static IEnumerable<object[]> Cases()
        => Scenarios().Select(scenario => new object[] { scenario.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProductionFilterGraph_MatchesPreExtractionCharacterization(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsWindows(), "NVIDIA production routing is supported only on Linux and Windows");
        var scenario = Scenarios().Single(candidate => candidate.Name == name);
        var (helper, state, options, _) = Create(scenario);
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FixturePath()))!;
        Assert.Equal(expected[name], helper.GetVideoProcessingFilterParam(state, options, scenario.Encoder));
    }

    internal static string FixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "Tesserafin.Controller.Tests", "MediaEncoding", "NvidiaPipelineCharacterization.json");
    }

    internal static IEnumerable<Scenario> Scenarios()
    {
        foreach (var cuda in new[] { false, true })
        {
            foreach (var encoder in new[] { "h264_nvenc", "libx264", "mjpeg" })
            {
                foreach (var hdr in new[] { false, true })
                {
                    foreach (var subtitles in new[] { "none", "srt", "ass", "pgs" })
                    {
                        yield return new Scenario($"{(cuda ? "cuda" : "memory")}-{encoder}-{(hdr ? "hdr" : "sdr")}-{subtitles}", cuda, encoder, hdr, subtitles);
                    }
                }
            }
        }

        yield return new Scenario("cuda-rotation90", Rotation: 90);
        yield return new Scenario("cuda-rotationMinus90-pgs", Subtitles: "pgs", Rotation: -90);
        yield return new Scenario("cuda-rotation180-ass", Subtitles: "ass", Rotation: 180);
        yield return new Scenario("memory-rotation90-ass", Cuda: false, Subtitles: "ass", Rotation: 90);
        yield return new Scenario("cuda-no-transpose", Rotation: 90, MissingFilter: "transpose_cuda");
        yield return new Scenario("cuda-deinterlace-bwdif", Interlaced: true);
        yield return new Scenario("cuda-deinterlace-yadif", Interlaced: true, MissingFilter: "bwdif_cuda");
        yield return new Scenario("cuda-deinterlace-single-rate", Interlaced: true, FrameRate: 60);
        yield return new Scenario("memory-deinterlace", Cuda: false, Interlaced: true);
        yield return new Scenario("cuda-ass-no-alpha-option", Subtitles: "ass", AlphaOption: false);
        yield return new Scenario("cuda-ass-high-framerate", Subtitles: "ass", FrameRate: 120);
        yield return new Scenario("cuda-hdr-rext", Hdr: true, Rext: true);
        yield return new Scenario("cuda-hdr-old-version", Hdr: true, Version: "4.0");
        yield return new Scenario("cuda-hdr-no-tone", Hdr: true, ToneMapping: false);
        yield return new Scenario("cuda-copyback-no-alphasrc", Subtitles: "pgs", MissingFilter: "alphasrc");
        yield return new Scenario("cuda-copyback-no-yadif", MissingFilter: "yadif_cuda");
        yield return new Scenario("cuda-limited-dimensions", LimitedDimensions: true);
        yield return new Scenario("cuda-fps-after-deinterlace", Interlaced: true, RequestedFps: 15);
        yield return new Scenario("cuda-fps-before-filters", RequestedFps: 15);
    }

    internal static (EncodingHelper Helper, EncodingJobInfo State, EncodingOptions Options, Mock<IMediaEncoder> Encoder) Create(Scenario scenario)
    {
        var encoder = new Mock<IMediaEncoder>();
        encoder.Setup(x => x.EncoderVersion).Returns(new Version(scenario.Version));
        encoder.Setup(x => x.SupportsHwaccel(It.IsAny<string>())).Returns((string name) => name == "cuda");
        encoder.Setup(x => x.SupportsFilter(It.IsAny<string>())).Returns((string name) => name != scenario.MissingFilter);
        encoder.Setup(x => x.SupportsFilterWithOption(It.IsAny<FilterOptionType>())).Returns((FilterOptionType option) => option != FilterOptionType.OverlayCudaAlphaFormat || scenario.AlphaOption);
        encoder.Setup(x => x.EscapeSubtitleFilterPath(It.IsAny<string>())).Returns((string value) => value);
        var helper = new EncodingHelper(Mock.Of<IApplicationPaths>(), encoder.Object, Mock.Of<ISubtitleEncoder>(), Mock.Of<IConfiguration>(), Mock.Of<IConfigurationManager>(), Mock.Of<IPathManager>());
        var state = EncodingHelperHevcMain10Tests.State(null, null);
        state.VideoStream.BitDepth = scenario.Hdr ? 10 : 8;
        state.VideoStream.PixelFormat = scenario.Rext ? "yuv444p10le" : scenario.Hdr ? "yuv420p10le" : "yuv420p";
        state.VideoStream.ColorTransfer = scenario.Hdr ? "smpte2084" : "bt709";
        state.VideoStream.ColorPrimaries = scenario.Hdr ? "bt2020" : "bt709";
        state.VideoStream.ColorSpace = scenario.Hdr ? "bt2020nc" : "bt709";
        state.VideoStream.Profile = scenario.Rext ? "Rext" : "Main";
        state.VideoStream.Rotation = scenario.Rotation;
        state.VideoStream.IsInterlaced = scenario.Interlaced;
        state.VideoStream.RealFrameRate = scenario.FrameRate;
        state.BaseRequest.Width = scenario.LimitedDimensions ? null : 160;
        state.BaseRequest.Height = scenario.LimitedDimensions ? null : 90;
        state.BaseRequest.MaxWidth = scenario.LimitedDimensions ? 160 : null;
        state.BaseRequest.MaxHeight = scenario.LimitedDimensions ? 90 : null;
        state.BaseRequest.MaxFramerate = scenario.RequestedFps;
        state.BaseRequest.StartTimeTicks = TimeSpan.FromSeconds(3.5).Ticks;
        if (scenario.Subtitles != "none")
        {
            state.SubtitleStream = new MediaStream
            {
                Index = 1,
                Type = MediaStreamType.Subtitle,
                Codec = scenario.Subtitles == "pgs" ? "hdmv_pgs_subtitle" : scenario.Subtitles,
                IsExternal = true,
                Path = "/media/subtitle." + scenario.Subtitles,
                Width = 320,
                Height = 180,
            };
            state.SubtitleDeliveryMethod = SubtitleDeliveryMethod.Encode;
            state.MediaSource.MediaStreams = [state.VideoStream, state.SubtitleStream];
        }

        var options = new EncodingOptions
        {
            HardwareAccelerationType = HardwareAccelerationType.nvenc,
            HardwareDecodingCodecs = scenario.Cuda ? ["hevc"] : [],
            EnableDecodingColorDepth10Hevc = true,
            EnableDecodingColorDepth10HevcRext = true,
            EnableTonemapping = scenario.ToneMapping,
            DeinterlaceMethod = DeinterlaceMethod.bwdif,
            DeinterlaceDoubleRate = true,
            TonemappingDesat = 0.5,
            TonemappingPeak = 100.5,
            TonemappingParam = 0.25,
            TonemappingRange = TonemappingRange.tv,
        };
        return (helper, state, options, encoder);
    }

    internal sealed record Scenario(
        string Name,
        bool Cuda = true,
        string Encoder = "h264_nvenc",
        bool Hdr = false,
        string Subtitles = "none",
        int Rotation = 0,
        bool Interlaced = false,
        float FrameRate = 24,
        string? MissingFilter = null,
        bool AlphaOption = true,
        bool Rext = false,
        string Version = "7.1.1",
        bool ToneMapping = true,
        bool LimitedDimensions = false,
        float? RequestedFps = null);
}
