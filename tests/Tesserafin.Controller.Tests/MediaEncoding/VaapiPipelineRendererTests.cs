using System;
using System.Globalization;
using System.Linq;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.MediaEncoding.Vaapi;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

public sealed class VaapiPipelineRendererTests
{
    [Theory]
    [InlineData("intel", "none")]
    [InlineData("intel", "ass")]
    [InlineData("intel", "pgs")]
    [InlineData("amd", "none")]
    [InlineData("amd", "ass")]
    [InlineData("amd", "pgs")]
    [InlineData("i965", "none")]
    [InlineData("i965", "ass")]
    [InlineData("i965", "pgs")]
    [InlineData("generic", "none")]
    [InlineData("generic", "ass")]
    [InlineData("generic", "pgs")]
    public void CapturedPipeline_DoesNotReadLaterJobConfigurationOrDeviceCapabilities(string driver, string subtitles)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "VAAPI capture requires Linux");
        var scenario = new VaapiPipelineCharacterizationTests.Scenario("snapshot", driver, Hdr: true, Subtitles: subtitles, Rotation: 90, Interlaced: true);
        var (helper, state, options, encoder) = VaapiPipelineCharacterizationTests.Create(scenario);
        var plan = helper.CaptureVaapiPipeline(state, options, scenario.Encoder);
        var expected = VaapiPipelineRenderer.Render(plan);
        state.BaseRequest.Width = 12;
        state.BaseRequest.Height = 8;
        state.BaseRequest.StartTimeTicks = 0;
        state.VideoStream.Rotation = 0;
        state.VideoStream.IsInterlaced = false;
        state.VideoStream.ColorTransfer = "bt709";
        state.SubtitleStream = null;
        options.HardwareAccelerationType = HardwareAccelerationType.none;
        options.EnableTonemapping = false;
        options.EnableVppTonemapping = false;
        options.TonemappingDesat = 0.9;
        encoder.Invocations.Clear();
        encoder.Setup(x => x.SupportsFilter(It.IsAny<string>())).Throws(new InvalidOperationException("capability re-read"));
        encoder.Setup(x => x.SupportsFilterWithOption(It.IsAny<FilterOptionType>())).Throws(new InvalidOperationException("capability re-read"));
        encoder.Setup(x => x.EncoderVersion).Throws(new InvalidOperationException("version re-read"));
        encoder.Setup(x => x.IsVaapiDeviceInteliHD).Throws(new InvalidOperationException("device re-read"));

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-BE");
            var actual = VaapiPipelineRenderer.Render(plan);
            Assert.Equal(expected.MainFilters, actual.MainFilters);
            Assert.Equal(expected.SubFilters, actual.SubFilters);
            Assert.Equal(expected.OverlayFilters, actual.OverlayFilters);
            encoder.VerifyNoOtherCalls();
            var mutable = actual.MainFilters.ToList();
            mutable.Clear();
            Assert.Equal(expected.MainFilters, VaapiPipelineRenderer.Render(plan).MainFilters);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("intel", null, true, typeof(VaapiPipelinePlan.Intel))]
    [InlineData("amd", null, true, typeof(VaapiPipelinePlan.AmdVulkan))]
    [InlineData("i965", null, true, typeof(VaapiPipelinePlan.Limited))]
    [InlineData("generic", null, true, typeof(VaapiPipelinePlan.Limited))]
    [InlineData("intel", "alphasrc", true, typeof(VaapiPipelinePlan.Software))]
    [InlineData("amd", "scale_opencl", true, typeof(VaapiPipelinePlan.Software))]
    [InlineData("amd", null, false, typeof(VaapiPipelinePlan.Limited))]
    public void Capture_SelectsOneStrategyBeforeRendering(string driver, string? missingFilter, bool interop, Type expected)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "VAAPI capture requires Linux");
        Assert.SkipUnless(driver != "amd" || Environment.OSVersion.Version >= new Version(5, 15), "AMD Vulkan requires kernel 5.15 or newer");
        var scenario = new VaapiPipelineCharacterizationTests.Scenario("selection", driver, MissingFilter: missingFilter, Interop: interop);
        var (helper, state, options, _) = VaapiPipelineCharacterizationTests.Create(scenario);
        Assert.IsType(expected, helper.CaptureVaapiPipeline(state, options, scenario.Encoder));
    }
}
