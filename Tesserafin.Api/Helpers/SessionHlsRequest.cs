using System;
using System.Linq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Streaming;
using Tesserafin.Data;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations.Enums;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Execution;
using SubtitleDeliveryMethod = Tesserafin.Model.Dlna.SubtitleDeliveryMethod;

namespace Tesserafin.Api.Helpers;

/// <summary>
/// An encoding request built only on the server from a retained film plan. Unlike query-bound
/// requests it never asks the legacy executor to choose copy versus encode or reduce resolution.
/// The shared executor still owns FFmpeg command rendering and hardware/software selection.
/// </summary>
internal sealed class SessionHlsRequest : VideoRequestDto
{
    private const int DefaultAacBitrate = 192_000;
    private readonly PlaybackExecutionPlan _plan;

    internal SessionHlsRequest(PlaybackExecutionPlan plan)
    {
        _plan = plan;
        Container = plan.Container;
        SegmentContainer = plan.Container;
        MinSegments = 1;
        VideoCodec = plan.VideoCodec;
        AudioCodec = plan.AudioCodec;
        VideoStreamIndex = plan.VideoStreamIndex;
        AudioStreamIndex = plan.AudioStreamIndex;
        VideoBitRate = plan.VideoBitrate;
        AudioBitRate = plan.AudioBitrate;
        MaxAudioChannels = plan.AudioChannels;
        MaxWidth = plan.Resolution?.Width;
        MaxHeight = plan.Resolution?.Height;
        VideoRangeType = plan.VideoRange;
        AllowVideoStreamCopy = plan.Transforms.Contains(TransformKind.CopyVideo);
        AllowAudioStreamCopy = plan.Transforms.Contains(TransformKind.CopyAudio);
        EnableAutoStreamCopy = true;
        SubtitleMethod = SubtitleDeliveryMethod.External;
        BuiltFromRetainedPlan = true;
    }

    /// <summary>Gets a value indicating whether the resolved source and permissions let the retained plan execute.</summary>
    internal bool IsExecutable { get; private set; }

    /// <summary>
    /// Initializes output from the retained plan, checking source indices, copy feasibility and
    /// current encoding permissions. A refusal is returned by the HLS route before any job starts.
    /// TotalBitrate is a combined ceiling: its video budget excludes the selected audio's budget.
    /// </summary>
    /// <param name="state">The resolved source state.</param>
    /// <param name="helper">The shared command renderer and copy feasibility checks.</param>
    internal void Apply(StreamState state, EncodingHelper helper)
    {
        IsExecutable = false;
        var video = state.VideoStream;
        var audio = state.AudioStream;
        var encodeVideo = _plan.Transforms.Contains(TransformKind.TranscodeVideo);
        var encodeAudio = _plan.Transforms.Contains(TransformKind.TranscodeAudio);
        if (video?.Index != _plan.VideoStreamIndex || audio?.Index != _plan.AudioStreamIndex
            || (encodeVideo == AllowVideoStreamCopy)
            || (audio is not null && encodeAudio == AllowAudioStreamCopy)
            || (_plan.Method == PlaybackMethod.Transcode && video?.VideoRangeType is not (Tesserafin.Data.Enums.VideoRangeType.Unknown or Tesserafin.Data.Enums.VideoRangeType.SDR))
            || (encodeVideo && state.User?.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding) == false)
            || (encodeAudio && state.User?.HasPermission(PermissionKind.EnableAudioPlaybackTranscoding) == false))
        {
            return;
        }

        state.OutputContainer = _plan.Container;
        state.OutputVideoCodec = _plan.VideoCodec;
        state.OutputAudioCodec = _plan.AudioCodec;
        state.OutputAudioChannels = _plan.AudioChannels ?? audio?.Channels;

        if (AllowVideoStreamCopy && !helper.CanStreamCopyVideo(state, video))
        {
            return;
        }

        if (audio is not null && AllowAudioStreamCopy && !helper.CanStreamCopyAudio(state, audio, state.SupportedAudioCodecs))
        {
            return;
        }

        var audioBudget = audio is null ? 0 : encodeAudio
            ? Math.Min(_plan.AudioBitrate ?? DefaultAacBitrate, audio.BitRate ?? DefaultAacBitrate)
            : audio.BitRate;
        if (_plan.TotalBitrate is int total)
        {
            // An unknown copied bitrate cannot satisfy a known combined ceiling. Encoding an
            // audio track may use a smaller budget; it never silently changes a copied axis.
            if (audioBudget is null || total <= 0)
            {
                return;
            }

            if (encodeAudio)
            {
                audioBudget = Math.Min(audioBudget.Value, encodeVideo ? total / 4 : total);
            }

            if (encodeVideo)
            {
                var remaining = total - audioBudget.Value;
                if (remaining <= 0)
                {
                    return;
                }

                VideoBitRate = Math.Min(_plan.VideoBitrate ?? remaining, remaining);
            }
            else if (video?.BitRate is not int copiedVideoBitrate || (long)copiedVideoBitrate + audioBudget.Value > total)
            {
                return;
            }
        }

        if (encodeAudio && audioBudget is not > 0)
        {
            return;
        }

        AudioBitRate = encodeAudio ? audioBudget : null;
        state.OutputVideoCodec = AllowVideoStreamCopy ? "copy" : _plan.VideoCodec;
        state.OutputAudioCodec = AllowAudioStreamCopy ? "copy" : _plan.AudioCodec;
        state.OutputVideoBitrate = helper.GetVideoBitrateParamValue(this, video, state.OutputVideoCodec);
        state.OutputAudioBitrate = audioBudget;
        IsExecutable = true;
    }
}
