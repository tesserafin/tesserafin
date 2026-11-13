using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Api.Extensions;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.Models.StreamingDtos;
using Tesserafin.Common.Api;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Controller.Streaming;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.MediaEncoding.Hls.Playlist;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.Net;
using Tesserafin.Model.Session;

namespace Tesserafin.Api.Controllers;

/// <summary>
/// Dynamic hls controller.
/// </summary>
[Route("")]
[Authorize(Policy = Policies.MediaDelivery)]
[ApiExplorerSettings(IgnoreApi = true)]
public partial class DynamicHlsController : BaseTesserafinApiController
{
    /// <summary>
    /// Response header naming what a client can do about a failed transcode: <c>software</c> when
    /// reloading the stream will be served by a software transcode, <c>none</c> otherwise.
    /// </summary>
    internal const string PlaybackRecoveryHeader = "X-Tesserafin-Playback-Recovery";

    private const EncoderPreset DefaultVodEncoderPreset = EncoderPreset.veryfast;
    private const EncoderPreset DefaultEventEncoderPreset = EncoderPreset.superfast;
    private const TranscodingJobType TranscodingJobType = Tesserafin.Controller.MediaEncoding.TranscodingJobType.Hls;

    private readonly Version _minFFmpegFlacInMp4 = new Version(6, 0);
    private readonly Version _minFFmpegX265BframeInFmp4 = new Version(7, 0, 1);
    private readonly Version _minFFmpegHlsSegmentOptions = new Version(5, 0);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IFileSystem _fileSystem;
    private readonly ITranscodeManager _transcodeManager;
    private readonly ILogger<DynamicHlsController> _logger;
    private readonly EncodingHelper _encodingHelper;
    private readonly IDynamicHlsPlaylistGenerator _dynamicHlsPlaylistGenerator;
    private readonly DynamicHlsHelper _dynamicHlsHelper;
    private readonly IPlaybackSessionManager _playbackSessionManager;
    private readonly IHlsJobOwnershipAuthorizer _jobOwnership;
    private EncodingOptions _encodingOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicHlsController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DynamicHlsController}"/> interface.</param>
    /// <param name="dynamicHlsHelper">Instance of <see cref="DynamicHlsHelper"/>.</param>
    /// <param name="encodingHelper">Instance of <see cref="EncodingHelper"/>.</param>
    /// <param name="dynamicHlsPlaylistGenerator">Instance of <see cref="IDynamicHlsPlaylistGenerator"/>.</param>
    /// <param name="playbackSessionManager">Instance of the <see cref="IPlaybackSessionManager"/> interface.</param>
    /// <param name="jobOwnership">Instance of the <see cref="IHlsJobOwnershipAuthorizer"/> interface.</param>
    public DynamicHlsController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IServerConfigurationManager serverConfigurationManager,
        IMediaEncoder mediaEncoder,
        IFileSystem fileSystem,
        ITranscodeManager transcodeManager,
        ILogger<DynamicHlsController> logger,
        DynamicHlsHelper dynamicHlsHelper,
        EncodingHelper encodingHelper,
        IDynamicHlsPlaylistGenerator dynamicHlsPlaylistGenerator,
        IPlaybackSessionManager playbackSessionManager,
        IHlsJobOwnershipAuthorizer jobOwnership)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _serverConfigurationManager = serverConfigurationManager;
        _mediaEncoder = mediaEncoder;
        _fileSystem = fileSystem;
        _transcodeManager = transcodeManager;
        _logger = logger;
        _dynamicHlsHelper = dynamicHlsHelper;
        _encodingHelper = encodingHelper;
        _dynamicHlsPlaylistGenerator = dynamicHlsPlaylistGenerator;
        _playbackSessionManager = playbackSessionManager;
        _jobOwnership = jobOwnership;

        _encodingOptions = serverConfigurationManager.GetEncodingOptions();
    }

    /// <summary>
    /// Gets a hls live stream.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="container">The audio container.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="maxWidth">Optional. The max width.</param>
    /// <param name="maxHeight">Optional. The max height.</param>
    /// <param name="enableSubtitlesInManifest">Optional. Whether to enable subtitles in the manifest.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <param name="alwaysBurnInSubtitleWhenTranscoding">Whether to always burn in subtitles when transcoding.</param>
    /// <response code="200">Hls live stream retrieved.</response>
    /// <returns>A <see cref="FileResult"/> containing the hls file.</returns>
    [HttpGet("Videos/{itemId}/live.m3u8")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    public async Task<ActionResult> GetLiveHlsStream(
        [FromRoute, Required] Guid itemId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? container,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery][RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] int? maxWidth,
        [FromQuery] int? maxHeight,
        [FromQuery] bool? enableSubtitlesInManifest,
        [FromQuery] bool enableAudioVbrEncoding = true,
        [FromQuery] bool alwaysBurnInSubtitleWhenTranscoding = false)
    {
        VideoRequestDto streamingRequest = new VideoRequestDto
        {
            Id = itemId,
            Container = container,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            MaxHeight = maxHeight,
            MaxWidth = maxWidth,
            EnableSubtitlesInManifest = enableSubtitlesInManifest ?? true,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = alwaysBurnInSubtitleWhenTranscoding
        };

        // CTS lifecycle is managed internally.
        var cancellationTokenSource = new CancellationTokenSource();
        // Due to CTS.Token calling ThrowIfDisposed (https://github.com/dotnet/runtime/issues/29970) we have to "cache" the token
        // since it gets disposed when ffmpeg exits
        var cancellationToken = cancellationTokenSource.Token;
        var state = await ResolveStreamingState(streamingRequest, cancellationToken)
            .ConfigureAwait(false);

        TranscodingJob? job = null;
        var playlistPath = Path.ChangeExtension(state.OutputFilePath, ".m3u8");

        // #153-LTV-R3. This path is MD5(mediaPath-userAgent-deviceId-playSessionId), and the
        // device, the play session and the User-Agent all come from the caller: a second
        // authenticated user replaying this url arrives at the same file. A live job that this
        // caller does not own is a refusal, and a residual playlist whose job is gone is not
        // served either — but a caller whose own job has been reaped may still start a new one.
        var ownership = _jobOwnership.AuthorizeByOutputPath(HttpContext, playlistPath);
        if (ownership.Outcome == HlsJobOwnershipOutcome.Refused
            || (!ownership.IsAuthorized && System.IO.File.Exists(playlistPath)))
        {
            state.Dispose();
            return Unauthorized();
        }

        // tesserafin#289, on this route. The attempt that wrote this playlist failed and software
        // takes over from it: neither its playlist, while it is still there, nor a second process
        // started in its place on the same play session. The caller is told, as on the segment route.
        if (ownership.IsAuthorized
            && _transcodeManager.GetTranscodeFailure(playlistPath, TranscodingJobType) is { Decision.ShouldFallback: true } failedAttempt)
        {
            state.Dispose();
            return TranscodeFailed(failedAttempt);
        }

        if (!System.IO.File.Exists(playlistPath))
        {
            // tesserafin#119: the live command is built with the options in force, too.
            _encodingOptions = _transcodeManager.GetEffectiveEncodingOptions(streamingRequest.MediaSourceId) ?? _encodingOptions;

            using (await _transcodeManager.LockAsync(playlistPath, cancellationToken).ConfigureAwait(false))
            {
                if (!System.IO.File.Exists(playlistPath))
                {
                    // Asked again under the lock: the failure can have been published, and the
                    // playlist removed, since the look above. So is who owns this output - the job
                    // may not have been registered yet when that was decided.
                    ownership = _jobOwnership.AuthorizeByOutputPath(HttpContext, playlistPath);
                    if (ownership.Outcome == HlsJobOwnershipOutcome.Refused)
                    {
                        state.Dispose();
                        return Unauthorized();
                    }

                    if (ownership.IsAuthorized
                        && _transcodeManager.GetTranscodeFailure(playlistPath, TranscodingJobType) is { Decision.ShouldFallback: true } failedMeanwhile)
                    {
                        state.Dispose();
                        return TranscodeFailed(failedMeanwhile);
                    }

                    // If the playlist doesn't already exist, startup ffmpeg
                    try
                    {
                        if (!ownership.IsAuthorized)
                        {
                            // The playlist is gone and its segments need not be: a removal takes
                            // the files in the directory's order, and can stop half-way.
                            RemoveUnownedOutput(playlistPath);
                        }

                        job = await _transcodeManager.StartFfMpeg(
                                state,
                                playlistPath,
                                GetCommandLineArguments(playlistPath, state, true, 0),
                                Request.HttpContext.User.GetUserId(),
                                TranscodingJobType,
                                cancellationTokenSource)
                            .ConfigureAwait(false);
                        job.IsLiveOutput = true;
                    }
                    catch (Tesserafin.Common.FfmpegException) when (_transcodeManager.GetTranscodeFailure(playlistPath, TranscodingJobType) is not null)
                    {
                        // ffmpeg ended before its first output, and why is known: answered as on
                        // the segment route rather than as an unexplained 500.
                        state.Dispose();
                        return TranscodeFailed(_transcodeManager.GetTranscodeFailure(playlistPath, TranscodingJobType)!);
                    }
                    catch
                    {
                        state.Dispose();
                        throw;
                    }

                    minSegments = state.MinSegments;
                    if (minSegments > 0)
                    {
                        // This wait holds the output's lock. A transcode that ends by itself cancels
                        // nothing, so the wait asks: without that it never ended, and neither did
                        // anything else that needs this lock.
                        var started = job;
                        await HlsHelpers.WaitForMinimumSegmentCount(playlistPath, minSegments, _logger, () => started.HasExited, cancellationToken).ConfigureAwait(false);
                    }

                    if (job.Failure is { } failure)
                    {
                        state.Dispose();
                        return TranscodeFailed(failure);
                    }
                }
            }
        }

        if (job is null)
        {
            // This request started nothing, so the playlist it is about to read is somebody's
            // job's - possibly one registered since ownership was decided above, when there was
            // neither a job nor a file. Decided again, on what is there now.
            var owner = _jobOwnership.AuthorizeByOutputPath(HttpContext, playlistPath);
            if (owner.Outcome == HlsJobOwnershipOutcome.Refused || !owner.IsAuthorized)
            {
                state.Dispose();
                return Unauthorized();
            }

            job = _transcodeManager.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
        }

        if (job is not null)
        {
            _transcodeManager.OnTranscodeEndRequest(job);
        }

        var playlistText = HlsHelpers.GetLivePlaylistText(playlistPath, state);

        // #153-LTV-S1. ffmpeg wrote that file with `-hls_base_url "hls/{hash}/"`, so its segment
        // uris are bare relative paths with no query at all — and the legacy route that serves them
        // demands a capability. The client cannot add one: it never sees these uris until it reads
        // this response. So the capability THIS request was authorized with is added here, to the
        // response only. The file on disk, ffmpeg's argv, its environment and its logs are all
        // untouched by construction.
        // #153-LTV-R1. The source is named in exactly one place — PlaybackCapabilityProvenance —
        // so "the propagated value came from a validated result" is a decision a test can reach
        // rather than a line a reviewer has to read. LTV-R0's control M1 replaced the read below
        // with Request.Query["playbackCapability"] and nothing in the repository went red.
        var provenance = PlaybackCapabilityProvenance.Resolve(HttpContext);
        if (provenance.Outcome == PlaybackCapabilityProvenanceOutcome.Refuse)
        {
            return Unauthorized();
        }

        if (provenance.Outcome == PlaybackCapabilityProvenanceOutcome.Propagate)
        {
            var validated = provenance.Capability!;
            playlistText = HlsManifestCredentialPropagator.Propagate(
                playlistText,
                validated.Value,
                validated.MediaSourceId,
                new Uri($"{Request.Scheme}://{Request.Host}"),
                validated.PlaySessionId);

            // The body now carries a credential. A shared cache holding it would hand one user's
            // capability to the next reader of the same url.
            Response.Headers.CacheControl = "private, no-store";
        }

        return Content(playlistText, MimeTypes.GetMimeType("playlist.m3u8"));
    }

    /// <summary>
    /// Gets a video hls playlist stream.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="maxWidth">Optional. The maximum horizontal resolution of the encoded video.</param>
    /// <param name="maxHeight">Optional. The maximum vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAdaptiveBitrateStreaming">Enable adaptive bitrate streaming.</param>
    /// <param name="enableTrickplay">Enable trickplay image playlists being added to master playlist.</param>
    /// <param name="enableAudioVbrEncoding">Whether to enable Audio Encoding.</param>
    /// <param name="alwaysBurnInSubtitleWhenTranscoding">Whether to always burn in subtitles when transcoding.</param>
    /// <response code="200">Video stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the playlist file.</returns>
    [HttpGet("Videos/{itemId}/master.m3u8")]
    [HttpHead("Videos/{itemId}/master.m3u8", Name = "HeadMasterHlsVideoPlaylist")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    public async Task<ActionResult> GetMasterHlsVideoPlaylist(
        [FromRoute, Required] Guid itemId,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery, Required] string mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery][RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? maxWidth,
        [FromQuery] int? maxHeight,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAdaptiveBitrateStreaming = false,
        [FromQuery] bool enableTrickplay = true,
        [FromQuery] bool enableAudioVbrEncoding = true,
        [FromQuery] bool alwaysBurnInSubtitleWhenTranscoding = false)
    {
        var streamingRequest = new HlsVideoRequestDto
        {
            Id = itemId,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAdaptiveBitrateStreaming = enableAdaptiveBitrateStreaming,
            EnableTrickplay = enableTrickplay,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = alwaysBurnInSubtitleWhenTranscoding
        };

        return await _dynamicHlsHelper.GetMasterHlsPlaylist(TranscodingJobType, streamingRequest, enableAdaptiveBitrateStreaming).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets an audio hls playlist stream.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="maxStreamingBitrate">Optional. The maximum streaming bitrate.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAdaptiveBitrateStreaming">Enable adaptive bitrate streaming.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <response code="200">Audio stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the playlist file.</returns>
    [HttpGet("Audio/{itemId}/master.m3u8")]
    [HttpHead("Audio/{itemId}/master.m3u8", Name = "HeadMasterHlsAudioPlaylist")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    public async Task<ActionResult> GetMasterHlsAudioPlaylist(
        [FromRoute, Required] Guid itemId,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery, Required] string mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? maxStreamingBitrate,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery][RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAdaptiveBitrateStreaming = false,
        [FromQuery] bool enableAudioVbrEncoding = true)
    {
        var streamingRequest = new HlsAudioRequestDto
        {
            Id = itemId,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate ?? maxStreamingBitrate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAdaptiveBitrateStreaming = enableAdaptiveBitrateStreaming,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = false
        };

        return await _dynamicHlsHelper.GetMasterHlsPlaylist(TranscodingJobType, streamingRequest, enableAdaptiveBitrateStreaming).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a video stream using HTTP live streaming.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="maxWidth">Optional. The maximum horizontal resolution of the encoded video.</param>
    /// <param name="maxHeight">Optional. The maximum vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <param name="alwaysBurnInSubtitleWhenTranscoding">Whether to always burn in subtitles when transcoding.</param>
    /// <response code="200">Video stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
    [HttpGet("Videos/{itemId}/main.m3u8")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    public async Task<ActionResult> GetVariantHlsVideoPlaylist(
        [FromRoute, Required] Guid itemId,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery][RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? maxWidth,
        [FromQuery] int? maxHeight,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAudioVbrEncoding = true,
        [FromQuery] bool alwaysBurnInSubtitleWhenTranscoding = false)
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var streamingRequest = new VideoRequestDto
        {
            Id = itemId,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = alwaysBurnInSubtitleWhenTranscoding
        };

        return await GetVariantPlaylistInternal(streamingRequest, cancellationTokenSource)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets an audio stream using HTTP live streaming.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="maxStreamingBitrate">Optional. The maximum streaming bitrate.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <response code="200">Audio stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
    [HttpGet("Audio/{itemId}/main.m3u8")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesPlaylistFile]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    public async Task<ActionResult> GetVariantHlsAudioPlaylist(
        [FromRoute, Required] Guid itemId,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? maxStreamingBitrate,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery][RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAudioVbrEncoding = true)
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var streamingRequest = new StreamingRequestDto
        {
            Id = itemId,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate ?? maxStreamingBitrate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = false
        };

        return await GetVariantPlaylistInternal(streamingRequest, cancellationTokenSource)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a video stream using HTTP live streaming.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="container">The video container. Possible values are: ts, webm, asf, wmv, ogv, mp4, m4v, mkv, mpeg, mpg, avi, 3gp, wmv, wtv, m2ts, mov, iso, flv. </param>
    /// <param name="runtimeTicks">The position of the requested segment in ticks.</param>
    /// <param name="actualSegmentLengthTicks">The length of the requested segment in ticks.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The desired segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="maxWidth">Optional. The maximum horizontal resolution of the encoded video.</param>
    /// <param name="maxHeight">Optional. The maximum vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <param name="alwaysBurnInSubtitleWhenTranscoding">Whether to always burn in subtitles when transcoding.</param>
    /// <response code="200">Video stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
    [HttpGet("Videos/{itemId}/hls1/{playlistId}/{segmentId}.{container}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesVideoFile]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "playlistId", Justification = "Imported from ServiceStack")]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    [RejectsStartTimeTicks("startTimeTicks")]
    public async Task<ActionResult> GetHlsVideoSegment(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] string playlistId,
        [FromRoute, Required] int segmentId,
        [FromRoute, Required][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string container,
        [FromQuery, Required] long runtimeTicks,
        [FromQuery, Required] long actualSegmentLengthTicks,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery][RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? maxWidth,
        [FromQuery] int? maxHeight,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAudioVbrEncoding = true,
        [FromQuery] bool alwaysBurnInSubtitleWhenTranscoding = false)
    {
        var streamingRequest = new VideoRequestDto
        {
            Id = itemId,
            CurrentRuntimeTicks = runtimeTicks,
            ActualSegmentLengthTicks = actualSegmentLengthTicks,
            Container = container,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = alwaysBurnInSubtitleWhenTranscoding
        };

        return await GetDynamicSegment(streamingRequest, segmentId)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a video stream using HTTP live streaming.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="playlistId">The playlist id.</param>
    /// <param name="segmentId">The segment id.</param>
    /// <param name="container">The video container. Possible values are: ts, webm, asf, wmv, ogv, mp4, m4v, mkv, mpeg, mpg, avi, 3gp, wmv, wtv, m2ts, mov, iso, flv. </param>
    /// <param name="runtimeTicks">The position of the requested segment in ticks.</param>
    /// <param name="actualSegmentLengthTicks">The length of the requested segment in ticks.</param>
    /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
    /// <param name="params">The streaming parameters.</param>
    /// <param name="tag">The tag.</param>
    /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
    /// <param name="playSessionId">The play session id.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <param name="segmentLength">The segment length.</param>
    /// <param name="minSegments">The minimum number of segments.</param>
    /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
    /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
    /// <param name="audioCodec">Optional. Specify an audio codec to encode to, e.g. mp3.</param>
    /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
    /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
    /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
    /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
    /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
    /// <param name="maxStreamingBitrate">Optional. The maximum streaming bitrate.</param>
    /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
    /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
    /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
    /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
    /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
    /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
    /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
    /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
    /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
    /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
    /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
    /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
    /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
    /// <param name="maxRefFrames">Optional.</param>
    /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
    /// <param name="requireAvc">Optional. Whether to require avc.</param>
    /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
    /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
    /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
    /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
    /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264.</param>
    /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
    /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
    /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
    /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
    /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
    /// <param name="streamOptions">Optional. The streaming options.</param>
    /// <param name="enableAudioVbrEncoding">Optional. Whether to enable Audio Encoding.</param>
    /// <response code="200">Video stream returned.</response>
    /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
    [HttpGet("Audio/{itemId}/hls1/{playlistId}/{segmentId}.{container}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesAudioFile]
    [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "playlistId", Justification = "Imported from ServiceStack")]
    [RequiresPlaybackCapability(PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId")]
    [RejectsStartTimeTicks("startTimeTicks")]
    public async Task<ActionResult> GetHlsAudioSegment(
        [FromRoute, Required] Guid itemId,
        [FromRoute, Required] string playlistId,
        [FromRoute, Required] int segmentId,
        [FromRoute, Required][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string container,
        [FromQuery, Required] long runtimeTicks,
        [FromQuery, Required] long actualSegmentLengthTicks,
        [FromQuery] bool? @static,
        [FromQuery] string? @params,
        [FromQuery] string? tag,
        [FromQuery, ParameterObsolete] string? deviceProfileId,
        [FromQuery] string? playSessionId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? segmentContainer,
        [FromQuery] int? segmentLength,
        [FromQuery] int? minSegments,
        [FromQuery] string? mediaSourceId,
        [FromQuery] string? deviceId,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? audioCodec,
        [FromQuery] bool? enableAutoStreamCopy,
        [FromQuery] bool? allowVideoStreamCopy,
        [FromQuery] bool? allowAudioStreamCopy,
        [FromQuery] int? audioSampleRate,
        [FromQuery] int? maxAudioBitDepth,
        [FromQuery] int? maxStreamingBitrate,
        [FromQuery] int? audioBitRate,
        [FromQuery] int? audioChannels,
        [FromQuery] int? maxAudioChannels,
        [FromQuery] string? profile,
        [FromQuery][RegularExpression(EncodingHelper.LevelValidationRegexStr)] string? level,
        [FromQuery] float? framerate,
        [FromQuery] float? maxFramerate,
        [FromQuery] bool? copyTimestamps,
        [FromQuery] long? startTimeTicks,
        [FromQuery] int? width,
        [FromQuery] int? height,
        [FromQuery] int? videoBitRate,
        [FromQuery] int? subtitleStreamIndex,
        [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
        [FromQuery] int? maxRefFrames,
        [FromQuery] int? maxVideoBitDepth,
        [FromQuery] bool? requireAvc,
        [FromQuery] bool? deInterlace,
        [FromQuery] bool? requireNonAnamorphic,
        [FromQuery] int? transcodingMaxAudioChannels,
        [FromQuery] int? cpuCoreLimit,
        [FromQuery] string? liveStreamId,
        [FromQuery] bool? enableMpegtsM2TsMode,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? videoCodec,
        [FromQuery][RegularExpression(EncodingHelper.ContainerValidationRegexStr)] string? subtitleCodec,
        [FromQuery] string? transcodeReasons,
        [FromQuery] int? audioStreamIndex,
        [FromQuery] int? videoStreamIndex,
        [FromQuery] EncodingContext? context,
        [FromQuery] Dictionary<string, string> streamOptions,
        [FromQuery] bool enableAudioVbrEncoding = true)
    {
        var streamingRequest = new StreamingRequestDto
        {
            Id = itemId,
            Container = container,
            CurrentRuntimeTicks = runtimeTicks,
            ActualSegmentLengthTicks = actualSegmentLengthTicks,
            Static = @static ?? false,
            Params = @params,
            Tag = tag,
            PlaySessionId = playSessionId,
            SegmentContainer = segmentContainer,
            SegmentLength = segmentLength,
            MinSegments = minSegments,
            MediaSourceId = mediaSourceId,
            DeviceId = deviceId,
            AudioCodec = audioCodec,
            EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
            AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
            AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
            AudioSampleRate = audioSampleRate,
            MaxAudioChannels = maxAudioChannels,
            AudioBitRate = audioBitRate ?? maxStreamingBitrate,
            MaxAudioBitDepth = maxAudioBitDepth,
            AudioChannels = audioChannels,
            Profile = profile,
            Level = level,
            Framerate = framerate,
            MaxFramerate = maxFramerate,
            CopyTimestamps = copyTimestamps ?? false,
            StartTimeTicks = startTimeTicks,
            Width = width,
            Height = height,
            VideoBitRate = videoBitRate,
            SubtitleStreamIndex = subtitleStreamIndex,
            SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.External,
            MaxRefFrames = maxRefFrames,
            MaxVideoBitDepth = maxVideoBitDepth,
            RequireAvc = requireAvc ?? false,
            DeInterlace = deInterlace ?? false,
            RequireNonAnamorphic = requireNonAnamorphic ?? false,
            TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
            CpuCoreLimit = cpuCoreLimit,
            LiveStreamId = liveStreamId,
            EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
            VideoCodec = videoCodec,
            SubtitleCodec = subtitleCodec,
            TranscodeReasons = transcodeReasons,
            AudioStreamIndex = audioStreamIndex,
            VideoStreamIndex = videoStreamIndex,
            Context = context ?? EncodingContext.Streaming,
            StreamOptions = streamOptions,
            EnableAudioVbrEncoding = enableAudioVbrEncoding,
            AlwaysBurnInSubtitleWhenTranscoding = false
        };

        return await GetDynamicSegment(streamingRequest, segmentId)
            .ConfigureAwait(false);
    }

    // Resolves the StreamState for a streaming request. Called from GetLiveHlsStream,
    // GetVariantPlaylistInternal, and GetDynamicSegment, each with the same 7 dependencies —
    // collapsed here instead of duplicating the call at each site.
    private Task<StreamState> ResolveStreamingState(StreamingRequestDto streamingRequest, CancellationToken cancellationToken)
        => StreamingHelpers.GetStreamingState(
            streamingRequest,
            HttpContext,
            _mediaSourceManager,
            _userManager,
            _libraryManager,
            _serverConfigurationManager,
            _mediaEncoder,
            _encodingHelper,
            _transcodeManager,
            TranscodingJobType,
            cancellationToken);

    private async Task<ActionResult> GetVariantPlaylistInternal(StreamingRequestDto streamingRequest, CancellationTokenSource cancellationTokenSource, bool sessionOwnedRemux = false)
    {
        using var state = await ResolveStreamingState(streamingRequest, cancellationTokenSource.Token)
            .ConfigureAwait(false);
        var mediaSourceId = state.BaseRequest.MediaSourceId;
        double fps = state.TargetFramerate ?? 0.0f;
        int segmentLength = state.SegmentLength * 1000;

        if (sessionOwnedRemux)
        {
            // The session already exists and already holds its plan (PlaybackSessionDeliveryGuard
            // keeps it alive): nothing is tracked or replaced here, and a state that is not the
            // plan - another stream, or one this server would have to encode - is refused.
            if (!IsThePlannedCopy(state, streamingRequest))
            {
                return RemuxNotExecutable();
            }
        }
        else
        {
            // Track this session for diagnostics using the codecs/reasons the client already decided
            // (this controller does not itself plan direct-play-vs-transcode). The session ends
            // automatically when the transcoding job with this PlaySessionId ends.
            _playbackSessionManager.TrackTranscodeOutput(state.OutputVideoCodec, state.OutputAudioCodec, state.TranscodeReasons, streamingRequest.PlaySessionId);
        }

        // If video is transcoded and framerate is fractional (i.e. 23.976), we need to slightly adjust segment length
        if (!EncodingHelper.IsCopyCodec(state.OutputVideoCodec) && Math.Abs(fps - Math.Floor(fps + 0.001f)) > 0.001)
        {
            double nearestIntFramerate = Math.Ceiling(fps);
            segmentLength = (int)Math.Ceiling(segmentLength * (nearestIntFramerate / fps));
        }

        var request = new CreateMainPlaylistRequest(
            mediaSourceId is null ? null : Guid.Parse(mediaSourceId),
            state.MediaPath,
            segmentLength,
            state.RunTimeTicks ?? 0,
            state.Request.SegmentContainer ?? string.Empty,
            "hls1/main/",
            Request.QueryString.ToString(),
            EncodingHelper.IsCopyCodec(state.OutputVideoCodec));
        var playlist = _dynamicHlsPlaylistGenerator.CreateMainPlaylist(request);

        return new FileContentResult(Encoding.UTF8.GetBytes(playlist), MimeTypes.GetMimeType("playlist.m3u8"));
    }

    private async Task<ActionResult> GetDynamicSegment(StreamingRequestDto streamingRequest, int segmentId, bool sessionOwnedRemux = false)
    {
        // NO BRANCH ON THE REQUEST BEFORE THE AUTHORIZER, BY CONSTRUCTION. A caller-supplied
        // StartTimeTicks is refused by [RejectsStartTimeTicks] on both entry actions, at the MVC
        // boundary, before this method is entered at all. It used to be refused here, by a guard
        // that threw — and a guard here is a query parameter deciding whether
        // _jobOwnership.AuthorizeByOutputPath below runs, which is exactly the shape CodeQL's
        // cs/user-controlled-bypass reports. The ownership decision is unconditional in this
        // method and must stay that way; anything this route cannot honour belongs on the filter.
        //
        // CTS lifecycle is managed internally.
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        var state = await ResolveStreamingState(streamingRequest, cancellationToken)
            .ConfigureAwait(false);

        var playlistPath = Path.ChangeExtension(state.OutputFilePath, ".m3u8");

        // tesserafin#119. Any command built below uses the options that hold for this source
        // now, which is not always the stored configuration: see GetEffectiveEncodingOptions.
        _encodingOptions = _transcodeManager.GetEffectiveEncodingOptions(streamingRequest.MediaSourceId) ?? _encodingOptions;

        // #153-LTV-R3. This path is MD5(mediaPath-userAgent-deviceId-playSessionId): the device,
        // the play session and the User-Agent all come from the caller, so a second authenticated
        // user replaying this url arrives at the same file. The decision is taken here, once,
        // before any file is named — the fMP4 init map is this same route at segmentId -1 and is
        // covered by it too.
        var ownership = _jobOwnership.AuthorizeByOutputPath(HttpContext, playlistPath);
        if (ownership.Outcome == HlsJobOwnershipOutcome.Refused)
        {
            state.Dispose();
            return Unauthorized();
        }

        // A session-owned remux is executed from the engine's retained plan, and that plan copies
        // both streams. The executor does not re-decide: a state that would encode is refused
        // before anything is served or started. Derived from the server's own plan and source,
        // and placed after the ownership decision above, which stays unconditional.
        if (sessionOwnedRemux && !IsThePlannedCopy(state, streamingRequest))
        {
            state.Dispose();
            return RemuxNotExecutable();
        }

        // NoSuchJob is not authorization to read what is already on disk. A finished job's
        // segments outlive it, and those are unreachable rather than served to whoever names them
        // — so nothing below may return a pre-existing file unless a live job says this caller
        // owns it. What NoSuchJob DOES still allow is starting a new transcode, which is how a
        // client whose own job has been reaped resumes.
        var mayServeWhatIsAlreadyThere = ownership.IsAuthorized;

        var segmentPath = GetSegmentPath(state, playlistPath, segmentId);

        var segmentExtension = EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer);

        TranscodingJob? job;

        // tesserafin#119. The job that was writing here ended with a failure, and this segment is
        // not something it finished. Starting another process from a segment request is what kept
        // a dead device being retried forever, so the caller is told instead: it reloads the
        // stream, once, and the failed job is removed when it does.
        if (mayServeWhatIsAlreadyThere
            && await AnswerFromWhatIsThere(state, playlistPath, segmentPath, segmentExtension, segmentId, cancellationToken).ConfigureAwait(false) is { } answer)
        {
            return answer;
        }

        using (await _transcodeManager.LockAsync(playlistPath, cancellationToken).ConfigureAwait(false))
        {
            var startTranscoding = false;

            // Who owns this output is decided again now that nothing can start on it: the attempt
            // the decision above was taken on can have been replaced while this request waited,
            // and by one that is not this caller's. What is stopped below is what is found here.
            ownership = _jobOwnership.AuthorizeByOutputPath(HttpContext, playlistPath);
            if (ownership.Outcome == HlsJobOwnershipOutcome.Refused)
            {
                state.Dispose();
                return Unauthorized();
            }

            mayServeWhatIsAlreadyThere = ownership.IsAuthorized;

            if (!mayServeWhatIsAlreadyThere)
            {
                try
                {
                    RemoveUnownedOutput(playlistPath);
                }
                catch
                {
                    state.Dispose();
                    throw;
                }
            }

            // tesserafin#289. Asked again now that nothing can start, or finish being removed, on
            // this output: the look above and this lock are not one step, and a failure published
            // in between must not be answered by a new process.
            if (mayServeWhatIsAlreadyThere
                && await AnswerFromWhatIsThere(state, playlistPath, segmentPath, segmentExtension, segmentId, cancellationToken).ConfigureAwait(false) is { } answerUnderLock)
            {
                return answerUnderLock;
            }

            // Only an attempt whose failure answer has expired gets this far. Its last file is the
            // one it was writing when it ended, and is not left for its successor to serve.
            var replacesFailedAttempt = _transcodeManager.GetTranscodingJob(playlistPath, TranscodingJobType)?.Failure is not null;

            var currentTranscodingIndex = GetCurrentTranscodingIndex(playlistPath, segmentExtension);
            var segmentGapRequiringTranscodingChange = 24 / state.SegmentLength;

            if (segmentId == -1)
            {
                _logger.LogDebug("Starting transcoding because fmp4 init file is being requested");
                startTranscoding = true;
                segmentId = 0;
            }
            else if (currentTranscodingIndex is null)
            {
                _logger.LogDebug("Starting transcoding because currentTranscodingIndex=null");
                startTranscoding = true;
            }
            else if (segmentId < currentTranscodingIndex.Value)
            {
                _logger.LogDebug("Starting transcoding because requestedIndex={0} and currentTranscodingIndex={1}", segmentId, currentTranscodingIndex);
                startTranscoding = true;
            }
            else if (segmentId - currentTranscodingIndex.Value > segmentGapRequiringTranscodingChange)
            {
                _logger.LogDebug("Starting transcoding because segmentGap is {0} and max allowed gap is {1}. requestedIndex={2}", segmentId - currentTranscodingIndex.Value, segmentGapRequiringTranscodingChange, segmentId);
                startTranscoding = true;
            }

            if (startTranscoding)
            {
                // If the playlist doesn't already exist, startup ffmpeg
                // tesserafin#289. Listing the output took time, and the attempt that looked healthy
                // above can have failed meanwhile. Asked once more before it is stopped as if it
                // were: past this point a failure that is published late finds a successor.
                if (mayServeWhatIsAlreadyThere
                    && _transcodeManager.GetTranscodeFailure(playlistPath, TranscodingJobType) is { } lateFailure)
                {
                    state.Dispose();
                    return TranscodeFailed(lateFailure);
                }

                try
                {
                    // The attempt on this output, which this caller was just found to own - and
                    // no other. The device and play session ids are the caller's to send: one that
                    // sends neither would select every transcode started the same way, and one
                    // that sends somebody else's would select theirs.
                    if (ownership.Binding is { } owned)
                    {
                        await _transcodeManager.StopTranscodingJob(playlistPath, TranscodingJobType, owned.Generation, p => false)
                            .ConfigureAwait(false);
                    }

                    if (currentTranscodingIndex.HasValue || replacesFailedAttempt)
                    {
                        await DeleteLastFile(playlistPath, segmentExtension, 0).ConfigureAwait(false);
                    }

                    streamingRequest.StartTimeTicks = streamingRequest.CurrentRuntimeTicks;

                    state.WaitForPath = segmentPath;
                    job = await _transcodeManager.StartFfMpeg(
                        state,
                        playlistPath,
                        GetCommandLineArguments(playlistPath, state, false, segmentId),
                        Request.HttpContext.User.GetUserId(),
                        TranscodingJobType,
                        cancellationTokenSource).ConfigureAwait(false);
                }
                catch (Tesserafin.Common.FfmpegException) when (_transcodeManager.GetTranscodeFailure(playlistPath, TranscodingJobType) is not null)
                {
                    // ffmpeg ended before its first output, and why is known. Answered like any
                    // other failed job rather than as an unexplained 500.
                    state.Dispose();
                    return TranscodeFailed(_transcodeManager.GetTranscodeFailure(playlistPath, TranscodingJobType)!);
                }
                catch
                {
                    state.Dispose();
                    throw;
                }

                // await WaitForMinimumSegmentCount(playlistPath, 1, cancellationTokenSource.Token).ConfigureAwait(false);
            }
            else
            {
                job = _transcodeManager.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
                if (job?.TranscodingThrottler is not null)
                {
                    await job.TranscodingThrottler.UnpauseTranscoding().ConfigureAwait(false);
                }
            }
        }

        _logger.LogDebug("returning {0} [general case]", segmentPath);
        job ??= _transcodeManager.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
        return await GetSegmentResult(state, playlistPath, segmentPath, segmentExtension, segmentId, job, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves the segment if it is on disk and may be served, or says the attempt that was to
    /// write it failed. Neither is the case when a transcode has to be started, or waited for.
    /// </summary>
    /// <remarks>
    /// tesserafin#289. THE ORDER IS THE POINT: the file is looked at first and the attempt's state
    /// read after. The manager publishes a failure before it removes anything, so a file found
    /// missing because of that removal always comes with its reason. An attempt that failed is
    /// not one that finished: when software takes over nothing it wrote is served, and otherwise
    /// only a segment it had moved on from - the one it was writing may be truncated.
    /// </remarks>
    private async Task<ActionResult?> AnswerFromWhatIsThere(
        StreamState state,
        string playlistPath,
        string segmentPath,
        string segmentExtension,
        int segmentId,
        CancellationToken cancellationToken)
    {
        var servable = _fileSystem.FileExists(segmentPath);
        var failed = _transcodeManager.GetTranscodingJob(playlistPath, TranscodingJobType)?.Failure;
        if (servable && failed is not null)
        {
            servable = !failed.Decision.ShouldFallback && IsFinished(state, playlistPath, segmentId);
        }

        if (servable)
        {
            var job = _transcodeManager.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
            _logger.LogDebug("returning {0} [it exists]", segmentPath);
            return await GetSegmentResult(state, playlistPath, segmentPath, segmentExtension, segmentId, job, cancellationToken).ConfigureAwait(false);
        }

        // For as long as the manager keeps the answer. After that the caller starts afresh.
        if (_transcodeManager.GetTranscodeFailure(playlistPath, TranscodingJobType) is { } knownFailure)
        {
            state.Dispose();
            return TranscodeFailed(knownFailure);
        }

        return null;
    }

    /// <summary>
    /// Whether an attempt that failed had finished this file: it had if it went on to the next
    /// one. The initialisation segment is written before any segment and is never the last file.
    /// </summary>
    private bool IsFinished(StreamState state, string playlistPath, int segmentId)
        => segmentId == -1 || _fileSystem.FileExists(GetSegmentPath(state, playlistPath, segmentId + 1));

    private static double[] GetSegmentLengths(StreamState state)
        => GetSegmentLengthsInternal(state.RunTimeTicks ?? 0, state.SegmentLength);

    internal static double[] GetSegmentLengthsInternal(long runtimeTicks, int segmentlength)
    {
        var segmentLengthTicks = TimeSpan.FromSeconds(segmentlength).Ticks;
        var wholeSegments = runtimeTicks / segmentLengthTicks;
        var remainingTicks = runtimeTicks % segmentLengthTicks;

        var segmentsLen = wholeSegments + (remainingTicks == 0 ? 0 : 1);
        var segments = new double[segmentsLen];
        for (int i = 0; i < wholeSegments; i++)
        {
            segments[i] = segmentlength;
        }

        if (remainingTicks != 0)
        {
            segments[^1] = TimeSpan.FromTicks(remainingTicks).TotalSeconds;
        }

        return segments;
    }

    private string GetCommandLineArguments(string outputPath, StreamState state, bool isEventPlaylist, int startNumber)
    {
        var videoCodec = _encodingHelper.GetVideoEncoder(state, _encodingOptions);
        var threads = EncodingHelper.GetNumberOfThreads(state, _encodingOptions, videoCodec);

        var mapArgs = state.IsOutputVideo ? _encodingHelper.GetMapArgs(state) : string.Empty;

        var directory = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath));
        var outputFileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputPath);
        var outputPrefix = Path.Combine(directory, outputFileNameWithoutExtension);
        var outputExtension = EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer);
        var outputTsArg = outputPrefix + "%d" + outputExtension;

        var segmentFormat = string.Empty;
        var segmentContainer = outputExtension.TrimStart('.');
        var inputModifier = _encodingHelper.GetInputModifier(state, _encodingOptions, segmentContainer);
        var hlsArguments = $"-hls_playlist_type {(isEventPlaylist ? "event" : "vod")} -hls_list_size 0";

        if (string.Equals(segmentContainer, "ts", StringComparison.OrdinalIgnoreCase))
        {
            segmentFormat = "mpegts";
        }
        else if (string.Equals(segmentContainer, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            var outputFmp4HeaderArg = OperatingSystem.IsWindows() switch
            {
                // on Windows, the path of fmp4 header file needs to be configured
                true => " -hls_fmp4_init_filename \"" + outputPrefix + "-1" + outputExtension + "\"",
                // on Linux/Unix, ffmpeg generate fmp4 header file to m3u8 output folder
                false => " -hls_fmp4_init_filename \"" + outputFileNameWithoutExtension + "-1" + outputExtension + "\""
            };

            var useLegacySegmentOption = _mediaEncoder.EncoderVersion < _minFFmpegHlsSegmentOptions;

            if (state.VideoStream is not null && state.IsOutputVideo)
            {
                // fMP4 needs this flag to write the audio packet DTS/PTS including the initial delay into MOOF::TRAF::TFDT
                hlsArguments += $" {(useLegacySegmentOption ? "-hls_ts_options" : "-hls_segment_options")} movflags=+frag_discont";
            }

            segmentFormat = "fmp4" + outputFmp4HeaderArg;
        }
        else
        {
            _logger.LogError("Invalid HLS segment container: {SegmentContainer}, default to mpegts", segmentContainer);
            segmentFormat = "mpegts";
        }

        var maxMuxingQueueSize = _encodingOptions.MaxMuxingQueueSize > 128
            ? _encodingOptions.MaxMuxingQueueSize.ToString(CultureInfo.InvariantCulture)
            : "128";

        var baseUrlParam = string.Empty;
        if (isEventPlaylist)
        {
            baseUrlParam = string.Format(
                CultureInfo.InvariantCulture,
                " -hls_base_url \"hls/{0}/\"",
                Path.GetFileNameWithoutExtension(outputPath));
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} -map_metadata -1 -map_chapters -1 -threads {2} {3} {4} {5} -copyts -avoid_negative_ts disabled -max_muxing_queue_size {6} -f hls -max_delay 5000000 -hls_time {7} -hls_segment_type {8} -start_number {9}{10} -hls_segment_filename \"{11}\" {12} -y \"{13}\"",
            inputModifier,
            _encodingHelper.GetInputArgument(state, _encodingOptions, segmentContainer),
            threads,
            mapArgs,
            GetVideoArguments(state, startNumber, isEventPlaylist, segmentContainer),
            GetAudioArguments(state),
            maxMuxingQueueSize,
            state.SegmentLength.ToString(CultureInfo.InvariantCulture),
            segmentFormat,
            startNumber.ToString(CultureInfo.InvariantCulture),
            baseUrlParam,
            EncodingUtils.NormalizePath(outputTsArg),
            hlsArguments,
            EncodingUtils.NormalizePath(outputPath)).Trim();
    }

    /// <summary>
    /// Gets the audio arguments for transcoding.
    /// </summary>
    /// <param name="state">The <see cref="StreamState"/>.</param>
    /// <returns>The command line arguments for audio transcoding.</returns>
    private string GetAudioArguments(StreamState state)
    {
        if (state.AudioStream is null)
        {
            return string.Empty;
        }

        var audioCodec = _encodingHelper.GetAudioEncoder(state);
        var bitStreamArgs = _encodingHelper.GetAudioBitStreamArguments(state, state.Request.SegmentContainer, state.MediaSource.Container);

        // opus, dts, truehd and flac (in FFmpeg 5 and older) are experimental in mp4 muxer
        var strictArgs = string.Empty;
        var actualOutputAudioCodec = state.ActualOutputAudioCodec;
        if (string.Equals(actualOutputAudioCodec, "opus", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actualOutputAudioCodec, "dts", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actualOutputAudioCodec, "truehd", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(actualOutputAudioCodec, "flac", StringComparison.OrdinalIgnoreCase)
                && _mediaEncoder.EncoderVersion < _minFFmpegFlacInMp4))
        {
            strictArgs = " -strict -2";
        }

        if (!state.IsOutputVideo)
        {
            var audioTranscodeParams = string.Empty;

            // -vn to drop any video streams
            audioTranscodeParams += "-vn";

            if (EncodingHelper.IsCopyCodec(audioCodec))
            {
                return audioTranscodeParams + " -acodec copy" + bitStreamArgs + strictArgs;
            }

            audioTranscodeParams += " -acodec " + audioCodec + bitStreamArgs + strictArgs;

            var audioBitrate = state.OutputAudioBitrate;
            var audioChannels = state.OutputAudioChannels;

            if (audioBitrate.HasValue && !EncodingHelper.LosslessAudioCodecs.Contains(state.ActualOutputAudioCodec, StringComparison.OrdinalIgnoreCase))
            {
                var vbrParam = _encodingHelper.GetAudioVbrModeParam(audioCodec, audioBitrate.Value, audioChannels ?? 2);
                if (_encodingOptions.EnableAudioVbr && state.EnableAudioVbrEncoding && vbrParam is not null)
                {
                    audioTranscodeParams += vbrParam;
                }
                else
                {
                    audioTranscodeParams += " -ab " + audioBitrate.Value.ToString(CultureInfo.InvariantCulture);
                }
            }

            if (audioChannels.HasValue)
            {
                audioTranscodeParams += " -ac " + audioChannels.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (state.OutputAudioSampleRate.HasValue)
            {
                audioTranscodeParams += " -ar " + state.OutputAudioSampleRate.Value.ToString(CultureInfo.InvariantCulture);
            }

            return audioTranscodeParams;
        }

        if (EncodingHelper.IsCopyCodec(audioCodec))
        {
            var videoCodec = _encodingHelper.GetVideoEncoder(state, _encodingOptions);
            var copyArgs = "-codec:a:0 copy" + bitStreamArgs + strictArgs;

            return copyArgs;
        }

        var args = "-codec:a:0 " + audioCodec + bitStreamArgs + strictArgs;

        var channels = state.OutputAudioChannels;

        var useDownMixAlgorithm = DownMixAlgorithmsHelper.AlgorithmFilterStrings.ContainsKey((_encodingOptions.DownMixStereoAlgorithm, DownMixAlgorithmsHelper.InferChannelLayout(state.AudioStream)));

        if (channels.HasValue
            && (channels.Value != 2
                || (state.AudioStream?.Channels is not null && !useDownMixAlgorithm)))
        {
            args += " -ac " + channels.Value;
        }

        var bitrate = state.OutputAudioBitrate;
        if (bitrate.HasValue && !EncodingHelper.LosslessAudioCodecs.Contains(actualOutputAudioCodec, StringComparison.OrdinalIgnoreCase))
        {
            var vbrParam = _encodingHelper.GetAudioVbrModeParam(audioCodec, bitrate.Value, channels ?? 2);
            if (_encodingOptions.EnableAudioVbr && state.EnableAudioVbrEncoding && vbrParam is not null)
            {
                args += vbrParam;
            }
            else
            {
                args += " -ab " + bitrate.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (state.OutputAudioSampleRate.HasValue)
        {
            args += " -ar " + state.OutputAudioSampleRate.Value.ToString(CultureInfo.InvariantCulture);
        }
        else if (state.AudioStream?.CodecTag is not null && state.AudioStream.CodecTag.Equals("ac-4", StringComparison.Ordinal))
        {
            // ac-4 audio tends to have a super weird sample rate that will fail most encoders
            // force resample it to 48KHz
            args += " -ar 48000";
        }

        args += _encodingHelper.GetAudioFilterParam(state, _encodingOptions);

        return args;
    }

    /// <summary>
    /// Gets the video arguments for transcoding.
    /// </summary>
    /// <param name="state">The <see cref="StreamState"/>.</param>
    /// <param name="startNumber">The first number in the hls sequence.</param>
    /// <param name="isEventPlaylist">Whether the playlist is EVENT or VOD.</param>
    /// <param name="segmentContainer">The segment container.</param>
    /// <returns>The command line arguments for video transcoding.</returns>
    private string GetVideoArguments(StreamState state, int startNumber, bool isEventPlaylist, string segmentContainer)
    {
        if (state.VideoStream is null)
        {
            return string.Empty;
        }

        if (!state.IsOutputVideo)
        {
            return string.Empty;
        }

        var codec = _encodingHelper.GetVideoEncoder(state, _encodingOptions);

        var args = "-codec:v:0 " + codec;

        var isActualOutputVideoCodecAv1 = string.Equals(state.ActualOutputVideoCodec, "av1", StringComparison.OrdinalIgnoreCase);
        var isActualOutputVideoCodecHevc = string.Equals(state.ActualOutputVideoCodec, "h265", StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase);

        if (isActualOutputVideoCodecHevc || isActualOutputVideoCodecAv1)
        {
            var requestedRange = state.GetRequestedRangeTypes(state.ActualOutputVideoCodec);
            // Clients reporting Dolby Vision capabilities with fallbacks may only support the fallback layer.
            // Only enable Dolby Vision remuxing if the client explicitly declares support for profiles without fallbacks.
            var clientSupportsDoVi = requestedRange.Contains(VideoRangeType.DOVI.ToString(), StringComparison.OrdinalIgnoreCase);
            var videoIsDoVi = EncodingHelper.IsDovi(state.VideoStream);

            if (EncodingHelper.IsCopyCodec(codec)
                && (videoIsDoVi && clientSupportsDoVi)
                && !_encodingHelper.IsDoviRemoved(state))
            {
                if (isActualOutputVideoCodecHevc)
                {
                    // Use hvc1 for 8.4. This is what Dolby uses for its official sample streams. Tagging with dvh1 would break some players with strict tag checking like Apple Safari.
                    var codecTag = state.VideoStream.VideoRangeType == VideoRangeType.DOVIWithHLG ? "hvc1" : "dvh1";
                    args += $" -tag:v:0 {codecTag} -strict -2";
                }
                else if (isActualOutputVideoCodecAv1)
                {
                    args += " -tag:v:0 dav1 -strict -2";
                }
            }
            else if (isActualOutputVideoCodecHevc)
            {
                // Prefer hvc1 to hev1
                args += " -tag:v:0 hvc1";
            }
        }

        // if  (state.EnableMpegtsM2TsMode)
        // {
        //     args += " -mpegts_m2ts_mode 1";
        // }

        // See if we can save come cpu cycles by avoiding encoding.
        if (EncodingHelper.IsCopyCodec(codec))
        {
            // If h264_mp4toannexb is ever added, do not use it for live tv.
            if (state.VideoStream is not null && !string.Equals(state.VideoStream.NalLengthSize, "0", StringComparison.OrdinalIgnoreCase))
            {
                string bitStreamArgs = _encodingHelper.GetBitStreamArgs(state, MediaStreamType.Video);
                if (!string.IsNullOrEmpty(bitStreamArgs))
                {
                    args += " " + bitStreamArgs;
                }
            }

            args += " -start_at_zero";
        }
        else
        {
            args += _encodingHelper.GetVideoQualityParam(state, codec, _encodingOptions, isEventPlaylist ? DefaultEventEncoderPreset : DefaultVodEncoderPreset);

            // Set the key frame params for video encoding to match the hls segment time.
            args += _encodingHelper.GetHlsVideoKeyFrameArguments(state, codec, state.SegmentLength, isEventPlaylist, startNumber);

            // Currently b-frames in libx265 breaks the FMP4-HLS playback on iOS, disable it for now.
            if (string.Equals(codec, "libx265", StringComparison.OrdinalIgnoreCase)
                && _mediaEncoder.EncoderVersion < _minFFmpegX265BframeInFmp4)
            {
                args += " -bf 0";
            }

            // video processing filters.
            var videoProcessParam = _encodingHelper.GetVideoProcessingFilterParam(state, _encodingOptions, codec);

            var negativeMapArgs = _encodingHelper.GetNegativeMapArgsByFilters(state, videoProcessParam);

            args = negativeMapArgs + args + videoProcessParam;

            // -start_at_zero is necessary to use with -ss when seeking,
            // otherwise the target position cannot be determined.
            if (state.SubtitleStream is not null)
            {
                // Disable start_at_zero for external graphical subs
                if (!(state.SubtitleStream.IsExternal && !state.SubtitleStream.IsTextSubtitleStream))
                {
                    args += " -start_at_zero";
                }
            }
        }

        // TODO why was this not enabled for VOD?
        if (isEventPlaylist && string.Equals(segmentContainer, "ts", StringComparison.OrdinalIgnoreCase))
        {
            args += " -flags -global_header";
        }

        if (!string.IsNullOrEmpty(state.OutputVideoSync))
        {
            args += EncodingHelper.GetVideoSyncOption(state.OutputVideoSync, _mediaEncoder.EncoderVersion);
        }

        args += _encodingHelper.GetOutputFFlags(state);

        return args;
    }

    private string GetSegmentPath(StreamState state, string playlist, int index)
    {
        var folder = Path.GetDirectoryName(playlist) ?? throw new ArgumentException($"Provided path ({playlist}) is not valid.", nameof(playlist));
        var filename = Path.GetFileNameWithoutExtension(playlist);

        return Path.Combine(folder, filename + index.ToString(CultureInfo.InvariantCulture) + EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer));
    }

    private async Task<ActionResult> GetSegmentResult(
        StreamState state,
        string playlistPath,
        string segmentPath,
        string segmentExtension,
        int segmentIndex,
        TranscodingJob? transcodingJob,
        CancellationToken cancellationToken)
    {
        var nextSegmentPath = GetSegmentPath(state, playlistPath, segmentIndex + 1);

        // The request for the initialisation segment that started the transcode arrives here as
        // index 0 with the initialisation segment's path. It is that file's rule that applies.
        var isInitialisation = string.Equals(segmentPath, GetSegmentPath(state, playlistPath, -1), StringComparison.Ordinal);
        var segmentExists = _fileSystem.FileExists(segmentPath);
        if (segmentExists)
        {
            if (transcodingJob is not null && transcodingJob.HasExited)
            {
                // tesserafin#289. Over is not finished: see AnswerFromWhatIsThere. The job can have
                // failed since that was asked.
                if (transcodingJob.Failure is { } failed
                    && (failed.Decision.ShouldFallback || !(isInitialisation || IsFinished(state, playlistPath, segmentIndex))))
                {
                    state.Dispose();
                    _transcodeManager.OnTranscodeEndRequest(transcodingJob);
                    return TranscodeFailed(failed);
                }

                // Transcoding job is over, so assume all existing files are ready
                _logger.LogDebug("serving up {0} as transcode is over", segmentPath);
                return GetSegmentResult(state, segmentPath, transcodingJob);
            }

            var currentTranscodingIndex = GetCurrentTranscodingIndex(playlistPath, segmentExtension);

            // If requested segment is less than transcoding position, we can't transcode backwards, so assume it's ready
            if (segmentIndex < currentTranscodingIndex)
            {
                _logger.LogDebug("serving up {0} as transcode index {1} is past requested point {2}", segmentPath, currentTranscodingIndex, segmentIndex);
                return GetSegmentResult(state, segmentPath, transcodingJob);
            }
        }

        if (transcodingJob is not null)
        {
            while (!cancellationToken.IsCancellationRequested && !transcodingJob.HasExited)
            {
                // To be considered ready, the segment file has to exist AND
                // either the transcoding job should be done or next segment should also exist
                if (segmentExists)
                {
                    if (transcodingJob.HasExited || _fileSystem.FileExists(nextSegmentPath))
                    {
                        _logger.LogDebug("Serving up {SegmentPath} as it deemed ready", segmentPath);
                        return GetSegmentResult(state, segmentPath, transcodingJob);
                    }
                }
                else
                {
                    segmentExists = _fileSystem.FileExists(segmentPath);
                    if (segmentExists)
                    {
                        continue; // avoid unnecessary waiting if segment just became available
                    }
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            // tesserafin#289. A file that was being waited on when the job failed is the one the
            // job was writing, unless the job had already moved on from it.
            if (transcodingJob.Failure is { } failure
                && (failure.Decision.ShouldFallback || !_fileSystem.FileExists(segmentPath) || !(isInitialisation || IsFinished(state, playlistPath, segmentIndex))))
            {
                _logger.LogWarning("cannot serve {0} as transcoding failed before we got there", segmentPath);
                state.Dispose();
                _transcodeManager.OnTranscodeEndRequest(transcodingJob);
                return TranscodeFailed(failure);
            }

            if (!_fileSystem.FileExists(segmentPath))
            {
                _logger.LogWarning("cannot serve {0} as transcoding quit before we got there", segmentPath);
            }
            else
            {
                _logger.LogDebug("serving {0} as it's on disk and transcoding stopped", segmentPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        else
        {
            _logger.LogWarning("cannot serve {0} as it doesn't exist and no transcode is running", segmentPath);
        }

        return GetSegmentResult(state, segmentPath, transcodingJob);
    }

    /// <summary>
    /// Answers a request for output a failed transcode never produced.
    /// </summary>
    /// <remarks>
    /// 410, not 5xx: the resource is gone and will not come back at this address, and a 4xx is
    /// what stops an HLS client from retrying the same segment. The header tells the caller
    /// whether reloading the stream can help - it can when the server has since switched this
    /// playback to software. Nothing about the process, its command or any path is disclosed.
    /// </remarks>
    private ObjectResult TranscodeFailed(TranscodeFailure failure)
        => TranscodeFailed(Response, failure);

    internal static ObjectResult TranscodeFailed(HttpResponse response, TranscodeFailure failure)
    {
        var recovery = failure.Decision.ShouldFallback ? "software" : "none";
        response.Headers[PlaybackRecoveryHeader] = recovery;
        response.Headers.CacheControl = "no-store";

        return new ObjectResult(new { title = "The transcode for this stream ended with a failure.", recovery }) { StatusCode = StatusCodes.Status410Gone };
    }

    private ActionResult GetSegmentResult(StreamState state, string segmentPath, TranscodingJob? transcodingJob)
    {
        var segmentEndingPositionTicks = state.Request.CurrentRuntimeTicks + state.Request.ActualSegmentLengthTicks;

        Response.OnCompleted(() =>
        {
            _logger.LogDebug("Finished serving {SegmentPath}", segmentPath);
            if (transcodingJob is not null)
            {
                transcodingJob.DownloadPositionTicks = Math.Max(transcodingJob.DownloadPositionTicks ?? segmentEndingPositionTicks, segmentEndingPositionTicks);
                _transcodeManager.OnTranscodeEndRequest(transcodingJob);
            }

            return Task.CompletedTask;
        });

        return FileStreamResponseHelpers.GetStaticFileResult(segmentPath, MimeTypes.GetMimeType(segmentPath));
    }

    /// <summary>
    /// Removes what is on an output that no job is registered on, before anything is started there.
    /// </summary>
    /// <remarks>
    /// POLISH-2-R4. ONLY UNDER THE OUTPUT'S LOCK, AND ONLY ONCE NO HLS JOB WAS FOUND ON IT THERE.
    /// An output's path is derived from what the client sends - the User-Agent, the device id,
    /// the play session id - so a second user replaying the first user's url arrives at the first
    /// user's files. While a job is registered the caller is compared with its owner. Once it is
    /// released there is nobody to compare with, and its files are still there: for the second
    /// and a half the removal waits, for good when the removal fails, and until the next start
    /// when the release asked for none. A transcode started on such an output found the file it
    /// was to wait for already present, and the caller was served it - bytes produced for
    /// somebody else, by a request the server had just authorized to start its own.
    ///
    /// So an output with no job is nobody's, its former owner included, and nothing on it is
    /// reused: it is emptied before the start, and the start does not happen if it cannot be.
    /// The server does not have to remember who a released output was for, and no client-named
    /// id is taken as proof of it.
    ///
    /// A job that is stopped stays registered until its process has been waited for
    /// (TranscodeManager.StopJob), and a process that outlives its kill, or one whose start
    /// failed, keeps its job: an output does not get this far with that process still writing.
    ///
    /// A PROGRESSIVE TRANSCODE'S FILE IS NOT THIS OUTPUT'S. It carries the same name with another
    /// extension, in the same folder, and its job is not an HLS job: finding no HLS job here says
    /// nothing about it, and it may be running. It is left alone here, and the legacy routes
    /// open nothing but "{name}{index}.{ext}" (HlsSegmentController.NamesASegmentOf).
    /// </remarks>
    private void RemoveUnownedOutput(string playlistPath)
    {
        var directory = Path.GetDirectoryName(playlistPath) ?? throw new ArgumentException("Path can't be a root directory.", nameof(playlistPath));
        if (!Directory.Exists(directory))
        {
            return;
        }

        // ffmpeg is given "{name}%d.{ext}", "{name}-1.{ext}" and "{name}.m3u8". "{name}.{ext}"
        // alone is what a progressive transcode of the same request writes.
        var name = Path.GetFileNameWithoutExtension(playlistPath);
        foreach (var file in _fileSystem.GetFilePaths(directory))
        {
            var fileName = Path.GetFileName(file);
            var isProgressiveSibling = string.Equals(Path.GetFileNameWithoutExtension(fileName), name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetExtension(fileName), ".m3u8", StringComparison.OrdinalIgnoreCase);

            if (fileName.StartsWith(name, StringComparison.OrdinalIgnoreCase) && !isProgressiveSibling)
            {
                _logger.LogDebug("Removing {Path}: left on an output no job is registered on", file);
                _fileSystem.DeleteFile(file);
            }
        }
    }

    private int? GetCurrentTranscodingIndex(string playlist, string segmentExtension)
    {
        var job = _transcodeManager.GetTranscodingJob(playlist, TranscodingJobType);

        if (job is null || job.HasExited)
        {
            return null;
        }

        var file = GetLastTranscodingFile(playlist, segmentExtension, _fileSystem);

        if (file is null)
        {
            return null;
        }

        var playlistFilename = Path.GetFileNameWithoutExtension(playlist.AsSpan());

        var indexString = Path.GetFileNameWithoutExtension(file.Name.AsSpan()).Slice(playlistFilename.Length);

        return int.Parse(indexString, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static FileSystemMetadata? GetLastTranscodingFile(string playlist, string segmentExtension, IFileSystem fileSystem)
    {
        var folder = Path.GetDirectoryName(playlist) ?? throw new ArgumentException("Path can't be a root directory.", nameof(playlist));

        var filePrefix = Path.GetFileNameWithoutExtension(playlist);

        try
        {
            return fileSystem.GetFiles(folder, new[] { segmentExtension }, true, false)
                .Where(i => Path.GetFileNameWithoutExtension(i.Name).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                .MaxBy(fileSystem.GetLastWriteTimeUtc);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task DeleteLastFile(string playlistPath, string segmentExtension, int retryCount)
    {
        var file = GetLastTranscodingFile(playlistPath, segmentExtension, _fileSystem);

        if (file is null)
        {
            return;
        }

        await DeleteFile(file.FullName, retryCount).ConfigureAwait(false);
    }

    private async Task DeleteFile(string path, int retryCount)
    {
        if (retryCount >= 5)
        {
            return;
        }

        _logger.LogDebug("Deleting partial HLS file {Path}", path);

        try
        {
            _fileSystem.DeleteFile(path);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);

            await Task.Delay(100).ConfigureAwait(false);
            await DeleteFile(path, retryCount + 1).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);
        }
    }
}
