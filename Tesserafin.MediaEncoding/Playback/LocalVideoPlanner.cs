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
/// Consolidation step 1 (docs/playback-path-inventory.md §4 and §5): a film or episode read from a local file
/// is decided by the v2 engine alone. The engine receives the capabilities and constraints the
/// client declared on <c>POST Playback/Sessions</c>, not a <c>DeviceProfile</c> round trip, and the
/// legacy planner (<c>StreamBuilder</c>) is never invoked for it. The supported answers are owned here:
/// a direct play, served verbatim from the session's own file route, and a remux this server can
/// execute as a pure stream copy (<see cref="IsExecutableRemux"/>), served from the session's own
/// HLS route, and SDR H.264/AAC transcodes executed from the same retained plan. In these cases the session carries no legacy <c>StreamInfo</c>: its plan is the
/// retained <see cref="V2PlanRecord"/>. Any other answer — a transcode or remux outside that
/// definition, refusal, a source that is not a local file — falls through to the existing chain
/// unchanged, inside the same planning call, so one request still produces exactly one session.
/// </summary>
/// <remarks>
/// The declared inputs reach the planner through an ambient request scope opened by the
/// controller around <see cref="IPlaybackSessionManager.Create"/>/<see cref="IPlaybackSessionManager.Patch"/>,
/// the same capture pattern <see cref="IV2PlanStore"/> already uses. Without a scope (the legacy
/// <c>PlaybackInfo</c> path, audio, Live TV, other video families), this decorator only delegates.
/// </remarks>
public sealed class LocalVideoPlanner : IPlaybackSessionPlanner
{
    private static readonly AsyncLocal<LocalVideoRequest?> _current = new();

    private readonly IPlaybackSessionPlanner _inner;
    private readonly IPlaybackEngine _engine;
    private readonly IV2PlanStore _v2PlanStore;
    private readonly ILogger<LocalVideoPlanner> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalVideoPlanner"/> class.
    /// </summary>
    /// <param name="inner">The existing chain, used for every decision this journey does not own.</param>
    /// <param name="engine">The v2 decision engine.</param>
    /// <param name="v2PlanStore">The store the decided plan is published into.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock stamping the published record; the system clock when null.</param>
    public LocalVideoPlanner(
        IPlaybackSessionPlanner inner,
        IPlaybackEngine engine,
        IV2PlanStore v2PlanStore,
        ILogger<LocalVideoPlanner> logger,
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
    /// for a film or episode; the scope ends when the returned handle is disposed.
    /// </summary>
    /// <param name="request">The capabilities and constraints the client declared.</param>
    /// <returns>The handle closing the scope.</returns>
    public static IDisposable BeginRequest(LocalVideoRequest request)
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

        // A version the request names and this user's source list does not hold (withdrawn, or
        // never there) is not a decision to hand to another planner: there is nothing to plan.
        if (!string.IsNullOrEmpty(options.MediaSourceId)
            && !options.MediaSources.Any(source => string.Equals(source.Id, options.MediaSourceId, StringComparison.Ordinal)))
        {
            return null;
        }

        var record = TryDecide(request, options);
        if (record is null)
        {
            return _inner.PlanVideo(options);
        }

        _v2PlanStore.Publish(record);
        var plan = record.ExecutionPlan!;
        _logger.LogInformation(
            "Playback for item {ItemId} decided by the v2 engine alone ({Method}, source {MediaSourceId}, {Protocol} {Container}).",
            options.ItemId,
            plan.Method,
            plan.SourceId,
            plan.Protocol,
            plan.Container);
        return plan.Method switch
        {
            PlaybackMethod.DirectPlay => new PlaybackPlan(PlayMethod.DirectPlay, 0),
            PlaybackMethod.Remux => new PlaybackPlan(PlayMethod.DirectStream, TranscodeReason.ContainerNotSupported),
            _ => new PlaybackPlan(PlayMethod.Transcode, 0),
        };
    }

