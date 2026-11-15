using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.VideoToolbox;

/// <summary>Captured VideoToolbox/Metal strategy with no runtime service references.</summary>
internal abstract record VideoToolboxPipelinePlan
{
    private VideoToolboxPipelinePlan()
    {
    }

    /// <summary>The shared software/copy-back implementation.</summary>
    internal sealed record Software(SoftwarePipelinePlan Pipeline) : VideoToolboxPipelinePlan;

    /// <summary>Immutable surface, filtering and subtitle requirements.</summary>
    internal sealed record Hardware(
        bool HardwareOutput,
        bool UsingHardwareSurface,
        string? DeinterlaceFilter,
        string? TransposeDirection,
        string ScalingFilter,
        ToneMapping? Tone,
        Subtitle? Subtitles) : VideoToolboxPipelinePlan;

    /// <summary>Exclusive native or Metal tone mapping.</summary>
    internal abstract record ToneMapping
    {
        private ToneMapping()
        {
        }

        /// <summary>Native scale_vt tone mapping.</summary>
        internal sealed record Native : ToneMapping;

        /// <summary>Resolved Metal tone-mapping filter.</summary>
        internal sealed record Metal(string Filter) : ToneMapping;
    }

    /// <summary>Exclusive hardware overlay inputs.</summary>
    internal abstract record Subtitle
    {
        private Subtitle()
        {
        }

        /// <summary>Resolved alpha source and text filter.</summary>
        internal sealed record Text(string AlphaSourceFilter, string Filter) : Subtitle;

        /// <summary>Resolved graphical preprocessing geometry.</summary>
        internal sealed record Graphical(string PreprocessFilter) : Subtitle;
    }
}
