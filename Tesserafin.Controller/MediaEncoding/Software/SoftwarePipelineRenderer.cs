using System;
using System.Collections.Immutable;
using System.Globalization;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding.Software;

/// <summary>
/// Renders resolved software filtering requirements. This type has no configuration, I/O or
/// encoding services; every output depends only on its immutable input.
/// </summary>
internal static class SoftwarePipelineRenderer
{
    /// <summary>
    /// Builds the main video, subtitle and overlay filter sequences without mutating the plan.
    /// Empty color/scaling slots are retained until the command assembler removes empty filters.
    /// </summary>
    /// <param name="plan">The captured pipeline.</param>
    /// <returns>The three immutable filter sequences.</returns>
    internal static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) Render(SoftwarePipelinePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var main = ImmutableArray.CreateBuilder<string>();
        var subtitles = ImmutableArray.CreateBuilder<string>();
        var overlays = ImmutableArray.CreateBuilder<string>();
        main.Add(plan.ColorFilter ?? string.Empty);
        if (plan.DeinterlaceFilter is not null)
        {
            main.Add(plan.DeinterlaceFilter);
        }

        main.Add(RenderScaling(plan.Scaling));
        main.Add(plan.Tonemap is { } tone ? RenderTonemapping(tone) : "format=" + plan.PixelFormat);
        switch (plan.Subtitles)
        {
            case SoftwarePipelinePlan.Subtitle.Text text:
                main.Add(text.Filter);
                break;
            case SoftwarePipelinePlan.Subtitle.Graphical graphical:
                subtitles.Add(graphical.PreprocessFilter);
                overlays.Add(plan.PixelFormat == "yuv420p10le"
                    ? "overlay=format=yuv420p10:eof_action=pass:repeatlast=0"
                    : "overlay=eof_action=pass:repeatlast=0");
                break;
        }

        return (main.ToImmutable(), subtitles.ToImmutable(), overlays.ToImmutable());
    }

    /// <summary>
    /// Renders deinterlacing after the caller has resolved the method and frame-rate policy.
    /// </summary>
    /// <param name="method">The resolved FFmpeg filter name.</param>
    /// <param name="doubleRate">Whether the resolved policy doubles the frame rate.</param>
    /// <returns>The deinterlacing filter.</returns>
    internal static string RenderDeinterlacing(string method, bool doubleRate)
        => string.Format(CultureInfo.InvariantCulture, "{0}={1}:-1:0", method, doubleRate ? "1" : "0");

    private static string RenderTonemapping(SoftwarePipelinePlan.ToneMapping tone)
    {
        var result = string.Format(
            CultureInfo.InvariantCulture,
            "tonemapx=tonemap={0}:desat={1}:peak={2}:t=bt709:m=bt709:p=bt709:format={3}",
            tone.Algorithm,
            tone.Desaturation,
            tone.Peak,
            tone.PixelFormat);
        if (tone.Parameter != 0)
        {
            result += ":param=" + tone.Parameter.ToString(CultureInfo.InvariantCulture);
        }

        return tone.Range is null ? result : result + ":range=" + tone.Range;
    }

    /// <summary>
    /// Renders scaling for software surfaces, including hardware copy-back consumers.
    /// </summary>
    /// <param name="plan">The captured scaling requirements.</param>
    /// <returns>The scaling filter, or an empty string when no dimensions were requested.</returns>
    internal static string RenderScaling(SoftwareScalingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var videoEncoder = plan.VideoEncoder;
        var threedFormat = plan.ThreeDFormat;
        var requestedWidth = plan.Width;
        var requestedHeight = plan.Height;
        var requestedMaxWidth = plan.MaxWidth;
        var requestedMaxHeight = plan.MaxHeight;
        var isV4l2 = string.Equals(videoEncoder, "h264_v4l2m2m", StringComparison.OrdinalIgnoreCase);
        var isMjpeg = videoEncoder is not null && videoEncoder.Contains("mjpeg", StringComparison.OrdinalIgnoreCase);
        var scaleVal = isV4l2 ? 64 : 2;
        var targetAr = isMjpeg ? "(a*sar)" : "a"; // manually calculate AR when using mjpeg encoder

        // If fixed dimensions were supplied
        if (requestedWidth.HasValue && requestedHeight.HasValue)
        {
            if (isV4l2)
            {
                var widthParam = requestedWidth.Value.ToString(CultureInfo.InvariantCulture);
                var heightParam = requestedHeight.Value.ToString(CultureInfo.InvariantCulture);

                return string.Format(
                        CultureInfo.InvariantCulture,
                        "scale=trunc({0}/64)*64:trunc({1}/2)*2",
                        widthParam,
                        heightParam);
            }

            return RenderFixedScaling(threedFormat, requestedWidth.Value, requestedHeight.Value);
        }

        // If Max dimensions were supplied, for width selects lowest even number between input width and width req size and selects lowest even number from in width*display aspect and requested size

        if (requestedMaxWidth.HasValue && requestedMaxHeight.HasValue)
        {
            var maxWidthParam = requestedMaxWidth.Value.ToString(CultureInfo.InvariantCulture);
            var maxHeightParam = requestedMaxHeight.Value.ToString(CultureInfo.InvariantCulture);

            return string.Format(
                CultureInfo.InvariantCulture,
                @"scale=trunc(min(max(iw\,ih*{3})\,min({0}\,{1}*{3}))/{2})*{2}:trunc(min(max(iw/{3}\,ih)\,min({0}/{3}\,{1}))/2)*2",
                maxWidthParam,
                maxHeightParam,
                scaleVal,
                targetAr);
        }

        // If a fixed width was requested
        if (requestedWidth.HasValue)
        {
            if (threedFormat.HasValue)
            {
                // This method can handle 0 being passed in for the requested height
                return RenderFixedScaling(threedFormat, requestedWidth.Value, 0);
            }

            var widthParam = requestedWidth.Value.ToString(CultureInfo.InvariantCulture);

            return string.Format(
                CultureInfo.InvariantCulture,
                "scale={0}:trunc(ow/{1}/2)*2",
                widthParam,
                targetAr);
        }

        // If a fixed height was requested
        if (requestedHeight.HasValue)
        {
            var heightParam = requestedHeight.Value.ToString(CultureInfo.InvariantCulture);

            return string.Format(
                CultureInfo.InvariantCulture,
                "scale=trunc(oh*{2}/{1})*{1}:{0}",
                heightParam,
                scaleVal,
                targetAr);
        }

        // If a max width was requested
        if (requestedMaxWidth.HasValue)
        {
            var maxWidthParam = requestedMaxWidth.Value.ToString(CultureInfo.InvariantCulture);

            return string.Format(
                CultureInfo.InvariantCulture,
                @"scale=trunc(min(max(iw\,ih*{2})\,{0})/{1})*{1}:trunc(ow/{2}/2)*2",
                maxWidthParam,
                scaleVal,
                targetAr);
        }

        // If a max height was requested
        if (requestedMaxHeight.HasValue)
        {
            var maxHeightParam = requestedMaxHeight.Value.ToString(CultureInfo.InvariantCulture);

            return string.Format(
                CultureInfo.InvariantCulture,
                @"scale=trunc(oh*{2}/{1})*{1}:min(max(iw/{2}\,ih)\,{0})",
                maxHeightParam,
                scaleVal,
                targetAr);
        }

        return string.Empty;
    }

    private static string RenderFixedScaling(Video3DFormat? threedFormat, int requestedWidth, int requestedHeight)
    {
        var widthParam = requestedWidth.ToString(CultureInfo.InvariantCulture);
        var heightParam = requestedHeight.ToString(CultureInfo.InvariantCulture);

        string? filter = null;

        if (threedFormat.HasValue)
        {
            switch (threedFormat.Value)
            {
                case Video3DFormat.HalfSideBySide:
                    filter = @"crop=iw/2:ih:0:0,scale=(iw*2):ih,setdar=dar=a,crop=min(iw\,ih*dar):min(ih\,iw/dar):(iw-min(iw\,iw*sar))/2:(ih - min (ih\,ih/sar))/2,setsar=sar=1,scale={0}:trunc({0}/dar/2)*2";
                    // hsbs crop width in half,scale to correct size, set the display aspect,crop out any black bars we may have made the scale width to requestedWidth. Work out the correct height based on the display aspect it will maintain the aspect where -1 in this case (3d) may not.
                    break;
                case Video3DFormat.FullSideBySide:
                    filter = @"crop=iw/2:ih:0:0,setdar=dar=a,crop=min(iw\,ih*dar):min(ih\,iw/dar):(iw-min(iw\,iw*sar))/2:(ih - min (ih\,ih/sar))/2,setsar=sar=1,scale={0}:trunc({0}/dar/2)*2";
                    // fsbs crop width in half,set the display aspect,crop out any black bars we may have made the scale width to requestedWidth.
                    break;
                case Video3DFormat.HalfTopAndBottom:
                    filter = @"crop=iw:ih/2:0:0,scale=(iw*2):ih),setdar=dar=a,crop=min(iw\,ih*dar):min(ih\,iw/dar):(iw-min(iw\,iw*sar))/2:(ih - min (ih\,ih/sar))/2,setsar=sar=1,scale={0}:trunc({0}/dar/2)*2";
                    // htab crop height in half,scale to correct size, set the display aspect,crop out any black bars we may have made the scale width to requestedWidth
                    break;
                case Video3DFormat.FullTopAndBottom:
                    filter = @"crop=iw:ih/2:0:0,setdar=dar=a,crop=min(iw\,ih*dar):min(ih\,iw/dar):(iw-min(iw\,iw*sar))/2:(ih - min (ih\,ih/sar))/2,setsar=sar=1,scale={0}:trunc({0}/dar/2)*2";
                    // ftab crop height in half, set the display aspect,crop out any black bars we may have made the scale width to requestedWidth
                    break;
                default:
                    break;
            }
        }

        // default
        if (filter is null)
        {
            if (requestedHeight > 0)
            {
                filter = "scale=trunc({0}/2)*2:trunc({1}/2)*2";
            }
            else
            {
                filter = "scale={0}:trunc({0}/a/2)*2";
            }
        }

        return string.Format(CultureInfo.InvariantCulture, filter, widthParam, heightParam);
    }
}
