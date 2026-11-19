using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Helpers;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Streaming;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// The HLS routes of a playback session the v2 engine alone decided to remux or to transcode
/// (docs/playback-path-inventory.md §5 and §6). They live on this controller because they execute through
/// the same playlist generator, segment path, job ownership and transcode manager as every other
/// HLS output; what differs is where the encoding request comes from. Here the client names no
/// codec, container, stream or offset: the request is built from the session's retained execution
/// plan, and the item, media source and play session the client sends are only compared with it.
/// </summary>
public partial class DynamicHlsController
{
    /// <summary>
    /// Gets the media playlist of a session decided as a remux or a transcode.
    /// </summary>
    /// <param name="id">The session.</param>
    /// <param name="itemId">The item the session plays; must match the session.</param>
    /// <param name="mediaSourceId">The media source the session's plan selected; must match it.</param>
    /// <param name="playSessionId">The session's play session id; must match it.</param>
    /// <param name="deliveryGuard">Instance of <see cref="PlaybackSessionDeliveryGuard"/>.</param>
    /// <response code="200">Playlist returned.</response>
    /// <response code="403">The caller does not own the session.</response>
    /// <response code="404">No such session-executed plan, or nothing servable for it.</response>
    /// <response code="422">The retained plan cannot be executed as planned.</response>
    /// <returns>The media playlist, naming this session's segment route.</returns>
    [HttpGet("Playback/Sessions/{id}/Hls/main.m3u8")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    public async Task<ActionResult> GetPlaybackSessionHlsPlaylist(
        [FromRoute] PlaybackSessionId id,
        [FromQuery, Required] Guid itemId,
        [FromQuery, Required] string mediaSourceId,
        [FromQuery, Required] string playSessionId,
        [FromServices] PlaybackSessionDeliveryGuard deliveryGuard)
    {
        var (refusal, request) = ResolveSessionOutput(deliveryGuard, id, itemId, mediaSourceId, playSessionId);
        if (refusal is not null)
        {
            return refusal;
        }

        using var cancellationTokenSource = new CancellationTokenSource();
        return await GetVariantPlaylistInternal(request!, cancellationTokenSource, sessionOwned: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a segment (or, at <c>-1</c>, the fMP4 initialisation segment) of a session decided as a
    /// remux or a transcode, starting the session's own job when the segment is not there yet.
    /// </summary>
    /// <param name="id">The session.</param>
    /// <param name="playlistId">The playlist id the media playlist named.</param>
    /// <param name="segmentId">The segment index; <c>-1</c> for the initialisation segment.</param>
    /// <param name="container">The segment container the media playlist named.</param>
    /// <param name="runtimeTicks">The position of the segment, as the media playlist named it.</param>
    /// <param name="actualSegmentLengthTicks">The length of the segment, as the media playlist named it.</param>
    /// <param name="itemId">The item the session plays; must match the session.</param>
    /// <param name="mediaSourceId">The media source the session's plan selected; must match it.</param>
    /// <param name="playSessionId">The session's play session id; must match it.</param>
    /// <param name="deliveryGuard">Instance of <see cref="PlaybackSessionDeliveryGuard"/>.</param>
    /// <response code="200">Segment returned.</response>
    /// <response code="403">The session belongs to another user.</response>
    /// <response code="404">No such session-executed plan, no such segment container, or nothing servable.</response>
    /// <response code="422">The source cannot be copied or encoded as the retained plan says.</response>
    /// <returns>The segment file.</returns>
    [HttpGet("Playback/Sessions/{id}/Hls/hls1/{playlistId}/{segmentId}.{container}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesVideoFile]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "playlistId", Justification = "Part of the segment url the playlist generator writes.")]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    public async Task<ActionResult> GetPlaybackSessionHlsSegment(
        [FromRoute] PlaybackSessionId id,
        [FromRoute, Required] string playlistId,
        [FromRoute, Required] int segmentId,
        [FromRoute, Required][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string container,
        [FromQuery, Required] long runtimeTicks,
        [FromQuery, Required] long actualSegmentLengthTicks,
        [FromQuery, Required] Guid itemId,
        [FromQuery, Required] string mediaSourceId,
        [FromQuery, Required] string playSessionId,
        [FromServices] PlaybackSessionDeliveryGuard deliveryGuard)
    {
        var (refusal, request) = ResolveSessionOutput(deliveryGuard, id, itemId, mediaSourceId, playSessionId);
        if (refusal is not null)
        {
            return refusal;
        }

        // The segment container is the plan's; a url naming another one names nothing.
        if (!string.Equals(EncodingHelper.GetSegmentFileExtension(request!.SegmentContainer).TrimStart('.'), container, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        request.CurrentRuntimeTicks = runtimeTicks;
        request.ActualSegmentLengthTicks = actualSegmentLengthTicks;
        return await GetDynamicSegment(request, segmentId, sessionOwned: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the state the executor resolved is the plan and nothing else: the streams the plan
    /// selected, each copied exactly when the plan copies it, and each encoded stream encoded to
    /// the plan's codec; an audio plan is an audio output, with no video stream at all. The executor
    /// falls back to a first stream when an index no longer exists (a file replaced since planning,
    /// or, for an audio output, a plan that selected another than the source's default audio stream)
    /// and refuses a copy it cannot make; either is a different playback, not this plan.
    /// </summary>
    private static bool IsThePlan(StreamState state, StreamingRequestDto request)
        => state.VideoStream?.Index == request.VideoStreamIndex
            && state.AudioStream?.Index == request.AudioStreamIndex
            && (request is VideoRequestDto
                ? IsThePlannedCodec(state.OutputVideoCodec, request.AllowVideoStreamCopy, request.VideoCodec)
                : !state.IsOutputVideo && state.OutputVideoCodec is null)
            && (state.AudioStream is null || IsThePlannedCodec(state.OutputAudioCodec, request.AllowAudioStreamCopy, request.AudioCodec));

    private static bool IsThePlannedCodec(string? outputCodec, bool planCopies, string? plannedCodec)
        => planCopies
            ? EncodingHelper.IsCopyCodec(outputCodec)
            : !EncodingHelper.IsCopyCodec(outputCodec) && string.Equals(outputCodec, plannedCodec, StringComparison.OrdinalIgnoreCase);

    private ObjectResult PlanNotExecutable() => UnprocessableEntity(new ProblemDetails
    {
        Status = StatusCodes.Status422UnprocessableEntity,
        Title = "Plan not executable",
        Detail = "This source cannot be copied or encoded as the session's plan says. Plan the session again, forbidding the stream copy.",
    });

    /// <summary>
    /// Asks <see cref="PlaybackSessionDeliveryGuard"/> whether this caller is served this session's
    /// HLS output and, when it is, builds the encoding request from the retained plan alone
    /// (<see cref="EnginePlanEncodingRequest"/>). Only the session's owner: the job a segment
    /// request starts is owned by its caller, and the session's stop ends its owner's jobs.
    /// </summary>
    private (ActionResult? Refusal, StreamingRequestDto? Request) ResolveSessionOutput(
        PlaybackSessionDeliveryGuard deliveryGuard,
        PlaybackSessionId id,
        Guid itemId,
        string mediaSourceId,
        string playSessionId)
    {
        var delivery = deliveryGuard.Resolve(id, User, itemId, mediaSourceId, playSessionId, LocalMediaPlanner.IsExecutableHlsOutput, allowAdministrator: false);
        if (delivery.Outcome == PlaybackSessionDeliveryOutcome.Forbidden)
        {
            return (Forbid(), null);
        }

        if (delivery is not { Outcome: PlaybackSessionDeliveryOutcome.Granted, Plan: { } plan, Session: { } session, Options: { } options })
        {
            return (NotFound(), null);
        }

        return (null, EnginePlanEncodingRequest.Build(
            plan,
            itemId,
            session.PlaySessionId,
            options.DeviceId,
            delivery.Source,
            session.Plan.TranscodeReasons == 0 ? null : session.Plan.TranscodeReasons.ToString()));
    }
}
