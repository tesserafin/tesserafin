namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// Optional, operationally probed software capabilities. Kept separate from
/// <see cref="IMediaEncoder"/> so existing implementations remain compatible.
/// </summary>
public interface ISoftwareVideoEncoderCapabilities
{
    /// <summary>
    /// Gets a value indicating whether the configured encoder successfully
    /// encoded a strict HEVC Main10 probe during this server start.
    /// </summary>
    bool SupportsHevcMain10 { get; }
}
