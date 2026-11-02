using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.ModelBinders;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.Database.Implementations.Entities;
using Tesserafin.Extensions;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Session;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// Playstate controller.
/// </summary>
[Route("")]
[Authorize]
[Tags("Session")]
public class PlaystateController : BaseTesserafinApiController
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataRepository;
    private readonly IItemAccessService _itemAccessService;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<PlaystateController> _logger;
    private readonly ITranscodeManager _transcodeManager;
    private readonly IHlsJobOwnershipAuthorizer _jobOwnership;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaystateController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataRepository">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="itemAccessService">Instance of the <see cref="IItemAccessService"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="jobOwnership">Instance of the <see cref="IHlsJobOwnershipAuthorizer"/> interface.</param>
    public PlaystateController(
        IUserManager userManager,
        IUserDataManager userDataRepository,
        IItemAccessService itemAccessService,
        ISessionManager sessionManager,
        ILoggerFactory loggerFactory,
        ITranscodeManager transcodeManager,
        IHlsJobOwnershipAuthorizer jobOwnership)
    {
        _jobOwnership = jobOwnership;
        _userManager = userManager;
        _userDataRepository = userDataRepository;
        _itemAccessService = itemAccessService;
        _sessionManager = sessionManager;
        _logger = loggerFactory.CreateLogger<PlaystateController>();

        _transcodeManager = transcodeManager;
    }

    /// <summary>
    /// Marks an item as played for user.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="datePlayed">Optional. The date the item was played.</param>
    /// <response code="200">Item marked as played.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>, or a <see cref="NotFoundResult"/> if item was not found.</returns>
    [HttpPost("UserPlayedItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Tags("UserData")]
    public async Task<ActionResult<UserItemDataDto?>> MarkPlayedItem(
        [FromQuery] Guid? userId,
        [FromRoute, Required] Guid itemId,
        [FromQuery, ModelBinder(typeof(LegacyDateTimeModelBinder))] DateTime? datePlayed)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var item = _itemAccessService.GetVisibleItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var session = await RequestHelpers.GetSession(_sessionManager, _userManager, HttpContext, userId).ConfigureAwait(false);

        var dto = UpdatePlayedStatus(user, item, true, datePlayed);
        foreach (var additionalUserInfo in session.AdditionalUsers)
        {
            var additionalUser = _userManager.GetUserById(additionalUserInfo.UserId);
            if (additionalUser is null)
            {
                return NotFound();
            }

            UpdatePlayedStatus(additionalUser, item, true, datePlayed);
        }

        return dto;
    }

    /// <summary>
    /// Marks an item as played for user.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="datePlayed">Optional. The date the item was played.</param>
    /// <response code="200">Item marked as played.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>An <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>, or a <see cref="NotFoundResult"/> if item was not found.</returns>
    [HttpPost("Users/{userId}/PlayedItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult<UserItemDataDto?>> MarkPlayedItemLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId,
        [FromQuery, ModelBinder(typeof(LegacyDateTimeModelBinder))] DateTime? datePlayed)
        => MarkPlayedItem(userId, itemId, datePlayed);

    /// <summary>
    /// Marks an item as unplayed for user.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Item marked as unplayed.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>, or a <see cref="NotFoundResult"/> if item was not found.</returns>
    [HttpDelete("UserPlayedItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Tags("UserData")]
    public async Task<ActionResult<UserItemDataDto?>> MarkUnplayedItem(
        [FromQuery] Guid? userId,
        [FromRoute, Required] Guid itemId)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var item = _itemAccessService.GetVisibleItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var session = await RequestHelpers.GetSession(_sessionManager, _userManager, HttpContext, userId).ConfigureAwait(false);

        var dto = UpdatePlayedStatus(user, item, false, null);
        foreach (var additionalUserInfo in session.AdditionalUsers)
        {
            var additionalUser = _userManager.GetUserById(additionalUserInfo.UserId);
            if (additionalUser is null)
            {
                return NotFound();
            }

            UpdatePlayedStatus(additionalUser, item, false, null);
        }

        return dto;
    }

    /// <summary>
    /// Marks an item as unplayed for user.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <response code="200">Item marked as unplayed.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>A <see cref="OkResult"/> containing the <see cref="UserItemDataDto"/>, or a <see cref="NotFoundResult"/> if item was not found.</returns>
    [HttpDelete("Users/{userId}/PlayedItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public Task<ActionResult<UserItemDataDto?>> MarkUnplayedItemLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId)
        => MarkUnplayedItem(userId, itemId);

    /// <summary>
    /// Reports playback has started within a session.
    /// </summary>
    /// <param name="playbackStartInfo">The playback start info.</param>
    /// <response code="204">Playback start recorded.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Sessions/Playing")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ReportPlaybackStart([FromBody] PlaybackStartInfo playbackStartInfo)
    {
        playbackStartInfo.PlayMethod = ValidatePlayMethod(playbackStartInfo.PlayMethod, playbackStartInfo.PlaySessionId);
        playbackStartInfo.SessionId = await RequestHelpers.GetSessionId(_sessionManager, _userManager, HttpContext).ConfigureAwait(false);
        await _sessionManager.OnPlaybackStart(playbackStartInfo).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Reports playback progress within a session.
    /// </summary>
    /// <param name="playbackProgressInfo">The playback progress info.</param>
    /// <response code="204">Playback progress recorded.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Sessions/Playing/Progress")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ReportPlaybackProgress([FromBody] PlaybackProgressInfo playbackProgressInfo)
    {
        playbackProgressInfo.PlayMethod = ValidatePlayMethod(playbackProgressInfo.PlayMethod, playbackProgressInfo.PlaySessionId);
        playbackProgressInfo.SessionId = await RequestHelpers.GetSessionId(_sessionManager, _userManager, HttpContext).ConfigureAwait(false);
        await _sessionManager.OnPlaybackProgress(playbackProgressInfo).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Pings a playback session.
    /// </summary>
    /// <param name="playSessionId">Playback session id.</param>
    /// <response code="204">Playback session pinged.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Sessions/Playing/Ping")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult PingPlaybackSession([FromQuery, Required] string playSessionId)
    {
        // Only the caller's jobs: a play session id is the client's to send, and another user's
        // transcode under the same one must still reach its idle timeout.
        _transcodeManager.PingTranscodingJobs(playSessionId, null, job => _jobOwnership.OwnsJob(HttpContext, job.UserId, job.OwnerDeviceId));
        return NoContent();
    }

    /// <summary>
    /// Reports playback has stopped within a session.
    /// </summary>
    /// <param name="playbackStopInfo">The playback stop info.</param>
    /// <response code="204">Playback stop recorded.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Sessions/Playing/Stopped")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ReportPlaybackStopped([FromBody] PlaybackStopInfo playbackStopInfo)
    {
        _logger.LogDebug("ReportPlaybackStopped PlaySessionId: {0}", (playbackStopInfo.PlaySessionId ?? string.Empty).ToSingleLogLine());
        try
        {
            if (!string.IsNullOrWhiteSpace(playbackStopInfo.PlaySessionId))
            {
                await StopCallersTranscodes(playbackStopInfo.PlaySessionId).ConfigureAwait(false);
            }
        }
        finally
        {
            // The report is recorded whether or not the transcode could be stopped: where the
            // viewer got to does not depend on a process ending in time.
            playbackStopInfo.SessionId = await RequestHelpers.GetSessionId(_sessionManager, _userManager, HttpContext).ConfigureAwait(false);
            await _sessionManager.OnPlaybackStopped(playbackStopInfo).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Reports that a session has begun playing an item.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">The id of the MediaSource.</param>
    /// <param name="audioStreamIndex">The audio stream index.</param>
    /// <param name="subtitleStreamIndex">The subtitle stream index.</param>
    /// <param name="playMethod">The play method.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="canSeek">Indicates if the client can seek.</param>
    /// <response code="204">Play start recorded.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("PlayingItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [Obsolete("This endpoint is obsolete. Use ReportPlaybackStart instead")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> OnPlaybackStart(
        [FromRoute, Required] Guid itemId,
        [FromQuery] string? mediaSourceId,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] PlayMethod? playMethod,
        [FromQuery] string? liveStreamId,
        [FromQuery] string? playSessionId,
        [FromQuery] bool canSeek = false)
    {
        var playbackStartInfo = new PlaybackStartInfo
        {
            CanSeek = canSeek,
            ItemId = itemId,
            MediaSourceId = mediaSourceId,
            AudioStreamIndex = audioStreamIndex,
            SubtitleStreamIndex = subtitleStreamIndex,
            PlayMethod = playMethod ?? PlayMethod.Transcode,
            PlaySessionId = playSessionId,
            LiveStreamId = liveStreamId
        };

        playbackStartInfo.PlayMethod = ValidatePlayMethod(playbackStartInfo.PlayMethod, playbackStartInfo.PlaySessionId);
        playbackStartInfo.SessionId = await RequestHelpers.GetSessionId(_sessionManager, _userManager, HttpContext).ConfigureAwait(false);
        await _sessionManager.OnPlaybackStart(playbackStartInfo).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Reports that a user has begun playing an item.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">The id of the MediaSource.</param>
    /// <param name="audioStreamIndex">The audio stream index.</param>
    /// <param name="subtitleStreamIndex">The subtitle stream index.</param>
    /// <param name="playMethod">The play method.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="canSeek">Indicates if the client can seek.</param>
    /// <response code="204">Play start recorded.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Users/{userId}/PlayingItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "userId", Justification = "Required for ServiceStack")]
    public Task<ActionResult> OnPlaybackStartLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId,
        [FromQuery] string? mediaSourceId,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] PlayMethod? playMethod,
        [FromQuery] string? liveStreamId,
        [FromQuery] string? playSessionId,
        [FromQuery] bool canSeek = false)
        => OnPlaybackStart(itemId, mediaSourceId, audioStreamIndex, subtitleStreamIndex, playMethod, liveStreamId, playSessionId, canSeek);

    /// <summary>
    /// Reports a session's playback progress.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">The id of the MediaSource.</param>
    /// <param name="positionTicks">Optional. The current position, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="audioStreamIndex">The audio stream index.</param>
    /// <param name="subtitleStreamIndex">The subtitle stream index.</param>
    /// <param name="volumeLevel">Scale of 0-100.</param>
    /// <param name="playMethod">The play method.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="repeatMode">The repeat mode.</param>
    /// <param name="isPaused">Indicates if the player is paused.</param>
    /// <param name="isMuted">Indicates if the player is muted.</param>
    /// <response code="204">Play progress recorded.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("PlayingItems/{itemId}/Progress")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [Obsolete("This endpoint is obsolete. Use ReportPlaybackProgress instead")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> OnPlaybackProgress(
        [FromRoute, Required] Guid itemId,
        [FromQuery] string? mediaSourceId,
        [FromQuery] long? positionTicks,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] int? volumeLevel,
        [FromQuery] PlayMethod? playMethod,
        [FromQuery] string? liveStreamId,
        [FromQuery] string? playSessionId,
        [FromQuery] RepeatMode? repeatMode,
        [FromQuery] bool isPaused = false,
        [FromQuery] bool isMuted = false)
    {
        var playbackProgressInfo = new PlaybackProgressInfo
        {
            ItemId = itemId,
            PositionTicks = positionTicks,
            IsMuted = isMuted,
            IsPaused = isPaused,
            MediaSourceId = mediaSourceId,
            AudioStreamIndex = audioStreamIndex,
            SubtitleStreamIndex = subtitleStreamIndex,
            VolumeLevel = volumeLevel,
            PlayMethod = playMethod ?? PlayMethod.Transcode,
            PlaySessionId = playSessionId,
            LiveStreamId = liveStreamId,
            RepeatMode = repeatMode ?? RepeatMode.RepeatNone
        };

        playbackProgressInfo.PlayMethod = ValidatePlayMethod(playbackProgressInfo.PlayMethod, playbackProgressInfo.PlaySessionId);
        playbackProgressInfo.SessionId = await RequestHelpers.GetSessionId(_sessionManager, _userManager, HttpContext).ConfigureAwait(false);
        await _sessionManager.OnPlaybackProgress(playbackProgressInfo).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Reports a user's playback progress.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">The id of the MediaSource.</param>
    /// <param name="positionTicks">Optional. The current position, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="audioStreamIndex">The audio stream index.</param>
    /// <param name="subtitleStreamIndex">The subtitle stream index.</param>
    /// <param name="volumeLevel">Scale of 0-100.</param>
    /// <param name="playMethod">The play method.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="repeatMode">The repeat mode.</param>
    /// <param name="isPaused">Indicates if the player is paused.</param>
    /// <param name="isMuted">Indicates if the player is muted.</param>
    /// <response code="204">Play progress recorded.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Users/{userId}/PlayingItems/{itemId}/Progress")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "userId", Justification = "Required for ServiceStack")]
    public Task<ActionResult> OnPlaybackProgressLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId,
        [FromQuery] string? mediaSourceId,
        [FromQuery] long? positionTicks,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] int? volumeLevel,
        [FromQuery] PlayMethod? playMethod,
        [FromQuery] string? liveStreamId,
        [FromQuery] string? playSessionId,
        [FromQuery] RepeatMode? repeatMode,
        [FromQuery] bool isPaused = false,
        [FromQuery] bool isMuted = false)
        => OnPlaybackProgress(itemId, mediaSourceId, positionTicks, audioStreamIndex, subtitleStreamIndex, volumeLevel, playMethod, liveStreamId, playSessionId, repeatMode, isPaused, isMuted);

    /// <summary>
    /// Reports that a session has stopped playing an item.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">The id of the MediaSource.</param>
    /// <param name="nextMediaType">The next media type that will play.</param>
    /// <param name="positionTicks">Optional. The position, in ticks, where playback stopped. 1 tick = 10000 ms.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <response code="204">Playback stop recorded.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("PlayingItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [Obsolete("This endpoint is obsolete. Use ReportPlaybackStop instead")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult> OnPlaybackStopped(
        [FromRoute, Required] Guid itemId,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? nextMediaType,
        [FromQuery] long? positionTicks,
        [FromQuery] string? liveStreamId,
        [FromQuery] string? playSessionId)
    {
        var playbackStopInfo = new PlaybackStopInfo
        {
            ItemId = itemId,
            PositionTicks = positionTicks,
            MediaSourceId = mediaSourceId,
            PlaySessionId = playSessionId,
            LiveStreamId = liveStreamId,
            NextMediaType = nextMediaType
        };

        _logger.LogDebug("ReportPlaybackStopped PlaySessionId: {0}", (playbackStopInfo.PlaySessionId ?? string.Empty).ToSingleLogLine());
        try
        {
            if (!string.IsNullOrWhiteSpace(playbackStopInfo.PlaySessionId))
            {
                await StopCallersTranscodes(playbackStopInfo.PlaySessionId).ConfigureAwait(false);
            }
        }
        finally
        {
            // The report is recorded whether or not the transcode could be stopped: where the
            // viewer got to does not depend on a process ending in time.
            playbackStopInfo.SessionId = await RequestHelpers.GetSessionId(_sessionManager, _userManager, HttpContext).ConfigureAwait(false);
            await _sessionManager.OnPlaybackStopped(playbackStopInfo).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Reports that a user has stopped playing an item.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="mediaSourceId">The id of the MediaSource.</param>
    /// <param name="nextMediaType">The next media type that will play.</param>
    /// <param name="positionTicks">Optional. The position, in ticks, where playback stopped. 1 tick = 10000 ms.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <response code="204">Playback stop recorded.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Users/{userId}/PlayingItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [Obsolete("Kept for backwards compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "userId", Justification = "Required for ServiceStack")]
    public Task<ActionResult> OnPlaybackStoppedLegacy(
        [FromRoute, Required] Guid userId,
        [FromRoute, Required] Guid itemId,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? nextMediaType,
        [FromQuery] long? positionTicks,
        [FromQuery] string? liveStreamId,
        [FromQuery] string? playSessionId)
        => OnPlaybackStopped(itemId, mediaSourceId, nextMediaType, positionTicks, liveStreamId, playSessionId);

    /// <summary>
    /// Updates the played status.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item.</param>
    /// <param name="wasPlayed">if set to <c>true</c> [was played].</param>
    /// <param name="datePlayed">The date played.</param>
    /// <returns>Task.</returns>
    private UserItemDataDto? UpdatePlayedStatus(User user, BaseItem item, bool wasPlayed, DateTime? datePlayed)
    {
        if (wasPlayed)
        {
            item.MarkPlayed(user, datePlayed, true);
        }
        else
        {
            item.MarkUnplayed(user);
        }

        return _userDataRepository.GetUserDataDto(item, user);
    }

    /// <summary>
    /// Stops the transcodes of a play session that are this caller's.
    /// </summary>
    /// <remarks>
    /// POLISH-2-R4. The play session id is in the body of the report, and so is whoever's the
    /// caller chooses to send: selecting by it alone stopped another user's transcode. The same
    /// comparison as <c>DELETE Videos/ActiveEncodings</c>, job by job.
    /// </remarks>
    private Task StopCallersTranscodes(string playSessionId)
        => _transcodeManager.StopTranscodingJobs(playSessionId, job => _jobOwnership.OwnsJob(HttpContext, job.UserId, job.OwnerDeviceId));

    private PlayMethod ValidatePlayMethod(PlayMethod method, string? playSessionId)
    {
        if (method == PlayMethod.Transcode)
        {
            var job = string.IsNullOrWhiteSpace(playSessionId) ? null : _transcodeManager.GetTranscodingJob(playSessionId);
            if (job is null)
            {
                return PlayMethod.DirectPlay;
            }
        }

        return method;
    }
}
