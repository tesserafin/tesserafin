using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// The live streams (Live TV tuner streams) that playback sessions opened, one per session at
/// most. A session that plays a channel opens its tuner stream when it is created and holds that
/// one reference until the session ends - however it ends: the client's <c>DELETE</c>, a
/// replacement, a legacy stop report, the TTL sweep, a revoked device. Sharing a tuner between
/// sessions is the media source manager's own (each open is one more consumer, each close one
/// less); this registry only makes sure every open is closed exactly once.
/// </summary>
public sealed class PlaybackSessionLiveStreams
{
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<PlaybackSessionLiveStreams> _logger;
    private readonly Dictionary<PlaybackSessionId, string> _bySession = new();
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionLiveStreams"/> class.
    /// </summary>
    /// <param name="mediaSourceManager">The media source manager that opened the streams and closes them.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackSessionLiveStreams(IMediaSourceManager mediaSourceManager, ILogger<PlaybackSessionLiveStreams> logger)
    {
        ArgumentNullException.ThrowIfNull(mediaSourceManager);
        ArgumentNullException.ThrowIfNull(logger);
        _mediaSourceManager = mediaSourceManager;
        _logger = logger;
    }

    /// <summary>
    /// Records that <paramref name="sessionId"/> holds the live stream <paramref name="liveStreamId"/>.
    /// A different stream the session held before (a replacement that opened again) is closed.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="liveStreamId">The live stream it opened.</param>
    public void Attach(PlaybackSessionId sessionId, string liveStreamId)
    {
        ArgumentException.ThrowIfNullOrEmpty(liveStreamId);
        string? previous;
        lock (_lock)
        {
            _bySession.TryGetValue(sessionId, out previous);
            _bySession[sessionId] = liveStreamId;
        }

        if (previous is not null && !string.Equals(previous, liveStreamId, StringComparison.Ordinal))
        {
            Close(previous, sessionId);
        }
    }

    /// <summary>
    /// The live stream a session holds, if any.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The live stream id, or <see langword="null"/>.</returns>
    public string? Get(PlaybackSessionId sessionId)
    {
        lock (_lock)
        {
            return _bySession.TryGetValue(sessionId, out var liveStreamId) ? liveStreamId : null;
        }
    }

    /// <summary>
    /// Releases the live stream a session held, if any. Called on every removal path of the
    /// session; never blocks the caller (the close runs in the background).
    /// </summary>
    /// <param name="sessionId">The session that ended.</param>
    public void Release(PlaybackSessionId sessionId)
    {
        string? liveStreamId;
        lock (_lock)
        {
            if (!_bySession.Remove(sessionId, out liveStreamId))
            {
                return;
            }
        }

        Close(liveStreamId, sessionId);
    }

    /// <summary>
    /// Closes a live stream that was opened for a session that was never created (no viable plan,
    /// a refusal): the open must not outlive the request that made it.
    /// </summary>
    /// <param name="liveStreamId">The live stream to close.</param>
    /// <returns>A task completing when the stream is closed.</returns>
    public Task CloseUnattachedAsync(string liveStreamId) => CloseAsync(liveStreamId, null);

    private void Close(string liveStreamId, PlaybackSessionId sessionId)
        => _ = Task.Run(() => CloseAsync(liveStreamId, sessionId));

    private async Task CloseAsync(string liveStreamId, PlaybackSessionId? sessionId)
    {
        try
        {
            await _mediaSourceManager.CloseLiveStream(liveStreamId).ConfigureAwait(false);
            _logger.LogInformation("Live stream {LiveStreamId} of playback session {SessionId} closed.", liveStreamId, sessionId?.Value.ToString("N"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Closing live stream {LiveStreamId} of playback session {SessionId} failed.", liveStreamId, sessionId?.Value.ToString("N"));
        }
    }
}
