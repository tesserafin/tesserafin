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
    /// <returns>The request the executor is started with.</returns>
    public static VideoRequestDto Build(
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

        return new VideoRequestDto
        {
            Id = itemId,
            MediaSourceId = plan.SourceId,
            PlaySessionId = playSessionId,
            DeviceId = deviceId,
            SegmentContainer = plan.Container,
            MinSegments = 1,
            VideoCodec = plan.VideoCodec,
            AudioCodec = plan.AudioCodec,
            VideoStreamIndex = plan.VideoStreamIndex,
            AudioStreamIndex = plan.AudioStreamIndex,
            EnableAutoStreamCopy = copiesVideo || copiesAudio,
            AllowVideoStreamCopy = copiesVideo,
            AllowAudioStreamCopy = copiesAudio,
            VideoBitRate = copiesVideo ? null : VideoBitrate(plan, source, audioBitrate),
            MaxWidth = copiesVideo ? null : plan.Resolution?.Width,
            MaxHeight = copiesVideo ? null : plan.Resolution?.Height,
            AudioBitRate = audioBitrate,
            MaxAudioChannels = copiesAudio ? null : plan.AudioChannels,
            TranscodingMaxAudioChannels = copiesAudio ? null : plan.AudioChannels,
            SubtitleMethod = Tesserafin.Model.Dlna.SubtitleDeliveryMethod.External,
            Context = EncodingContext.Streaming,
            TranscodeReasons = transcodeReasons,
            BuiltFromRetainedPlan = true,
        };
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
