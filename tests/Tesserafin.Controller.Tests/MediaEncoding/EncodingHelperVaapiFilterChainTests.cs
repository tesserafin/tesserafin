using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.MediaEncoding.Vaapi;
using Tesserafin.Controller.Streaming;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Xunit;

using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace Tesserafin.Controller.Tests.MediaEncoding;

/// <summary>
/// Characterization tests locking the VAAPI software/copy-back renderer's current
/// output before any renderer-extraction refactor (transcoding-pipeline plan PR5). This is the
/// safety net PR1 did not cover - PR1 characterized encoder *selection*
/// (<c>GetVideoEncoder</c> -> <c>"h264_vaapi"</c>), not the filter-chain string this method
/// builds, which is exactly the code PR5 would extract into a VaapiPipelineRenderer.
/// </summary>
/// <remarks>
/// Only the "legacy copy-back" branch is covered: a bare <see cref="Mock{IMediaEncoder}"/> has
/// every <c>Supports*</c> call return false, which is precisely what routes here (the "preferred"
/// Intel-iHD/AMD-vulkan branches require real driver capability probing - <c>IsVaapiDeviceAmd</c>,
/// kernel version checks - that a mock can't faithfully stand in for). This is also the realistic
/// default: hitting the "full" pipeline requires driver/kernel alignment most systems don't have.
/// </remarks>
public class EncodingHelperVaapiFilterChainTests
{
    private const string VaapiRenderNode = "/dev/dri/renderD128";

    [Fact]
    public void SoftwareDecodeToVaapiEncode_UsesHwuploadDeriveDevice()
    {
        var state = BuildState();
        var options = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi, EnableHardwareEncoding = true };

        var (mainFilters, subFilters, overlayFilters) = VaapiPipelineRenderer.Render(CreateHelper().CaptureVaapiPipeline(state, options, "h264_vaapi"));

        // Captured from current behavior, not hand-authored (see class remarks). The empty
        // element between setparams and format=nv12 is real - the software renderer doesn't
        // strip it itself; only the public GetVideoProcessingFilterParam entry point does
        // (RemoveAll(string.IsNullOrEmpty)) before joining into the final -vf string.
        Assert.Equal(
            [
                "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709",
                string.Empty,
                "format=nv12",
                "hwupload=derive_device=vaapi",
            ],
            mainFilters);
        Assert.Empty(subFilters);
        Assert.Empty(overlayFilters);
    }

    [Fact]
    public async Task SoftwareDecodeToVaapiEncode_FilterStringIsAcceptedByRealVaapiHardware()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists(VaapiRenderNode), "requires a real VAAPI render node - not present in this environment");

        var state = BuildState();
        var options = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi, EnableHardwareEncoding = true };
        var (mainFilters, _, _) = VaapiPipelineRenderer.Render(CreateHelper().CaptureVaapiPipeline(state, options, "h264_vaapi"));
        var nonEmptyFilters = mainFilters.Where(filter => !string.IsNullOrEmpty(filter)); // the command assembler removes empty slots

        // Runs the exact filter chain EncodingHelper produces today through real ffmpeg against
        // real AMD VAAPI hardware - not just "does it parse", but "does it actually encode a
        // frame". This is the validation on top of the lock, per the PR5 characterization plan.
        var filterArg = string.Join(',', nonEmptyFilters);
        var arguments = $"-hide_banner -init_hw_device vaapi=va:{VaapiRenderNode} -f lavfi -i testsrc=duration=1:size=320x240:rate=10 -vf {filterArg} -c:v h264_vaapi -f null -";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("ffmpeg", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        var cancellationToken = TestContext.Current.CancellationToken;
        process.Start();
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdErr = await stdErrTask;
        await stdOutTask;

        Assert.True(process.ExitCode == 0, $"Real ffmpeg rejected the filter chain '{filterArg}':\n{stdErr}");
    }

    private static EncodingJobInfo BuildState()
    {
        var video = new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 320, Height = 240 };

        return new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            MediaSource = new MediaSourceInfo
            {
                Container = "mkv",
                MediaStreams = new List<MediaStream> { video },
                VideoType = VideoType.VideoFile,
            },
            VideoStream = video,
            VideoType = VideoType.VideoFile,
            BaseRequest = new VideoRequestDto(),
            IsVideoRequest = true,
            IsInputVideo = true,
        };
    }

    private static EncodingHelper CreateHelper()
    {
        var appPaths = Mock.Of<IApplicationPaths>();
        var mediaEncoder = new Mock<IMediaEncoder>();
        var subtitleEncoder = new Mock<ISubtitleEncoder>();
        var config = new Mock<IConfiguration>();
        var configurationManager = new Mock<IConfigurationManager>();
        var pathManager = new Mock<IPathManager>();

        return new EncodingHelper(
            appPaths,
            mediaEncoder.Object,
            subtitleEncoder.Object,
            config.Object,
            configurationManager.Object,
            pathManager.Object);
    }
}
