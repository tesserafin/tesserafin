using System;
using System.Linq;
using Tesserafin.Controller.Streaming;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Execution;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// The executor's input, rendered from a retained execution plan and nothing else
/// (docs/playback-path-inventory.md §5 and §6): which source and streams, which of them are copied,
/// what the others are encoded to, under which bitrate, resolution and channel ceilings. The
/// executor is told; it does not decide. Whatever the client's request carries is only compared
/// with the session, never read as an encoding option.
/// </summary>
public static class EnginePlanEncodingRequest
{
    /// <summary>
    /// The audio bitrate assumed per channel pair when the plan re-encodes audio and names no
    /// ceiling for it; used both as the encoder's target and as the audio share of a total ceiling.
    /// </summary>
    internal const int DefaultAudioBitratePerChannelPair = 128_000;

    /// <summary>
    /// The lowest video bitrate a total ceiling can be narrowed to once the audio share is taken.
    /// </summary>
    internal const int MinimumVideoBitrate = 100_000;

    /// <summary>
    /// The lowest bitrate encoded audio is given under a total ceiling.
    /// </summary>
    internal const int MinimumAudioBitrate = 32_000;

    /// <summary>
    /// Whether the plan copies its video stream.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> when the video stream is copied.</returns>
    public static bool CopiesVideo(PlaybackExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.VideoStreamIndex is not null && !plan.Transforms.Contains(TransformKind.TranscodeVideo);
    }

    /// <summary>
    /// Whether the plan copies its audio stream.
    /// </summary>
    /// <param name="plan">The execution plan.</param>
    /// <returns><see langword="true"/> when the audio stream is copied.</returns>
    public static bool CopiesAudio(PlaybackExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.AudioStreamIndex is not null && !plan.Transforms.Contains(TransformKind.TranscodeAudio);
    }

