using System;
using System.Globalization;
using System.Linq;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.MediaEncoding.Amf;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

public sealed class AmfPipelineSnapshotTests
{
    [Theory]
    [InlineData(true, "none")]
    [InlineData(true, "ass")]
    [InlineData(true, "pgs")]
    [InlineData(false, "none")]
    [InlineData(false, "ass")]
    [InlineData(false, "pgs")]
    public void CapturedGraph_IsIndependentOfLaterJobOptionsAndCapabilityChanges(bool hardware, string subtitles)
    {
        var scenario = new RemainingPipelineCharacterizationTests.Scenario("snapshot", "Amf", Hardware: hardware, Encoder: "h264_amf", Hdr: true, Subtitles: subtitles, Rotation: 90, Interlaced: true);
        var (helper, state, options, encoder) = RemainingPipelineCharacterizationTests.Create(scenario);
        var plan = helper.CaptureAmfHardwarePipeline(state, options, RemainingPipelineCharacterizationTests.Decoder(scenario), scenario.Encoder);
        var expected = AmfPipelineRenderer.Render(plan);
        state.BaseRequest.Width = 12;
        state.BaseRequest.Height = 8;
        state.VideoStream.Rotation = 0;
        state.VideoStream.IsInterlaced = false;
        state.SubtitleStream = null;
        options.EnableTonemapping = false;
        options.EnableVideoToolboxTonemapping = false;
        options.TonemappingDesat = 0.9;
        encoder.Invocations.Clear();
        encoder.Setup(value => value.SupportsFilter(It.IsAny<string>())).Throws(new InvalidOperationException("capability re-read"));
        encoder.Setup(value => value.EncoderVersion).Throws(new InvalidOperationException("version re-read"));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-BE");
            var actual = AmfPipelineRenderer.Render(plan);
            Assert.Equal(expected.MainFilters, actual.MainFilters);
            Assert.Equal(expected.SubFilters, actual.SubFilters);
            Assert.Equal(expected.OverlayFilters, actual.OverlayFilters);
            encoder.VerifyNoOtherCalls();
            actual.MainFilters.ToList().Clear();
            actual.SubFilters.ToList().Clear();
            actual.OverlayFilters.ToList().Clear();
            var repeat = AmfPipelineRenderer.Render(plan);
            Assert.Equal(expected.MainFilters, repeat.MainFilters);
            Assert.Equal(expected.SubFilters, repeat.SubFilters);
            Assert.Equal(expected.OverlayFilters, repeat.OverlayFilters);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void MissingRequiredCapability_SelectsTheSharedSoftwareStrategy()
    {
        var scenario = new RemainingPipelineCharacterizationTests.Scenario("capability", "Amf", Encoder: "h264_amf", MissingFilter: "alphasrc");
        var (helper, state, options, _) = RemainingPipelineCharacterizationTests.Create(scenario);
        Assert.IsType<AmfPipelinePlan.Software>(helper.CaptureAmfPipeline(state, options, scenario.Encoder));
    }

    [Fact]
    public void NativeRouting_UsesHardwareStrategyOnlyOnItsSupportedPlatform()
    {
        var scenario = new RemainingPipelineCharacterizationTests.Scenario("platform", "Amf", Encoder: "h264_amf");
        var (helper, state, options, _) = RemainingPipelineCharacterizationTests.Create(scenario);
        var plan = helper.CaptureAmfPipeline(state, options, scenario.Encoder);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<AmfPipelinePlan.Hardware>(plan);
        }
        else
        {
            Assert.IsType<AmfPipelinePlan.Software>(plan);
        }
    }
}
