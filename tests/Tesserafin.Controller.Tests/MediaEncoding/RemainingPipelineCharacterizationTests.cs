using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.MediaEncoding.Amf;
using Tesserafin.Controller.MediaEncoding.VideoToolbox;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

public sealed class RemainingPipelineCharacterizationTests
{
    public static IEnumerable<object[]> Cases() => Scenarios().Select(value => new object[] { value.Name });

    public static IEnumerable<object[]> LinuxCases() => Scenarios().Where(value => value.Backend == "Rkmpp").Select(value => new object[] { value.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public void SelectedBackendGraph_MatchesPreExtractionReference(string name)
    {
        var scenario = Scenarios().Single(value => value.Name == name);
        var (helper, state, options, _) = Create(scenario);
        var expected = ReadFixture()[name];
        var actual = Render(helper, state, options, scenario);
        Assert.Equal(expected.Main, actual.Main);
        Assert.Equal(expected.Subtitles, actual.Subtitles);
        Assert.Equal(expected.Overlay, actual.Overlay);
    }

    [Theory]
    [MemberData(nameof(LinuxCases))]
    public void NativeLinuxProductionGraph_MatchesPreExtractionReference(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "RKMPP production routing requires Linux");
        var scenario = Scenarios().Single(value => value.Name == name);
        var (helper, state, options, _) = Create(scenario);
        Assert.Equal(ReadFixture()[name].LinuxProduction, helper.GetVideoProcessingFilterParam(state, options, scenario.Encoder));
    }

    internal static Graph Render(EncodingHelper helper, EncodingJobInfo state, EncodingOptions options, Scenario scenario)
    {
        switch (scenario.Backend)
        {
            case "Amf":
            {
                var graph = AmfPipelineRenderer.Render(helper.CaptureAmfHardwarePipeline(state, options, Decoder(scenario), scenario.Encoder));
                return Snapshot(graph.MainFilters, graph.SubFilters, graph.OverlayFilters);
            }

            case "VideoToolbox":
            {
                var graph = VideoToolboxPipelineRenderer.Render(helper.CaptureVideoToolboxHardwarePipeline(state, options, Decoder(scenario), scenario.Encoder));
                return Snapshot(graph.MainFilters, graph.SubFilters, graph.OverlayFilters);
            }

            default:
            {
                var graph = helper.GetRkmppVidFiltersPrefered(state, options, Decoder(scenario), scenario.Encoder);
                return Snapshot(graph.MainFilters, graph.SubFilters, graph.OverlayFilters);
            }
        }
    }

    internal static Graph Snapshot(IEnumerable<string> main, IEnumerable<string> subtitles, IEnumerable<string> overlay)
        => new(main.ToArray(), subtitles.ToArray(), overlay.ToArray(), null);

    internal static string Decoder(Scenario scenario) => !scenario.Hardware ? string.Empty : scenario.Backend switch
    {
        "Amf" => " -hwaccel d3d11va",
        "VideoToolbox" => " -hwaccel videotoolbox",
        _ => " -hwaccel rkmpp",
    };

    internal static Dictionary<string, Graph> ReadFixture() => JsonSerializer.Deserialize<Dictionary<string, Graph>>(File.ReadAllText(FixturePath()))!;

    private static string FixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "Tesserafin.Controller.Tests", "MediaEncoding", "RemainingPipelineCharacterization.json");
    }

    internal static IEnumerable<Scenario> Scenarios()
    {
        foreach (var backend in new[] { "Amf", "VideoToolbox", "Rkmpp" })
        {
            var encoders = backend switch
            {
                "Amf" => new[] { "h264_amf", "hevc_amf", "libx264", "mjpeg" },
                "VideoToolbox" => new[] { "h264_videotoolbox", "hevc_videotoolbox", "libx264", "mjpeg" },
                _ => new[] { "h264_rkmpp", "hevc_rkmpp", "av1_rkmpp", "mjpeg_rkmpp", "libx264", "mjpeg" },
            };
            foreach (var hardware in new[] { false, true })
            {
                foreach (var encoder in encoders)
                {
                    foreach (var hdr in new[] { false, true })
                    {
                        foreach (var subtitles in new[] { "none", "srt", "ass", "pgs" })
                        {
                            yield return new Scenario($"{backend}-{hardware}-{encoder}-{hdr}-{subtitles}", backend, hardware, encoder, hdr, subtitles);
                        }
                    }
                }
            }

            var primary = encoders[0];
            yield return new Scenario(backend + "-rotate90", backend, Encoder: primary, Rotation: 90);
            yield return new Scenario(backend + "-rotateMinus90-pgs", backend, Encoder: primary, Rotation: -90, Subtitles: "pgs");
            yield return new Scenario(backend + "-rotate180-ass", backend, Encoder: primary, Rotation: 180, Subtitles: "ass");
            yield return new Scenario(backend + "-deinterlace", backend, Encoder: primary, Interlaced: true);
            yield return new Scenario(backend + "-memory-deinterlace", backend, Hardware: false, Encoder: primary, Interlaced: true);
            yield return new Scenario(backend + "-ass120fps", backend, Encoder: primary, Subtitles: "ass", FrameRate: 120);
            yield return new Scenario(backend + "-large-pgs", backend, Encoder: primary, Subtitles: "pgs", Large: true);
            yield return new Scenario(backend + "-large-ass", backend, Encoder: primary, Subtitles: "ass", Large: true);
            yield return new Scenario(backend + "-no-resize", backend, Encoder: primary, NoResize: true);
            yield return new Scenario(backend + "-no-resize-ass", backend, Encoder: primary, NoResize: true, Subtitles: "ass");
            yield return new Scenario(backend + "-no-resize-hdr", backend, Encoder: primary, NoResize: true, Hdr: true);
            yield return new Scenario(backend + "-fps-after-deint", backend, Encoder: primary, Interlaced: true, Fps: 15);
            yield return new Scenario(backend + "-no-alphasrc", backend, Encoder: primary, MissingFilter: "alphasrc");
            yield return new Scenario(backend + "-no-opencl", backend, Encoder: primary, MissingFilter: "scale_opencl");
        }

        yield return new Scenario("Amf-no-alpha-option", "Amf", Encoder: "h264_amf", Subtitles: "ass", AlphaOption: false);
        yield return new Scenario("Amf-no-transpose-option", "Amf", Encoder: "h264_amf", Rotation: 90, TransposeOption: false);
        yield return new Scenario("VideoToolbox-metal", "VideoToolbox", Encoder: "h264_videotoolbox", Hdr: true, VtToneMapping: false);
        yield return new Scenario("VideoToolbox-metal-format", "VideoToolbox", Encoder: "h264_videotoolbox", Hdr: true, VtToneMapping: false, PixelFormat: "p010le");
        yield return new Scenario("VideoToolbox-metal-no-scale", "VideoToolbox", Encoder: "h264_videotoolbox", Hdr: true, VtToneMapping: false, NoResize: true);
        yield return new Scenario("VideoToolbox-old-surface", "VideoToolbox", Encoder: "h264_videotoolbox", Version: "6.0");
        yield return new Scenario("VideoToolbox-no-transpose", "VideoToolbox", Encoder: "h264_videotoolbox", Rotation: 90, MissingFilter: "transpose_vt");
        yield return new Scenario("VideoToolbox-no-bwdif", "VideoToolbox", Encoder: "h264_videotoolbox", Interlaced: true, MissingFilter: "bwdif_videotoolbox");
        yield return new Scenario("VideoToolbox-dovi", "VideoToolbox", Encoder: "h264_videotoolbox", Hdr: true, Dovi: true);
        yield return new Scenario("Rkmpp-heavy-scale", "Rkmpp", HeavyScale: true);
        yield return new Scenario("Rkmpp-heavy-hdr", "Rkmpp", HeavyScale: true, Hdr: true);
        yield return new Scenario("Rkmpp-heavy-mjpeg", "Rkmpp", Encoder: "mjpeg_rkmpp", HeavyScale: true);
        yield return new Scenario("Rkmpp-afbc-overlay-no-scale", "Rkmpp", Subtitles: "pgs", NoResize: true);
        yield return new Scenario("Rkmpp-av1-overlay-no-scale", "Rkmpp", Encoder: "av1_rkmpp", Subtitles: "pgs", NoResize: true);
        yield return new Scenario("Rkmpp-ratio-decoder-refused", "Rkmpp", ExtremeScale: true);
    }

    internal static (EncodingHelper Helper, EncodingJobInfo State, EncodingOptions Options, Mock<IMediaEncoder> Encoder) Create(Scenario scenario)
    {
        var source = new VaapiPipelineCharacterizationTests.Scenario(
            scenario.Name,
            Hardware: scenario.Hardware,
            Encoder: scenario.Encoder,
            Hdr: scenario.Hdr,
            Subtitles: scenario.Subtitles,
            Rotation: scenario.Rotation,
            Interlaced: scenario.Interlaced,
            FrameRate: scenario.FrameRate,
            MissingFilter: scenario.MissingFilter,
            Version: scenario.Version,
            Large: scenario.Large);
        var (helper, state, options, encoder) = VaapiPipelineCharacterizationTests.Create(source);
        options.HardwareAccelerationType = scenario.Backend switch
        {
            "Amf" => HardwareAccelerationType.amf,
            "VideoToolbox" => HardwareAccelerationType.videotoolbox,
            _ => HardwareAccelerationType.rkmpp,
        };
        options.EnableVideoToolboxTonemapping = scenario.VtToneMapping;
        encoder.Setup(value => value.SupportsHwaccel(It.IsAny<string>())).Returns(true);
        encoder.Setup(value => value.SupportsDecoder(It.IsAny<string>())).Returns(true);
        encoder.Setup(value => value.SupportsFilterWithOption(It.IsAny<FilterOptionType>())).Returns((FilterOptionType option) => option switch
        {
            FilterOptionType.OverlayOpenclAlphaFormat => scenario.AlphaOption,
            FilterOptionType.TransposeOpenclReversal => scenario.TransposeOption,
            _ => true,
        });
        state.BaseRequest.MaxFramerate = scenario.Fps;
        if (scenario.NoResize)
        {
            state.BaseRequest.Width = null;
            state.BaseRequest.Height = null;
            state.BaseRequest.MaxWidth = null;
            state.BaseRequest.MaxHeight = null;
        }

        if (scenario.HeavyScale || scenario.ExtremeScale)
        {
            state.VideoStream.Width = 3840;
            state.VideoStream.Height = 2160;
            state.BaseRequest.Width = scenario.ExtremeScale ? 160 : 320;
            state.BaseRequest.Height = scenario.ExtremeScale ? 90 : 180;
        }

        if (scenario.PixelFormat is not null)
        {
            state.VideoStream.PixelFormat = scenario.PixelFormat;
        }

        if (scenario.Dovi)
        {
            state.VideoStream.DvProfile = 5;
            state.VideoStream.CodecTag = "dvhe";
        }

        return (helper, state, options, encoder);
    }

    internal sealed record Scenario(
        string Name,
        string Backend,
        bool Hardware = true,
        string Encoder = "h264_rkmpp",
        bool Hdr = false,
        string Subtitles = "none",
        int Rotation = 0,
        bool Interlaced = false,
        float FrameRate = 24,
        string? MissingFilter = null,
        string Version = "7.1.1",
        bool Large = false,
        bool NoResize = false,
        bool VtToneMapping = true,
        bool AlphaOption = true,
        bool TransposeOption = true,
        string? PixelFormat = null,
        bool Dovi = false,
        bool HeavyScale = false,
        bool ExtremeScale = false,
        float? Fps = null);

    internal sealed record Graph(string[] Main, string[] Subtitles, string[] Overlay, string? LinuxProduction);
}
