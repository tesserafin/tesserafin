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
/// The HLS routes of a playback session the v2 engine alone decided to remux
/// (docs/playback-path-inventory.md §5). They live on this controller because they execute through
/// the same playlist generator, segment path, job ownership and transcode manager as every other
/// HLS output; what differs is where the encoding request comes from. Here the client names no
/// codec, container, stream or offset: the request is built from the session's retained execution
/// plan, and the item, media source and play session the client sends are only compared with it.
/// </summary>
public partial class DynamicHlsController
{
    /// <summary>
    /// Gets the media playlist of a session decided as a remux.
    /// </summary>
    /// <param name="id">The session.</param>
    /// <param name="itemId">The item the session plays; must match the session.</param>
    /// <param name="mediaSourceId">The media source the session's plan selected; must match it.</param>
    /// <param name="playSessionId">The session's play session id; must match it.</param>
    /// <param name="deliveryGuard">Instance of <see cref="PlaybackSessionDeliveryGuard"/>.</param>
    /// <response code="200">Playlist returned.</response>
    /// <response code="403">The caller does not own the session.</response>
    /// <response code="404">No such remux session, or nothing servable for it.</response>
    /// <response code="422">The retained plan cannot be executed as a stream copy.</response>
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
        var (refusal, request) = ResolveSessionRemux(deliveryGuard, id, itemId, mediaSourceId, playSessionId);
        if (refusal is not null)
        {
            return refusal;
        }

        using var cancellationTokenSource = new CancellationTokenSource();
        return await GetVariantPlaylistInternal(request!, cancellationTokenSource, sessionOwnedRemux: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a segment (or, at <c>-1</c>, the fMP4 initialisation segment) of a session decided as a
    /// remux, starting the session's own stream-copy job when the segment is not there yet.
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
    /// <response code="404">No such remux session, no such segment container, or nothing servable.</response>
    /// <response code="422">The source cannot be copied as the retained plan says.</response>
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
        var (refusal, request) = ResolveSessionRemux(deliveryGuard, id, itemId, mediaSourceId, playSessionId);
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
        return await GetDynamicSegment(request, segmentId, sessionOwnedRemux: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the state the executor resolved is the plan and nothing else: both streams copied,
    /// and they are the streams the plan selected. The executor falls back to a first stream when
    /// an index no longer exists (a file replaced since planning); that is a different playback,
    /// not this plan.
    /// </summary>
    private static bool IsThePlannedCopy(StreamState state, StreamingRequestDto request)
        => EncodingHelper.IsCopyCodec(state.OutputVideoCodec)
            && state.VideoStream?.Index == request.VideoStreamIndex
            && state.AudioStream?.Index == request.AudioStreamIndex
            && (state.AudioStream is null || EncodingHelper.IsCopyCodec(state.OutputAudioCodec));

    private ObjectResult RemuxNotExecutable() => UnprocessableEntity(new ProblemDetails
    {
        Status = StatusCodes.Status422UnprocessableEntity,
        Title = "Remux not executable",
        Detail = "The session's plan copies the selected streams, and this source cannot be copied as planned. Plan the session again without stream copy.",
    });

    /// <summary>
    /// Asks <see cref="PlaybackSessionDeliveryGuard"/> whether this caller is served this session's
    /// remux and, when it is, builds the encoding request from the retained plan alone. Only the
    /// session's owner: the job a segment request starts is owned by its caller, and the session's
    /// stop ends its owner's jobs.
    /// </summary>
    private (ActionResult? Refusal, VideoRequestDto? Request) ResolveSessionRemux(
        PlaybackSessionDeliveryGuard deliveryGuard,
        PlaybackSessionId id,
        Guid itemId,
        string mediaSourceId,
        string playSessionId)
    {
        var delivery = deliveryGuard.Resolve(id, User, itemId, mediaSourceId, playSessionId, EngineFilmPlanner.IsExecutableRemux, allowAdministrator: false);
        if (delivery.Outcome == PlaybackSessionDeliveryOutcome.Forbidden)
        {
            return (Forbid(), null);
        }

        if (delivery is not { Outcome: PlaybackSessionDeliveryOutcome.Granted, Plan: { } plan, Session: { } session, Options: { } options })
        {
            return (NotFound(), null);
        }

        return (null, new VideoRequestDto
        {
            Id = itemId,
            MediaSourceId = plan.SourceId,
            PlaySessionId = session.PlaySessionId,
            DeviceId = options.DeviceId,
            SegmentContainer = plan.Container,
            MinSegments = 1,
            VideoCodec = plan.VideoCodec,
            AudioCodec = plan.AudioCodec,
            VideoStreamIndex = plan.VideoStreamIndex,
            AudioStreamIndex = plan.AudioStreamIndex,
            EnableAutoStreamCopy = true,
            AllowVideoStreamCopy = true,
            AllowAudioStreamCopy = true,
            SubtitleMethod = SubtitleDeliveryMethod.External,
            Context = EncodingContext.Streaming,
            TranscodeReasons = nameof(Tesserafin.Model.Session.TranscodeReason.ContainerNotSupported),
            BuiltFromRetainedPlan = true,
        });
    }
}
