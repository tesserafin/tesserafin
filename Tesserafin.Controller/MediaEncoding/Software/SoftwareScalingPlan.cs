using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding.Software;

/// <summary>
/// Frozen scaling requirements. The encoder is already selected; dimensions are fixed values or
/// ceilings from the execution request. No job, configuration or capability service is retained.
/// </summary>
internal sealed record SoftwareScalingPlan(
    string VideoEncoder,
    Video3DFormat? ThreeDFormat,
    int? Width,
    int? Height,
    int? MaxWidth,
    int? MaxHeight);
