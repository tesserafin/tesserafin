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
/// Consolidation step 1 (docs/playback-path-inventory.md §4 to §7): a library video or a library
/// audio track read from a local file is decided by the v2 engine alone. The engine receives the capabilities and constraints the
/// client declared on <c>POST Playback/Sessions</c>, not a <c>DeviceProfile</c> round trip, and the
/// legacy planner (<c>StreamBuilder</c>) is never invoked for it. Three answers are owned here:
/// a direct play, served verbatim from the session's own file route; a remux this server can
/// execute as a pure stream copy (<see cref="IsExecutableRemux"/>); and an SDR transcode to
/// H.264 and/or AAC (<see cref="IsExecutableTranscode"/>). The last two are served from the
/// session's own HLS route, which builds its encoding request from the retained plan. In every
/// case the session carries no legacy <c>StreamInfo</c>: its plan is the retained
/// <see cref="V2PlanRecord"/>. Any other answer — tone mapping, a burnt-in subtitle, a delivery
/// that is not HLS, refusal, a source that is not a local file — falls through to the existing
/// chain unchanged, inside the same planning call, so one request still produces exactly one
/// session.
/// </summary>
/// <remarks>
/// The declared inputs reach the planner through an ambient request scope opened by the
/// controller around <see cref="IPlaybackSessionManager.Create"/>/<see cref="IPlaybackSessionManager.Patch"/>,
/// the same capture pattern <see cref="IV2PlanStore"/> already uses. Without a scope (the legacy
/// <c>PlaybackInfo</c> path, a channel item, Live TV), this decorator only delegates. For audio a
/// direct play, a remux into HLS (<see cref="IsExecutableAudioRemux"/>) and a transcode to AAC over
/// HLS (<see cref="IsExecutableAudioTranscode"/>) are owned here.
/// </remarks>
public sealed class LocalMediaPlanner : IPlaybackSessionPlanner
{
    private static readonly AsyncLocal<LocalMediaRequest?> _current = new();

    private readonly IPlaybackSessionPlanner _inner;
    private readonly IPlaybackEngine _engine;
    private readonly IV2PlanStore _v2PlanStore;
    private readonly ILogger<LocalMediaPlanner> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalMediaPlanner"/> class.
    /// </summary>
    /// <param name="inner">The existing chain, used for every decision this journey does not own.</param>
    /// <param name="engine">The v2 decision engine.</param>
    /// <param name="v2PlanStore">The store the decided plan is published into.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock stamping the published record; the system clock when null.</param>
    public LocalMediaPlanner(
        IPlaybackSessionPlanner inner,
        IPlaybackEngine engine,
        IV2PlanStore v2PlanStore,
        ILogger<LocalMediaPlanner> logger,
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
    /// for a library video or audio track; the scope ends when the returned handle is disposed.
    /// </summary>
    /// <param name="request">The capabilities and constraints the client declared.</param>
    /// <returns>The handle closing the scope.</returns>
    public static IDisposable BeginRequest(LocalMediaRequest request)
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
    public PlaybackPlan? PlanAudio(MediaOptions options) => Plan(options, MediaKind.Audio, _inner.PlanAudio);

    /// <inheritdoc/>
    public PlaybackPlan? PlanVideo(MediaOptions options) => Plan(options, MediaKind.Video, _inner.PlanVideo);

    private PlaybackPlan? Plan(MediaOptions options, MediaKind kind, Func<MediaOptions, PlaybackPlan?> inner)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = _current.Value;
        if (request is null)
        {
            return inner(options);
        }

        // A version the request names and this user's source list does not hold (withdrawn, or
        // never there) is not a decision to hand to another planner: there is nothing to plan.
        if (!string.IsNullOrEmpty(options.MediaSourceId)
            && !options.MediaSources.Any(source => string.Equals(source.Id, options.MediaSourceId, StringComparison.Ordinal)))
        {
            return null;
        }

        var record = TryDecide(request, options, kind);
        if (record is null)
        {
            return inner(options);
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
            _ => new PlaybackPlan(PlayMethod.Transcode, TranscodeReasonsOf(record.Decision.Reasoning)),
        };
    }

