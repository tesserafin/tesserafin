using System;
using System.Globalization;
using System.Linq;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.MediaEncoding.Nvidia;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

public sealed class NvidiaPipelineRendererTests
{
    [Theory]
    [InlineData(true, "none")]
    [InlineData(true, "ass")]
    [InlineData(true, "pgs")]
    [InlineData(false, "none")]
    [InlineData(false, "ass")]
    [InlineData(false, "pgs")]
    public void CapturedPipeline_DoesNotReadLaterStateConfigurationOrCapabilities(bool cuda, string subtitles)
    {
        var scenario = new NvidiaPipelineCharacterizationTests.Scenario("snapshot", Cuda: cuda, Hdr: true, Subtitles: subtitles, Interlaced: true, Rotation: 90);
        var (helper, state, options, encoder) = NvidiaPipelineCharacterizationTests.Create(scenario);
        var plan = helper.CaptureNvidiaPipeline(state, options, cuda, scenario.Encoder);
        var expected = NvidiaPipelineRenderer.Render(plan);
        state.BaseRequest.Width = 12;
        state.BaseRequest.Height = 8;
        state.BaseRequest.StartTimeTicks = 0;
        state.VideoStream.Rotation = 0;
        state.VideoStream.IsInterlaced = false;
        state.VideoStream.ColorTransfer = "bt709";
        state.SubtitleStream = null;
        options.HardwareAccelerationType = HardwareAccelerationType.none;
        options.EnableTonemapping = false;
        options.TonemappingDesat = 0.9;
        encoder.Invocations.Clear();
        encoder.Setup(x => x.SupportsFilter(It.IsAny<string>())).Throws(new InvalidOperationException("capability re-read"));
        encoder.Setup(x => x.SupportsFilterWithOption(It.IsAny<FilterOptionType>())).Throws(new InvalidOperationException("capability re-read"));

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-BE");
            var actual = NvidiaPipelineRenderer.Render(plan);
            Assert.Equal(expected.MainFilters, actual.MainFilters);
            Assert.Equal(expected.SubFilters, actual.SubFilters);
            Assert.Equal(expected.OverlayFilters, actual.OverlayFilters);
            Assert.Contains(actual.MainFilters, filter => filter.Contains("peak=100.5:desat=0.5", StringComparison.Ordinal));
            encoder.VerifyNoOtherCalls();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(false, false, "yadif_cuda=0:-1:0")]
    [InlineData(false, true, "yadif_cuda=1:-1:0")]
    [InlineData(true, false, "bwdif_cuda=0:-1:0")]
    [InlineData(true, true, "bwdif_cuda=1:-1:0")]
    public void Deinterlacing_UsesTheResolvedMethodAndFieldRate(bool bwdif, bool doubleRate, string expected)
        => Assert.Equal(expected, NvidiaPipelineRenderer.RenderDeinterlacing(bwdif, doubleRate));

    [Fact]
    public void RenderedGraphs_DoNotShareMutableCollectionsWithCommandAssembly()
    {
        var scenario = new NvidiaPipelineCharacterizationTests.Scenario("overlay", Subtitles: "ass");
        var (helper, state, options, _) = NvidiaPipelineCharacterizationTests.Create(scenario);
        var plan = helper.CaptureNvidiaPipeline(state, options, true, scenario.Encoder);
        var first = NvidiaPipelineRenderer.Render(plan);
        var main = first.MainFilters.ToList();
        var subtitles = first.SubFilters.ToList();
        var overlays = first.OverlayFilters.ToList();
        main.Clear();
        subtitles.Clear();
        overlays.Clear();
        var second = NvidiaPipelineRenderer.Render(plan);
        Assert.Equal(first.MainFilters, second.MainFilters);
        Assert.Equal(first.SubFilters, second.SubFilters);
        Assert.Equal(first.OverlayFilters, second.OverlayFilters);
        Assert.NotEmpty(second.MainFilters);
        Assert.NotEmpty(second.SubFilters);
        Assert.NotEmpty(second.OverlayFilters);
    }
}
