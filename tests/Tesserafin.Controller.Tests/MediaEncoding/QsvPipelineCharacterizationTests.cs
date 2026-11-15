using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.MediaEncoding.Qsv;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

public sealed class QsvPipelineCharacterizationTests
{
    public static IEnumerable<object[]> Cases() => Scenarios().Select(value => new object[] { value.Name });

    public static IEnumerable<object[]> LinuxCases() => Scenarios().Where(value => value.Vaapi).Select(value => new object[] { value.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public void SelectedBackendGraph_MatchesPreExtractionCharacterization(string name)
    {
        var scenario = Scenarios().Single(value => value.Name == name);
        var (helper, state, options, _) = Create(scenario);
        var expected = ReadFixture()[name];
        var filters = QsvPipelineRenderer.Render(helper.CaptureQsvHardwarePipeline(state, options, Decoder(scenario), scenario.Encoder, scenario.Vaapi));
        Assert.Equal(expected.Main, filters.MainFilters);
        Assert.Equal(expected.Subtitles, filters.SubFilters);
        Assert.Equal(expected.Overlay, filters.OverlayFilters);
    }

    [Theory]
    [MemberData(nameof(LinuxCases))]
    public void NativeLinuxProductionGraph_MatchesPreExtractionCharacterization(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The production fixture was captured on Linux");
        var scenario = Scenarios().Single(value => value.Name == name);
        var (helper, state, options, _) = Create(scenario);
        Assert.Equal(ReadFixture()[name].LinuxProduction, helper.GetVideoProcessingFilterParam(state, options, scenario.Encoder));
    }

    internal static Dictionary<string, Graph> ReadFixture()
        => JsonSerializer.Deserialize<Dictionary<string, Graph>>(File.ReadAllText(FixturePath()))!;

    internal static string FixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "tests", "Tesserafin.Controller.Tests", "MediaEncoding", "QsvPipelineCharacterization.json");
    }

    internal static string Decoder(Scenario scenario) => scenario.Decoder switch
    {
        "memory" => string.Empty,
        "qsv" => " -hwaccel qsv",
        _ => scenario.Vaapi ? " -hwaccel vaapi" : " -hwaccel d3d11va",
    };

    internal static IEnumerable<Scenario> Scenarios()
    {
        foreach (var vaapi in new[] { true, false })
        {
            var backend = vaapi ? "vaapi" : "d3d11";
            foreach (var decoder in new[] { "memory", "native", "qsv" })
            {
                foreach (var encoder in new[] { "h264_qsv", "libx264", "mjpeg", "mjpeg_qsv" })
                {
                    foreach (var hdr in new[] { false, true })
                    {
                        foreach (var subtitles in new[] { "none", "srt", "ass", "pgs" })
                        {
                            yield return new Scenario($"{backend}-{decoder}-{encoder}-{(hdr ? "hdr" : "sdr")}-{subtitles}", vaapi, decoder, encoder, hdr, subtitles);
                        }
                    }
                }
            }

            yield return new Scenario(backend + "-rotation90", vaapi, Rotation: 90);
            yield return new Scenario(backend + "-qsv-rotationMinus90", vaapi, "qsv", Rotation: -90);
            yield return new Scenario(backend + "-rotation180-ass", vaapi, Subtitles: "ass", Rotation: 180);
            yield return new Scenario(backend + "-deinterlace", vaapi, Interlaced: true);
            yield return new Scenario(backend + "-deinterlace-60fps", vaapi, Interlaced: true, FrameRate: 60);
            yield return new Scenario(backend + "-hdr-opencl", vaapi, Hdr: true, Vpp: false);
            yield return new Scenario(backend + "-hdr-rext", vaapi, Hdr: true, Rext: true);
            yield return new Scenario(backend + "-procamp-brightness", vaapi, Hdr: true, Brightness: 10.5);
            yield return new Scenario(backend + "-procamp-contrast", vaapi, Hdr: true, Contrast: 2.5);
            yield return new Scenario(backend + "-procamp-both", vaapi, Hdr: true, Brightness: -10.5, Contrast: 2.5);
            yield return new Scenario(backend + "-procamp-outside-range", vaapi, Hdr: true, Brightness: 101, Contrast: 11);
            yield return new Scenario(backend + "-old-range-scale-options", vaapi, Encoder: "mjpeg_qsv", Vpp: false, Version: "5.1.3");
            yield return new Scenario(backend + "-fps", vaapi, Interlaced: true, RequestedFps: 15);
            yield return new Scenario(backend + "-large-text-overlay", vaapi, Subtitles: "ass", Large: true);
            yield return new Scenario(backend + "-large-graphical-overlay", vaapi, Subtitles: "pgs", Large: true);
            yield return new Scenario(backend + "-copyback-no-alpha", vaapi, MissingFilter: "alphasrc", Subtitles: "pgs");
            yield return new Scenario(backend + "-copyback-no-opencl", vaapi, MissingFilter: "scale_opencl");
            yield return new Scenario(backend + "-no-resize", vaapi, NoResize: true);
            yield return new Scenario(backend + "-no-resize-opencl", vaapi, Hdr: true, Vpp: false, NoResize: true);
            yield return new Scenario(backend + "-no-resize-procamp", vaapi, Hdr: true, Brightness: 10.5, NoResize: true);
            yield return new Scenario(backend + "-ass-high-framerate", vaapi, Subtitles: "ass", FrameRate: 120);
        }
    }