    /// <summary>
    /// Renders the encoding request of an executable HLS plan
    /// (<see cref="LocalMediaPlanner.IsExecutableHlsOutput"/>).
    /// </summary>
    /// <param name="plan">The retained execution plan.</param>
    /// <param name="itemId">The item the session plays.</param>
    /// <param name="playSessionId">The session's play session id; the output is named after it.</param>
    /// <param name="deviceId">The device the session was planned for.</param>
    /// <param name="source">The media source the plan selected, for the bitrate of a copied audio stream.</param>
    /// <param name="transcodeReasons">The session's recorded reasons, for the job's diagnostics.</param>
    /// <returns>
    /// The request the executor is started with: a <see cref="VideoRequestDto"/> when the plan has a
    /// video stream, otherwise an audio-only <see cref="StreamingRequestDto"/>, which the executor
    /// reads as an audio output (no video stream mapped, an embedded picture included).
    /// </returns>
    public static StreamingRequestDto Build(
        PlaybackExecutionPlan plan,
        Guid itemId,
        string? playSessionId,
        string? deviceId,
        MediaSourceInfo? source,
        string? transcodeReasons)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!LocalMediaPlanner.IsExecutableHlsOutput(plan))
        {
            throw new ArgumentException("Only an executable remux or transcode plan has an encoding request.", nameof(plan));
        }

        var copiesVideo = CopiesVideo(plan);
        var copiesAudio = CopiesAudio(plan);
        int? audioBitrate = copiesAudio || plan.AudioStreamIndex is null ? null : plan.AudioBitrate ?? DefaultAudioBitrate(plan, source);

        var request = plan.VideoStreamIndex is null ? new StreamingRequestDto() : new VideoRequestDto();
        request.Id = itemId;
        request.MediaSourceId = plan.SourceId;
        request.PlaySessionId = playSessionId;
        request.DeviceId = deviceId;
        request.SegmentContainer = plan.Container;
        request.MinSegments = 1;
        request.VideoCodec = plan.VideoCodec;
        // An audio-only request takes its output codec as it is: the executor weighs a stream copy
        // for video requests only. A copied audio stream is therefore named "copy" here.
        request.AudioCodec = plan.VideoStreamIndex is null && copiesAudio ? "copy" : plan.AudioCodec;
        request.VideoStreamIndex = plan.VideoStreamIndex;
        request.AudioStreamIndex = plan.AudioStreamIndex;
        request.EnableAutoStreamCopy = copiesVideo || copiesAudio;
        request.AllowVideoStreamCopy = copiesVideo;
        request.AllowAudioStreamCopy = copiesAudio;
        request.VideoBitRate = copiesVideo || plan.VideoStreamIndex is null ? null : VideoBitrate(plan, source, audioBitrate);
        request.MaxWidth = copiesVideo ? null : plan.Resolution?.Width;
        request.MaxHeight = copiesVideo ? null : plan.Resolution?.Height;
        request.AudioBitRate = audioBitrate;
        request.MaxAudioChannels = copiesAudio ? null : plan.AudioChannels;
        request.TranscodingMaxAudioChannels = copiesAudio ? null : plan.AudioChannels;
        // A burnt-in subtitle is part of the video encode: the executor overlays the plan's stream.
        // Any other subtitle is the client's to fetch beside the stream.
        var burns = plan.SubtitleDelivery == Tesserafin.Playback.Decision.SubtitleDeliveryMethod.Burn;
        request.SubtitleStreamIndex = burns ? plan.SubtitleStreamIndex : null;
        request.SubtitleMethod = burns ? Tesserafin.Model.Dlna.SubtitleDeliveryMethod.Encode : Tesserafin.Model.Dlna.SubtitleDeliveryMethod.External;
        request.Context = EncodingContext.Streaming;
        request.TranscodeReasons = transcodeReasons;
        request.BuiltFromRetainedPlan = true;

        // An opened tuner stream is read through its provider (pipe:0), which the executor finds by
        // this id; the stream's own path is a route the executor has no credential for.
        request.LiveStreamId = string.IsNullOrEmpty(source?.LiveStreamId) ? null : source.LiveStreamId;
        return request;
    }

    /// <summary>
    /// The video bitrate the encoder is given: the plan's own video ceiling, narrowed so that video
    /// plus audio stays under the plan's total ceiling. <see langword="null"/> when the plan bounds
    /// neither: the encoder then keeps the source's bitrate.
    /// </summary>
    private static int? VideoBitrate(PlaybackExecutionPlan plan, MediaSourceInfo? source, int? encodedAudioBitrate)
    {
        var video = plan.VideoBitrate;
        if (plan.TotalBitrate is int total)
        {
            var audioShare = plan.AudioStreamIndex is null
                ? 0
                : encodedAudioBitrate ?? AudioStream(plan, source)?.BitRate ?? 0;
            var remainder = Math.Max(total - audioShare, MinimumVideoBitrate);
            video = video is int ceiling ? Math.Min(ceiling, remainder) : remainder;
        }

        return video;
    }

    private static int DefaultAudioBitrate(PlaybackExecutionPlan plan, MediaSourceInfo? source)
    {
        var channels = plan.AudioChannels ?? AudioStream(plan, source)?.Channels ?? 2;
        var bitrate = DefaultAudioBitratePerChannelPair * Math.Clamp((channels + 1) / 2, 1, 4);

        // Under a total ceiling the encoded audio takes at most a quarter of it, so the ceiling
        // the client chose bounds the whole output, not only its video.
        return plan.TotalBitrate is int total
            ? Math.Min(bitrate, Math.Max(MinimumAudioBitrate, total / 4))
            : bitrate;
    }

    private static MediaStream? AudioStream(PlaybackExecutionPlan plan, MediaSourceInfo? source)
        => source?.MediaStreams?.FirstOrDefault(stream => stream.Type == MediaStreamType.Audio && stream.Index == plan.AudioStreamIndex);
}
