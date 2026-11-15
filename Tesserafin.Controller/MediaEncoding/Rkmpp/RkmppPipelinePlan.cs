using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.Rkmpp;

/// <summary>Captured Rockchip strategy with resolved geometry and capability decisions.</summary>
internal abstract record RkmppPipelinePlan
{
    private RkmppPipelinePlan()
    {
    }

    /// <summary>The shared software/copy-back implementation.</summary>
    internal sealed record Software(SoftwarePipelinePlan Pipeline) : RkmppPipelinePlan;

    /// <summary>Immutable DRM/RGA/OpenCL requirements.</summary>
    internal sealed record Hardware(
        bool HardwareInput,
        bool HardwareOutput,
        bool MjpegOutput,
        bool EncoderSupportsAfbc,
        string ColorFilter,
        string? SoftwareDeinterlaceFilter,
        SoftwareScalingPlan SoftwareScaling,
        string? TransposeDirection,
        string HardwareScalingFilter,
        bool ScalingNeeded,
        bool SinglePassScalingSupported,
        string? ToneMappingFilter,
        Subtitle? Subtitles,
        int? OverlayWidth,
        int? OverlayHeight) : RkmppPipelinePlan;

    /// <summary>Exclusive subtitle input variants.</summary>
    internal abstract record Subtitle
    {
        private Subtitle()
        {
        }

        /// <summary>A resolved text filter on a memory surface.</summary>
        internal sealed record Text(string Filter) : Subtitle;

        /// <summary>A resolved alpha source and text filter for RGA overlay.</summary>
        internal sealed record OverlayText(string AlphaSourceFilter, string Filter) : Subtitle;

        /// <summary>Resolved graphical subtitle preprocessing geometry.</summary>
        internal sealed record Graphical(string PreprocessFilter) : Subtitle;
    }
}
