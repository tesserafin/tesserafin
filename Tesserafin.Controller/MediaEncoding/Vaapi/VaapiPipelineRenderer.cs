using System;
using System.Collections.Immutable;
using System.Globalization;
using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.Vaapi;

/// <summary>
/// Renders captured VAAPI strategies without accessing jobs, configuration, hardware or I/O.
/// Surface transfers and filter ordering are owned here rather than by orchestration code.
/// </summary>
internal static class VaapiPipelineRenderer
{
    /// <summary>
    /// Renders the selected strategy into immutable main, subtitle and overlay sequences.
    /// </summary>
    /// <param name="plan">The fully captured pipeline.</param>
    /// <returns>The three immutable filter sequences.</returns>
    internal static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) Render(VaapiPipelinePlan plan)
        => plan switch
        {
            VaapiPipelinePlan.Software software => RenderSoftware(software),
            VaapiPipelinePlan.Intel intel => RenderIntel(intel),
            VaapiPipelinePlan.AmdVulkan amd => RenderAmd(amd),
            VaapiPipelinePlan.Limited limited => RenderLimited(limited),
            _ => throw new ArgumentException("Unknown VAAPI rendering strategy.", nameof(plan)),
        };

    private static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) RenderSoftware(VaapiPipelinePlan.Software plan)
    {
        var filters = SoftwarePipelineRenderer.Render(plan.Pipeline);
        if (!plan.VaapiOutput)
        {
            return filters;
        }

        return filters.OverlayFilters.IsEmpty
            ? (filters.MainFilters.Add("hwupload=derive_device=vaapi"), filters.SubFilters, filters.OverlayFilters)
            : (filters.MainFilters, filters.SubFilters, filters.OverlayFilters.Add("hwupload=derive_device=vaapi"));
    }

    private static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) RenderIntel(VaapiPipelinePlan.Intel plan)
    {
        var input = plan.Input;
        var isVaapiDecoder = input.VaapiInput;
        var isVaapiEncoder = input.VaapiOutput;
        var isSwDecoder = !isVaapiDecoder;
        var isSwEncoder = !isVaapiEncoder;
        var isMjpegEncoder = input.MjpegOutput;
        var doDeintH2645 = input.DeinterlaceFilter is not null;
        var hasSubs = input.Subtitles is not null;
        var hasTextSubs = input.Subtitles is VaapiPipelinePlan.Subtitle.Text or VaapiPipelinePlan.Subtitle.OverlayText;
        var hasGraphicalSubs = input.Subtitles is VaapiPipelinePlan.Subtitle.Graphical;
        var isVaInVaOut = isVaapiDecoder && isVaapiEncoder;
        var doVaVppTonemap = plan.ToneMapping is { Vpp: true };
        var doOclTonemap = plan.ToneMapping is { Vpp: false };
        var doVaVppTranspose = input.TransposeDirection is not null;
        var transposeDir = input.TransposeDirection;

        /* Make main filters for video stream */
        var mainFilters = ImmutableArray.CreateBuilder<string>();

        mainFilters.Add(input.ColorFilter);

        if (isSwDecoder)
        {
            // INPUT sw surface(memory)
            // sw deint
            if (doDeintH2645)
            {
                var swDeintFilter = input.DeinterlaceFilter!;
                mainFilters.Add(swDeintFilter);
            }

            var outFormat = doOclTonemap ? "yuv420p10le" : "nv12";
            var swScaleFilter = SoftwarePipelineRenderer.RenderScaling(input.SoftwareScaling);
            if (isMjpegEncoder && !doOclTonemap)
            {
                // sw decoder + hw mjpeg encoder
                swScaleFilter = string.IsNullOrEmpty(swScaleFilter) ? "scale=out_range=pc" : $"{swScaleFilter}:out_range=pc";
            }

            // sw scale
            mainFilters.Add(swScaleFilter);
            mainFilters.Add($"format={outFormat}");

            // keep video at memory except ocl tonemap,
            // since the overhead caused by hwupload >>> using sw filter.
            // sw => hw
            if (doOclTonemap)
            {
                mainFilters.Add("hwupload=derive_device=opencl");
            }
        }
        else if (isVaapiDecoder)
        {
            // INPUT vaapi surface(vram)
            // hw deint
            if (doDeintH2645)
            {
                var deintFilter = input.DeinterlaceFilter!;
                mainFilters.Add(deintFilter);
            }

            // hw transpose
            if (doVaVppTranspose)
            {
                mainFilters.Add($"transpose_vaapi=dir={transposeDir}");
            }

            var hwScaleFilter = input.HardwareScalingFilter;

            if (!string.IsNullOrEmpty(hwScaleFilter) && isMjpegEncoder)
            {
                hwScaleFilter += doOclTonemap ? string.Empty : ":out_range=pc";
                hwScaleFilter += ":mode=hq";
            }

            // allocate extra pool sizes for vaapi vpp
            if (!string.IsNullOrEmpty(hwScaleFilter))
            {
                hwScaleFilter += ":extra_hw_frames=24";
            }

            // hw scale
            mainFilters.Add(hwScaleFilter);
        }

        // vaapi vpp tonemap
        if (doVaVppTonemap && isVaapiDecoder)
        {
            var tonemapFilter = plan.ToneMapping!.Filter;
            mainFilters.Add(tonemapFilter);
        }

        if (doOclTonemap && isVaapiDecoder)
        {
            // map from vaapi to opencl via vaapi-opencl interop(Intel only).
            mainFilters.Add("hwmap=derive_device=opencl:mode=read");
        }

        // ocl tonemap
        if (doOclTonemap)
        {
            var tonemapFilter = plan.ToneMapping!.Filter;
            mainFilters.Add(tonemapFilter);
        }

        if (doOclTonemap && isVaInVaOut)
        {
            // OUTPUT vaapi(nv12) surface(vram)
            // reverse-mapping via vaapi-opencl interop.
            mainFilters.Add("hwmap=derive_device=vaapi:mode=write:reverse=1");
            mainFilters.Add("format=vaapi");
        }

        var memoryOutput = false;
        var isUploadForOclTonemap = isSwDecoder && doOclTonemap;
        var isHwmapNotUsable = isUploadForOclTonemap && isVaapiEncoder;
        if ((isVaapiDecoder && isSwEncoder) || isUploadForOclTonemap)
        {
            memoryOutput = true;

            // OUTPUT nv12 surface(memory)
            // prefer hwmap to hwdownload on opencl/vaapi.
            mainFilters.Add(isHwmapNotUsable ? "hwdownload" : "hwmap=mode=read");
            mainFilters.Add("format=nv12");
        }

        // OUTPUT nv12 surface(memory)
        if (isSwDecoder && isVaapiEncoder)
        {
            memoryOutput = true;
        }

        if (memoryOutput)
        {
            // text subtitles
            if (hasTextSubs)
            {
                var textSubtitlesFilter = ((VaapiPipelinePlan.Subtitle.Text)input.Subtitles!).Filter;
                mainFilters.Add(textSubtitlesFilter);
            }
        }

        if (memoryOutput && isVaapiEncoder)
        {
            if (!hasGraphicalSubs)
            {
                mainFilters.Add("hwupload_vaapi");
            }
        }

        /* Make sub and overlay filters for subtitle stream */
        var subFilters = ImmutableArray.CreateBuilder<string>();
        var overlayFilters = ImmutableArray.CreateBuilder<string>();
        if (isVaInVaOut)
        {
            if (hasSubs)
            {
                if (hasGraphicalSubs)
                {
                    // overlay_vaapi can handle overlay scaling, setup a smaller height to reduce transfer overhead
                    var subPreProcFilters = ((VaapiPipelinePlan.Subtitle.Graphical)input.Subtitles!).PreprocessFilter;
                    subFilters.Add(subPreProcFilters);
                    subFilters.Add("format=bgra");
                }
                else if (hasTextSubs)
                {
                    var alphaSrcFilter = ((VaapiPipelinePlan.Subtitle.OverlayText)input.Subtitles!).AlphaSourceFilter;
                    var subTextSubtitlesFilter = ((VaapiPipelinePlan.Subtitle.OverlayText)input.Subtitles!).Filter;
                    subFilters.Add(alphaSrcFilter);
                    subFilters.Add("format=bgra");
                    subFilters.Add(subTextSubtitlesFilter);
                }

                subFilters.Add("hwupload=derive_device=vaapi");

                var overlayW = plan.OverlayWidth;
                var overlayH = plan.OverlayHeight;
                var overlaySize = (overlayW.HasValue && overlayH.HasValue)
                    ? $":w={overlayW.Value}:h={overlayH.Value}"
                    : string.Empty;
                var overlayVaapiFilter = string.Format(
                    CultureInfo.InvariantCulture,
                    "overlay_vaapi=eof_action=pass:repeatlast=0{0}",
                    overlaySize);
                overlayFilters.Add(overlayVaapiFilter);
            }
        }
        else if (memoryOutput)
        {
            if (hasGraphicalSubs)
            {
                var subPreProcFilters = ((VaapiPipelinePlan.Subtitle.Graphical)input.Subtitles!).PreprocessFilter;
                subFilters.Add(subPreProcFilters);
                overlayFilters.Add("overlay=eof_action=pass:repeatlast=0");

                if (isVaapiEncoder)
                {
                    overlayFilters.Add("hwupload_vaapi");
                }
            }
        }

        return (mainFilters.ToImmutable(), subFilters.ToImmutable(), overlayFilters.ToImmutable());
    }

    private static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) RenderAmd(VaapiPipelinePlan.AmdVulkan plan)
    {
        var input = plan.Input;
        var isVaapiDecoder = input.VaapiInput;
        var isVaapiEncoder = input.VaapiOutput;
        var isSwDecoder = !isVaapiDecoder;
        var isSwEncoder = !isVaapiEncoder;
        var isMjpegEncoder = input.MjpegOutput;
        var doDeintH2645 = input.DeinterlaceFilter is not null;
        var hasSubs = input.Subtitles is not null;
        var hasTextSubs = input.Subtitles is VaapiPipelinePlan.Subtitle.Text or VaapiPipelinePlan.Subtitle.OverlayText;
        var hasGraphicalSubs = input.Subtitles is VaapiPipelinePlan.Subtitle.Graphical;
        var doVkTonemap = plan.ToneMapping;
        var doVkTranspose = input.TransposeDirection is not null;
        var transposeDir = input.TransposeDirection;

        /* Make main filters for video stream */
        var mainFilters = ImmutableArray.CreateBuilder<string>();

        mainFilters.Add(input.ColorFilter);

        if (isSwDecoder)
        {
            // INPUT sw surface(memory)
            // sw deint
            if (doDeintH2645)
            {
                var swDeintFilter = input.DeinterlaceFilter!;
                mainFilters.Add(swDeintFilter);
            }

            if (doVkTonemap || hasSubs)
            {
                // sw => hw
                mainFilters.Add("hwupload=derive_device=vulkan");
                mainFilters.Add("format=vulkan");
            }
            else
            {
                // sw scale
                var swScaleFilter = SoftwarePipelineRenderer.RenderScaling(input.SoftwareScaling);
                mainFilters.Add(swScaleFilter);
                mainFilters.Add("format=nv12");
            }
        }
        else if (isVaapiDecoder)
        {
            // INPUT vaapi surface(vram)
            if (doVkTranspose || doVkTonemap || hasSubs)
            {
                // map from vaapi to vulkan/drm via interop (Polaris/gfx8+).
                if (plan.Import != VaapiPipelinePlan.VulkanImport.DirectVulkan)
                {
                    if (plan.Import is VaapiPipelinePlan.VulkanImport.DrmVulkan or VaapiPipelinePlan.VulkanImport.DrmVulkanCopy)
                    {
                        // disable the indirect va-drm-vk mapping since it's no longer reliable.
                        mainFilters.Add("hwmap=derive_device=drm");
                        mainFilters.Add("format=drm_prime");
                        mainFilters.Add("hwmap=derive_device=vulkan");
                        mainFilters.Add("format=vulkan");

                        // workaround for libplacebo using the imported vulkan frame on gfx8.
                        if (plan.Import == VaapiPipelinePlan.VulkanImport.DrmVulkanCopy)
                        {
                            mainFilters.Add("scale_vulkan");
                        }
                    }
                    else if (doVkTonemap || hasSubs)
                    {
                        // non ad-hoc libplacebo also accepts drm_prime direct input.
                        mainFilters.Add("hwmap=derive_device=drm");
                        mainFilters.Add("format=drm_prime");
                    }
                }
                else // legacy va-vk mapping that works only in jellyfin-ffmpeg6
                {
                    mainFilters.Add("hwmap=derive_device=vulkan");
                    mainFilters.Add("format=vulkan");
                }
            }
            else
            {
                // hw deint
                if (doDeintH2645)
                {
                    var deintFilter = input.DeinterlaceFilter!;
                    mainFilters.Add(deintFilter);
                }

                // hw scale
                var hwScaleFilter = input.HardwareScalingFilter;

                if (!string.IsNullOrEmpty(hwScaleFilter) && isMjpegEncoder && !doVkTonemap)
                {
                    hwScaleFilter += ":out_range=pc:mode=hq";
                }

                mainFilters.Add(hwScaleFilter);
            }
        }

        // vk transpose
        if (doVkTranspose)
        {
            if (string.Equals(transposeDir, "reversal", StringComparison.OrdinalIgnoreCase))
            {
                mainFilters.Add("flip_vulkan");
            }
            else
            {
                mainFilters.Add($"transpose_vulkan=dir={transposeDir}");
            }
        }

        // vk libplacebo
        if (doVkTonemap || hasSubs)
        {
            var libplaceboFilter = plan.LibplaceboFilter!;
            mainFilters.Add(libplaceboFilter);
            mainFilters.Add("format=vulkan");
        }

        if (doVkTonemap && !hasSubs)
        {
            // OUTPUT vaapi(nv12) surface(vram)
            // map from vulkan/drm to vaapi via interop (Polaris/gfx8+).
            mainFilters.Add("hwmap=derive_device=vaapi");
            mainFilters.Add("format=vaapi");

            // clear the surf->meta_offset and output nv12
            mainFilters.Add("scale_vaapi=format=nv12");

            // hw deint
            if (doDeintH2645)
            {
                var deintFilter = input.DeinterlaceFilter!;
                mainFilters.Add(deintFilter);
            }
        }

        if (!hasSubs)
        {
            // OUTPUT nv12 surface(memory)
            if (isSwEncoder && (doVkTonemap || isVaapiDecoder))
            {
                mainFilters.Add("hwdownload");
                mainFilters.Add("format=nv12");
            }

            if (isSwDecoder && isVaapiEncoder && !doVkTonemap)
            {
                mainFilters.Add("hwupload_vaapi");
            }
        }

        /* Make sub and overlay filters for subtitle stream */
        var subFilters = ImmutableArray.CreateBuilder<string>();
        var overlayFilters = ImmutableArray.CreateBuilder<string>();
        if (hasSubs)
        {
            if (hasGraphicalSubs)
            {
                var subPreProcFilters = ((VaapiPipelinePlan.Subtitle.Graphical)input.Subtitles!).PreprocessFilter;
                subFilters.Add(subPreProcFilters);
                subFilters.Add("format=bgra");
            }
            else if (hasTextSubs)
            {
                var alphaSrcFilter = ((VaapiPipelinePlan.Subtitle.OverlayText)input.Subtitles!).AlphaSourceFilter;
                var subTextSubtitlesFilter = ((VaapiPipelinePlan.Subtitle.OverlayText)input.Subtitles!).Filter;
                subFilters.Add(alphaSrcFilter);
                subFilters.Add("format=bgra");
                subFilters.Add(subTextSubtitlesFilter);
            }

            subFilters.Add("hwupload=derive_device=vulkan");
            subFilters.Add("format=vulkan");

            overlayFilters.Add("overlay_vulkan=eof_action=pass:repeatlast=0");

            if (isSwEncoder)
            {
                // OUTPUT nv12 surface(memory)
                overlayFilters.Add("scale_vulkan=format=nv12");
                overlayFilters.Add("hwdownload");
                overlayFilters.Add("format=nv12");
            }
            else if (isVaapiEncoder)
            {
                // OUTPUT vaapi(nv12) surface(vram)
                // map from vulkan/drm to vaapi via interop (Polaris/gfx8+).
                overlayFilters.Add("hwmap=derive_device=vaapi");
                overlayFilters.Add("format=vaapi");

                // clear the surf->meta_offset and output nv12
                overlayFilters.Add("scale_vaapi=format=nv12");

                // hw deint
                if (doDeintH2645)
                {
                    var deintFilter = input.DeinterlaceFilter!;
                    overlayFilters.Add(deintFilter);
                }
            }
        }

        return (mainFilters.ToImmutable(), subFilters.ToImmutable(), overlayFilters.ToImmutable());
    }

    private static (ImmutableArray<string> MainFilters, ImmutableArray<string> SubFilters, ImmutableArray<string> OverlayFilters) RenderLimited(VaapiPipelinePlan.Limited plan)
    {
        var input = plan.Input;
        var isVaapiDecoder = input.VaapiInput;
        var isVaapiEncoder = input.VaapiOutput;
        var isSwDecoder = !isVaapiDecoder;
        var isSwEncoder = !isVaapiEncoder;
        var isMjpegEncoder = input.MjpegOutput;
        var doDeintH2645 = input.DeinterlaceFilter is not null;
        var hasSubs = input.Subtitles is not null;
        var hasTextSubs = input.Subtitles is VaapiPipelinePlan.Subtitle.Text or VaapiPipelinePlan.Subtitle.OverlayText;
        var hasGraphicalSubs = input.Subtitles is VaapiPipelinePlan.Subtitle.Graphical;
        var isVaInVaOut = isVaapiDecoder && isVaapiEncoder;
        var isi965Driver = plan.I965Interop;
        var doOclTonemap = plan.OpenClToneMapping is not null;

        /* Make main filters for video stream */
        var mainFilters = ImmutableArray.CreateBuilder<string>();

        mainFilters.Add(input.ColorFilter);

        if (isSwDecoder)
        {
            // INPUT sw surface(memory)
            // sw deint
            if (doDeintH2645)
            {
                var swDeintFilter = input.DeinterlaceFilter!;
                mainFilters.Add(swDeintFilter);
            }

            var outFormat = doOclTonemap ? "yuv420p10le" : "nv12";
            var swScaleFilter = SoftwarePipelineRenderer.RenderScaling(input.SoftwareScaling);
            if (isMjpegEncoder && !doOclTonemap)
            {
                // sw decoder + hw mjpeg encoder
                swScaleFilter = string.IsNullOrEmpty(swScaleFilter) ? "scale=out_range=pc" : $"{swScaleFilter}:out_range=pc";
            }

            // sw scale
            mainFilters.Add(swScaleFilter);
            mainFilters.Add("format=" + outFormat);

            // keep video at memory except ocl tonemap,
            // since the overhead caused by hwupload >>> using sw filter.
            // sw => hw
            if (doOclTonemap)
            {
                mainFilters.Add("hwupload=derive_device=opencl");
            }
        }
        else if (isVaapiDecoder)
        {
            // INPUT vaapi surface(vram)
            // hw deint
            if (doDeintH2645)
            {
                var deintFilter = input.DeinterlaceFilter!;
                mainFilters.Add(deintFilter);
            }

            var hwScaleFilter = input.HardwareScalingFilter;

            if (!string.IsNullOrEmpty(hwScaleFilter) && isMjpegEncoder)
            {
                hwScaleFilter += doOclTonemap ? string.Empty : ":out_range=pc";
                hwScaleFilter += ":mode=hq";
            }

            // allocate extra pool sizes for vaapi vpp
            if (!string.IsNullOrEmpty(hwScaleFilter))
            {
                hwScaleFilter += ":extra_hw_frames=24";
            }

            // hw scale
            mainFilters.Add(hwScaleFilter);
        }

        if (doOclTonemap && isVaapiDecoder)
        {
            if (isi965Driver)
            {
                // map from vaapi to opencl via vaapi-opencl interop(Intel only).
                mainFilters.Add("hwmap=derive_device=opencl");
            }
            else
            {
                mainFilters.Add("hwdownload");
                mainFilters.Add("format=p010le");
                mainFilters.Add("hwupload=derive_device=opencl");
            }
        }

        // ocl tonemap
        if (doOclTonemap)
        {
            var tonemapFilter = plan.OpenClToneMapping!;
            mainFilters.Add(tonemapFilter);
        }

        if (doOclTonemap && isVaInVaOut)
        {
            if (isi965Driver)
            {
                // OUTPUT vaapi(nv12) surface(vram)
                // reverse-mapping via vaapi-opencl interop.
                mainFilters.Add("hwmap=derive_device=vaapi:reverse=1");
                mainFilters.Add("format=vaapi");
            }
        }

        var memoryOutput = false;
        var isUploadForOclTonemap = doOclTonemap && (isSwDecoder || (isVaapiDecoder && !isi965Driver));
        var isHwmapNotUsable = hasGraphicalSubs || isUploadForOclTonemap;
        var isHwmapForSubs = hasSubs && isVaapiDecoder;
        var isHwUnmapForTextSubs = hasTextSubs && isVaInVaOut && !isUploadForOclTonemap;
        if ((isVaapiDecoder && isSwEncoder) || isUploadForOclTonemap || isHwmapForSubs)
        {
            memoryOutput = true;

            // OUTPUT nv12 surface(memory)
            // prefer hwmap to hwdownload on opencl/vaapi.
            mainFilters.Add(isHwmapNotUsable ? "hwdownload" : "hwmap");
            mainFilters.Add("format=nv12");
        }

        // OUTPUT nv12 surface(memory)
        if (isSwDecoder && isVaapiEncoder)
        {
            memoryOutput = true;
        }

        if (memoryOutput)
        {
            // text subtitles
            if (hasTextSubs)
            {
                var textSubtitlesFilter = ((VaapiPipelinePlan.Subtitle.Text)input.Subtitles!).Filter;
                mainFilters.Add(textSubtitlesFilter);
            }
        }

        if (isHwUnmapForTextSubs)
        {
            mainFilters.Add("hwmap");
            mainFilters.Add("format=vaapi");
        }
        else if (memoryOutput && isVaapiEncoder)
        {
            if (!hasGraphicalSubs)
            {
                mainFilters.Add("hwupload_vaapi");
            }
        }

        /* Make sub and overlay filters for subtitle stream */
        var subFilters = ImmutableArray.CreateBuilder<string>();
        var overlayFilters = ImmutableArray.CreateBuilder<string>();
        if (memoryOutput)
        {
            if (hasGraphicalSubs)
            {
                var subPreProcFilters = ((VaapiPipelinePlan.Subtitle.Graphical)input.Subtitles!).PreprocessFilter;
                subFilters.Add(subPreProcFilters);
                overlayFilters.Add("overlay=eof_action=pass:repeatlast=0");

                if (isVaapiEncoder)
                {
                    overlayFilters.Add("hwupload_vaapi");
                }
            }
        }

        return (mainFilters.ToImmutable(), subFilters.ToImmutable(), overlayFilters.ToImmutable());
    }
}
