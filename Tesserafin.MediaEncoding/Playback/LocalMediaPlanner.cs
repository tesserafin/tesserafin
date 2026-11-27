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
    private readonly Func<bool> _canTonemap;
    private readonly Func<IsoType, bool> _canReadDiscImage;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalMediaPlanner"/> class.
    /// </summary>
    /// <param name="inner">The existing chain, used for every decision this journey does not own.</param>
    /// <param name="engine">The v2 decision engine.</param>
    /// <param name="v2PlanStore">The store the decided plan is published into.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock stamping the published record; the system clock when null.</param>
    /// <param name="canTonemap">
    /// Whether this server's executor tone-maps HDR to SDR (the <c>tonemapx</c> filter, or hardware
    /// tone mapping enabled): a tone-mapping plan is owned only then. Never, when null.
    /// </param>
    /// <param name="canReadDiscImage">
    /// Whether this server's FFmpeg opens a disc image of the given kind (libbluray for a Blu-ray
    /// image, libdvdnav for a DVD image): a disc image is executed by the session only then, and
    /// refused otherwise. Never, when null.
    /// </param>
    public LocalMediaPlanner(
        IPlaybackSessionPlanner inner,
        IPlaybackEngine engine,
        IV2PlanStore v2PlanStore,
        ILogger<LocalMediaPlanner> logger,
        TimeProvider? timeProvider = null,
        Func<bool>? canTonemap = null,
        Func<IsoType, bool>? canReadDiscImage = null)
    {
        _canTonemap = canTonemap ?? (static () => false);
        _canReadDiscImage = canReadDiscImage ?? (static _ => false);
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

    /// <summary>
    /// Determines whether a media source is a local disc structure - a DVD or Blu-ray folder rip -
    /// that the session's executor reads as one input (a concatenation of its title files). It is
    /// remuxed or transcoded into a session output, never handed to the client as it is (decided
    /// 2026-11-20).
    /// </summary>
    /// <param name="source">The media source.</param>
    /// <returns><see langword="true"/> if the source is a local DVD or Blu-ray folder.</returns>
    public static bool IsLocalDiscSource(MediaSourceInfo? source)
        => source is { Protocol: MediaProtocol.File, IsRemote: false, IsInfiniteStream: false, RequiresOpening: false, IsoType: null, VideoType: VideoType.Dvd or VideoType.BluRay }
            && string.IsNullOrEmpty(source.LiveStreamId)
            && !string.IsNullOrEmpty(source.Path);

    /// <summary>
    /// Determines whether a media source is a local disc image - a DVD or Blu-ray ISO whose kind the
    /// library scan recognised - that the session's executor opens itself (the <c>dvdvideo</c>
    /// demuxer, the <c>bluray</c> protocol). It is remuxed or transcoded into a session output, never
    /// handed to the client as it is (decided 2026-11-25).
    /// </summary>
    /// <param name="source">The media source.</param>
    /// <returns><see langword="true"/> if the source is a local DVD or Blu-ray image.</returns>
    public static bool IsLocalDiscImageSource(MediaSourceInfo? source)
        => source is { Protocol: MediaProtocol.File, IsRemote: false, IsInfiniteStream: false, RequiresOpening: false, IsoType: IsoType.Dvd or IsoType.BluRay }
            && source.VideoType is null or VideoType.Iso
            && string.IsNullOrEmpty(source.LiveStreamId)
            && !string.IsNullOrEmpty(source.Path);

    /// <summary>
    /// Determines whether a media source is a remote file the server reads over HTTP (a remote url, a
    /// <c>.strm</c> target): one the session's executor reads from its url, and can seek in because
    /// its duration is known (decided 2026-11-20: non-local sources go through a provider that says
    /// whether seeking is possible). It is never handed to the client as it is.
    /// </summary>
    /// <param name="source">The media source.</param>
    /// <returns><see langword="true"/> if the source is a seekable remote HTTP file.</returns>
    public static bool IsSeekableRemoteSource(MediaSourceInfo? source)
        => source is { Protocol: MediaProtocol.Http, RequiresOpening: false, IsInfiniteStream: false, RunTimeTicks: > 0 }
            && string.IsNullOrEmpty(source.LiveStreamId)
            && Uri.TryCreate(source.Path, UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Determines whether a media source is the tuner stream this session opened: the source the
    /// media source manager returned for <paramref name="liveStreamId"/>.
    /// </summary>
    /// <param name="source">The media source.</param>
    /// <param name="liveStreamId">The live stream the session holds.</param>
    /// <returns><see langword="true"/> if the source is that opened stream.</returns>
    public static bool IsSessionLiveSource(MediaSourceInfo? source, string? liveStreamId)
        => source is not null
            && !string.IsNullOrEmpty(liveStreamId)
            && string.Equals(source.LiveStreamId, liveStreamId, StringComparison.Ordinal);

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

        request.Refusal = LocalMediaRefusal.None;
        var record = TryDecide(request, options, kind, out var refusal);
        if (refusal != LocalMediaRefusal.None)
        {
            // Refused, never handed to the existing chain: the caller says why in its answer.
            request.Refusal = refusal;
            _logger.LogInformation("Playback for item {ItemId} refused: {Refusal}.", options.ItemId, refusal);
            return null;
        }

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
    /// Determines whether a plan is one the session's progressive route executes from the plan alone:
    /// a remux or a transcode the HLS route would execute, delivered instead as one HTTP stream - for
    /// a client that declares no HLS output (decided 2026-11-20). A video goes out in MPEG-TS or
    /// Matroska, an audio track in ADTS AAC or MPEG-TS; the streams and their targets are the HLS
    /// route's own rules.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if the session's progressive route produces this plan.</returns>
    public static bool IsExecutableProgressive(PlaybackExecutionPlan? plan)
        => plan is { Protocol: StreamingProtocol.Http, Method: PlaybackMethod.Remux or PlaybackMethod.Transcode }
            && (plan.VideoStreamIndex is null
                ? plan.Container is "aac" or "ts"
                : plan.Container is "ts" or "mkv")
            && IsExecutableHlsOutput(plan with { Protocol = StreamingProtocol.Hls, Container = "ts" });

    /// <summary>
    /// Determines whether a plan is one of the session's own outputs it executes from the plan: its
    /// HLS route or its progressive route.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if a session route produces this plan.</returns>
    public static bool IsExecutableSessionOutput(PlaybackExecutionPlan? plan)
        => IsExecutableHlsOutput(plan) || IsExecutableProgressive(plan);

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
    /// (optionally down-mixed); the output stays in the source's video range, or an HDR source is
    /// tone-mapped to SDR (owned only on a server whose executor can). A subtitle is either
    /// delivered beside the stream or burnt into the encoded video.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if the plan is an executable SDR transcode.</returns>
    public static bool IsExecutableTranscode(PlaybackExecutionPlan? plan)
        => plan is { Method: PlaybackMethod.Transcode, Protocol: StreamingProtocol.Hls, VideoStreamIndex: not null }
            && IsExecutableVideoRange(plan)
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
                or TransformKind.ExtractSubtitle or TransformKind.ConvertSubtitle or TransformKind.BurnInSubtitle
                or TransformKind.Tonemap)
            && IsExecutableSubtitle(plan);

    /// <summary>
    /// Determines whether a plan tone-maps an HDR source to SDR: the one tone mapping the executor
    /// performs from a plan, on the video it encodes.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if the plan tone-maps to SDR.</returns>
    public static bool IsToneMappingToSdr(PlaybackExecutionPlan? plan)
        => plan is not null
            && plan.Transforms.Contains(TransformKind.Tonemap)
            && plan.Transforms.Contains(TransformKind.TranscodeVideo)
            && string.Equals(plan.VideoRange, "SDR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The output stays in the source's video range, or the plan tone-maps to SDR. A plan that keeps
    /// HDR10 out of Dolby Vision is decided but not executed from here: the executor tone-maps every
    /// HDR source it encodes all the way to SDR.
    /// </summary>
    private static bool IsExecutableVideoRange(PlaybackExecutionPlan plan)
        => plan.Transforms.Contains(TransformKind.Tonemap)
            ? IsToneMappingToSdr(plan)
            : plan.VideoRange is null;

    /// <summary>
    /// A transcode's subtitle is executable from the plan when it is delivered beside the stream,
    /// or burnt into a video stream this server encodes: the plan names the stream, and burning
    /// it in is part of that video encode, never of a copy.
    /// </summary>
    private static bool IsExecutableSubtitle(PlaybackExecutionPlan plan)
        => plan.SubtitleDelivery switch
        {
            null or Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External => !plan.Transforms.Contains(TransformKind.BurnInSubtitle),
            Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Burn => plan.SubtitleStreamIndex is not null
                && plan.Transforms.Contains(TransformKind.BurnInSubtitle)
                && plan.Transforms.Contains(TransformKind.TranscodeVideo),
            _ => false,
        };

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

    private static bool IsDiscImage(MediaSourceInfo source)
        => source.VideoType == VideoType.Iso || source.IsoType is not null;

    private bool IsReadableDiscImage(MediaSourceInfo source)
        => IsLocalDiscImageSource(source) && _canReadDiscImage(source.IsoType!.Value);

    private V2PlanRecord? TryDecide(LocalMediaRequest request, MediaOptions options, MediaKind kind, out LocalMediaRefusal refusal)
    {
        refusal = LocalMediaRefusal.None;
        var sources = options.MediaSources
            .Where(source => string.IsNullOrEmpty(options.MediaSourceId) || string.Equals(source.Id, options.MediaSourceId, StringComparison.Ordinal))
            .ToList();
        var live = !string.IsNullOrEmpty(request.LiveStreamId);
        if (sources.Count == 0)
        {
            // Nothing to decide on is not a decision for another planner (decided 2026-11-27).
            refusal = LocalMediaRefusal.NoSource;
            return null;
        }

        if (!sources.All(source => live
            ? IsSessionLiveSource(source, request.LiveStreamId)
            : IsLocalFileSource(source) || IsSeekableRemoteSource(source) || IsLocalDiscSource(source) || IsReadableDiscImage(source)))
        {
            // A live session decides only on the stream it holds (decided 2026-11-27). A non-local
            // source no session provider reads (another protocol, a remote stream with no known
            // duration), and a disc image this server's FFmpeg cannot open or whose kind the scan
            // did not recognise: refused, not handed to the existing chain (decided 2026-11-20,
            // 2026-11-25).
            if (live)
            {
                refusal = LocalMediaRefusal.NotTheHeldLiveStream;
            }
            else if (sources.Any(source => source.Protocol != MediaProtocol.File || IsDiscImage(source)))
            {
                refusal = LocalMediaRefusal.NoSessionProvider;
            }

            return null;
        }

        // An opened tuner stream, a remote file, a disc structure and a disc image are readable by
        // the server only (through its provider, its url, a concatenation of its title files, or
        // FFmpeg's disc reader): they are remuxed or transcoded into a session output, never handed
        // to the client as they are.
        var serverOnly = live || sources.Any(source => IsSeekableRemoteSource(source) || IsLocalDiscSource(source) || IsLocalDiscImageSource(source));
        var constraints = serverOnly ? request.Constraints with { AllowDirectPlay = false } : request.Constraints;

        var context = DlnaPlaybackAdapter.ToContext(options.ItemId, options.UserId, options.MediaSourceId, kind, PlaybackEngine.EngineVersion);
        // Resolved track indices belong to a source, not to the item. A later playable version
        // can have different indices from the first candidate. Preserve the engine's preference
        // for the first direct-play candidate - a remux is only taken when no candidate direct
        // plays, a transcode only when none can be copied either - while giving each candidate
        // its own user defaults.
        V2PlanRecord? remux = null;
        V2PlanRecord? transcode = null;
        var capabilities = WithExecutableTargetsFirst(request.Capabilities);
        foreach (var source in sources)
        {
            var sourceCapabilities = capabilities;
            var decision = _engine.Decide(context, sourceCapabilities, [DlnaPlaybackAdapter.ToSnapshot(source)], WithUserDefaults(constraints, source));
            if (!decision.IsViable
                || !PlaybackExecutionPlanBuilder.TryBuild(decision, out var plan, out _)
                || plan is null)
            {
                continue;
            }

            if (plan.Method != PlaybackMethod.DirectPlay && IsSubtitleDeliveryNotExecuted(plan))
            {
                // A subtitle delivered in the output (embedded, or as an HLS rendition), which a
                // session does not produce (decided 2026-11-27): an external track when the client
                // renders one for that subtitle, else burnt in where possible - what the engine
                // decides once those two are all the client declares. Embedded subtitles a client
                // reads in a direct play stay as they are.
                sourceCapabilities = WithoutSubtitleDeliveryInTheOutput(sourceCapabilities);
                decision = _engine.Decide(context, sourceCapabilities, [DlnaPlaybackAdapter.ToSnapshot(source)], WithUserDefaults(constraints, source));
                if (!decision.IsViable
                    || !PlaybackExecutionPlanBuilder.TryBuild(decision, out plan, out _)
                    || plan is null
                    || IsSubtitleDeliveryNotExecuted(plan))
                {
                    refusal = LocalMediaRefusal.SubtitleDeliveryNotExecuted;
                    continue;
                }
            }

            if (IsHdrOutputNotExecuted(plan))
            {
                // An HDR output the session does not execute (decided 2026-11-27): SDR instead, when
                // the client accepts SDR for that codec - executed where this server can tone-map,
                // refused below where it cannot. Otherwise the session is refused.
                var sdrOnly = WithSdrTarget(sourceCapabilities, plan.VideoCodec);
                if (sdrOnly is null)
                {
                    refusal = LocalMediaRefusal.HdrOutputNotExecuted;
                    continue;
                }

                decision = _engine.Decide(context, sdrOnly, [DlnaPlaybackAdapter.ToSnapshot(source)], WithUserDefaults(constraints, source));
                if (!decision.IsViable
                    || !PlaybackExecutionPlanBuilder.TryBuild(decision, out plan, out _)
                    || plan is null
                    || IsHdrOutputNotExecuted(plan))
                {
                    refusal = LocalMediaRefusal.HdrOutputNotExecuted;
                    continue;
                }
            }

            if (plan.Method == PlaybackMethod.DirectPlay && !serverOnly)
            {
                return new V2PlanRecord(decision, plan, _timeProvider.GetUtcNow());
            }

            var progressive = IsExecutableProgressive(plan);
            if (remux is null && (IsExecutableRemux(plan) || IsExecutableAudioRemux(plan) || (progressive && plan.Method == PlaybackMethod.Remux)))
            {
                remux = new V2PlanRecord(decision, plan, _timeProvider.GetUtcNow());
            }
            else if (transcode is null
                && (((IsExecutableTranscode(plan) || (progressive && plan.Method == PlaybackMethod.Transcode && plan.VideoStreamIndex is not null)) && (!IsToneMappingToSdr(plan) || _canTonemap()))
                    || IsExecutableAudioTranscode(plan)
                    || (progressive && plan.Method == PlaybackMethod.Transcode && plan.VideoStreamIndex is null)))
            {
                transcode = new V2PlanRecord(decision, plan, _timeProvider.GetUtcNow());
            }
            else if ((IsExecutableTranscode(plan) || (progressive && plan.Method == PlaybackMethod.Transcode && plan.VideoStreamIndex is not null)) && IsToneMappingToSdr(plan))
            {
                // The tone mapping this transcode needs, on a server whose FFmpeg cannot perform it:
                // refused unless another source plays (decided 2026-11-27).
                refusal = LocalMediaRefusal.ToneMappingUnavailable;
            }
            else if (IsRefusedTranscodeTarget(plan))
            {
                refusal = LocalMediaRefusal.TranscodeTargetNotExecuted;
            }
        }

        if (remux is not null || transcode is not null)
        {
            refusal = LocalMediaRefusal.None;
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
                    VideoCodecs = ExecutableFirst(profile.VideoCodecs, "h264", declared.Decode.VideoCodecs.Any(c => string.Equals(c.Codec, "h264", StringComparison.OrdinalIgnoreCase))),
                    AudioCodecs = ExecutableFirst(profile.AudioCodecs, "aac", declared.Decode.AudioCodecs.Any(c => string.Equals(c.Codec, "aac", StringComparison.OrdinalIgnoreCase))),
                })
                .ToList(),
        };

    /// <summary>
    /// Determines whether a plan encodes to an HDR range the session's executor does not produce: it
    /// tone-maps only all the way to SDR (<see cref="IsToneMappingToSdr"/>), so an HDR10 output kept
    /// out of another HDR source (Dolby Vision, for example) is decided but not executed.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if the plan keeps an HDR range in its encode.</returns>
    public static bool IsHdrOutputNotExecuted(PlaybackExecutionPlan? plan)
        => plan is { Method: PlaybackMethod.Transcode }
            && plan.Transforms.Contains(TransformKind.Tonemap)
            && plan.VideoRange is not null
            && !string.Equals(plan.VideoRange, "SDR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether a plan delivers its subtitle inside the output - embedded, or as an HLS
    /// subtitle rendition - which a session does not produce: it delivers a subtitle beside the
    /// stream or burns it into the video it encodes (<see cref="IsExecutableSubtitle"/>).
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if the plan delivers its subtitle in the output.</returns>
    public static bool IsSubtitleDeliveryNotExecuted(PlaybackExecutionPlan? plan)
        => plan?.SubtitleDelivery is Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Embed or Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Hls;

    /// <summary>
    /// The client's capabilities without the subtitle deliveries a session does not produce
    /// (embedded, HLS rendition): what remains is an external track for the formats the client
    /// renders as one, and burn-in.
    /// </summary>
    /// <param name="declared">The capabilities the engine decided with.</param>
    /// <returns>The capabilities to decide with again.</returns>
    internal static Tesserafin.Playback.Decision.ClientCapabilities WithoutSubtitleDeliveryInTheOutput(Tesserafin.Playback.Decision.ClientCapabilities declared)
        => declared with
        {
            Decode = declared.Decode with
            {
                SubtitleDelivery = declared.Decode.SubtitleDelivery
                    .Where(capability => capability.Method is Tesserafin.Playback.Decision.SubtitleDeliveryMethod.External or Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Burn)
                    .ToList(),
            },
        };

    /// <summary>
    /// The client's capabilities with <paramref name="videoCodec"/> declared SDR only, when the client
    /// declares SDR for it: the engine then tone-maps to SDR instead of keeping HDR10. <see langword="null"/>
    /// when the client does not accept SDR for that codec - it is not given a range it did not declare.
    /// </summary>
    /// <param name="declared">The capabilities the engine decided with.</param>
    /// <param name="videoCodec">The codec the plan encodes to.</param>
    /// <returns>The capabilities to decide with again, or <see langword="null"/>.</returns>
    internal static Tesserafin.Playback.Decision.ClientCapabilities? WithSdrTarget(Tesserafin.Playback.Decision.ClientCapabilities declared, string? videoCodec)
    {
        var target = declared.Decode.VideoCodecs.FirstOrDefault(codec => string.Equals(codec.Codec, videoCodec, StringComparison.OrdinalIgnoreCase));
        if (target is null || !target.VideoRangeTypes.Contains("SDR", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return declared with
        {
            Decode = declared.Decode with
            {
                VideoCodecs = declared.Decode.VideoCodecs
                    .Select(codec => ReferenceEquals(codec, target) ? codec with { VideoRangeTypes = ["SDR"] } : codec)
                    .ToList(),
            },
        };
    }

    /// <summary>
    /// The executable target first in a profile's codec list. A profile that names codecs of this
    /// kind but not the executable one gains it in front when the client declares it decodes it:
    /// a transcode is then re-planned to H.264/AAC rather than to a codec this server does not
    /// encode from a session (decided 2026-11-20). A profile with no codec of the kind (an audio
    /// output's video list) is left as it is.
    /// </summary>
    private static string[] ExecutableFirst(System.Collections.Generic.IReadOnlyList<string> codecs, string executable, bool clientDecodesIt)
    {
        if (codecs.Any(codec => string.Equals(codec, executable, StringComparison.OrdinalIgnoreCase)))
        {
            return codecs.OrderBy(codec => string.Equals(codec, executable, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
        }

        return codecs.Count > 0 && clientDecodesIt ? [executable, .. codecs] : codecs.ToArray();
    }

    /// <summary>
    /// Determines whether a plan is a transcode refused for its target: a stream it encodes is
    /// encoded to another codec than H.264 (video) or AAC (audio). Such a plan is refused
    /// explicitly rather than handed to the existing chain (decided 2026-11-20); a direct play or a
    /// remux of those codecs is not a transcode and stays allowed.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> if the plan encodes to an unexecuted target.</returns>
    public static bool IsRefusedTranscodeTarget(PlaybackExecutionPlan? plan)
        => plan is { Method: PlaybackMethod.Transcode }
            && ((plan.Transforms.Contains(TransformKind.TranscodeVideo) && !string.Equals(plan.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
                || (plan.Transforms.Contains(TransformKind.TranscodeAudio) && !string.Equals(plan.AudioCodec, "aac", StringComparison.OrdinalIgnoreCase)));

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
