using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.Nvidia;

/// <summary>
/// Captured NVIDIA filter requirements. Device/codec selection and capability probing are
/// complete before this value is created. Only immutable values cross the rendering boundary.
/// </summary>
internal sealed record NvidiaPipelinePlan(
    bool CudaInput,
    bool NvencOutput,
    string ColorFilter,
    string? DeinterlaceFilter,
    string? TransposeDirection,
    SoftwareScalingPlan SoftwareScaling,
    string CudaScalingFilter,
    string? TonemapFilter,
    NvidiaPipelinePlan.Subtitle? Subtitles)
{
    /// <summary>
    /// The exclusive subtitle input selected for burn-in.
    /// </summary>
    internal abstract record Subtitle
    {
        private Subtitle()
        {
        }

        /// <summary>
        /// A resolved text-subtitle filter applied to a memory surface.
        /// </summary>
        internal sealed record Text(string Filter) : Subtitle;

        /// <summary>
        /// Resolved alpha source and text filter for a CUDA overlay surface.
        /// </summary>
        internal sealed record CudaText(string Filter, string AlphaSourceFilter, bool PremultipliedAlpha) : Subtitle;

        /// <summary>
        /// Resolved graphical subtitle geometry.
        /// </summary>
        internal sealed record Graphical(string PreprocessFilter) : Subtitle;
    }
}
