using System;
using System.Collections.Immutable;
using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.Amf;

/// <summary>Pure AMF filter composition, including D3D11/OpenCL transfers.</summary>
internal static class AmfPipelineRenderer
{
    /// <summary>Renders captured requirements without accessing a job or service.</summary>
    /// <param name="plan">The captured strategy.</param>
    /// <returns>Immutable main, subtitle and overlay filter sequences.</returns>
    internal static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) Render(AmfPipelinePlan plan)
        => plan switch
        {
            AmfPipelinePlan.Software software => SoftwarePipelineRenderer.Render(software.Pipeline),
            AmfPipelinePlan.Hardware hardware => RenderHardware(hardware),
            _ => throw new ArgumentException("Unknown AMF strategy.", nameof(plan)),
        };

    private static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) RenderHardware(AmfPipelinePlan.Hardware plan)
    {
        var main = ImmutableArray.CreateBuilder<string>();
        var subs = ImmutableArray.CreateBuilder<string>();
        var overlays = ImmutableArray.CreateBuilder<string>();
        var tonemap = plan.ToneMappingFilter is not null;
        var graphical = plan.Subtitles is AmfPipelinePlan.Subtitle.Graphical;
        main.Add(plan.ColorFilter);
        if (!plan.D3d11Input)
        {
            if (plan.DeinterlaceFilter is not null)
            {
                main.Add(plan.DeinterlaceFilter);
            }

            main.Add(SoftwarePipelineRenderer.RenderScaling(plan.SoftwareScaling));
            main.Add(tonemap ? "format=yuv420p10le" : "format=yuv420p");
            if (tonemap)
            {
                main.Add("hwupload=derive_device=d3d11va:extra_hw_frames=24");
                main.Add("format=d3d11");
                main.Add("hwmap=derive_device=opencl:mode=read");
            }
        }
        else
        {
            main.Add("hwmap=derive_device=opencl:mode=read");
            if (plan.DeinterlaceFilter is not null)
            {
                main.Add(plan.DeinterlaceFilter);
            }

            if (plan.TransposeDirection is not null)
            {
                main.Add("transpose_opencl=dir=" + plan.TransposeDirection);
            }

            main.Add(plan.HardwareScalingFilter);
        }

        if (plan.ToneMappingFilter is { } tone)
        {
            main.Add(tone);
        }

        var memoryOutput = plan.D3d11Input && !plan.AmfOutput;
        if (memoryOutput)
        {
            main.Add(graphical ? "hwdownload" : "hwmap=mode=read");
            main.Add("format=nv12");
        }

        var uploadedForTonemap = !plan.D3d11Input && tonemap;
        memoryOutput |= !plan.D3d11Input && plan.AmfOutput && !uploadedForTonemap;
        if (memoryOutput && plan.Subtitles is AmfPipelinePlan.Subtitle.Text text)
        {
            main.Add(text.Filter);
        }

        var hardwareOverlay = (plan.D3d11Input && plan.AmfOutput) || uploadedForTonemap;
        if (hardwareOverlay && plan.Subtitles is null)
        {
            main.Add("hwmap=derive_device=d3d11va:mode=write:reverse=1");
            main.Add("format=d3d11");
        }

        if (hardwareOverlay && plan.Subtitles is not null)
        {
            var alpha = string.Empty;
            switch (plan.Subtitles)
            {
                case AmfPipelinePlan.Subtitle.Graphical image:
                    subs.Add(image.PreprocessFilter);
                    subs.Add("format=yuva420p");
                    break;
                case AmfPipelinePlan.Subtitle.OverlayText overlayText:
                    subs.Add(overlayText.AlphaSourceFilter);
                    subs.Add("format=yuva420p");
                    subs.Add(overlayText.Filter);
                    alpha = overlayText.PremultipliedAlpha ? ":alpha_format=premultiplied" : string.Empty;
                    break;
            }

            subs.Add("hwupload=derive_device=opencl");
            overlays.Add("overlay_opencl=eof_action=pass:repeatlast=0" + alpha);
            overlays.Add("hwmap=derive_device=d3d11va:mode=write:reverse=1");
            overlays.Add("format=d3d11");
        }
        else if (memoryOutput && plan.Subtitles is AmfPipelinePlan.Subtitle.Graphical image)
        {
            subs.Add(image.PreprocessFilter);
            overlays.Add("overlay=eof_action=pass:repeatlast=0");
        }

        return (main.ToImmutable(), subs.ToImmutable(), overlays.ToImmutable());
    }
}
