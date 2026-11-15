using System;
using System.Collections.Immutable;
using System.Globalization;
using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.Qsv;

/// <summary>
/// Renders QSV filter graphs from immutable inputs. The selected VAAPI or D3D11 strategy
/// determines surface transfers; no platform, configuration or device is queried here.
/// </summary>
internal static class QsvPipelineRenderer
{
    /// <summary>Renders the selected strategy into independent immutable sequences.</summary>
    /// <param name="plan">The captured pipeline.</param>
    /// <returns>The main, subtitle and overlay sequences.</returns>
    internal static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) Render(QsvPipelinePlan plan)
        => plan switch
        {
            QsvPipelinePlan.Software software => SoftwarePipelineRenderer.Render(software.Pipeline),
            QsvPipelinePlan.Vaapi vaapi => RenderHardware(vaapi.Input, vaapi.Tone, true),
            QsvPipelinePlan.D3d11 d3d11 => RenderHardware(d3d11.Input, d3d11.Tone, false),
            _ => throw new ArgumentException("Unknown QSV rendering strategy.", nameof(plan)),
        };

    private static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) RenderHardware(
        QsvPipelinePlan.Inputs input,
        QsvPipelinePlan.ToneMapping? tone,
        bool vaapi)
    {
        var main = ImmutableArray.CreateBuilder<string>();
        var subtitles = ImmutableArray.CreateBuilder<string>();
        var overlays = ImmutableArray.CreateBuilder<string>();
        var hardwareInput = input.Decoder != QsvPipelinePlan.Surface.Memory;
        var opencl = tone is QsvPipelinePlan.ToneMapping.OpenCl;
        main.Add(input.ColorFilter);
        if (!hardwareInput)
        {
            if (input.DeinterlaceFilter is not null)
            {
                main.Add(input.DeinterlaceFilter);
            }

            var scale = SoftwarePipelineRenderer.RenderScaling(input.SoftwareScaling);
            if (input.MjpegOutput && !opencl)
            {
                scale = string.IsNullOrEmpty(scale) ? "scale=out_range=pc" : scale + ":out_range=pc";
            }

            main.Add(scale);
            main.Add("format=" + (opencl ? "yuv420p10le" : input.Subtitles is QsvPipelinePlan.Subtitle.Graphical ? "yuv420p" : "nv12"));
            if (opencl)
            {
                main.Add("hwupload=derive_device=opencl");
            }
        }
        else if (vaapi)
        {
            RenderVaapiInput(main, input, tone);
        }
        else
        {
            RenderD3d11Input(main, input, tone);
        }

        if (opencl && hardwareInput)
        {
            main.Add("hwmap=derive_device=opencl:mode=read");
        }

        if (tone is QsvPipelinePlan.ToneMapping.OpenCl openclTone)
        {
            main.Add(openclTone.Filter);
        }

        var memoryOutput = (hardwareInput && !input.QsvOutput) || (!hardwareInput && opencl);
        if (memoryOutput)
        {
            var map = !input.QsvOutput && (opencl || (vaapi && input.Decoder == QsvPipelinePlan.Surface.Vaapi));
            main.Add(map ? "hwmap=mode=read" : "hwdownload");
            main.Add("format=nv12");
        }

        memoryOutput |= !hardwareInput && input.QsvOutput;
        if (memoryOutput && input.Subtitles is QsvPipelinePlan.Subtitle.Text text)
        {
            main.Add(text.Filter);
        }

        if (hardwareInput && input.QsvOutput)
        {
            if (opencl)
            {
                main.Add(vaapi
                    ? "hwmap=derive_device=qsv:mode=write:reverse=1:extra_hw_frames=16"
                    : "hwmap=derive_device=qsv:mode=write:reverse=1");
                main.Add("format=qsv");
            }
            else if (vaapi && input.Decoder == QsvPipelinePlan.Surface.Vaapi)
            {
                main.Add("hwmap=derive_device=qsv");
                main.Add("format=qsv");
            }

            if (input.Subtitles is not null)
            {
                switch (input.Subtitles)
                {
                    case QsvPipelinePlan.Subtitle.Graphical graphical:
                        subtitles.Add(graphical.PreprocessFilter);
                        subtitles.Add("format=bgra");
                        break;
                    case QsvPipelinePlan.Subtitle.OverlayText overlayText:
                        subtitles.Add(overlayText.AlphaSourceFilter);
                        subtitles.Add("format=bgra");
                        subtitles.Add(overlayText.Filter);
                        break;
                }

                // QSV overlays require a fixed pool, including on low-memory iGPUs.
                subtitles.Add("hwupload=derive_device=qsv:extra_hw_frames=64");
                var size = input.OverlayWidth.HasValue && input.OverlayHeight.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, ":w={0}:h={1}", input.OverlayWidth.Value, input.OverlayHeight.Value)
                    : string.Empty;
                overlays.Add("overlay_qsv=eof_action=pass:repeatlast=0" + size);
            }
        }
        else if (memoryOutput && input.Subtitles is QsvPipelinePlan.Subtitle.Graphical graphical)
        {
            subtitles.Add(graphical.PreprocessFilter);
            overlays.Add("overlay=eof_action=pass:repeatlast=0");
        }

        return (main.ToImmutable(), subtitles.ToImmutable(), overlays.ToImmutable());
    }

    private static void RenderVaapiInput(ImmutableArray<string>.Builder main, QsvPipelinePlan.Inputs input, QsvPipelinePlan.ToneMapping? tone)
    {
        var native = input.Decoder == QsvPipelinePlan.Surface.Vaapi;
        var opencl = tone is QsvPipelinePlan.ToneMapping.OpenCl;
        if (input.DeinterlaceFilter is not null)
        {
            main.Add(input.DeinterlaceFilter);
        }

        if (native && input.TransposeDirection is not null)
        {
            main.Add("transpose_vaapi=dir=" + input.TransposeDirection);
        }

        var scale = input.HardwareScalingFilter;
        if (!string.IsNullOrEmpty(scale))
        {
            if (!native && input.TransposeDirection is not null)
            {
                scale += ":transpose=" + input.TransposeDirection;
            }

            if (input.MjpegOutput)
            {
                scale += ((!native && !input.FullRangeSupported) || opencl) ? string.Empty : ":out_range=pc";
                scale += native ? ":mode=hq" : input.HqScalingSupported ? ":scale_mode=hq" : string.Empty;
            }

            if (native)
            {
                scale += ":extra_hw_frames=24";
            }
        }

        main.Add(scale);
        if (tone is QsvPipelinePlan.ToneMapping.VaapiVpp vpp)
        {
            if (!native)
            {
                main.Add("hwmap=derive_device=vaapi");
                main.Add("format=vaapi");
            }

            main.Add(vpp.Filter);
            if (!native)
            {
                main.Add("hwmap=derive_device=qsv");
                main.Add("format=qsv");
            }
        }
    }

    private static void RenderD3d11Input(ImmutableArray<string>.Builder main, QsvPipelinePlan.Inputs input, QsvPipelinePlan.ToneMapping? tone)
    {
        var native = input.Decoder == QsvPipelinePlan.Surface.D3d11;
        var opencl = tone is QsvPipelinePlan.ToneMapping.OpenCl;
        var scale = input.HardwareScalingFilter;
        if (!string.IsNullOrEmpty(scale))
        {
            if (native)
            {
                // D3D11 has a fixed decoder pool: relay via VPP before asynchronous encoding.
                scale += ":passthrough=0";
            }

            if (input.TransposeDirection is not null)
            {
                scale += ":transpose=" + input.TransposeDirection;
            }

            if (input.MjpegOutput)
            {
                scale += input.FullRangeSupported && !opencl ? ":out_range=pc" : string.Empty;
                scale += input.HqScalingSupported ? ":scale_mode=hq" : string.Empty;
            }

            if (tone is QsvPipelinePlan.ToneMapping.WindowsVpp vpp)
            {
                var procamp = RenderProcamp(vpp);
                scale += procamp.Length > 0 ? procamp : vpp.SecondPass ? string.Empty : ":tonemap=1";
            }
        }

        if (native && (!string.IsNullOrEmpty(scale) || input.DeinterlaceFilter is not null))
        {
            main.Add("hwmap=derive_device=qsv");
        }

        if (input.DeinterlaceFilter is not null)
        {
            main.Add(input.DeinterlaceFilter);
        }

        main.Add(scale);
        if (tone is QsvPipelinePlan.ToneMapping.WindowsVpp windowsVpp)
        {
            if (windowsVpp.SecondPass)
            {
                main.Add("vpp_qsv=tonemap=1:format=nv12:async_depth=2");
            }

            main.Add(windowsVpp.OutputColorFilter);
        }
    }

    private static string RenderProcamp(QsvPipelinePlan.ToneMapping.WindowsVpp tone)
    {
        var result = string.Empty;
        if (tone.Brightness is { } brightness)
        {
            result += ":brightness=" + brightness.ToString(CultureInfo.InvariantCulture);
        }

        if (tone.Contrast is { } contrast)
        {
            result += ":contrast=" + contrast.ToString(CultureInfo.InvariantCulture);
        }

        return result.Length > 0 ? result + ":procamp=1:async_depth=2" : string.Empty;
    }
}
