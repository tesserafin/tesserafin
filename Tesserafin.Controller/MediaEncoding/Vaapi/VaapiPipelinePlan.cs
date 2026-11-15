using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.Vaapi;

/// <summary>
/// A selected VAAPI rendering strategy. Capabilities, device identity, configuration and paths
/// are resolved before capture. Rendering cannot select a different device or playback plan.
/// </summary>
internal abstract record VaapiPipelinePlan
{
    private VaapiPipelinePlan()
    {
    }

    /// <summary>
    /// The import sequence selected from FFmpeg support and DRM modifier capabilities.
    /// </summary>
    internal enum VulkanImport
    {
        /// <summary>Direct VAAPI to Vulkan import.</summary>
        DirectVulkan,

        /// <summary>Explicit VAAPI to DRM to Vulkan import.</summary>
        DrmVulkan,

        /// <summary>Explicit import followed by a Vulkan surface copy.</summary>
        DrmVulkanCopy,

        /// <summary>DRM Prime input accepted directly by libplacebo.</summary>
        DrmPrime,
    }

    /// <summary>
    /// Software/copy-back filtering, with an optional final upload for a VAAPI encoder.
    /// </summary>
    internal sealed record Software(SoftwarePipelinePlan Pipeline, bool VaapiOutput) : VaapiPipelinePlan;

    /// <summary>
    /// Intel iHD filtering with exclusive VPP or OpenCL tone mapping and VAAPI overlays.
    /// </summary>
    internal sealed record Intel(Inputs Input, IntelToneMapping? ToneMapping, int? OverlayWidth, int? OverlayHeight) : VaapiPipelinePlan;

    /// <summary>
    /// AMD Vulkan/DRM filtering and overlays with a captured import strategy.
    /// </summary>
    internal sealed record AmdVulkan(Inputs Input, bool ToneMapping, string? LibplaceboFilter, VulkanImport Import) : VaapiPipelinePlan;

    /// <summary>
    /// Limited VAAPI filtering with memory overlays and optional OpenCL tone mapping.
    /// </summary>
    internal sealed record Limited(Inputs Input, bool I965Interop, string? OpenClToneMapping) : VaapiPipelinePlan;

    /// <summary>
    /// Frozen common filter requirements; geometry strings come from shared pure helpers.
    /// </summary>
    internal sealed record Inputs(
        bool VaapiInput,
        bool VaapiOutput,
        bool MjpegOutput,
        string ColorFilter,
        string? DeinterlaceFilter,
        string? TransposeDirection,
        SoftwareScalingPlan SoftwareScaling,
        string HardwareScalingFilter,
        Subtitle? Subtitles);

    /// <summary>
    /// A single resolved Intel tone-mapping mode: VPP or OpenCL.
    /// </summary>
    internal sealed record IntelToneMapping(bool Vpp, string Filter);

    /// <summary>
    /// Exclusive subtitle input variants for memory or hardware composition.
    /// </summary>
    internal abstract record Subtitle
    {
        private Subtitle()
        {
        }

        /// <summary>
        /// A resolved text filter on a memory surface.
        /// </summary>
        internal sealed record Text(string Filter) : Subtitle;

        /// <summary>
        /// A resolved alpha source and text filter for a hardware overlay.
        /// </summary>
        internal sealed record OverlayText(string AlphaSourceFilter, string Filter) : Subtitle;

        /// <summary>
        /// Resolved graphical subtitle preprocessing geometry.
        /// </summary>
        internal sealed record Graphical(string PreprocessFilter) : Subtitle;
    }
}
