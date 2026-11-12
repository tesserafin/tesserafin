using System;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Dlna;
using Tesserafin.Playback.Engine;
using Tesserafin.Playback.Execution;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// Consolidation step 1, first journey (docs/playback-path-inventory.md §4): a film read from a
/// local file is decided by the v2 engine alone. The engine receives the capabilities and
/// constraints the client declared on <c>POST Playback/Sessions</c>, not a <c>DeviceProfile</c>
/// round trip, and the legacy planner (<c>StreamBuilder</c>) is never invoked for it. When the
/// engine's answer is a direct play, the session carries no legacy <c>StreamInfo</c>: its plan is
/// the retained <see cref="V2PlanRecord"/> and its bytes come from the session's own file route.
/// Any other answer — remux, transcode, refusal, a source that is not a local file — falls through
/// to the existing chain unchanged, inside the same planning call, so one request still produces
/// exactly one session.
/// </summary>
/// <remarks>
/// The declared inputs reach the planner through an ambient request scope opened by the
/// controller around <see cref="IPlaybackSessionManager.Create"/>/<see cref="IPlaybackSessionManager.Patch"/>,
/// the same capture pattern <see cref="IV2PlanStore"/> already uses. Without a scope (the legacy
/// <c>PlaybackInfo</c> path, an episode, audio, Live TV), this decorator only delegates.
/// </remarks>
public sealed class DirectPlayFilmPlanner : IPlaybackSessionPlanner
{
    private static readonly AsyncLocal<DirectPlayFilmRequest?> _current = new();

    private readonly IPlaybackSessionPlanner _inner;
    private readonly IPlaybackEngine _engine;
    private readonly IV2PlanStore _v2PlanStore;
    private readonly ILogger<DirectPlayFilmPlanner> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectPlayFilmPlanner"/> class.
    /// </summary>
    /// <param name="inner">The existing chain, used for every decision this journey does not own.</param>
    /// <param name="engine">The v2 decision engine.</param>
    /// <param name="v2PlanStore">The store the decided plan is published into.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock stamping the published record; the system clock when null.</param>
    public DirectPlayFilmPlanner(
        IPlaybackSessionPlanner inner,
        IPlaybackEngine engine,
        IV2PlanStore v2PlanStore,
        ILogger<DirectPlayFilmPlanner> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(v2PlanStore);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
        _engine = engine;
        _v2PlanStore = v2PlanStore;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Opens the scope in which a video planning call is offered to the engine first. Open it only
    /// for a film; the scope ends when the returned handle is disposed.
    /// </summary>
    /// <param name="request">The capabilities and constraints the client declared.</param>
    /// <returns>The handle closing the scope.</returns>
    public static IDisposable BeginRequest(DirectPlayFilmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var previous = _current.Value;
        _current.Value = request;
        return new Scope(previous);
    }

    /// <summary>
    /// Determines whether a media source is a single local file the session's file route can serve
    /// verbatim: no remote or live source, no disc structure, nothing that must be opened first.
    /// </summary>
    /// <param name="source">The media source.</param>
    /// <returns><see langword="true"/> if the source is a local file.</returns>
    public static bool IsLocalFileSource(MediaSourceInfo? source)
        => source is not null
            && source.Protocol == MediaProtocol.File
            && !source.IsRemote
            && !source.IsInfiniteStream
            && !source.RequiresOpening
            && string.IsNullOrEmpty(source.LiveStreamId)
            && source.IsoType is null
            && source.VideoType is null or VideoType.VideoFile
            && !string.IsNullOrEmpty(source.Path);

    /// <inheritdoc/>
    public PlaybackPlan? PlanAudio(MediaOptions options) => _inner.PlanAudio(options);

    /// <inheritdoc/>
    public PlaybackPlan? PlanVideo(MediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = _current.Value;
        if (request is null)
        {
            return _inner.PlanVideo(options);
        }

        var record = TryDecideDirectPlay(request, options);
        if (record is null)
        {
            return _inner.PlanVideo(options);
        }

        _v2PlanStore.Publish(record);
        _logger.LogInformation(
            "Playback for item {ItemId} decided by the v2 engine alone (direct play, source {MediaSourceId}).",
            options.ItemId,
            record.ExecutionPlan!.SourceId);
        return new PlaybackPlan(PlayMethod.DirectPlay, 0);
    }

    private V2PlanRecord? TryDecideDirectPlay(DirectPlayFilmRequest request, MediaOptions options)
    {
        var sources = options.MediaSources
            .Where(source => string.IsNullOrEmpty(options.MediaSourceId) || string.Equals(source.Id, options.MediaSourceId, StringComparison.Ordinal))
            .ToList();
        if (sources.Count == 0 || !sources.All(IsLocalFileSource))
        {
            return null;
        }

        var context = DlnaPlaybackAdapter.ToContext(options.ItemId, options.UserId, options.MediaSourceId, MediaKind.Video, PlaybackEngine.EngineVersion);
        // Resolved track indices belong to a source, not to the film. A later playable version
        // can have different indices from the first candidate. Preserve the engine's preference
        // for the first direct-play candidate, while giving each candidate its own user defaults.
        foreach (var source in sources)
        {
            var decision = _engine.Decide(context, request.Capabilities, [DlnaPlaybackAdapter.ToSnapshot(source)], WithUserDefaults(request.Constraints, source));
            if (!decision.IsViable || decision.Method != PlaybackMethod.DirectPlay)
            {
                continue;
            }

            if (PlaybackExecutionPlanBuilder.TryBuild(decision, out var plan, out _) && plan is not null)
            {
                return new V2PlanRecord(decision, plan, _timeProvider.GetUtcNow());
            }
        }

        return null;
    }

    /// <summary>
    /// The user's own audio and subtitle preferences, as <c>MediaSourceManager</c> already resolved
    /// them onto the source (<see cref="MediaSourceInfo.DefaultAudioStreamIndex"/>,
    /// <see cref="MediaSourceInfo.DefaultSubtitleStreamIndex"/>: language preferences, subtitle mode,
    /// remembered selections; <c>-1</c> or <see langword="null"/> for "no subtitle"), fill whatever the client did not choose
    /// explicitly. An explicit index from the client still wins, exactly as on the legacy path; a
    /// client-declared subtitle mode can therefore never turn on subtitles a user set to "None".
    /// </summary>
    /// <param name="declared">The constraints the client declared.</param>
    /// <param name="source">The source the user defaults were resolved onto.</param>
    /// <returns>The constraints the engine decides with.</returns>
    internal static PlaybackConstraints WithUserDefaults(PlaybackConstraints declared, MediaSourceInfo source)
        => declared with
        {
            PreferredAudioStreamIndex = declared.PreferredAudioStreamIndex ?? source.DefaultAudioStreamIndex,
            // MediaSourceManager resolved the user's subtitle default onto every source it handed out;
            // null there means "no subtitle" (mode None, or nothing matching), which the engine
            // spells -1. Leaving it null would let the client's declared mode pick one instead.
            PreferredSubtitleStreamIndex = declared.PreferredSubtitleStreamIndex ?? source.DefaultSubtitleStreamIndex ?? -1,
        };

    private sealed class Scope : IDisposable
    {
        private readonly DirectPlayFilmRequest? _previous;

        public Scope(DirectPlayFilmRequest? previous) => _previous = previous;

        public void Dispose() => _current.Value = _previous;
    }
}
