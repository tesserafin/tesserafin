using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tesserafin.Controller.MediaEncoding.Software;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Controller.Tests.MediaEncoding;

public sealed class SoftwarePipelineRendererTests
{
    [Theory]
    [InlineData("libx264", null, null, null, null, "")]
    [InlineData("libx264", 318, 178, null, null, "scale=trunc(318/2)*2:trunc(178/2)*2")]
    [InlineData("libx264", 160, null, null, null, "scale=160:trunc(ow/a/2)*2")]
    [InlineData("libx264", null, 90, null, null, "scale=trunc(oh*a/2)*2:90")]
    [InlineData("libx264", null, null, 160, null, "scale=trunc(min(max(iw\\,ih*a)\\,160)/2)*2:trunc(ow/a/2)*2")]
    [InlineData("libx264", null, null, null, 90, "scale=trunc(oh*a/2)*2:min(max(iw/a\\,ih)\\,90)")]
    [InlineData("h264_v4l2m2m", 318, 178, null, null, "scale=trunc(318/64)*64:trunc(178/2)*2")]
    [InlineData("mjpeg", 160, null, null, null, "scale=160:trunc(ow/(a*sar)/2)*2")]
    public void Scaling_RendersTheCharacterizedGeometry(
        string encoder, int? width, int? height, int? maxWidth, int? maxHeight, string expected)
    {
        var plan = new SoftwareScalingPlan(encoder, null, width, height, maxWidth, maxHeight);
        Assert.Equal(expected, SoftwarePipelineRenderer.RenderScaling(plan));
    }

    [Fact]
    public void Scaling_TwoCeilings_RendersAspectPreservingEvenDimensions()
    {
        var plan = new SoftwareScalingPlan("libx264", null, null, null, 160, 90);
        Assert.Equal(
            @"scale=trunc(min(max(iw\,ih*a)\,min(160\,90*a))/2)*2:trunc(min(max(iw/a\,ih)\,min(160/a\,90))/2)*2",
            SoftwarePipelineRenderer.RenderScaling(plan));
    }

    [Fact]
    public void Scaling_SideBySide_PreservesTheExistingCropAndAspectOperations()
    {
        var plan = new SoftwareScalingPlan("libx264", Video3DFormat.HalfSideBySide, 320, 180, null, null);
        Assert.Equal(
            @"crop=iw/2:ih:0:0,scale=(iw*2):ih,setdar=dar=a,crop=min(iw\,ih*dar):min(ih\,iw/dar):(iw-min(iw\,iw*sar))/2:(ih - min (ih\,ih/sar))/2,setsar=sar=1,scale=320:trunc(320/dar/2)*2",
            SoftwarePipelineRenderer.RenderScaling(plan));
    }

    [Fact]
    public void Pipeline_OrdersColorDeinterlaceScaleFormatAndTextSubtitles()
    {
        var plan = Plan() with
        {
            DeinterlaceFilter = SoftwarePipelineRenderer.RenderDeinterlacing("bwdif", true),
            Scaling = new SoftwareScalingPlan("libx264", null, 160, 90, null, null),
            Subtitles = new SoftwarePipelinePlan.Subtitle.Text("subtitles=f='/media/film.ass':alpha=0"),
        };
        var filters = SoftwarePipelineRenderer.Render(plan);
        Assert.Equal(
            ["setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709", "bwdif=1:-1:0", "scale=trunc(160/2)*2:trunc(90/2)*2", "format=yuv420p", "subtitles=f='/media/film.ass':alpha=0"],
            filters.MainFilters);
        Assert.Empty(filters.SubFilters);
        Assert.Empty(filters.OverlayFilters);
    }

    [Theory]
    [InlineData("yuv420p", "overlay=eof_action=pass:repeatlast=0")]
    [InlineData("nv12", "overlay=eof_action=pass:repeatlast=0")]
    [InlineData("yuv420p10le", "overlay=format=yuv420p10:eof_action=pass:repeatlast=0")]
    public void GraphicalSubtitles_RetainTheChosenOutputSurface(string format, string overlay)
    {
        var plan = Plan() with { PixelFormat = format, Subtitles = new SoftwarePipelinePlan.Subtitle.Graphical("scale,scale=320:180:fast_bilinear") };
        var filters = SoftwarePipelineRenderer.Render(plan);
        Assert.Equal(["scale,scale=320:180:fast_bilinear"], filters.SubFilters);
        Assert.Equal([overlay], filters.OverlayFilters);
        Assert.Contains("format=" + format, filters.MainFilters);
    }

