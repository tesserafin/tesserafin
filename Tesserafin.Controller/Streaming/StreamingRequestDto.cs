using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.Controller.Streaming;

/// <summary>
/// The audio streaming request dto.
/// </summary>
public class StreamingRequestDto : BaseEncodingJobOptions
{
    /// <summary>
    /// Gets or sets the params.
    /// </summary>
    public string? Params { get; set; }

    /// <summary>
    /// Gets or sets the play session id.
    /// </summary>
    public string? PlaySessionId { get; set; }

    /// <summary>
    /// Gets or sets the tag.
    /// </summary>
    public string? Tag { get; set; }

    /// <summary>
    /// Gets or sets the segment container.
    /// </summary>
    public string? SegmentContainer { get; set; }

    /// <summary>
    /// Gets or sets the segment length.
    /// </summary>
    public int? SegmentLength { get; set; }

    /// <summary>
    /// Gets or sets the min segments.
    /// </summary>
    public int? MinSegments { get; set; }

    /// <summary>
    /// Gets or sets the position of the requested segment in ticks.
    /// </summary>
    public long CurrentRuntimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the actual segment length in ticks.
    /// </summary>
    public long ActualSegmentLengthTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this request was built by the server from a
    /// playback session's retained execution plan, rather than bound from a client's query.
    /// </summary>
    public bool BuiltFromRetainedPlan { get; set; }
}