    /// <summary>
    /// Determines whether a plan is a remux this server executes from the plan alone: the engine
    /// chose <see cref="PlaybackMethod.Remux"/>, nothing is re-encoded, tone-mapped, down-mixed or
    /// burnt in, a video stream is copied, and the delivery the client declared is HLS in fMP4 or
    /// MPEG-TS segments. The executor copies; it never re-decides, so a plan outside this
    /// definition is not offered to it.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if the plan is an executable stream-copy remux.</returns>
    public static bool IsExecutableRemux(PlaybackExecutionPlan? plan)
        => plan is { Method: PlaybackMethod.Remux, Protocol: StreamingProtocol.Hls, VideoStreamIndex: not null }
            && !string.IsNullOrEmpty(plan.VideoCodec)
            && (plan.AudioStreamIndex is null || !string.IsNullOrEmpty(plan.AudioCodec))
            && (string.Equals(plan.Container, "mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(plan.Container, "ts", StringComparison.OrdinalIgnoreCase))
            && plan.Transforms.Contains(TransformKind.RemuxContainer)
            && plan.Transforms.All(transform => transform is TransformKind.RemuxContainer or TransformKind.CopyVideo or TransformKind.CopyAudio or TransformKind.ExtractSubtitle or TransformKind.ConvertSubtitle)
            && plan.SubtitleDelivery is null or Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External;

    /// <summary>
    /// Whether the HLS executor implements this plan. Transcoding is bounded to SDR H.264/AAC;
    /// copied axes must already carry those codecs. HDR conversion and subtitle burn-in are separate
    /// journeys, never implicit work delegated to the legacy decision engine by this executor.
    /// </summary>
    /// <param name="plan">The retained plan.</param>
    /// <returns>Whether this plan has a session-owned HLS delivery.</returns>
    public static bool IsExecutableHls(PlaybackExecutionPlan? plan)
        => IsExecutableRemux(plan) || (plan is { Method: PlaybackMethod.Transcode, Protocol: StreamingProtocol.Hls, VideoStreamIndex: not null }
            && (string.Equals(plan.Container, "mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(plan.Container, "ts", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrEmpty(plan.VideoCodec)
            && (plan.AudioStreamIndex is null || !string.IsNullOrEmpty(plan.AudioCodec))
            && (plan.Transforms.Contains(TransformKind.TranscodeVideo) || plan.Transforms.Contains(TransformKind.TranscodeAudio))
            && string.Equals(plan.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase)
            && (plan.AudioStreamIndex is null || string.Equals(plan.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase))
            && plan.Transforms.All(transform => transform is TransformKind.RemuxContainer or TransformKind.CopyVideo or TransformKind.CopyAudio
                or TransformKind.TranscodeVideo or TransformKind.TranscodeAudio or TransformKind.Downmix or TransformKind.ExtractSubtitle or TransformKind.ConvertSubtitle)
            && plan.SubtitleDelivery is null or Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External);

    private V2PlanRecord? TryDecide(LocalVideoRequest request, MediaOptions options)
    {
        var sources = options.MediaSources
            .Where(source => string.IsNullOrEmpty(options.MediaSourceId) || string.Equals(source.Id, options.MediaSourceId, StringComparison.Ordinal))
            .ToList();
        if (sources.Count == 0 || !sources.All(IsLocalFileSource))
        {
            return null;
        }

        var context = DlnaPlaybackAdapter.ToContext(options.ItemId, options.UserId, options.MediaSourceId, MediaKind.Video, PlaybackEngine.EngineVersion);
        // Resolved track indices belong to a source, not to the item. A later playable version
        // can have different indices from the first candidate. Preserve the engine's preference
        // for the first direct-play candidate - a remux is only taken when no candidate direct
        // plays - while giving each candidate its own user defaults.
        V2PlanRecord? remux = null;
        V2PlanRecord? transcode = null;
        foreach (var source in sources)
        {
            var decision = _engine.Decide(context, request.Capabilities, [DlnaPlaybackAdapter.ToSnapshot(source)], WithUserDefaults(request.Constraints, source));
            if (!decision.IsViable
                || !PlaybackExecutionPlanBuilder.TryBuild(decision, out var plan, out _)
                || plan is null)
            {
                continue;
            }

            if (plan.Method == PlaybackMethod.DirectPlay)
            {
                return new V2PlanRecord(decision, plan, _timeProvider.GetUtcNow());
            }

            if (remux is null && IsExecutableRemux(plan))
            {
                remux = new V2PlanRecord(decision, plan, _timeProvider.GetUtcNow());
            }

            var video = source.MediaStreams.FirstOrDefault(stream => stream.Index == plan.VideoStreamIndex && stream.Type == MediaStreamType.Video);
            if (transcode is null && IsExecutableHls(plan) && video is not null && video.VideoRangeType is VideoRangeType.Unknown or VideoRangeType.SDR)
            {
                transcode = new V2PlanRecord(decision, plan, _timeProvider.GetUtcNow());
            }
        }

        return remux ?? transcode;
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
        private readonly LocalVideoRequest? _previous;

        public Scope(LocalVideoRequest? previous) => _previous = previous;

        public void Dispose() => _current.Value = _previous;
    }
}
