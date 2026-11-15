using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.Amf;

/// <summary>Resolved AMF strategy; configuration and device probes stay outside rendering.</summary>
internal abstract record AmfPipelinePlan
{
    private AmfPipelinePlan()
    {
    }

    /// <summary>The single shared software/copy-back implementation.</summary>
    internal sealed record Software(SoftwarePipelinePlan Pipeline) : AmfPipelinePlan;

    /// <summary>Captured D3D11/OpenCL surface and filter requirements.</summary>
    internal sealed record Hardware(
        bool D3d11Input,
        bool AmfOutput,
        string ColorFilter,
        string? DeinterlaceFilter,
        SoftwareScalingPlan SoftwareScaling,
        string? TransposeDirection,
        string HardwareScalingFilter,
        string? ToneMappingFilter,
        Subtitle? Subtitles) : AmfPipelinePlan;

    /// <summary>Exclusive subtitle delivery inputs.</summary>
    internal abstract record Subtitle
    {
        private Subtitle()
        {
        }

        /// <summary>A memory-surface text filter.</summary>
        internal sealed record Text(string Filter) : Subtitle;

        /// <summary>A resolved alpha source and text filter for OpenCL composition.</summary>
        internal sealed record OverlayText(string AlphaSourceFilter, string Filter, bool PremultipliedAlpha) : Subtitle;

        /// <summary>Resolved graphical subtitle geometry.</summary>
        internal sealed record Graphical(string PreprocessFilter) : Subtitle;
    }
}
