using System;

namespace Tesserafin.Model.MediaInfo;

/// <summary>
/// Classifies media paths without accessing the file system.
/// </summary>
public static class MediaPath
{
    /// <summary>
    /// Gets the protocol for a media path.
    /// </summary>
    /// <param name="path">The media path.</param>
    /// <returns>The media protocol.</returns>
    public static MediaProtocol GetProtocol(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return MediaProtocol.File;
        }

        if (path.StartsWith("Rtsp", StringComparison.OrdinalIgnoreCase))
        {
            return MediaProtocol.Rtsp;
        }

        if (path.StartsWith("Rtmp", StringComparison.OrdinalIgnoreCase))
        {
            return MediaProtocol.Rtmp;
        }

        if (path.StartsWith("Http", StringComparison.OrdinalIgnoreCase))
        {
            return MediaProtocol.Http;
        }

        if (path.StartsWith("rtp", StringComparison.OrdinalIgnoreCase))
        {
            return MediaProtocol.Rtp;
        }

        if (path.StartsWith("ftp", StringComparison.OrdinalIgnoreCase))
        {
            return MediaProtocol.Ftp;
        }

        if (path.StartsWith("udp", StringComparison.OrdinalIgnoreCase))
        {
            return MediaProtocol.Udp;
        }

        return IsFile(path) ? MediaProtocol.File : MediaProtocol.Http;
    }

    /// <summary>
    /// Determines whether a path refers to a file.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>Whether the path refers to a file.</returns>
    public static bool IsFile(string path)
        => !path.Contains("://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
}
