using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Dlna;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The client-facing playback session protocol (docs/pr92-design-playback-api-and-diagnostics.md
/// §2-3): create, fully replace, and end a session. Responses are the stable, versioned
/// <see cref="PlaybackSessionResponse"/> (PR91 decision vocabulary), never the internal
/// <see cref="PlaybackSession"/> record. Responses reflect the engine's decision
/// (<c>DecisionVersion</c> = the real engine version): it is the only planner (tesserafin#129 step 7).
/// </summary>
[Route("Playback/Sessions")]
[Authorize]
[Tags("Playback")]
public class PlaybackSessionsController : BaseTesserafinApiController
{
    private readonly IPlaybackSessionManager _playbackSessionManager;
    private readonly IItemAccessService _itemAccessService;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IV2PlanStore _v2PlanStore;
    private readonly PlaybackSessionLiveStreams? _liveStreams;
    private readonly ILogger<PlaybackSessionsController> _logger;
    private readonly IRecordingsManager? _recordingsManager;
    private readonly ITranscodeManager? _transcodeManager;
    private readonly IPlaybackCredentialService? _credentialService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionsController"/> class.
    /// </summary>
    /// <param name="playbackSessionManager">Instance of the <see cref="IPlaybackSessionManager"/> interface.</param>
    /// <param name="itemAccessService">Instance of the <see cref="IItemAccessService"/> interface: the item as the user may see it.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="v2PlanStore">Instance of the <see cref="IV2PlanStore"/> interface (PR115a) — looked up to see whether v2 is authoritative for a session's response.</param>
    /// <param name="logger">
    /// Issue #43: carries the client-supplied <c>PlaybackAttemptId</c> into the structured log scope
    /// for the duration of the action, so every line emitted while serving a request of one attempt
    /// is joinable to the other requests of that same attempt. Trailing and optional so existing
    /// test constructions keep compiling.
    /// </param>
    /// <param name="transcodeManager">#301: ends the session's own jobs when the session is deleted.</param>
    /// <param name="credentialService">#301: revokes the session's capabilities when the session is deleted.</param>
    /// <param name="recordingsManager">Tells a Live TV recording still being written from a library video; none is active when null.</param>
    /// <param name="liveStreams">The tuner streams Live TV sessions hold; a channel cannot be planned without it.</param>
    public PlaybackSessionsController(
        IPlaybackSessionManager playbackSessionManager,
        IItemAccessService itemAccessService,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IV2PlanStore v2PlanStore,
        ILogger<PlaybackSessionsController>? logger = null,
        ITranscodeManager? transcodeManager = null,
        IPlaybackCredentialService? credentialService = null,
        IRecordingsManager? recordingsManager = null,
        PlaybackSessionLiveStreams? liveStreams = null)
    {
        _liveStreams = liveStreams;
        _recordingsManager = recordingsManager;
        _transcodeManager = transcodeManager;
        _credentialService = credentialService;
        _logger = logger ?? NullLogger<PlaybackSessionsController>.Instance;
        _playbackSessionManager = playbackSessionManager;
        _itemAccessService = itemAccessService;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _v2PlanStore = v2PlanStore;
    }

    /// <summary>
    /// Creates (or, if <see cref="CreatePlaybackSessionRequest.PlaySessionId"/> matches an
    /// existing session, replaces) a playback session.
    /// </summary>
    /// <param name="request">The session to plan.</param>
    /// <response code="200">Session created.</response>
    /// <response code="400">The declared capabilities or constraints are invalid.</response>
    /// <response code="404">Item not found.</response>
    /// <response code="422">No viable playback plan exists for the given options.</response>
    /// <returns>The created session's stable decision projection.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PlaybackSessionResponse>> CreatePlaybackSession([FromBody] CreatePlaybackSessionRequest request)
    {
        PlaybackSessionRequestValidator.Validate(request);

        using var attemptScope = BeginAttemptScope(request.PlaybackAttemptId);

        var (kind, options, isLocalMedia, liveStreamId) = await ResolveOptions(request, request.PlaySessionId, null, HttpContext.RequestAborted).ConfigureAwait(false);

        PlaybackSession? session;
        var localRequest = LocalMediaRequestOf(isLocalMedia, request, liveStreamId);
        using (localRequest is null ? null : LocalMediaPlanner.BeginRequest(localRequest))
        {
            session = _playbackSessionManager.Create(
                new PlaybackSessionRequest(kind, options),
                request.PlaySessionId,
                request.PlaybackAttemptId);
        }

        if (session is null)
        {
            // A channel's tuner stream was opened for this request: it must not outlive it.
            if (liveStreamId is not null && _liveStreams is not null)
            {
                await _liveStreams.CloseUnattachedAsync(liveStreamId).ConfigureAwait(false);
            }

            return NoViablePlan(localRequest?.Refusal ?? LocalMediaRefusal.None);
        }

        if (liveStreamId is not null)
        {
            _liveStreams!.Attach(session.Id, liveStreamId);
        }

        // A POST naming a play session that already has one replaces that session in place:
        // nothing of an earlier plan's job may be served for the new one.
        await StopJobsOfAnEnginePlan(session.PlaySessionId, options.UserId).ConfigureAwait(false);

        // Issue #43: the lifecycle line a browser e2e joins against X-Request-Id (the ambient
        // RequestId scope of #42) and the attempt scope opened above. One line per transition;
        // the PUT and DELETE transitions below each have their own.
        _logger.LogInformation(
            "Playback session {SessionId} created (method {PlayMethod}, attempt {PlaybackAttemptId}).",
            session.Id,
            session.Plan.PlayMethod,
            session.PlaybackAttemptId);

        // PR115a: the response reflects the v2 decision when one is retained and authoritative for
        // this session - see PlaybackSessionResponseMapper.Map(PlaybackSession, V2PlanRecord?).
        _v2PlanStore.TryGet(session.Id, out var v2Record);
        return Ok(PlaybackSessionResponseMapper.Map(session, v2Record));
    }

    /// <summary>
    /// Fully re-plans an existing session with a complete new set of options. Decision v1 (PR92
    /// §3): this replaces the misnamed <c>PATCH</c> the point-1 protocol shipped with, which
    /// already required a complete body — <c>PUT</c> states that honestly.
    /// </summary>
    /// <param name="id">The session to replace.</param>
    /// <param name="request">The complete new options to plan.</param>
    /// <response code="200">Session updated.</response>
    /// <response code="400">The declared capabilities or constraints are invalid.</response>
    /// <response code="403">The caller is neither the session's owner nor an administrator.</response>
    /// <response code="404">Item or session not found.</response>
    /// <response code="422">The session exists but re-planning it with these options produces no viable plan.</response>
    /// <returns>The updated session's stable decision projection.</returns>
    [HttpPut("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PlaybackSessionResponse>> ReplacePlaybackSession([FromRoute] PlaybackSessionId id, [FromBody] ReplacePlaybackSessionRequest request)
    {
        PlaybackSessionRequestValidator.Validate(request);

        using var attemptScope = BeginAttemptScope(request.PlaybackAttemptId);

        // PR118: PUT used to have no ownership check at all - a pre-existing gap (§4.2 of the PR117
        // design doc called it out explicitly for this endpoint) - any authenticated caller who knew
        // an id could re-plan someone else's session. Checked against the EXISTING session (not the
        // new request body's own UserId, which a caller fully controls) before doing any planning
        // work, same owner-or-admin semantics as GetPlaybackSessionStream.
        var existingSession = _playbackSessionManager.Get(id);
        if (existingSession is null)
        {
            return NotFound();
        }

        var authorization = EnsureCallerOwnsSessionOrIsAdmin(existingSession);
        if (authorization is not null)
        {
            return authorization;
        }

        // A Live TV session re-plans on the tuner stream it already holds: a PUT never opens a
        // second one for the same playback.
        var (kind, options, isLocalMedia, liveStreamId) = await ResolveOptions(request, existingSession.PlaySessionId, _liveStreams?.Get(id), HttpContext.RequestAborted).ConfigureAwait(false);

        // PR #38 (docs/design-playback-v2-lifecycle.md, decided in 2cf777d2): the session was just
        // proven to exist above, so a null here can only mean re-planning produced NO viable plan -
        // a 404 said "unknown id" for what is really an unsatisfiable request, and left the client's
        // track-change path unable to tell the two apart. 404 stays reserved for the unknown id.
        //
        // Issue #43: the SAME attempt id the POST carried, when the client is re-planning inside
        // one attempt. That identity across two different HTTP requests is exactly what a request
        // id could never provide, and is why this is a separate field.
        PlaybackSession? session;
        var localRequest = LocalMediaRequestOf(isLocalMedia, request, liveStreamId);
        using (localRequest is null ? null : LocalMediaPlanner.BeginRequest(localRequest))
        {
            session = _playbackSessionManager.Patch(id, new PlaybackSessionRequest(kind, options), request.PlaybackAttemptId);
        }

        if (session is null)
        {
            return NoViablePlan(localRequest?.Refusal ?? LocalMediaRefusal.None);
        }

        if (liveStreamId is not null)
        {
            _liveStreams!.Attach(session.Id, liveStreamId);
        }

        // The plan just changed and the play session did not: see StopJobsOfAnEnginePlan. The job
        // writing the previous plan's output must be gone before the new plan's playlist is read.
        await StopJobsOfAnEnginePlan(existingSession.PlaySessionId, existingSession.Request?.Options.UserId).ConfigureAwait(false);

        // Issue #43: the re-plan transition, with the decision bits that actually change on a track/
        // constraint change. The attempt id is read off the PATCHED session, which preserves the one
        // recorded at creation when this PUT omitted the field ("not sent" is not "forget it").
        _logger.LogInformation(
            "Playback session {SessionId} replaced (method {OldPlayMethod} -> {NewPlayMethod}, attempt {PlaybackAttemptId}).",
            session.Id,
            existingSession.Plan.PlayMethod,
            session.Plan.PlayMethod,
            session.PlaybackAttemptId);

        // PR115a: the response reflects the v2 decision when one is retained and authoritative for
        // this session - see PlaybackSessionResponseMapper.Map(PlaybackSession, V2PlanRecord?).
        _v2PlanStore.TryGet(session.Id, out var v2Record);
        return Ok(PlaybackSessionResponseMapper.Map(session, v2Record));
    }

    /// <summary>
    /// Removes a playback session.
    /// </summary>
    /// <param name="id">The session to remove.</param>
    /// <response code="204">Session removed.</response>
    /// <response code="403">The caller is neither the session's owner nor an administrator.</response>
    /// <response code="404">Session not found.</response>
    /// <returns>No content.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeletePlaybackSession([FromRoute] PlaybackSessionId id)
    {
        // PR118: same pre-existing gap as PUT (§4.2) - any authenticated caller who knew an id could
        // end someone else's session. Same owner-or-admin semantics as GetPlaybackSessionStream.
        var session = _playbackSessionManager.Get(id);
        if (session is null)
        {
            // Issue #43: no session means no stored attempt id to scope with - inventing one would
            // be worse than none. The line still ties the 404 to the request via the ambient
            // RequestId scope (#42).
            // #203: same projection as the deleted line below - same bytes, visible to the analysis.
            _logger.LogInformation("Playback session {SessionId} delete requested, but the session was already gone.", id.Value.ToString("N"));
            return NotFound();
        }

        // Issue #43: DELETE carries no body, so the attempt id is recovered from the stored session -
        // the same value the POST/PUT of this attempt recorded - and scopes everything after the
        // existence check, exactly like the request-supplied scope on POST/PUT.
        using var attemptScope = BeginAttemptScope(session.PlaybackAttemptId);

        var authorization = EnsureCallerOwnsSessionOrIsAdmin(session);
        if (authorization is not null)
        {
            return authorization;
        }

        if (!_playbackSessionManager.Delete(id))
        {
            // Raced away between the Get above and the Delete (PlaybackStopped, TTL sweep, a
            // concurrent DELETE) - same observable outcome as the not-found path above.
            // #203: same projection as the deleted line below - same bytes, visible to the analysis.
            _logger.LogInformation("Playback session {SessionId} delete requested, but the session was already gone.", id.Value.ToString("N"));
            return NotFound();
        }

        // #301: deleting the session is the client's stop, so it has to end what the session
        // started. Only its owner's jobs: a play session id is the client's to send, and another
        // user's job, and its capabilities, under the same one survive. Old capability links die with them.
        if (!string.IsNullOrEmpty(session.PlaySessionId))
        {
            var owner = session.Request?.Options.UserId;
            if (owner is { } ownerId)
            {
                _credentialService?.RevokePlaySession(session.PlaySessionId, ownerId, null);
                if (_transcodeManager is not null)
                {
                    await _transcodeManager.StopTranscodingJobs(session.PlaySessionId, job => ownerId.Equals(job.UserId)).ConfigureAwait(false);
                }
            }
        }

        // #203: `id.Value.ToString("N")` is the body of PlaybackSessionId.ToString() written out at
        // the logging boundary, so the bytes are the same 32 hexadecimal characters this statement
        // has always written - PlaybackSessionIdProjectionTests holds that down against both
        // shipped formatters. What changes is only what is visible: the route-bound value reached
        // this sink as a whole struct, which is a two-node flow with nothing in between to model.
        // Projecting the Guid out makes the safe rendering an ordinary expression instead of an
        // override the analysis has to take on trust. The value business logic uses is untouched:
        // Get, Delete and the ownership check above all still take `id` itself.
        _logger.LogInformation(
            "Playback session {SessionId} deleted (attempt {PlaybackAttemptId}).",
            id.Value.ToString("N"),
            session.PlaybackAttemptId.ToSingleLogLine());
        return NoContent();
    }

    /// <summary>
    /// PR117 (docs/pr116d-url-contract-design.md §2): resolves the executable URL for a session
    /// already planned via <see cref="CreatePlaybackSession"/>/<see cref="ReplacePlaybackSession"/>.
    /// A read, not a decision (§2.2): re-projects <c>session.Plan</c>/<c>session.Request</c> that
    /// already exist, re-evaluating the kill switch/stop-threshold guard/plan resolution at THIS
    /// call (§3.1) - the same live-wiring decision the legacy <c>PlaybackInfo</c> path makes on
    /// every request, never cached from the original <c>POST</c>/<c>PUT</c>.
    /// </summary>
    /// <param name="id">The session to resolve a stream URL for.</param>
    /// <param name="startTimeTicks">
    /// The position, in ticks, playback should start from - a property of the moment a client asks
    /// to read, not of the session's own planning decision (§2.2, option (i)).
    /// </param>
    /// <response code="200">Descriptor resolved.</response>
    /// <response code="403">The caller is neither the session's owner nor an administrator.</response>
    /// <response code="404">Session not found.</response>
    /// <response code="409">The session has no <c>PlaySessionId</c> - re-request via <see cref="ReplacePlaybackSession"/> supplying one.</response>
    /// <response code="422">The session has no plannable stream - nothing can be served for it.</response>
    /// <returns>The resolved stream descriptor.</returns>
    [HttpGet("{id}/Stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public ActionResult<PlaybackSessionStreamDescriptor> GetPlaybackSessionStream([FromRoute] PlaybackSessionId id, [FromQuery] long startTimeTicks = 0)
    {
        if (startTimeTicks < 0)
        {
            return BadRequest("startTimeTicks must be non-negative.");
        }

        var session = _playbackSessionManager.Get(id);
        if (session is null)
        {
            return NotFound();
        }

        // Issue #43: like DELETE, this request carries no attempt id of its own - it is recovered
        // from the stored session, so every line emitted while resolving the URL (the live-wiring
        // decision below logs its serve/fallback outcome) is joinable to the attempt that planned
        // the session. A missing session (the 404 above) has none to offer, so no scope there.
        using var attemptScope = BeginAttemptScope(session.PlaybackAttemptId);

        // §4.2 (mandatory, new on this endpoint): a URL carries the caller's own access token
        // (StreamInfo.ToUrl's &ApiKey=) - this endpoint must never hand that token to anyone but the
        // session's own owner or an administrator. PR118: the same check now also guards
        // ReplacePlaybackSession/DeletePlaybackSession - see EnsureCallerOwnsSessionOrIsAdmin.
        var authorization = EnsureCallerOwnsSessionOrIsAdmin(session);
        if (authorization is not null)
        {
            return authorization;
        }

        // §2.3 (mandatory): a session without a PlaySessionId cannot correlate a served URL to the
        // transcoding job lifecycle - emitting one anyway would break that correlation silently.
        if (string.IsNullOrEmpty(session.PlaySessionId))
        {
            return Conflict("Session has no PlaySessionId - PUT a replacement request supplying one before requesting a stream URL.");
        }

        // Every session is decided by the engine alone (tesserafin#129 step 7): its descriptor is
        // minted from the retained execution plan and names the session's own file or HLS route.
        // PR #38: a session with no such plan is structurally unservable, so it is 422, not 409.
        if (!_v2PlanStore.TryGet(session.Id, out var engineDecided)
            || engineDecided is not { ExecutionPlan: { } enginePlan }
            || !LocalMediaDescriptorMapper.CanMap(enginePlan)
            || session.Request?.Options is not { } engineOptions)
        {
            return UnprocessableEntity("Session has no plannable stream - this endpoint only serves sessions planned via Playback/Sessions.");
        }

        // PR118: a caller authenticated without a bearer token has no business reading a
        // descriptor. The token itself is not used: no descriptor carries a credential.
        if (string.IsNullOrEmpty(User.GetToken()))
        {
            return Forbid();
        }

        return Ok(LocalMediaDescriptorMapper.Map(session.Id, session.PlaySessionId, engineOptions.ItemId, enginePlan, engineDecided.Decision.EngineVersion, live: _liveStreams?.Get(session.Id) is not null));
    }

    /// <summary>
    /// PR118: the owner-or-admin authorization shared by all three verbs that operate on an
    /// existing session (GET Stream, PUT, DELETE) - originally only enforced on GET Stream (PR117),
    /// leaving PUT/DELETE reachable by any authenticated caller who knew the session id (§4.2 of the
    /// PR117 design doc flagged this as a pre-existing, separately tracked gap). Checked against the
    /// session's OWN stored request options, never anything the caller's request body supplies.
    /// </summary>
    /// <param name="session">The session being read, replaced, or deleted.</param>
    /// <returns>
    /// <c>null</c> if the caller may proceed; otherwise the <see cref="ForbidResult"/> to return
    /// as-is.
    /// </returns>
    private ActionResult? EnsureCallerOwnsSessionOrIsAdmin(PlaybackSession session)
    {
        if (User.IsInRole(UserRoles.Administrator))
        {
            return null;
        }

        var options = session.Request?.Options;
        if (options is null || !options.UserId.Equals(User.GetUserId()))
        {
            return Forbid();
        }

        return null;
    }

    /// <summary>
    /// A session the engine alone decided executes from its retained plan, into an output named
    /// after its play session, not after its plan. A job started for an earlier plan under the
    /// same play session (another audio track, or a transcode of the existing chain) would
    /// otherwise be found there and served as the new plan's output. Only the owner's jobs, as
    /// on DELETE (#301); a job of another user under the same client-chosen id is not touched.
    /// </summary>
    private async Task StopJobsOfAnEnginePlan(string? playSessionId, Guid? owner)
    {
        if (_transcodeManager is null || string.IsNullOrEmpty(playSessionId) || owner is not { } ownerId)
        {
            return;
        }

        await _transcodeManager.StopTranscodingJobs(playSessionId, job => ownerId.Equals(job.UserId)).ConfigureAwait(false);
    }

    /// <summary>
    /// #374: the refusal a client can act on when no delivery the request permits exists.
    /// </summary>
    private ObjectResult NoViablePlan(LocalMediaRefusal refusal)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "No playable output",
            Detail = refusal switch
            {
                LocalMediaRefusal.NoSource => "The item has no media source to play.",
                LocalMediaRefusal.NotTheHeldLiveStream => "The source to plan is not the live stream this session holds.",
                LocalMediaRefusal.NoSessionProvider => "This server cannot read the item's source for a playback session.",
                LocalMediaRefusal.ToneMappingUnavailable => "The item is HDR and the client plays SDR only, but this server's FFmpeg cannot convert HDR to SDR.",
                LocalMediaRefusal.SubtitleDeliveryNotExecuted => "The selected subtitle can be delivered only inside the output, which this server does not produce for a playback session, and it can neither be sent beside the stream nor burnt in.",
                LocalMediaRefusal.HdrOutputNotExecuted => "The item is HDR in a form the client cannot play, and the client accepts neither SDR nor another output this server produces for a playback session.",
                LocalMediaRefusal.TranscodeTargetNotExecuted => "The only transcode the client allows encodes to a codec this server does not encode for a playback session.",
                _ => "No permitted delivery matches the declared capabilities. Declare a compatible output profile, or allow transcoding.",
            },
        };
        if (refusal != LocalMediaRefusal.None)
        {
            // A stable code a client can show its own message for.
            problem.Extensions["reason"] = refusal.ToString();
        }

        return UnprocessableEntity(problem);
    }

    private async Task<(PlaybackMediaKind Kind, MediaOptions Options, bool IsLocalMedia, string? LiveStreamId)> ResolveOptions(
        PlaybackPlanRequestBase request,
        string? playSessionId,
        string? heldLiveStreamId,
        CancellationToken cancellationToken)
    {
        var userId = RequestHelpers.GetUserId(User, request.UserId);
        var user = _userManager.GetUserById(userId) ?? throw new ResourceNotFoundException();
        var item = _itemAccessService.GetVisibleItemById<BaseItem>(request.ItemId, user) ?? throw new ResourceNotFoundException();

        IReadOnlyList<MediaSourceInfo> mediaSources = await _mediaSourceManager.GetPlaybackMediaSources(item, user, true, true, cancellationToken)
            .ConfigureAwait(false);

        // A Live TV channel is planned on its opened tuner stream, which this session holds until it
        // ends (PlaybackSessionLiveStreams). The server's FFmpeg reads that stream through its
        // provider (pipe:0), never through the [Authorize]d LiveStreamFiles route.
        string? liveStreamId = null;
        // A channel-plugin item is read like any other source of its kind (a url, a file); one that
        // must be opened first is refused by the planner, since no provider opens channel sources.
        if (item is LiveTvChannel && _liveStreams is not null)
        {
            (mediaSources, liveStreamId) = await OpenChannelStream(request, mediaSources, userId, playSessionId, heldLiveStreamId, cancellationToken).ConfigureAwait(false);
        }
        else if (_liveStreams is not null && ActiveRecordingOf(item) is { } recording)
        {
            // A recording still being written is held the same way: opened as a stream read from the
            // start of its growing file, which ffmpeg reads through its provider (pipe:0), never
            // through the [Authorize]d LiveRecordings route.
            (mediaSources, liveStreamId) = await OpenRecordingStream(item, recording, heldLiveStreamId, cancellationToken).ConfigureAwait(false);
        }

        // The engine decides from the request's capabilities and constraints (LocalMediaRequest);
        // these options carry what the server resolved: the item, its sources, the caller.
        var options = new MediaOptions
        {
            MediaSources = mediaSources.ToArray(),
            Context = EncodingContext.Streaming,
            ItemId = item.Id,
            UserId = userId,
            // A channel is planned on the one tuner source the session opened, whatever the client named.
            MediaSourceId = liveStreamId is null ? request.MediaSourceId : mediaSources[0].Id,
            // PR115a: the canary cohort key is deterministic on user+device, so v2 authority for a
            // session depends on the requesting device being identifiable here.
            DeviceId = User.GetDeviceId(),
        };

        if (!request.Constraints.AllowTranscoding)
        {
            foreach (var mediaSource in mediaSources)
            {
                mediaSource.SupportsTranscoding = false;
            }
        }

        var kind = item.MediaType == MediaType.Audio ? PlaybackMediaKind.Audio : PlaybackMediaKind.Video;
        return (kind, options, kind == PlaybackMediaKind.Video ? IsLibraryVideo(item) : IsLibraryAudio(item), liveStreamId);
    }

    /// <summary>
    /// The channel's opened tuner stream as its one media source: the stream this session already
    /// holds when it re-plans, otherwise a new open of the source the request names (or the first).
    /// </summary>
    private async Task<(IReadOnlyList<MediaSourceInfo> Sources, string LiveStreamId)> OpenChannelStream(
        PlaybackPlanRequestBase request,
        IReadOnlyList<MediaSourceInfo> channelSources,
        Guid userId,
        string? playSessionId,
        string? heldLiveStreamId,
        CancellationToken cancellationToken)
    {
        if (heldLiveStreamId is not null)
        {
            var held = await _mediaSourceManager.GetLiveStreamWithDirectStreamProvider(heldLiveStreamId, cancellationToken).ConfigureAwait(false);
            return ([held.Item1], heldLiveStreamId);
        }

        // A channel's own id names no tuner source: some clients send it where a media source id goes.
        var named = string.IsNullOrEmpty(request.MediaSourceId)
            || (Guid.TryParse(request.MediaSourceId, out var namedId) && namedId.Equals(request.ItemId))
                ? null
                : request.MediaSourceId;
        var source = (named is null
                ? channelSources.FirstOrDefault()
                : channelSources.FirstOrDefault(s => string.Equals(s.Id, named, StringComparison.Ordinal)))
            ?? throw new ResourceNotFoundException();
        if (!source.RequiresOpening || string.IsNullOrEmpty(source.OpenToken))
        {
            throw new ResourceNotFoundException();
        }

        var opened = await _mediaSourceManager.OpenLiveStream(
            new LiveStreamRequest
            {
                OpenToken = source.OpenToken,
                UserId = userId,
                ItemId = request.ItemId,
                PlaySessionId = playSessionId,
                StartTimeTicks = request.Constraints.StartTimeTicks,
                AudioStreamIndex = request.Constraints.PreferredAudioStreamIndex,
                SubtitleStreamIndex = request.Constraints.PreferredSubtitleStreamIndex,
                MaxStreamingBitrate = request.Constraints.MaxBitrate,
                EnableDirectPlay = request.Constraints.AllowDirectPlay,
                EnableDirectStream = request.Constraints.AllowDirectStream,
            },
            cancellationToken).ConfigureAwait(false);

        var openedSource = opened.MediaSource;
        return ([openedSource], openedSource.LiveStreamId);
    }

    /// <summary>
    /// The recording still being written that <paramref name="item"/> is, if any.
    /// </summary>
    private ActiveRecordingInfo? ActiveRecordingOf(BaseItem item)
        => item is Video { Path: { Length: > 0 } path } ? _recordingsManager?.GetActiveRecordingInfo(path) : null;

    /// <summary>
    /// The recording's opened stream as its one media source: the stream this session already holds
    /// when it re-plans, otherwise a new open of the recording, named as the item's own source.
    /// </summary>
    private async Task<(IReadOnlyList<MediaSourceInfo> Sources, string LiveStreamId)> OpenRecordingStream(
        BaseItem item,
        ActiveRecordingInfo recording,
        string? heldLiveStreamId,
        CancellationToken cancellationToken)
    {
        if (heldLiveStreamId is not null)
        {
            var held = await _mediaSourceManager.GetLiveStreamWithDirectStreamProvider(heldLiveStreamId, cancellationToken).ConfigureAwait(false);
            return ([held.Item1], heldLiveStreamId);
        }

        var opened = await _mediaSourceManager.OpenRecordingStream(recording, item.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        return ([opened], opened.LiveStreamId);
    }

    /// <summary>
    /// Whether <paramref name="item"/> is a video the session journey may decide: any
    /// <see cref="Video"/> family (film, episode, home video, music video, trailer, channel-plugin
    /// item) that is not a Live TV recording still being written.
    /// </summary>
    private bool IsLibraryVideo(BaseItem item)
        => item is Video video
            && (string.IsNullOrEmpty(video.Path) || _recordingsManager?.GetActiveRecordingInfo(video.Path) is null);

    /// <summary>
    /// Whether <paramref name="item"/> is an audio track the session journey may decide, a
    /// channel-plugin track included.
    /// </summary>
    private static bool IsLibraryAudio(BaseItem item)
        => item is Tesserafin.Controller.Entities.Audio.Audio;

    private static LocalMediaRequest? LocalMediaRequestOf(bool isLocalMedia, PlaybackPlanRequestBase request, string? liveStreamId)
        => isLocalMedia || liveStreamId is not null
            ? new LocalMediaRequest(request.Capabilities, request.Constraints, liveStreamId)
            : null;

    /// <summary>
    /// Issue #43: opens a structured log scope carrying the attempt id for the rest of the action,
    /// or returns <c>null</c> when none is available — no scope is better than a scope carrying a
    /// null value. POST/PUT pass the request's own field; GET Stream/DELETE have no body, so they
    /// pass the id recovered from the stored session. Nests inside the per-request scope opened by
    /// <c>RequestCorrelationMiddleware</c> (issue #42), so lines emitted here carry BOTH the attempt
    /// id (same on a retry) and the request id (different on a retry).
    /// </summary>
    private IDisposable? BeginAttemptScope(string? playbackAttemptId)
    {
        if (string.IsNullOrEmpty(playbackAttemptId))
        {
            return null;
        }

        return _logger.BeginScope(new Dictionary<string, object>(1)
        {
            [PlaybackAttemptIdValidator.LogPropertyName] = playbackAttemptId
        });
    }
}
