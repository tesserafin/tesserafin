using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Helpers;
using Tesserafin.Common.Api;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
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
/// The client names no file. Every request goes through <see cref="PlaybackSessionDeliveryGuard"/>:
/// the session is re-read, the caller must own it (or be an administrator) and its owner must
/// still see the item and the selected version; what is served is the source the retained plan
/// selected, from the source list retained when the session was planned.
/// </remarks>
[Route("Playback/Sessions")]
[Tags("Playback")]
public class PlaybackSessionFilesController : BaseTesserafinApiController
{
    private readonly PlaybackSessionDeliveryGuard _deliveryGuard;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionFilesController"/> class.
    /// </summary>
    /// <param name="deliveryGuard">Instance of <see cref="PlaybackSessionDeliveryGuard"/>.</param>
    public PlaybackSessionFilesController(PlaybackSessionDeliveryGuard deliveryGuard)
    {
        _deliveryGuard = deliveryGuard;
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
        var delivery = _deliveryGuard.Resolve(
            id,
            User,
            itemId,
            mediaSourceId,
            playSessionId,
            plan => plan.Method == PlaybackMethod.DirectPlay,
            allowAdministrator: true);
        if (delivery.Outcome == PlaybackSessionDeliveryOutcome.Forbidden)
        {
            return Forbid();
        }

        if (delivery.Outcome != PlaybackSessionDeliveryOutcome.Granted || delivery.Source is not { } source)
        {
            return NotFound();
        }

        return PhysicalFile(source.Path, MimeTypes.GetMimeType(source.Path), enableRangeProcessing: true);
    }
}