    internal static (EncodingHelper Helper, EncodingJobInfo State, EncodingOptions Options, Mock<IMediaEncoder> Encoder) Create(Scenario scenario)
    {
        var source = new VaapiPipelineCharacterizationTests.Scenario(
            scenario.Name,
            Hardware: scenario.Decoder != "memory",
            Encoder: scenario.Encoder,
            Hdr: scenario.Hdr,
            Subtitles: scenario.Subtitles,
            Rotation: scenario.Rotation,
            Interlaced: scenario.Interlaced,
            FrameRate: scenario.FrameRate,
            MissingFilter: scenario.MissingFilter,
            Rext: scenario.Rext,
            Version: scenario.Version,
            Vpp: scenario.Vpp,
            RequestedFps: scenario.RequestedFps,
            Large: scenario.Large);
        var (helper, state, options, encoder) = VaapiPipelineCharacterizationTests.Create(source);
        options.HardwareAccelerationType = HardwareAccelerationType.qsv;
        options.PreferSystemNativeHwDecoder = scenario.Decoder == "native";
        options.VppTonemappingBrightness = scenario.Brightness;
        options.VppTonemappingContrast = scenario.Contrast;
        encoder.Setup(value => value.SupportsHwaccel(It.IsAny<string>())).Returns(true);
        encoder.Setup(value => value.SupportsDecoder(It.IsAny<string>())).Returns(true);
        if (scenario.NoResize)
        {
            state.BaseRequest.Width = null;
            state.BaseRequest.Height = null;
            state.BaseRequest.MaxWidth = null;
            state.BaseRequest.MaxHeight = null;
        }

        return (helper, state, options, encoder);
    }

    internal sealed record Scenario(
        string Name,
        bool Vaapi = true,
        string Decoder = "native",
        string Encoder = "h264_qsv",
        bool Hdr = false,
        string Subtitles = "none",
        int Rotation = 0,
        bool Interlaced = false,
        float FrameRate = 24,
        bool Vpp = true,
        bool Rext = false,
        string Version = "7.1.1",
        double Brightness = 0,
        double Contrast = 1,
        string? MissingFilter = null,
        float? RequestedFps = null,
        bool Large = false,
        bool NoResize = false);

    internal sealed record Graph(string[] Main, string[] Subtitles, string[] Overlay, string? LinuxProduction);
}
