using System;
using System.Collections.Immutable;
using System.Globalization;
using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.Nvidia;

/// <summary>
/// Composes the selected NVIDIA filter graph, including transfers between memory and CUDA.
/// It never reads mutable requests, configuration, hardware capabilities or services.
/// </summary>
internal static class NvidiaPipelineRenderer
{
    /// <summary>
    /// Renders the main video, subtitle and overlay sequences from captured requirements.
    /// </summary>
    /// <param name="plan">The captured pipeline.</param>
    /// <returns>The three immutable filter sequences.</returns>
    internal static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) Render(NvidiaPipelinePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var main = ImmutableArray.CreateBuilder<string>();
        var subtitles = ImmutableArray.CreateBuilder<string>();
        var overlays = ImmutableArray.CreateBuilder<string>();
        main.Add(plan.ColorFilter);

        if (plan.DeinterlaceFilter is not null)
        {
            main.Add(plan.DeinterlaceFilter);
        }

        if (plan.CudaInput)
        {
            if (plan.TransposeDirection is not null)
            {
                main.Add("transpose_cuda=dir=" + plan.TransposeDirection);
            }

            main.Add(plan.CudaScalingFilter);
        }
        else
        {
            main.Add(SoftwarePipelineRenderer.RenderScaling(plan.SoftwareScaling));
            main.Add(plan.TonemapFilter is null ? "format=yuv420p" : "format=yuv420p10le");
            if (plan.TonemapFilter is not null)
            {
                main.Add("hwupload=derive_device=cuda");
            }
        }

        if (plan.TonemapFilter is not null)
        {
            main.Add(plan.TonemapFilter);
        }

        var uploadedForTonemap = !plan.CudaInput && plan.TonemapFilter is not null;
        var download = (plan.CudaInput && !plan.NvencOutput) || (uploadedForTonemap && plan.Subtitles is not null);
        if (download)
        {
            main.Add("hwdownload");
            main.Add("format=yuv420p");
        }

        var memoryOutput = download || (!plan.CudaInput && plan.NvencOutput && !uploadedForTonemap);
        if (memoryOutput && plan.Subtitles is NvidiaPipelinePlan.Subtitle.Text memoryText)
        {
            main.Add(memoryText.Filter);
        }

        if (plan.CudaInput && plan.NvencOutput)
        {
            var alphaFormat = string.Empty;
            switch (plan.Subtitles)
            {
                case NvidiaPipelinePlan.Subtitle.Graphical graphical:
                    subtitles.Add(graphical.PreprocessFilter);
                    subtitles.Add("format=yuva420p");
                    break;
                case NvidiaPipelinePlan.Subtitle.CudaText text:
                    subtitles.Add(text.AlphaSourceFilter);
                    subtitles.Add("format=yuva420p");
                    subtitles.Add(text.Filter);
                    alphaFormat = text.PremultipliedAlpha ? ":alpha_format=premultiplied" : string.Empty;
                    break;
            }

            if (plan.Subtitles is not null)
            {
                subtitles.Add("hwupload=derive_device=cuda");
                overlays.Add("overlay_cuda=eof_action=pass:repeatlast=0" + alphaFormat);
            }
        }
        else if (plan.Subtitles is NvidiaPipelinePlan.Subtitle.Graphical graphical)
        {
            subtitles.Add(graphical.PreprocessFilter);
            overlays.Add("overlay=eof_action=pass:repeatlast=0");
        }

        return (main.ToImmutable(), subtitles.ToImmutable(), overlays.ToImmutable());
    }

    /// <summary>
    /// Formats CUDA deinterlacing after method support and frame-rate policy were resolved.
    /// </summary>
    /// <param name="useBwdif">Whether supported bwdif was selected instead of yadif.</param>
    /// <param name="doubleRate">Whether each field produces an output frame.</param>
    /// <returns>The CUDA deinterlacing filter.</returns>
    internal static string RenderDeinterlacing(bool useBwdif, bool doubleRate)
        => string.Format(CultureInfo.InvariantCulture, "{0}_cuda={1}:-1:0", useBwdif ? "bwdif" : "yadif", doubleRate ? "1" : "0");
}
