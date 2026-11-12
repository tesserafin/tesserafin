using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Extensions;
using Tesserafin.Common.Api;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Data;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations.Enums;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Net;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The byte route of a playback session the v2 engine alone decided to direct play
/// (docs/playback-path-inventory.md §4). Kept out of <see cref="PlaybackSessionsController"/>
/// because it is a media-delivery route: a media element cannot send a header, so it authenticates
/// with a <c>playbackCapability</c> (or the durable token) under <see cref="Policies.MediaDelivery"/>,
/// which that controller's class-level default policy would not accept.
/// </summary>
/// <remarks>
/// The client names no file. Every request re-reads the session, re-checks that the caller owns it
/// (or is an administrator) and may still see the item, and serves the source the retained plan
/// selected, from the source list retained when the session was planned. A deleted, expired or
/// re-planned session stops serving immediately; a revoked capability is refused by authentication
/// before this action runs.
/// </remarks>
[Route("Playback/Sessions")]
[Tags("Playback")]
public class PlaybackSessionFilesController : BaseTesserafinApiController
{
    private readonly IPlaybackSessionManager _playbackSessionManager;
    private readonly IV2PlanStore _v2PlanStore;
    private readonly IUserManager _userManager;
    private readonly IItemAccessService _itemAccessService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionFilesController"/> class.
    /// </summary>
    /// <param name="playbackSessionManager">Instance of the <see cref="IPlaybackSessionManager"/> interface.</param>
    /// <param name="v2PlanStore">Instance of the <see cref="IV2PlanStore"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="itemAccessService">Instance of the <see cref="IItemAccessService"/> interface.</param>
    public PlaybackSessionFilesController(
        IPlaybackSessionManager playbackSessionManager,
        IV2PlanStore v2PlanStore,
        IUserManager userManager,
        IItemAccessService itemAccessService)
    {
        _playbackSessionManager = playbackSessionManager;
        _v2PlanStore = v2PlanStore;
        _userManager = userManager;
        _itemAccessService = itemAccessService;
    }

    /// <summary>
    /// Serves the source file of a session decided as a direct play, with HTTP range support.
    /// </summary>
    /// <param name="id">The session.</param>
    /// <param name="itemId">The item the session plays; must match the session.</param>
    /// <param name="mediaSourceId">The media source the session's plan selected; must match it.</param>
    /// <param name="playSessionId">The session's play session id; must match it.</param>
    /// <response code="200">The file, or the requested range of it (206).</response>
    /// <response code="401">No valid credential.</response>
    /// <response code="403">The caller does not own the session.</response>
    /// <response code="404">No such direct-play session, or nothing servable for it.</response>
    /// <returns>The file.</returns>
    [HttpGet("{id}/File")]
    [HttpHead("{id}/File", Name = "HeadPlaybackSessionFile")]
    [Authorize(Policy = Policies.MediaDelivery)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesVideoFile]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    public ActionResult GetPlaybackSessionFile(
        [FromRoute] PlaybackSessionId id,
        [FromQuery, Required] Guid itemId,
        [FromQuery, Required] string mediaSourceId,
        [FromQuery, Required] string playSessionId)
    {
        var session = _playbackSessionManager.Get(id);
        var options = session?.Request?.Options;
        if (session is null || options is null)
        {
            return NotFound();
        }

        if (!User.IsInRole(UserRoles.Administrator) && !options.UserId.Equals(User.GetUserId()))
        {
            return Forbid();
        }

        if (session.Plan.StreamInfo is not null
            || !_v2PlanStore.TryGet(session.Id, out var record)
            || record?.ExecutionPlan is not { Method: PlaybackMethod.DirectPlay } plan
            || !options.ItemId.Equals(itemId)
            || !string.Equals(plan.SourceId, mediaSourceId, StringComparison.Ordinal)
            || string.IsNullOrEmpty(session.PlaySessionId)
            || !string.Equals(session.PlaySessionId, playSessionId, StringComparison.Ordinal))
        {
            return NotFound();
        }

        // Access can be withdrawn after planning (library access, parental rating, disabled
        // account): the session's owner must still see the item on every request.
        var owner = _userManager.GetUserById(options.UserId);
        if (owner is null || owner.HasPermission(PermissionKind.IsDisabled)
            || _itemAccessService.GetVisibleItemById<BaseItem>(itemId, owner!) is null)
        {
            return NotFound();
        }

        var source = options.MediaSources.FirstOrDefault(s => string.Equals(s.Id, plan.SourceId, StringComparison.Ordinal));
        if (!DirectPlayFilmPlanner.IsLocalFileSource(source) || !System.IO.File.Exists(source!.Path))
        {
            return NotFound();
        }

        return PhysicalFile(source.Path, MimeTypes.GetMimeType(source.Path), enableRangeProcessing: true);
    }
}
