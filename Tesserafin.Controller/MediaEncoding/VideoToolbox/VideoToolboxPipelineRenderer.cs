using System;
using System.Collections.Immutable;
using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.VideoToolbox;

/// <summary>Pure VideoToolbox and Metal filter composition.</summary>
internal static class VideoToolboxPipelineRenderer
{
    /// <summary>Renders the selected strategy without probing hardware or mutable state.</summary>
    /// <param name="plan">The captured strategy.</param>
    /// <returns>Immutable main, subtitle and overlay sequences.</returns>
    internal static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) Render(VideoToolboxPipelinePlan plan)
        => plan switch
        {
            VideoToolboxPipelinePlan.Software software => SoftwarePipelineRenderer.Render(software.Pipeline),
            VideoToolboxPipelinePlan.Hardware hardware => RenderHardware(hardware),
            _ => throw new ArgumentException("Unknown VideoToolbox strategy.", nameof(plan)),
        };

    private static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) RenderHardware(VideoToolboxPipelinePlan.Hardware plan)
    {
        var main = ImmutableArray.CreateBuilder<string>();
        var subs = ImmutableArray.CreateBuilder<string>();
        var overlays = ImmutableArray.CreateBuilder<string>();
        if (plan.DeinterlaceFilter is not null)
        {
            main.Add(plan.DeinterlaceFilter);
        }

        if (plan.TransposeDirection is not null)
        {
            main.Add("transpose_vt=dir=" + plan.TransposeDirection);
        }

        var scale = plan.ScalingFilter;
        if (plan.Tone is VideoToolboxPipelinePlan.ToneMapping.Native)
        {
            const string NativeTone = "color_matrix=bt709:color_primaries=bt709:color_transfer=bt709";
            scale = string.IsNullOrEmpty(scale) ? "scale_vt=" + NativeTone : scale + ":" + NativeTone;
        }

        main.Add(scale);
        if (plan.Tone is VideoToolboxPipelinePlan.ToneMapping.Metal metal)
        {
            main.Add(metal.Filter);
        }

        switch (plan.Subtitles)
        {
            case VideoToolboxPipelinePlan.Subtitle.Graphical graphical:
                subs.Add(graphical.PreprocessFilter);
                subs.Add("format=bgra");
                break;
            case VideoToolboxPipelinePlan.Subtitle.Text text:
                subs.Add(text.AlphaSourceFilter);
                subs.Add("format=bgra");
                subs.Add(text.Filter);
                break;
        }

        if (plan.Subtitles is not null)
        {
            subs.Add("hwupload");
            overlays.Add("overlay_videotoolbox=eof_action=pass:repeatlast=0");
        }

        var needFiltering = subs.Count > 0 || overlays.Count > 0;
        foreach (var filter in main)
        {
            needFiltering |= !string.IsNullOrEmpty(filter);
        }

        if (!plan.UsingHardwareSurface && needFiltering)
        {
            main.Insert(0, "hwupload");
            main.Insert(0, "format=nv12|p010le|videotoolbox_vld");
        }

        if (!plan.HardwareOutput && (plan.UsingHardwareSurface || needFiltering))
        {
            main.Add("hwdownload");
            main.Add("format=nv12");
        }

        return (main.ToImmutable(), subs.ToImmutable(), overlays.ToImmutable());
    }
}
