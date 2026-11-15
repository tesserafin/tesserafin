using Tesserafin.Controller.MediaEncoding.Software;

namespace Tesserafin.Controller.MediaEncoding.Qsv;

/// <summary>
/// A captured QSV filtering strategy. Native platform, device capabilities, subtitle paths
/// and configuration are resolved before rendering; no job or service is retained.
/// </summary>
internal abstract record QsvPipelinePlan
{
    private QsvPipelinePlan()
    {
    }

    /// <summary>The decoded frame surface selected at capture.</summary>
    internal enum Surface
    {
        /// <summary>Frames in system memory.</summary>
        Memory,

        /// <summary>Frames on a QSV device.</summary>
        Qsv,

        /// <summary>Frames on a VAAPI device.</summary>
        Vaapi,

        /// <summary>Frames on a D3D11 device.</summary>
        D3d11,
    }

    /// <summary>Software/copy-back composition reuses the single software renderer.</summary>
    internal sealed record Software(SoftwarePipelinePlan Pipeline) : QsvPipelinePlan;

    /// <summary>Linux VAAPI/QSV interop.</summary>
    internal sealed record Vaapi(Inputs Input, ToneMapping? Tone) : QsvPipelinePlan;

    /// <summary>Windows D3D11/QSV interop.</summary>
    internal sealed record D3d11(Inputs Input, ToneMapping? Tone) : QsvPipelinePlan;

    /// <summary>Immutable frame, geometry, filtering and overlay requirements.</summary>
    internal sealed record Inputs(
        Surface Decoder,
        bool QsvOutput,
        bool MjpegOutput,
        string ColorFilter,
        string? DeinterlaceFilter,
        string? TransposeDirection,
        SoftwareScalingPlan SoftwareScaling,
        string HardwareScalingFilter,
        bool FullRangeSupported,
        bool HqScalingSupported,
        Subtitle? Subtitles,
        int? OverlayWidth,
        int? OverlayHeight);

    /// <summary>Exclusive tone-mapping strategies.</summary>
    internal abstract record ToneMapping
    {
        private ToneMapping()
        {
        }

        /// <summary>A resolved OpenCL tone-mapping filter.</summary>
        internal sealed record OpenCl(string Filter) : ToneMapping;

        /// <summary>A resolved Linux VAAPI VPP tone-mapping filter.</summary>
        internal sealed record VaapiVpp(string Filter) : ToneMapping;

        /// <summary>Windows QSV VPP policy and validated optional procamp values.</summary>
        internal sealed record WindowsVpp(bool SecondPass, double? Brightness, double? Contrast, string OutputColorFilter) : ToneMapping;
    }

    /// <summary>Exclusive subtitle delivery inputs.</summary>
    internal abstract record Subtitle
    {
        private Subtitle()
        {
        }

        /// <summary>A resolved text filter on a memory surface.</summary>
        internal sealed record Text(string Filter) : Subtitle;

        /// <summary>A resolved alpha source and text filter for QSV composition.</summary>
        internal sealed record OverlayText(string AlphaSourceFilter, string Filter) : Subtitle;

        /// <summary>Resolved graphical subtitle geometry.</summary>
        internal sealed record Graphical(string PreprocessFilter) : Subtitle;
    }
}
