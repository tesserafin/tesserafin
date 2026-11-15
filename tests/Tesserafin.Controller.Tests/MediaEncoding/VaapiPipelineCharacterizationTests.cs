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

public sealed class VaapiPipelineCharacterizationTests
{
    public static IEnumerable<object[]> Cases()
        => Scenarios().Select(scenario => new object[] { scenario.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProductionFilterGraph_MatchesPreExtractionCharacterization(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "VAAPI production routing requires Linux");
        var scenario = Scenarios().Single(candidate => candidate.Name == name);
        Assert.SkipUnless(scenario.Driver != "amd" || Environment.OSVersion.Version >= new Version(5, 15), "AMD Vulkan production routing requires kernel 5.15 or newer");
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
        return Path.Combine(directory.FullName, "tests", "Tesserafin.Controller.Tests", "MediaEncoding", "VaapiPipelineCharacterization.json");
    }

    internal static IEnumerable<Scenario> Scenarios()
    {
        foreach (var driver in new[] { "intel", "amd", "i965", "generic" })
        {
            foreach (var hardware in new[] { false, true })
            {
                foreach (var encoder in new[] { "h264_vaapi", "libx264", "mjpeg", "mjpeg_vaapi" })
                {
                    foreach (var hdr in new[] { false, true })
                    {
                        foreach (var subtitles in new[] { "none", "srt", "ass", "pgs" })
                        {
                            yield return new Scenario($"{driver}-{(hardware ? "vaapi" : "memory")}-{encoder}-{(hdr ? "hdr" : "sdr")}-{subtitles}", driver, hardware, encoder, hdr, subtitles);
                        }
                    }
                }
            }

            yield return new Scenario(driver + "-rotation90", driver, Rotation: 90);
            yield return new Scenario(driver + "-rotationMinus90-pgs", driver, Subtitles: "pgs", Rotation: -90);
            yield return new Scenario(driver + "-rotation180-ass", driver, Subtitles: "ass", Rotation: 180);
            yield return new Scenario(driver + "-memory-rotation90-ass", driver, Hardware: false, Subtitles: "ass", Rotation: 90);
            yield return new Scenario(driver + "-deinterlace", driver, Interlaced: true);
            yield return new Scenario(driver + "-deinterlace-single-rate", driver, Interlaced: true, FrameRate: 60);
            yield return new Scenario(driver + "-ass-high-framerate", driver, Subtitles: "ass", FrameRate: 120);
            yield return new Scenario(driver + "-hdr-rext", driver, Hdr: true, Rext: true);
            yield return new Scenario(driver + "-hdr-opencl", driver, Hdr: true, Vpp: false);
            yield return new Scenario(driver + "-hdr-no-tone", driver, Hdr: true, ToneMapping: false);
            yield return new Scenario(driver + "-limited-dimensions", driver, LimitedDimensions: true);
            yield return new Scenario(driver + "-fps-after-deinterlace", driver, Interlaced: true, RequestedFps: 15);
            yield return new Scenario(driver + "-fps-before-filters", driver, RequestedFps: 15);
            yield return new Scenario(driver + "-copyback-no-alphasrc", driver, Subtitles: "pgs", MissingFilter: "alphasrc");
            yield return new Scenario(driver + "-copyback-no-opencl", driver, MissingFilter: "scale_opencl");
        }

        yield return new Scenario("amd-old-interop", "amd", Hdr: true, Version: "6.0");
        yield return new Scenario("amd-no-modifier", "amd", Hdr: true, Modifier: false);
        yield return new Scenario("amd-no-interop", "amd", Hdr: true, Interop: false);
        yield return new Scenario("amd-rotation-no-modifier", "amd", Rotation: 90, Modifier: false);
        yield return new Scenario("amd-deinterlace-hdr", "amd", Hdr: true, Interlaced: true);
        yield return new Scenario("amd-deinterlace-ass", "amd", Subtitles: "ass", Interlaced: true);
        yield return new Scenario("amd-software-output-ass", "amd", Encoder: "libx264", Subtitles: "ass", Interlaced: true);
        yield return new Scenario("intel-large-graphical-overlay", Subtitles: "pgs", Large: true);
        yield return new Scenario("intel-large-text-overlay", Subtitles: "ass", Large: true);
        yield return new Scenario("intel-vpp-procamp", Hdr: true, Procamp: true);
        yield return new Scenario("intel-opencl-hdr-ass", Hdr: true, Subtitles: "ass", Vpp: false);
        yield return new Scenario("intel-opencl-old-mode", Hdr: true, Vpp: false, Version: "4.0");
    }

    internal static (EncodingHelper Helper, EncodingJobInfo State, EncodingOptions Options, Mock<IMediaEncoder> Encoder) Create(Scenario scenario)
    {
        var encoder = new Mock<IMediaEncoder>();
        encoder.Setup(x => x.EncoderVersion).Returns(new Version(scenario.Version));
        encoder.Setup(x => x.SupportsHwaccel(It.IsAny<string>())).Returns((string name) => name is "vaapi" or "drm" or "opencl" or "vulkan");
        encoder.Setup(x => x.SupportsFilter(It.IsAny<string>())).Returns((string name) => name != scenario.MissingFilter);
        encoder.Setup(x => x.SupportsFilterWithOption(It.IsAny<FilterOptionType>())).Returns(true);
        encoder.Setup(x => x.IsVaapiDeviceInteliHD).Returns(scenario.Driver == "intel");
        encoder.Setup(x => x.IsVaapiDeviceInteli965).Returns(scenario.Driver == "i965");
        encoder.Setup(x => x.IsVaapiDeviceAmd).Returns(scenario.Driver == "amd");
        encoder.Setup(x => x.IsVaapiDeviceSupportVulkanDrmInterop).Returns(scenario.Interop);
        encoder.Setup(x => x.IsVaapiDeviceSupportVulkanDrmModifier).Returns(scenario.Modifier);
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
        state.VideoStream.Width = scenario.Large ? 3840 : 320;
        state.VideoStream.Height = scenario.Large ? 2160 : 180;
        state.BaseRequest.Width = scenario.Large || scenario.LimitedDimensions ? null : 160;
        state.BaseRequest.Height = scenario.Large || scenario.LimitedDimensions ? null : 90;
        state.BaseRequest.MaxWidth = scenario.LimitedDimensions ? 160 : null;
        state.BaseRequest.MaxHeight = scenario.Large ? 1440 : scenario.LimitedDimensions ? 90 : null;
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
            HardwareAccelerationType = HardwareAccelerationType.vaapi,
            HardwareDecodingCodecs = scenario.Hardware ? ["hevc"] : [],
            EnableDecodingColorDepth10Hevc = true,
            EnableDecodingColorDepth10HevcRext = true,
            EnableTonemapping = scenario.ToneMapping,
            EnableVppTonemapping = scenario.Vpp,
            VppTonemappingBrightness = scenario.Procamp ? 10 : 0,
            VppTonemappingContrast = scenario.Procamp ? 2 : 1,
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
        string Driver = "intel",
        bool Hardware = true,
        string Encoder = "h264_vaapi",
        bool Hdr = false,
        string Subtitles = "none",
        int Rotation = 0,
        bool Interlaced = false,
        float FrameRate = 24,
        string? MissingFilter = null,
        bool Rext = false,
        string Version = "7.1.1",
        bool ToneMapping = true,
        bool Vpp = true,
        bool Modifier = true,
        bool Interop = true,
        bool LimitedDimensions = false,
        float? RequestedFps = null,
        bool Large = false,
        bool Procamp = false);
}
