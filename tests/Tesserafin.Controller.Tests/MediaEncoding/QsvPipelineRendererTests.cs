using System;
using System.Globalization;
using System.Linq;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.MediaEncoding.Qsv;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

public sealed class QsvPipelineRendererTests
{
    [Theory]
    [InlineData(true, "native", "ass")]
    [InlineData(true, "native", "pgs")]
    [InlineData(true, "qsv", "ass")]
    [InlineData(true, "qsv", "pgs")]
    [InlineData(true, "memory", "ass")]
    [InlineData(true, "memory", "pgs")]
    [InlineData(false, "native", "ass")]
    [InlineData(false, "native", "pgs")]
    [InlineData(false, "qsv", "ass")]
    [InlineData(false, "qsv", "pgs")]
    [InlineData(false, "memory", "ass")]
    [InlineData(false, "memory", "pgs")]
    public void CapturedPipeline_RemainsIndependentOfMutatedJobsOptionsAndCapabilities(bool vaapi, string decoder, string subtitles)
    {
        var scenario = new QsvPipelineCharacterizationTests.Scenario("snapshot", vaapi, decoder, Hdr: true, Subtitles: subtitles, Rotation: 90, Interlaced: true, Brightness: 10.5, Contrast: 2.5);
        var (helper, state, options, encoder) = QsvPipelineCharacterizationTests.Create(scenario);
        var plan = helper.CaptureQsvHardwarePipeline(state, options, QsvPipelineCharacterizationTests.Decoder(scenario), scenario.Encoder, vaapi);
        var expected = QsvPipelineRenderer.Render(plan);
        state.BaseRequest.Width = 12;
        state.BaseRequest.Height = 8;
        state.BaseRequest.StartTimeTicks = 0;
        state.VideoStream.Rotation = 0;
        state.VideoStream.IsInterlaced = false;
        state.VideoStream.ColorTransfer = "bt709";
        state.SubtitleStream = null;
        options.HardwareAccelerationType = HardwareAccelerationType.none;
        options.EnableVppTonemapping = false;
        options.EnableTonemapping = false;
        options.VppTonemappingBrightness = -20;
        options.VppTonemappingContrast = 4;
        encoder.Invocations.Clear();
        encoder.Setup(value => value.SupportsFilter(It.IsAny<string>())).Throws(new InvalidOperationException("capability re-read"));
        encoder.Setup(value => value.EncoderVersion).Throws(new InvalidOperationException("version re-read"));

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-BE");
            var actual = QsvPipelineRenderer.Render(plan);
            Assert.Equal(expected.MainFilters, actual.MainFilters);
            Assert.Equal(expected.SubFilters, actual.SubFilters);
            Assert.Equal(expected.OverlayFilters, actual.OverlayFilters);
            encoder.VerifyNoOtherCalls();
            if (!vaapi && decoder != "memory")
            {
                Assert.Contains(actual.MainFilters, filter => filter.Contains(":brightness=10.5:contrast=2.5:procamp=1", StringComparison.Ordinal));
                Assert.Contains("vpp_qsv=tonemap=1:format=nv12:async_depth=2", actual.MainFilters);
            }

            var main = actual.MainFilters.ToList();
            var subs = actual.SubFilters.ToList();
            var overlays = actual.OverlayFilters.ToList();
            main.Clear();
            subs.Clear();
            overlays.Clear();
            var repeated = QsvPipelineRenderer.Render(plan);
            Assert.Equal(expected.MainFilters, repeated.MainFilters);
            Assert.Equal(expected.SubFilters, repeated.SubFilters);
            Assert.Equal(expected.OverlayFilters, repeated.OverlayFilters);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("alphasrc")]
    [InlineData("scale_opencl")]
    public void MissingRequiredCapability_SelectsTheSharedSoftwarePipeline(string missing)
    {
        var scenario = new QsvPipelineCharacterizationTests.Scenario("software", MissingFilter: missing);
        var (helper, state, options, _) = QsvPipelineCharacterizationTests.Create(scenario);
        Assert.IsType<QsvPipelinePlan.Software>(helper.CaptureQsvPipeline(state, options, scenario.Encoder));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("qsv")]
    [InlineData("memory")]
    public void NativeRoute_CapturesTheSupportedPlatformBeforeRendering(string decoder)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsWindows(), "Native QSV routing requires Linux or Windows");
        var scenario = new QsvPipelineCharacterizationTests.Scenario("native", Decoder: decoder);
        var (helper, state, options, _) = QsvPipelineCharacterizationTests.Create(scenario);
        var plan = helper.CaptureQsvPipeline(state, options, scenario.Encoder);
        if (OperatingSystem.IsLinux())
        {
            Assert.IsType<QsvPipelinePlan.Vaapi>(plan);
        }
        else
        {
            Assert.IsType<QsvPipelinePlan.D3d11>(plan);
        }
    }
}