    /// <summary>
    /// Determines whether a plan is one the session's HLS route executes from the plan alone: an
    /// executable remux, an executable video transcode or an executable audio transcode.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if the session's HLS route produces this plan.</returns>
    public static bool IsExecutableHlsOutput(PlaybackExecutionPlan? plan)
        => IsExecutableRemux(plan) || IsExecutableTranscode(plan) || IsExecutableAudioRemux(plan) || IsExecutableAudioTranscode(plan);

    /// <summary>
    /// Determines whether a plan is an audio remux this server executes from the plan alone: the
    /// engine chose <see cref="PlaybackMethod.Remux"/> for one audio stream and no video stream,
    /// delivered over HLS in fMP4 or MPEG-TS segments, the audio copied as it is. Nothing is
    /// re-encoded or down-mixed, and there is no subtitle.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if the plan is an executable audio remux.</returns>
    public static bool IsExecutableAudioRemux(PlaybackExecutionPlan? plan)
        => plan is { Method: PlaybackMethod.Remux, Protocol: StreamingProtocol.Hls, VideoStreamIndex: null, AudioStreamIndex: not null, SubtitleStreamIndex: null }
            && !string.IsNullOrEmpty(plan.AudioCodec)
            && (string.Equals(plan.Container, "mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(plan.Container, "ts", StringComparison.OrdinalIgnoreCase))
            && plan.Transforms.Contains(TransformKind.CopyAudio)
            && plan.Transforms.All(transform => transform is TransformKind.RemuxContainer or TransformKind.CopyAudio);

    /// <summary>
    /// Determines whether a plan is an audio transcode this server executes from the plan alone:
    /// the engine chose <see cref="PlaybackMethod.Transcode"/> for one audio stream and no video
    /// stream, delivered over HLS in fMP4 or MPEG-TS segments, re-encoded to AAC (optionally
    /// down-mixed). No subtitle, no copy: an audio remux is not this definition.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if the plan is an executable audio transcode.</returns>
    public static bool IsExecutableAudioTranscode(PlaybackExecutionPlan? plan)
        => plan is { Method: PlaybackMethod.Transcode, Protocol: StreamingProtocol.Hls, VideoStreamIndex: null, AudioStreamIndex: not null, SubtitleStreamIndex: null }
            && string.Equals(plan.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(plan.Container, "mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(plan.Container, "ts", StringComparison.OrdinalIgnoreCase))
            && plan.Transforms.Contains(TransformKind.TranscodeAudio)
            && plan.Transforms.All(transform => transform is TransformKind.RemuxContainer or TransformKind.TranscodeAudio or TransformKind.Downmix);

    /// <summary>
    /// Determines whether a plan is a transcode this server executes from the plan alone: the
    /// engine chose <see cref="PlaybackMethod.Transcode"/>, delivered over HLS in fMP4 or MPEG-TS
    /// segments; each stream is either copied or re-encoded, video to H.264 and audio to AAC
    /// (optionally down-mixed); the output stays in the source's video range. Tone mapping and a
    /// burnt-in subtitle are not this definition: they are decided, but not executed from here.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if the plan is an executable SDR transcode.</returns>
    public static bool IsExecutableTranscode(PlaybackExecutionPlan? plan)
        => plan is { Method: PlaybackMethod.Transcode, Protocol: StreamingProtocol.Hls, VideoStreamIndex: not null, VideoRange: null }
            && !string.IsNullOrEmpty(plan.VideoCodec)
            && (plan.AudioStreamIndex is null || !string.IsNullOrEmpty(plan.AudioCodec))
            && (string.Equals(plan.Container, "mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(plan.Container, "ts", StringComparison.OrdinalIgnoreCase))
            && (plan.Transforms.Contains(TransformKind.TranscodeVideo) || plan.Transforms.Contains(TransformKind.TranscodeAudio))
            && plan.Transforms.Contains(TransformKind.TranscodeVideo) != plan.Transforms.Contains(TransformKind.CopyVideo)
            && (plan.AudioStreamIndex is null || plan.Transforms.Contains(TransformKind.TranscodeAudio) != plan.Transforms.Contains(TransformKind.CopyAudio))
            && (!plan.Transforms.Contains(TransformKind.TranscodeVideo) || string.Equals(plan.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
            && (!plan.Transforms.Contains(TransformKind.TranscodeAudio) || string.Equals(plan.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase))
            && plan.Transforms.All(transform => transform is TransformKind.RemuxContainer
                or TransformKind.TranscodeVideo or TransformKind.CopyVideo
                or TransformKind.TranscodeAudio or TransformKind.CopyAudio or TransformKind.Downmix
                or TransformKind.ExtractSubtitle or TransformKind.ConvertSubtitle)
            && plan.SubtitleDelivery is null or Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External;

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

    private V2PlanRecord? TryDecide(LocalMediaRequest request, MediaOptions options, MediaKind kind)
    {
        var sources = options.MediaSources
            .Where(source => string.IsNullOrEmpty(options.MediaSourceId) || string.Equals(source.Id, options.MediaSourceId, StringComparison.Ordinal))
            .ToList();
        if (sources.Count == 0 || !sources.All(IsLocalFileSource))
        {
            return null;
        }

        var context = DlnaPlaybackAdapter.ToContext(options.ItemId, options.UserId, options.MediaSourceId, kind, PlaybackEngine.EngineVersion);
        // Resolved track indices belong to a source, not to the item. A later playable version
        // can have different indices from the first candidate. Preserve the engine's preference
        // for the first direct-play candidate - a remux is only taken when no candidate direct
        // plays, a transcode only when none can be copied either - while giving each candidate
        // its own user defaults.
        V2PlanRecord? remux = null;
        V2PlanRecord? transcode = null;
        foreach (var source in sources)
        {
            var decision = _engine.Decide(context, WithExecutableTargetsFirst(request.Capabilities), [DlnaPlaybackAdapter.ToSnapshot(source)], WithUserDefaults(request.Constraints, source));
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

            if (remux is null && (IsExecutableRemux(plan) || IsExecutableAudioRemux(plan)))
            {
                remux = new V2PlanRecord(decision, plan, _timeProvider.GetUtcNow());
            }
            else if (transcode is null && (IsExecutableTranscode(plan) || IsExecutableAudioTranscode(plan)))
            {
                transcode = new V2PlanRecord(decision, plan, _timeProvider.GetUtcNow());
            }
        }

        return remux ?? transcode;
    }

    /// <summary>
    /// The client's declared output profiles, with the target codecs this server executes from a
    /// session moved to the front of each profile: H.264 for video, AAC for audio. The engine
    /// encodes to a profile's first codec, and a browser declares its fMP4 HLS profile as
    /// <c>av1, hevc, h264</c> - which this server does not encode from a session (nor, by default,
    /// on the existing chain: <c>AllowHevcEncoding</c> and <c>AllowAv1Encoding</c> are off). The
    /// other codecs stay in the profile, after these, because a profile also says which copied
    /// streams it can carry: an HEVC source is still remuxed into fMP4. Nothing is added and
    /// nothing a client does not decode becomes a target.
    /// </summary>
    /// <param name="declared">The capabilities the client declared.</param>
    /// <returns>The capabilities the engine decides with.</returns>
    internal static Tesserafin.Playback.Decision.ClientCapabilities WithExecutableTargetsFirst(Tesserafin.Playback.Decision.ClientCapabilities declared)
        => declared with
        {
            OutputProfiles = declared.OutputProfiles
                .Select(profile => profile with
                {
                    VideoCodecs = ExecutableFirst(profile.VideoCodecs, "h264"),
                    AudioCodecs = ExecutableFirst(profile.AudioCodecs, "aac"),
                })
                .ToList(),
        };

    private static string[] ExecutableFirst(System.Collections.Generic.IReadOnlyList<string> codecs, string executable)
        => codecs.OrderBy(codec => string.Equals(codec, executable, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();

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

    /// <summary>
    /// The legacy-shaped reason flags of a transcode, for the session's diagnostics slot only:
    /// every rejection the engine recorded whose code has a <see cref="TranscodeReason"/> of the
    /// same name. Nothing is decided from them.
    /// </summary>
    private static TranscodeReason TranscodeReasonsOf(ReasonNode reasoning)
    {
        TranscodeReason reasons = 0;
        if (reasoning.Outcome == ReasonOutcome.Rejected && Enum.TryParse<TranscodeReason>(reasoning.Code.ToString(), out var reason))
        {
            reasons |= reason;
        }

        foreach (var child in reasoning.Children)
        {
            reasons |= TranscodeReasonsOf(child);
        }

        return reasons;
    }

    private sealed class Scope : IDisposable
    {
        private readonly LocalMediaRequest? _previous;

        public Scope(LocalMediaRequest? previous) => _previous = previous;

        public void Dispose() => _current.Value = _previous;
    }
}
