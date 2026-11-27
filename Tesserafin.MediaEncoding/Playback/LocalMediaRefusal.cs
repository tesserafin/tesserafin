namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// Why <see cref="LocalMediaPlanner"/> refused a session instead of deciding it: each is a refusal,
/// never a hand-over to the legacy planner.
/// </summary>
public enum LocalMediaRefusal
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>The request names no media source to decide on.</summary>
    NoSource,

    /// <summary>A live session's source is not the stream the session holds.</summary>
    NotTheHeldLiveStream,

    /// <summary>
    /// A non-local source no session provider reads, or a disc image this server's FFmpeg cannot
    /// open (or whose kind the scan did not recognise).
    /// </summary>
    NoSessionProvider,

    /// <summary>The only output the client accepts is an HDR-to-SDR tone mapping this server's FFmpeg cannot perform.</summary>
    ToneMappingUnavailable,

    /// <summary>The only transcode the client allows encodes to a target other than H.264/AAC.</summary>
    TranscodeTargetNotExecuted,
}
