using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Helpers;
using Tesserafin.Common.Api;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Streaming;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Entities;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The progressive output of a playback session the v2 engine alone decided to remux or transcode,
/// for a client that declares no HLS output (docs/playback-path-inventory.md §7, step 4): one HTTP
/// stream in the plan's container, produced by the same executor, job ownership and stop rules as
/// the session's HLS route. The request is built from the retained plan; the client names no codec,
/// container, stream or bitrate, only where in the item it starts.
/// </summary>
[Route("Playback/Sessions")]
[ApiExplorerSettings(IgnoreApi = true)]
public class PlaybackSessionProgressiveController : BaseTesserafinApiController
{
    private readonly PlaybackSessionDeliveryGuard _deliveryGuard;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly EncodingHelper _encodingHelper;
    private readonly ITranscodeManager _transcodeManager;
    private readonly IHlsJobOwnershipAuthorizer _jobOwnership;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionProgressiveController"/> class.
    /// </summary>
    /// <param name="deliveryGuard">Decides whether a caller is served a session's media.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="encodingHelper">Instance of <see cref="EncodingHelper"/>.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="jobOwnership">Compares the caller with the job that writes the output.</param>
    public PlaybackSessionProgressiveController(
        PlaybackSessionDeliveryGuard deliveryGuard,
        IMediaSourceManager mediaSourceManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IServerConfigurationManager serverConfigurationManager,
        IMediaEncoder mediaEncoder,
        EncodingHelper encodingHelper,
        ITranscodeManager transcodeManager,
        IHlsJobOwnershipAuthorizer jobOwnership)
    {
        _deliveryGuard = deliveryGuard;
        _mediaSourceManager = mediaSourceManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _serverConfigurationManager = serverConfigurationManager;
        _mediaEncoder = mediaEncoder;
        _encodingHelper = encodingHelper;
        _transcodeManager = transcodeManager;
        _jobOwnership = jobOwnership;
    }

    /// <summary>
    /// Gets the progressive output of a session decided as a remux or a transcode delivered over HTTP.
    /// </summary>
    /// <param name="id">The session.</param>
    /// <param name="container">The output container; must be the plan's.</param>
    /// <param name="itemId">The item the session plays; must match the session.</param>
    /// <param name="mediaSourceId">The media source the session's plan selected; must match it.</param>
    /// <param name="playSessionId">The session's play session id; must match it.</param>
    /// <param name="startTimeTicks">Where in the item the stream starts (a progressive seek); clamped to the item.</param>
    /// <response code="200">Stream returned.</response>
    /// <response code="403">The caller does not own the session.</response>
    /// <response code="404">No such session-executed progressive plan, or another container.</response>
    /// <response code="422">The retained plan cannot be executed as planned.</response>
    /// <returns>The stream.</returns>
    [HttpGet("{id}/Progressive.{container}")]
    [HttpHead("{id}/Progressive.{container}", Name = "HeadPlaybackSessionProgressive")]
    [Authorize(Policy = Policies.MediaDelivery)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesVideoFile]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    public async Task<ActionResult> GetPlaybackSessionProgressive(
        [FromRoute] PlaybackSessionId id,
        [FromRoute, Required] string container,
        [FromQuery, Required] Guid itemId,
        [FromQuery, Required] string mediaSourceId,
        [FromQuery, Required] string playSessionId,
        [FromQuery] long startTimeTicks = 0)
    {
        var delivery = _deliveryGuard.Resolve(id, User, itemId, mediaSourceId, playSessionId, LocalMediaPlanner.IsExecutableProgressive, allowAdministrator: false);
        if (delivery.Outcome == PlaybackSessionDeliveryOutcome.Forbidden)
        {
            return Forbid();
        }

        if (delivery is not { Outcome: PlaybackSessionDeliveryOutcome.Granted, Plan: { } plan, Session: { } session, Options: { } options }
            || !string.Equals(plan.Container, container, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var request = EnginePlanEncodingRequest.Build(
            plan,
            itemId,
            session.PlaySessionId,
            options.DeviceId,
            delivery.Source,
            session.Plan.TranscodeReasons == 0 ? null : session.Plan.TranscodeReasons.ToString());

        // The one input the client chooses: where the stream starts. Clamped, not checked, so it
        // never decides whether the job ownership below is asked.
        var runTimeTicks = delivery.Source?.RunTimeTicks ?? 0;
        var start = Math.Clamp(startTimeTicks, 0, Math.Max(0, runTimeTicks - 1));
        request.StartTimeTicks = start > 0 ? start : null;

        // A progressive stream cannot be sought within: a new start is a new stream. The output is
        // named after the session, not the position, so the caller's running job for this session is
        // stopped first, or the request would join it and be served from where it started.
        if (start > 0 && !HttpMethods.IsHead(Request.Method))
        {
            await _transcodeManager.StopTranscodingJobs(
                session.PlaySessionId!,
                job => _jobOwnership.OwnsJob(HttpContext, job.UserId, job.OwnerDeviceId)).ConfigureAwait(false);
        }

        // CTS lifecycle is managed internally, as on the legacy progressive route.
        var cancellationTokenSource = new CancellationTokenSource();
        var state = await StreamingHelpers.GetStreamingState(
                request,
                HttpContext,
                _mediaSourceManager,
                _userManager,
                _libraryManager,
                _serverConfigurationManager,
                _mediaEncoder,
                _encodingHelper,
                _transcodeManager,
                TranscodingJobType.Progressive,
                cancellationTokenSource.Token)
            .ConfigureAwait(false);

        if (!PlaybackSessionExecution.IsThePlan(state, request))
        {
            state.Dispose();
            return UnprocessableEntity(new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Plan not executable",
                Detail = "This source cannot be copied or encoded as the session's plan says.",
            });
        }

        var encodingOptions = _transcodeManager.GetEffectiveEncodingOptions(request.MediaSourceId)
            ?? _serverConfigurationManager.GetEncodingOptions();
        var command = request is VideoRequestDto
            ? _encodingHelper.GetProgressiveVideoCommand(state, encodingOptions, EncoderPreset.superfast)
            : _encodingHelper.GetProgressiveAudioCommand(state, encodingOptions, state.OutputFilePath);
        return await FileStreamResponseHelpers.GetTranscodedFile(
            state,
            HttpMethods.IsHead(Request.Method),
            HttpContext,
            _transcodeManager,
            _jobOwnership,
            command,
            TranscodingJobType.Progressive,
            cancellationTokenSource).ConfigureAwait(false);
    }
}
