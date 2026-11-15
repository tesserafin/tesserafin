using System;
using System.Collections.Immutable;
using System.Globalization;
using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.Rkmpp;

/// <summary>Pure RKMPP/RGA/OpenCL composition with captured surface and scaling policies.</summary>
internal static class RkmppPipelineRenderer
{
    /// <summary>Renders the captured strategy without probing a device or mutable state.</summary>
    /// <param name="plan">The captured strategy.</param>
    /// <returns>Immutable main, subtitle and overlay sequences.</returns>
    internal static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) Render(RkmppPipelinePlan plan)
        => plan switch
        {
            RkmppPipelinePlan.Software software => SoftwarePipelineRenderer.Render(software.Pipeline),
            RkmppPipelinePlan.Hardware hardware => RenderHardware(hardware),
            _ => throw new ArgumentException("Unknown RKMPP strategy.", nameof(plan)),
        };

    private static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) RenderHardware(RkmppPipelinePlan.Hardware plan)
    {
        var main = ImmutableArray.CreateBuilder<string>();
        var subs = ImmutableArray.CreateBuilder<string>();
        var overlays = ImmutableArray.CreateBuilder<string>();
        var tone = plan.ToneMappingFilter is not null;
        var graphical = plan.Subtitles is RkmppPipelinePlan.Subtitle.Graphical;
        var hardwarePair = plan.HardwareInput && plan.HardwareOutput;
        main.Add(plan.ColorFilter);
        if (!plan.HardwareInput)
        {
            if (plan.SoftwareDeinterlaceFilter is not null)
            {
                main.Add(plan.SoftwareDeinterlaceFilter);
            }

            var scale = SoftwarePipelineRenderer.RenderScaling(plan.SoftwareScaling);
            if (plan.MjpegOutput && !tone)
            {
                scale = string.IsNullOrEmpty(scale) ? "scale=out_range=pc" : scale + ":out_range=pc";
            }

            if (!string.IsNullOrEmpty(scale))
            {
                scale += ":flags=fast_bilinear";
            }

            main.Add(scale);
            main.Add("format=" + (tone ? "yuv420p10le" : graphical ? "yuv420p" : "nv12"));
            if (tone)
            {
                main.Add("hwupload=derive_device=opencl");
            }
        }
        else
        {
            var fullAfbc = plan.EncoderSupportsAfbc && hardwarePair && !tone;
            if (plan.Subtitles is null || plan.TransposeDirection is not null || !fullAfbc || plan.ScalingNeeded)
            {
                if (plan.ScalingNeeded && !plan.SinglePassScalingSupported)
                {
                    // Keep each RGA3 blit within its range; NV15 avoids the BSP P010 corruption.
                    var format = tone ? "nv15" : plan.MjpegOutput ? "bgra" : "nv12";
                    main.Add("scale_rkrga=w=iw/7.9:h=ih/7.9:format=" + format + ":force_original_aspect_ratio=increase:force_divisible_by=4:afbc=1");
                }

                if (plan.MjpegOutput && !tone && (!plan.ScalingNeeded || plan.SinglePassScalingSupported))
                {
                    main.Add("vpp_rkrga=format=bgra:afbc=1");
                }

                var scale = plan.HardwareScalingFilter;
                if (!string.IsNullOrEmpty(scale))
                {
                    if (plan.TransposeDirection is not null)
                    {
                        scale += ":transpose=" + plan.TransposeDirection;
                    }

                    if (fullAfbc)
                    {
                        scale += ":afbc=1";
                    }
                }

                main.Add(scale);
            }
        }

        if (tone && plan.HardwareInput)
        {
            main.Add("hwmap=derive_device=opencl");
        }

        if (plan.ToneMappingFilter is { } tonemap)
        {
            main.Add(tonemap);
        }

        var memoryOutput = (plan.HardwareInput && !plan.HardwareOutput) || (!plan.HardwareInput && tone);
        if (memoryOutput)
        {
            main.Add("hwdownload");
            main.Add("format=nv12");
        }

        memoryOutput |= !plan.HardwareInput && plan.HardwareOutput;
        if (memoryOutput && plan.Subtitles is RkmppPipelinePlan.Subtitle.Text text)
        {
            main.Add(text.Filter);
        }

        if (hardwarePair && tone)
        {
            main.Add("hwmap=derive_device=rkmpp:reverse=1");
            main.Add("format=drm_prime");
        }

        if (hardwarePair && plan.Subtitles is not null)
        {
            switch (plan.Subtitles)
            {
                case RkmppPipelinePlan.Subtitle.Graphical image:
                    subs.Add(image.PreprocessFilter);
                    subs.Add("format=bgra");
                    break;
                case RkmppPipelinePlan.Subtitle.OverlayText overlayText:
                    subs.Add(overlayText.AlphaSourceFilter);
                    subs.Add("format=bgra");
                    subs.Add(overlayText.Filter);
                    break;
            }

            subs.Add("hwupload=derive_device=rkmpp");
            if (plan.OverlayWidth.HasValue && plan.OverlayHeight.HasValue && plan.OverlayHeight.Value > 1080)
            {
                subs.Add(string.Format(CultureInfo.InvariantCulture, "vpp_rkrga=w={0}:h={1}:format=bgra:afbc=1", plan.OverlayWidth.Value, plan.OverlayHeight.Value));
            }

            overlays.Add("overlay_rkrga=eof_action=pass:repeatlast=0:format=nv12" + (plan.EncoderSupportsAfbc ? ":afbc=1" : string.Empty));
        }
        else if (memoryOutput && plan.Subtitles is RkmppPipelinePlan.Subtitle.Graphical image)
        {
            subs.Add(image.PreprocessFilter);
            overlays.Add("overlay=eof_action=pass:repeatlast=0");
        }

        return (main.ToImmutable(), subs.ToImmutable(), overlays.ToImmutable());
    }
}
