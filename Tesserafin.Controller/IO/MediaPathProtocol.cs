using System;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;

namespace Tesserafin.Controller.IO;

/// <summary>Classifies media paths independently of their playback sources.</summary>
public static class MediaPathProtocol
{
    /// <summary>Checks whether a path can refer to a local media file.</summary>
    /// <param name="path">The media path.</param>
    /// <param name="fileSystem">The filesystem used to classify paths without a recognized prefix.</param>
    /// <returns>Whether the path is nonempty and uses the file protocol.</returns>
    public static bool IsLocalFile(string? path, IFileSystem fileSystem)
        => !string.IsNullOrEmpty(path) && GetProtocol(path, fileSystem) == MediaProtocol.File;

    /// <summary>Determines the media protocol, using the filesystem for paths with no recognized prefix.</summary>
    /// <param name="path">The media path.</param>
    /// <param name="fileSystem">The filesystem used to classify paths without a recognized prefix.</param>
    /// <returns>The protocol represented by the path.</returns>
    public static MediaProtocol GetProtocol(string? path, IFileSystem fileSystem)
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

        return fileSystem.IsPathFile(path) ? MediaProtocol.File : MediaProtocol.Http;
    }
}
