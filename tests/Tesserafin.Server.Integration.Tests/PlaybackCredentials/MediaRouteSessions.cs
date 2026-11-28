using System;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// The playback sessions the session routes of <see cref="MediaRouteCatalog"/> are addressed
/// through, one per delivery a session serves.
/// </summary>
/// <param name="File">A direct-play session: its <c>File</c> route serves the source as it is.</param>
/// <param name="Progressive">A session whose plan is one progressive MPEG-TS stream.</param>
/// <param name="Hls">A session whose plan is an HLS output.</param>
public sealed record MediaRouteSessions(Guid File, Guid Progressive, Guid Hls)
{
    /// <summary>
    /// Gets placeholder sessions, for listing the routes by name before any session exists.
    /// </summary>
    public static MediaRouteSessions None { get; } = new(Guid.Empty, Guid.Empty, Guid.Empty);
}