    [Fact]
    public void ToneMapping_UsesCapturedValuesAndInvariantNumbers()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-BE");
            var plan = Plan() with { Tonemap = new SoftwarePipelinePlan.ToneMapping("hable", 0.5, 100.5, 0.25, "tv", "yuv420p") };
            Assert.Equal("tonemapx=tonemap=hable:desat=0.5:peak=100.5:t=bt709:m=bt709:p=bt709:format=yuv420p:param=0.25:range=tv", SoftwarePipelineRenderer.Render(plan).MainFilters[^1]);
            Assert.Equal("tonemapx=tonemap=clip:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=nv12", SoftwarePipelineRenderer.Render(plan with { Tonemap = new SoftwarePipelinePlan.ToneMapping("clip", 0, 100, 0, null, "nv12") }).MainFilters[^1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void CapturedPipeline_IsIndependentOfLaterJobAndConfigurationChanges()
    {
        var helper = EncodingHelperHevcMain10Tests.CreateHelper(true);
        var state = EncodingHelperHevcMain10Tests.State("main10", 10);
        var options = EncodingHelperHevcMain10Tests.Options();
        state.BaseRequest.MaxWidth = 160;
        state.BaseRequest.MaxHeight = 90;
        var plan = helper.CaptureSoftwarePipeline(state, options, "libx265");
        var expected = SoftwarePipelineRenderer.Render(plan);

        state.BaseRequest.MaxWidth = 12;
        state.BaseRequest.MaxHeight = 8;
        state.BaseRequest.MaxVideoBitDepth = 8;
        state.VideoStream.ColorPrimaries = "bt2020";
        options.HardwareAccelerationType = HardwareAccelerationType.vaapi;
        options.TonemappingDesat = 0.9;
        var rendered = SoftwarePipelineRenderer.Render(plan);
        Assert.Equal(expected.MainFilters, rendered.MainFilters);
        Assert.Contains("format=yuv420p10le", rendered.MainFilters);
        Assert.Contains("160", rendered.MainFilters[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnedFilters_DoNotShareMutableStateBetweenConsumers()
    {
        var plan = Plan();
        var first = SoftwarePipelineRenderer.Render(plan);
        var mutable = first.MainFilters.ToList();
        mutable.Add("unexpected");
        var second = SoftwarePipelineRenderer.Render(plan);
        Assert.Equal(first.MainFilters, second.MainFilters);
        Assert.DoesNotContain("unexpected", second.MainFilters);
    }

    [Theory]
    [InlineData(false, 160, 90)]
    [InlineData(true, 160, 90)]
    [InlineData(false, 320, 180)]
    public async Task SoftwareCommand_EncodesRealFramesWithTheRenderedFilters(bool deinterlace, int width, int height)
    {
        var work = Directory.CreateTempSubdirectory("software-renderer-");
        try
        {
            var output = Path.Combine(work.FullName, "output.mp4");
            var plan = Plan() with
            {
                Scaling = new SoftwareScalingPlan("libx264", null, null, null, width, height),
                DeinterlaceFilter = deinterlace ? SoftwarePipelineRenderer.RenderDeinterlacing("bwdif", false) : null,
            };
            var filters = string.Join(',', SoftwarePipelineRenderer.Render(plan).MainFilters.Where(filter => !string.IsNullOrEmpty(filter)));
            await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=10:duration=1", "-vf", filters, "-c:v", "libx264", "-preset", "ultrafast", "-an", "-y", output]);
            var actual = await RunAsync("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height,pix_fmt", "-of", "csv=p=0", output]);
            Assert.Equal(FormattableString.Invariant($"h264,{width},{height},yuv420p"), actual.Trim());
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static SoftwarePipelinePlan Plan()
        => new("yuv420p", "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709", null, new SoftwareScalingPlan("libx264", null, null, null, null, null), null, null);

    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var token = TestContext.Current.CancellationToken;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var error = await stderr;
        var result = await stdout;
        Assert.True(process.ExitCode == 0, $"{executable} rejected the software pipeline: {error}");
        return result;
    }
}
