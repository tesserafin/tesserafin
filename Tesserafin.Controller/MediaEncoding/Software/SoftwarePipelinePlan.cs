namespace Tesserafin.Controller.MediaEncoding.Software;

/// <summary>
/// A fully captured software filter pipeline. Strings are resolved filter inputs, not callbacks
/// into mutable job state. Rendering this plan cannot probe hardware or change encoder selection.
/// </summary>
internal sealed record SoftwarePipelinePlan(
    string PixelFormat,
    string? ColorFilter,
    string? DeinterlaceFilter,
    SoftwareScalingPlan Scaling,
    SoftwarePipelinePlan.ToneMapping? Tonemap,
    SoftwarePipelinePlan.Subtitle? Subtitles)
{
    /// <summary>
    /// Resolved tone-mapping arguments, including the output surface required for DV reshaping.
    /// </summary>
    internal sealed record ToneMapping(
        string Algorithm,
        double Desaturation,
        double Peak,
        double Parameter,
        string? Range,
        string PixelFormat);

    /// <summary>
    /// A selected subtitle delivery pipeline. Text and graphical overlay are exclusive.
    /// </summary>
    internal abstract record Subtitle
    {
        private Subtitle()
        {
        }

        /// <summary>
        /// A resolved text-subtitle burn-in filter, including escaped paths and font settings.
        /// </summary>
        internal sealed record Text(string Filter) : Subtitle;

        /// <summary>
        /// A graphical subtitle input whose preprocessing geometry was resolved at capture time.
        /// </summary>
        internal sealed record Graphical(string PreprocessFilter) : Subtitle;
    }
}
