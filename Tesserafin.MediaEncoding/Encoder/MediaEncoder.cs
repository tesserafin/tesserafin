#nullable disable
#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tesserafin.Common;
using Tesserafin.Common.Configuration;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Extensions;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Streaming;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Drawing;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;

namespace Tesserafin.MediaEncoding.Encoder
{
    /// <summary>
    /// Class MediaEncoder.
    /// </summary>
    public partial class MediaEncoder : IMediaEncoder, ISoftwareVideoEncoderCapabilities, IDisposable
    {
        /// <summary>
        /// The default SDR image extraction timeout in milliseconds.
        /// </summary>
        internal const int DefaultSdrImageExtractionTimeout = 10000;

        /// <summary>
        /// The default HDR image extraction timeout in milliseconds.
        /// </summary>
        internal const int DefaultHdrImageExtractionTimeout = 20000;

        private readonly ILogger<MediaEncoder> _logger;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IFileSystem _fileSystem;
        private readonly IBlurayExaminer _blurayExaminer;
        private readonly IConfiguration _config;
        private readonly IServerConfigurationManager _serverConfig;
        private readonly string _startupOptionFFmpegPath;

        private readonly FfmpegImageScheduler _images;

        private readonly MediaProbeService _probes;

        private readonly IFfmpegProcessRunner _processRunner;

        private HardwareCapabilitySnapshot _capabilities = HardwareCapabilitySnapshot.Empty;

        // This start's hardware selection. Null until SetFFmpegPath has run; after that it is the
        // authoritative record of what was decided and why, and it always agrees with the backend
        // the command-assembly path will use.
        private HardwareSelectionDecision _hardwareSelection;

        private bool _isPkeyPauseSupported = false;
        private bool _isLowPriorityHwDecodeSupported = false;
        private bool _proberSupportsFirstVideoFrame = false;

        private bool _isVideoToolboxAv1DecodeAvailable = false;

        private static string[] _vulkanImageDrmFmtModifierExts =
        {
            "VK_EXT_image_drm_format_modifier",
        };

        private static string[] _vulkanExternalMemoryDmaBufExts =
        {
            "VK_KHR_external_memory_fd",
            "VK_EXT_external_memory_dma_buf",
            "VK_KHR_external_semaphore_fd",
            "VK_EXT_external_memory_host"
        };

        private Version _ffmpegVersion = null;
        private string _ffmpegPath = string.Empty;
        private string _ffprobePath;
        private int _threads;

        public MediaEncoder(
            ILogger<MediaEncoder> logger,
            IServerConfigurationManager configurationManager,
            IFileSystem fileSystem,
            IBlurayExaminer blurayExaminer,
            ILocalizationManager localization,
            IConfiguration config,
            IServerConfigurationManager serverConfig,
            IFfmpegProcessRunner processRunner)
        {
            _logger = logger;
            _configurationManager = configurationManager;
            _fileSystem = fileSystem;
            _blurayExaminer = blurayExaminer;
            _config = config;
            _serverConfig = serverConfig;
            _processRunner = processRunner;
            _startupOptionFFmpegPath = config.GetValue<string>(Tesserafin.Controller.Extensions.ConfigurationExtensions.FfmpegPathKey) ?? string.Empty;

            _probes = new MediaProbeService(processRunner, logger, localization);

            // Although the type is not nullable, this might still be null during unit tests
            var semaphoreCount = serverConfig.Configuration?.ParallelImageEncodingLimit ?? 0;
            if (semaphoreCount < 1)
            {
                semaphoreCount = Environment.ProcessorCount;
            }

            _images = new FfmpegImageScheduler(processRunner, fileSystem, logger, semaphoreCount);
        }

        /// <inheritdoc />
        public string EncoderPath => _ffmpegPath;

        /// <inheritdoc />
        public string ProbePath => _ffprobePath;

        /// <inheritdoc />
        public Version EncoderVersion => _ffmpegVersion;

        /// <inheritdoc />
        public bool SupportsHevcMain10 { get; private set; }

        /// <inheritdoc />
        public bool IsPkeyPauseSupported => _isPkeyPauseSupported;

        /// <inheritdoc />
        public bool IsVaapiDeviceAmd => _capabilities.PrimaryDeviceVendor == HardwareDeviceVendor.Amd;

        /// <inheritdoc />
        public bool IsVaapiDeviceInteliHD => _capabilities.PrimaryDeviceVendor == HardwareDeviceVendor.IntelIHD;

        /// <inheritdoc />
        public bool IsVaapiDeviceInteli965 => _capabilities.PrimaryDeviceVendor == HardwareDeviceVendor.IntelI965;

        /// <inheritdoc />
        public bool IsVaapiDeviceSupportVulkanDrmModifier => _capabilities.PrimaryDeviceSupportsVulkanDrmModifier;

        /// <inheritdoc />
        public bool IsVaapiDeviceSupportVulkanDrmInterop => _capabilities.PrimaryDeviceSupportsVulkanDrmInterop;

        public bool IsVideoToolboxAv1DecodeAvailable => _isVideoToolboxAv1DecodeAvailable;

        /// <summary>
        /// Gets this start's hardware selection decision, or <c>null</c> if
        /// <see cref="SetFFmpegPath"/> has not run yet.
        /// </summary>
        internal HardwareSelectionDecision HardwareSelection => _hardwareSelection;

        [GeneratedRegex(@"[^\/\\]+?(\.[^\/\\\n.]+)?$")]
        private static partial Regex FfprobePathRegex();

        /// <summary>
        /// Run at startup to validate ffmpeg.
        /// Sets global variables FFmpegPath.
        /// Precedence is: CLI/Env var > Config > $PATH.
        /// </summary>
        /// <returns>bool indicates whether a valid ffmpeg is found.</returns>
        public bool SetFFmpegPath()
        {
            SupportsHevcMain10 = false;
            var skipValidation = _config.GetFFmpegSkipValidation();
            if (skipValidation)
            {
                _logger.LogWarning("FFmpeg: Skipping FFmpeg Validation due to FFmpeg:novalidation set to true");
                return true;
            }

            // 1) Check if the --ffmpeg CLI switch has been given
            var ffmpegPath = _startupOptionFFmpegPath;
            string ffmpegPathSetMethodText = "command line or environment variable";
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                // 2) Custom path stored in config/encoding xml file under tag <EncoderAppPath> should be used as a fallback
                ffmpegPath = _configurationManager.GetEncodingOptions().EncoderAppPath;
                ffmpegPathSetMethodText = "encoding.xml config file";
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    // 3) Check "ffmpeg"
                    ffmpegPath = "ffmpeg";
                    ffmpegPathSetMethodText = "system $PATH";
                }
            }

            if (!ValidatePath(ffmpegPath))
            {
                _ffmpegPath = null;
                _logger.LogError("FFmpeg: Path set by {FfmpegPathSetMethodText} is invalid", ffmpegPathSetMethodText);
                return false;
            }

            // Write the FFmpeg path to the config/encoding.xml file as <EncoderAppPathDisplay> so it appears in UI
            var options = _configurationManager.GetEncodingOptions();
            options.EncoderAppPathDisplay = _ffmpegPath ?? string.Empty;
            _configurationManager.SaveConfiguration("encoding", options);

            // Only if mpeg path is set, try and set path to probe
            if (_ffmpegPath is not null)
            {
                // Determine a probe path from the mpeg path
                _ffprobePath = FfprobePathRegex().Replace(_ffmpegPath, "ffprobe$1");

                // Interrogate to understand what coders are supported
                var validator = new EncoderValidator(_logger, _ffmpegPath, _processRunner);

                SetAvailableDecoders(validator.GetDecoders());
                SetAvailableEncoders(validator.GetEncoders());
                SetAvailableFilters(validator.GetFilters());
                SetAvailableFiltersWithOption(validator.GetFiltersWithOption());
                SetAvailableBitStreamFiltersWithOption(validator.GetBitStreamFiltersWithOption());
                SetAvailableHwaccels(validator.GetHwaccels());
                SetMediaEncoderVersion(validator);

                SupportsHevcMain10 = SupportsEncoder("libx265") && validator.CheckSoftwareHevcMain10();
                _logger.LogInformation("Software HEVC Main10 probe: {Supported}", SupportsHevcMain10);

                _threads = EncodingHelper.GetNumberOfThreads(null, options, null);

                _isPkeyPauseSupported = validator.CheckSupportedRuntimeKey("p      pause transcoding", _ffmpegVersion);
                _isLowPriorityHwDecodeSupported = validator.CheckSupportedHwaccelFlag("low_priority");
                _proberSupportsFirstVideoFrame = validator.CheckSupportedProberOption("only_first_vframe", _ffprobePath);

                // Decide, on every start, whether this run encodes in hardware or software. A
                // configured backend is treated as a preference and re-verified here rather than
                // trusted: that is what stops a config directory carried over from a GPU host from
                // producing an unprobed hardware command on a host that has no device. Probing
                // costs startup latency and spins the GPU once per applicable candidate, which is
                // the price of the guarantee. See HardwareSelectionPlanner for the full contract.
                SelectHardwareAcceleration(options);

                // Check the Vaapi device vendor
                if (OperatingSystem.IsLinux()
                    && SupportsHwaccel("vaapi")
                    && !string.IsNullOrEmpty(options.VaapiDevice)
                    && options.HardwareAccelerationType == HardwareAccelerationType.vaapi)
                {
                    var isVaapiDeviceAmd = validator.CheckVaapiDeviceByDriverName("Mesa Gallium driver", options.VaapiDevice);
                    var isVaapiDeviceInteliHD = validator.CheckVaapiDeviceByDriverName("Intel iHD driver", options.VaapiDevice);
                    var isVaapiDeviceInteli965 = validator.CheckVaapiDeviceByDriverName("Intel i965 driver", options.VaapiDevice);
                    var vendor = isVaapiDeviceAmd ? HardwareDeviceVendor.Amd
                        : isVaapiDeviceInteliHD ? HardwareDeviceVendor.IntelIHD
                        : isVaapiDeviceInteli965 ? HardwareDeviceVendor.IntelI965
                        : HardwareDeviceVendor.Unknown;
                    var supportsVulkanDrmModifier = validator.CheckVulkanDrmDeviceByExtensionName(options.VaapiDevice, _vulkanImageDrmFmtModifierExts);
                    var supportsVulkanDrmInterop = validator.CheckVulkanDrmDeviceByExtensionName(options.VaapiDevice, _vulkanExternalMemoryDmaBufExts);

                    _capabilities = _capabilities.WithSingleDevice(new HardwareDeviceCapabilities(
                        options.VaapiDevice,
                        vendor,
                        supportsVulkanDrmModifier,
                        supportsVulkanDrmInterop));

                    if (isVaapiDeviceAmd)
                    {
                        _logger.LogInformation("VAAPI device {RenderNodePath} is AMD GPU", options.VaapiDevice);
                    }
                    else if (isVaapiDeviceInteliHD)
                    {
                        _logger.LogInformation("VAAPI device {RenderNodePath} is Intel GPU (iHD)", options.VaapiDevice);
                    }
                    else if (isVaapiDeviceInteli965)
                    {
                        _logger.LogInformation("VAAPI device {RenderNodePath} is Intel GPU (i965)", options.VaapiDevice);
                    }

                    if (supportsVulkanDrmModifier)
                    {
                        _logger.LogInformation("VAAPI device {RenderNodePath} supports Vulkan DRM modifier", options.VaapiDevice);
                    }

                    if (supportsVulkanDrmInterop)
                    {
                        _logger.LogInformation("VAAPI device {RenderNodePath} supports Vulkan DRM interop", options.VaapiDevice);
                    }
                }

                // Check if VideoToolbox supports AV1 decode
                if (OperatingSystem.IsMacOS() && SupportsHwaccel("videotoolbox"))
                {
                    _isVideoToolboxAv1DecodeAvailable = validator.CheckIsVideoToolboxAv1DecodeAvailable();
                }
            }

            _logger.LogInformation("FFmpeg: {FfmpegPath}", _ffmpegPath ?? string.Empty);
            return !string.IsNullOrWhiteSpace(ffmpegPath);
        }

        /// <summary>
        /// Runs this start's hardware selection and makes its result the effective one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The decision is written back into <see cref="EncodingOptions.HardwareAccelerationType"/>
        /// because that property is what the whole command-assembly path
        /// (<see cref="EncodingHelper"/> and its callers) reads to choose an encoder. Making it the
        /// <em>effective, just-verified</em> backend rather than an unverified stored intention is
        /// the point: there is exactly one source of truth, so the structured decision log and the
        /// ffmpeg command line cannot disagree.
        /// </para>
        /// <para>
        /// Because the value is re-derived from a real trial encode on every start, persisting it
        /// cannot create a stale unverified backend later. Its meaning is stable in both
        /// directions: <see cref="HardwareAccelerationType.none"/> means "select automatically"
        /// (the fresh-install default), and any other value means "prefer this backend, subject to
        /// re-verification". Forcing software is a separate, explicit switch -
        /// <c>EnableHardwareEncoding=false</c> - which is preserved untouched here.
        /// </para>
        /// </remarks>
        /// <param name="options">The current encoding options. Mutated to carry the effective backend.</param>
        private void SelectHardwareAcceleration(EncodingOptions options)
        {
            var probe = new HardwareBackendProbe(_processRunner, _logger);

            var decision = HardwareSelectionPlanner.Decide(
                HardwareBackendCatalog.CandidatesInPriorityOrder,
                options,
                _capabilities.Ffmpeg,
                (candidate, arguments) =>
                {
                    try
                    {
                        return probe.ProbeDetailedAsync(_ffmpegPath, arguments, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        // Logged here rather than swallowed silently in the planner, which only
                        // sees "not verified". A probe that cannot even run must never be able to
                        // fail startup or leave a backend selected unverified.
                        _logger.LogWarning(ex, "Hardware backend probe for {Backend} could not be run", candidate.Type);
                        return HardwareProbeOutcome.Failure(FfmpegErrorCategory.Unknown);
                    }
                });

            _hardwareSelection = decision;

            if (options.HardwareAccelerationType != decision.Backend)
            {
                options.HardwareAccelerationType = decision.Backend;
                _configurationManager.SaveConfiguration("encoding", options);
            }

            // One conclusive Information-level event per start, with named structured fields so it
            // can be asserted on and, later, rendered as JSON (#91) without reparsing prose.
            _logger.LogInformation(
                "Hardware acceleration decision: Mode={Mode} Backend={Backend} Reason={Reason} ConfiguredBackend={ConfiguredBackend} CandidatesConsidered={CandidatesConsidered} CandidatesProbed={CandidatesProbed} ProbeFailureCategories={ProbeFailureCategories}",
                decision.ModeName,
                decision.Backend,
                decision.Reason,
                decision.ConfiguredBackend,
                decision.CandidatesConsidered,
                decision.CandidatesProbed,
                decision.ProbeFailureCategories);
        }

        /// <summary>
        /// Validates the supplied FQPN to ensure it is a ffmpeg utility.
        /// If checks pass, global variable FFmpegPath is updated.
        /// </summary>
        /// <param name="path">FQPN to test.</param>
        /// <returns><c>true</c> if the version validation succeeded; otherwise, <c>false</c>.</returns>
        private bool ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            bool rc = new EncoderValidator(_logger, path, _processRunner).ValidateVersion();
            if (!rc)
            {
                _logger.LogError("FFmpeg: Failed version check: {Path}", path);
                return false;
            }

            _ffmpegPath = path;
            return true;
        }

        private string GetEncoderPathFromDirectory(string path, string filename, bool recursive = false)
        {
            try
            {
                var files = _fileSystem.GetFilePaths(path, recursive);

                return files.FirstOrDefault(i => Path.GetFileNameWithoutExtension(i.AsSpan()).Equals(filename, StringComparison.OrdinalIgnoreCase)
                                                    && !Path.GetExtension(i.AsSpan()).Equals(".c", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                // Trap all exceptions, like DirNotExists, and return null
                return null;
            }
        }

        public void SetAvailableEncoders(IEnumerable<string> list)
        {
            _capabilities = _capabilities.WithFfmpeg(_capabilities.Ffmpeg.WithEncoders(list));
        }

        public void SetAvailableDecoders(IEnumerable<string> list)
        {
            _capabilities = _capabilities.WithFfmpeg(_capabilities.Ffmpeg.WithDecoders(list));
        }

        public void SetAvailableHwaccels(IEnumerable<string> list)
        {
            _capabilities = _capabilities.WithFfmpeg(_capabilities.Ffmpeg.WithHwaccels(list));
        }

        public void SetAvailableFilters(IEnumerable<string> list)
        {
            _capabilities = _capabilities.WithFfmpeg(_capabilities.Ffmpeg.WithFilters(list));
        }

        public void SetAvailableFiltersWithOption(IDictionary<FilterOptionType, bool> dict)
        {
            _capabilities = _capabilities.WithFfmpeg(_capabilities.Ffmpeg.WithFiltersWithOption(dict));
        }

        public void SetAvailableBitStreamFiltersWithOption(IDictionary<BitStreamFilterOptionType, bool> dict)
        {
            _capabilities = _capabilities.WithFfmpeg(_capabilities.Ffmpeg.WithBitStreamFiltersWithOption(dict));
        }

        public void SetMediaEncoderVersion(EncoderValidator validator)
        {
            _ffmpegVersion = validator.GetFFmpegVersion();
        }

        /// <inheritdoc />
        public bool SupportsEncoder(string encoder)
        {
            return _capabilities.Ffmpeg.SupportsEncoder(encoder);
        }

        /// <inheritdoc />
        public bool SupportsDecoder(string decoder)
        {
            return _capabilities.Ffmpeg.SupportsDecoder(decoder);
        }

        /// <inheritdoc />
        public bool SupportsHwaccel(string hwaccel)
        {
            return _capabilities.Ffmpeg.SupportsHwaccel(hwaccel);
        }

        /// <inheritdoc />
        public bool SupportsFilter(string filter)
        {
            return _capabilities.Ffmpeg.SupportsFilter(filter);
        }

        /// <inheritdoc />
        public bool SupportsFilterWithOption(FilterOptionType option)
        {
            return _capabilities.Ffmpeg.SupportsFilterWithOption(option);
        }

        public bool SupportsBitStreamFilterWithOption(BitStreamFilterOptionType option)
        {
            return _capabilities.Ffmpeg.SupportsBitStreamFilterWithOption(option);
        }

        public bool CanEncodeToAudioCodec(string codec)
        {
            if (string.Equals(codec, "opus", StringComparison.OrdinalIgnoreCase))
            {
                codec = "libopus";
            }
            else if (string.Equals(codec, "mp3", StringComparison.OrdinalIgnoreCase))
            {
                codec = "libmp3lame";
            }

            return SupportsEncoder(codec);
        }

        public bool CanEncodeToSubtitleCodec(string codec)
        {
            // TODO
            return true;
        }

        /// <inheritdoc />
        public Task<MediaInfo> GetMediaInfo(MediaInfoRequest request, CancellationToken cancellationToken)
        {
            var reader = request.DirectStreamReader;
            var input = reader is null ? GetInputArgument(request.MediaSource.Path, request.MediaSource) : "pipe:0";
            var args = request.ExtractChapters
                ? "{0} -i {1} -threads {2} -v warning -print_format json -show_streams -show_chapters -show_format"
                : "{0} -i {1} -threads {2} -v warning -print_format json -show_streams -show_format";
            var audio = request.MediaType == DlnaProfileType.Audio;
            if (request.MediaSource.Protocol == MediaProtocol.File && !audio && _proberSupportsFirstVideoFrame)
            {
                args += " -show_frames -only_first_vframe";
            }

            args = string.Format(CultureInfo.InvariantCulture, args, GetExtraArguments(request), input, _threads).Trim();
            return _probes.ProbeAsync(
                FfmpegCommand.FromArgumentLine(_ffprobePath, args),
                request.MediaSource.Path,
                request.MediaSource.Protocol,
                audio,
                request.MediaSource.VideoType,
                reader is null ? null : () => new ProgressiveFileStream(reader.GetStream()),
                cancellationToken);
        }

        internal string GetExtraArguments(MediaInfoRequest request)
        {
            var ffmpegAnalyzeDuration = _config.GetFFmpegAnalyzeDuration() ?? string.Empty;
            var ffmpegProbeSize = _config.GetFFmpegProbeSize() ?? string.Empty;
            var analyzeDuration = string.Empty;
            var extraArgs = string.Empty;

            if (request.MediaSource.AnalyzeDurationMs > 0)
            {
                analyzeDuration = "-analyzeduration " + (request.MediaSource.AnalyzeDurationMs * 1000);
            }
            else if (!string.IsNullOrEmpty(ffmpegAnalyzeDuration))
            {
                analyzeDuration = "-analyzeduration " + ffmpegAnalyzeDuration;
            }

            if (!string.IsNullOrEmpty(analyzeDuration))
            {
                extraArgs = analyzeDuration;
            }

            if (!string.IsNullOrEmpty(ffmpegProbeSize))
            {
                extraArgs += " -probesize " + ffmpegProbeSize;
            }

            // #153-LTV-R1. ffmpeg resolves protocol options against the INPUT's protocol, and
            // `pipe:` has none: -user_agent on a pipe input makes it refuse the whole invocation
            // with "Option user_agent not found." and exit 8. #153-LTV-S0 hit exactly this on the
            // transcode side. A piped probe therefore carries no HTTP or RTSP option at all — and
            // it does not need one, because it is not opening a network address.
            if (request.DirectStreamReader is null)
            {
                if (request.MediaSource.RequiredHttpHeaders.TryGetValue("User-Agent", out var userAgent))
                {
                    extraArgs += $" -user_agent \"{userAgent}\"";
                }

                if (request.MediaSource.Protocol == MediaProtocol.Rtsp)
                {
                    extraArgs += " -rtsp_transport tcp+udp -rtsp_flags prefer_tcp";
                }
            }

            return extraArgs;
        }

        /// <inheritdoc />
        public string GetInputArgument(IReadOnlyList<string> inputFiles, MediaSourceInfo mediaSource)
        {
            return EncodingUtils.GetInputArgument("file", inputFiles, mediaSource.Protocol);
        }

        /// <inheritdoc />
        public string GetInputArgument(string inputFile, MediaSourceInfo mediaSource)
        {
            var prefix = "file";
            if (mediaSource.IsoType == IsoType.BluRay)
            {
                prefix = "bluray";
            }

            return EncodingUtils.GetInputArgument(prefix, new[] { inputFile }, mediaSource.Protocol);
        }

        /// <inheritdoc />
        public string GetExternalSubtitleInputArgument(string inputFile)
        {
            const string Prefix = "file";

            return EncodingUtils.GetInputArgument(Prefix, new[] { inputFile }, MediaProtocol.File);
        }

        /// <inheritdoc />
        public Task<string> ExtractAudioImage(string path, int? imageStreamIndex, CancellationToken cancellationToken)
        {
            var mediaSource = new MediaSourceInfo
            {
                Protocol = MediaProtocol.File
            };

            return ExtractImage(path, null, null, imageStreamIndex, mediaSource, true, null, null, ImageFormat.Jpg, cancellationToken);
        }

        /// <inheritdoc />
        public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream videoStream, Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken)
        {
            return ExtractImage(inputFile, container, videoStream, null, mediaSource, false, threedFormat, offset, ImageFormat.Jpg, cancellationToken);
        }

        /// <inheritdoc />
        public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int? imageStreamIndex, ImageFormat? targetFormat, CancellationToken cancellationToken)
        {
            return ExtractImage(inputFile, container, imageStream, imageStreamIndex, mediaSource, false, null, null, targetFormat, cancellationToken);
        }

        private async Task<string> ExtractImage(
            string inputFile,
            string container,
            MediaStream videoStream,
            int? imageStreamIndex,
            MediaSourceInfo mediaSource,
            bool isAudio,
            Video3DFormat? threedFormat,
            TimeSpan? offset,
            ImageFormat? targetFormat,
            CancellationToken cancellationToken)
        {
            var inputArgument = GetInputPathArgument(inputFile, mediaSource);

            if (!isAudio)
            {
                try
                {
                    return await ExtractImageInternal(inputArgument, container, videoStream, imageStreamIndex, threedFormat, offset, true, targetFormat, false, cancellationToken).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ObjectDisposedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "I-frame image extraction failed, will attempt standard way. Input: {Arguments}", inputArgument);
                }
            }

            return await ExtractImageInternal(inputArgument, container, videoStream, imageStreamIndex, threedFormat, offset, false, targetFormat, isAudio, cancellationToken).ConfigureAwait(false);
        }

        private string GetImageResolutionParameter()
        {
            var imageResolutionParameter = _serverConfig.Configuration.ChapterImageResolution switch
            {
                ImageResolution.P144 => "256x144",
                ImageResolution.P240 => "426x240",
                ImageResolution.P360 => "640x360",
                ImageResolution.P480 => "854x480",
                ImageResolution.P720 => "1280x720",
                ImageResolution.P1080 => "1920x1080",
                ImageResolution.P1440 => "2560x1440",
                ImageResolution.P2160 => "3840x2160",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(imageResolutionParameter))
            {
                imageResolutionParameter = " -s " + imageResolutionParameter;
            }

            return imageResolutionParameter;
        }

        private async Task<string> ExtractImageInternal(
            string inputPath,
            string container,
            MediaStream videoStream,
            int? imageStreamIndex,
            Video3DFormat? threedFormat,
            TimeSpan? offset,
            bool useIFrame,
            ImageFormat? targetFormat,
            bool isAudio,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(inputPath);

            var useTradeoff = _config.GetFFmpegImgExtractPerfTradeoff();

            var outputExtension = targetFormat?.GetExtension() ?? ".jpg";

            var tempExtractPath = Path.Combine(_configurationManager.ApplicationPaths.TempDirectory, Guid.NewGuid() + outputExtension);

            // deint -> scale -> thumbnail -> tonemap.
            // put the SW tonemap right after the thumbnail to do it only once to reduce cpu usage.
            var filters = new List<string>();

            // deinterlace using bwdif algorithm for video stream.
            if (videoStream is not null && videoStream.IsInterlaced)
            {
                filters.Add("bwdif=0:-1:0");
            }

            // apply some filters to thumbnail extracted below (below) crop any black lines that we made and get the correct ar.
            // This filter chain may have adverse effects on recorded tv thumbnails if ar changes during presentation ex. commercials @ diff ar
            var scaler = threedFormat switch
            {
                // hsbs crop width in half,scale to correct size, set the display aspect,crop out any black bars we may have made. Work out the correct height based on the display aspect it will maintain the aspect where -1 in this case (3d) may not.
                Video3DFormat.HalfSideBySide => @"crop=iw/2:ih:0:0,scale=(iw*2):ih,setdar=dar=a,crop=min(iw\,ih*dar):min(ih\,iw/dar):(iw-min(iw\,iw*sar))/2:(ih - min (ih\,ih/sar))/2,setsar=sar=1",
                // fsbs crop width in half,set the display aspect,crop out any black bars we may have made
                Video3DFormat.FullSideBySide => @"crop=iw/2:ih:0:0,setdar=dar=a,crop=min(iw\,ih*dar):min(ih\,iw/dar):(iw-min(iw\,iw*sar))/2:(ih - min (ih\,ih/sar))/2,setsar=sar=1",
                // htab crop height in half,scale to correct size, set the display aspect,crop out any black bars we may have made
                Video3DFormat.HalfTopAndBottom => @"crop=iw:ih/2:0:0,scale=(iw*2):ih),setdar=dar=a,crop=min(iw\,ih*dar):min(ih\,iw/dar):(iw-min(iw\,iw*sar))/2:(ih - min (ih\,ih/sar))/2,setsar=sar=1",
                // ftab crop height in half, set the display aspect,crop out any black bars we may have made
                Video3DFormat.FullTopAndBottom => @"crop=iw:ih/2:0:0,setdar=dar=a,crop=min(iw\,ih*dar):min(ih\,iw/dar):(iw-min(iw\,iw*sar))/2:(ih - min (ih\,ih/sar))/2,setsar=sar=1",
                _ => "scale=round(iw*sar/2)*2:round(ih/2)*2"
            };

            filters.Add(scaler);

            // Use ffmpeg to sample N frames and pick the best thumbnail. Have a fall back just in case.
            var enableThumbnail = !useTradeoff && useIFrame && !string.Equals("wtv", container, StringComparison.OrdinalIgnoreCase);
            if (enableThumbnail)
            {
                filters.Add("thumbnail=n=24");
            }

            // Use SW tonemap on HDR video stream only when the zscale or tonemapx filter is available.
            // Only enable Dolby Vision tonemap when tonemapx is available
            var enableHdrExtraction = false;

            if (videoStream?.VideoRange == VideoRange.HDR)
            {
                if (SupportsFilter("tonemapx"))
                {
                    var peak = videoStream.VideoRangeType == VideoRangeType.DOVI ? "400" : "100";
                    enableHdrExtraction = true;
                    filters.Add($"tonemapx=tonemap=bt2390:desat=0:peak={peak}:t=bt709:m=bt709:p=bt709:format=yuv420p:range=full");
                }
                else if (SupportsFilter("zscale") && videoStream.VideoRangeType != VideoRangeType.DOVI)
                {
                    enableHdrExtraction = true;
                    filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0:peak=100,zscale=t=bt709:m=bt709:out_range=full,format=yuv420p");
                }
            }

            var vf = string.Join(',', filters);
            var mapArg = imageStreamIndex.HasValue ? (" -map 0:" + imageStreamIndex.Value.ToString(CultureInfo.InvariantCulture)) : string.Empty;
            var args = string.Format(
                CultureInfo.InvariantCulture,
                "-i {0}{1} -threads {2} -v quiet -vframes 1 -vf {3}{4}{5} -f image2 \"{6}\"",
                inputPath,
                mapArg,
                _threads,
                vf,
                isAudio ? string.Empty : GetImageResolutionParameter(),
                EncodingHelper.GetVideoSyncOption("-1", EncoderVersion), // auto decide fps mode
                tempExtractPath);

            if (offset.HasValue)
            {
                args = string.Format(CultureInfo.InvariantCulture, "-ss {0} ", GetTimeParameter(offset.Value)) + args;
            }

            // The mpegts demuxer cannot seek to keyframes, so we have to let the
            // decoder discard non-keyframes, which may contain corrupted images.
            var seekMpegTs = offset.HasValue && string.Equals("mpegts", container, StringComparison.OrdinalIgnoreCase);
            if (useIFrame && (useTradeoff || seekMpegTs))
            {
                args = "-skip_frame nokey " + args;
            }

            if (!string.IsNullOrWhiteSpace(container))
            {
                var inputFormat = EncodingHelper.GetInputFormat(container);
                if (!string.IsNullOrWhiteSpace(inputFormat))
                {
                    args = "-f " + inputFormat + " " + args;
                }
            }

            var timeoutMs = _configurationManager.Configuration.ImageExtractionTimeoutMs;
            if (timeoutMs <= 0)
            {
                timeoutMs = enableHdrExtraction ? DefaultHdrImageExtractionTimeout : DefaultSdrImageExtractionTimeout;
            }

            return await _images.ExtractImageAsync(
                FfmpegCommand.FromArgumentLine(_ffmpegPath, "-nostdin -y " + args),
                tempExtractPath,
                TimeSpan.FromMilliseconds(timeoutMs),
                cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<string> ExtractVideoImagesOnIntervalAccelerated(
            string inputFile,
            string container,
            MediaSourceInfo mediaSource,
            MediaStream imageStream,
            int maxWidth,
            TimeSpan interval,
            bool allowHwAccel,
            bool enableHwEncoding,
            int? threads,
            int? qualityScale,
            ProcessPriorityClass? priority,
            bool enableKeyFrameOnlyExtraction,
            EncodingHelper encodingHelper,
            CancellationToken cancellationToken)
        {
            var options = allowHwAccel ? _configurationManager.GetEncodingOptions() : new EncodingOptions();
            threads ??= _threads;

            if (allowHwAccel && enableKeyFrameOnlyExtraction)
            {
                var hardwareAccelerationType = options.HardwareAccelerationType;
                var supportsKeyFrameOnly = (hardwareAccelerationType == HardwareAccelerationType.nvenc && options.EnableEnhancedNvdecDecoder)
                                           || (hardwareAccelerationType == HardwareAccelerationType.amf && OperatingSystem.IsWindows())
                                           || (hardwareAccelerationType == HardwareAccelerationType.qsv && options.PreferSystemNativeHwDecoder)
                                           || hardwareAccelerationType == HardwareAccelerationType.vaapi
                                           || hardwareAccelerationType == HardwareAccelerationType.videotoolbox
                                           || hardwareAccelerationType == HardwareAccelerationType.rkmpp;
                if (!supportsKeyFrameOnly)
                {
                    // Disable hardware acceleration when the hardware decoder does not support keyframe only mode.
                    allowHwAccel = false;
                    options = new EncodingOptions();
                }
            }

            // A new EncodingOptions instance must be used as to not disable HW acceleration for all of Tesserafin.
            // Additionally, we must set a few fields without defaults to prevent null pointer exceptions.
            if (!allowHwAccel)
            {
                options.EnableHardwareEncoding = false;
                options.HardwareAccelerationType = HardwareAccelerationType.none;
                options.EnableTonemapping = false;
            }

            if (imageStream.Width is not null && imageStream.Height is not null && !string.IsNullOrEmpty(imageStream.AspectRatio))
            {
                // For hardware trickplay encoders, we need to re-calculate the size because they used fixed scale dimensions
                var darParts = imageStream.AspectRatio.Split(':');
                var (wa, ha) = (double.Parse(darParts[0], CultureInfo.InvariantCulture), double.Parse(darParts[1], CultureInfo.InvariantCulture));
                // When dimension / DAR does not equal to 1:1, then the frames are most likely stored stretched.
                // Note: this might be incorrect for 3D videos as the SAR stored might be per eye instead of per video, but we really can do little about it.
                var shouldResetHeight = Math.Abs((imageStream.Width.Value * ha) - (imageStream.Height.Value * wa)) > .05;
                if (shouldResetHeight)
                {
                    // SAR = DAR * Height / Width
                    // RealHeight = Height / SAR = Height / (DAR * Height / Width) = Width / DAR
                    imageStream.Height = Convert.ToInt32(imageStream.Width.Value * ha / wa);
                }
            }

            var baseRequest = new BaseEncodingJobOptions { MaxWidth = maxWidth, MaxFramerate = (float)(1.0 / interval.TotalSeconds) };
            var jobState = new EncodingJobInfo(TranscodingJobType.Progressive)
            {
                IsVideoRequest = true,  // must be true for InputVideoHwaccelArgs to return non-empty value
                MediaSource = mediaSource,
                VideoStream = imageStream,
                BaseRequest = baseRequest,  // GetVideoProcessingFilterParam errors if null
                MediaPath = inputFile,
                OutputVideoCodec = "mjpeg",
                EncoderUsage = VideoEncoderUsage.ImageExtraction
            };
            var vidEncoder = enableHwEncoding ? encodingHelper.GetVideoEncoder(jobState, options) : jobState.OutputVideoCodec;

            // Get input and filter arguments
            var input = encodingHelper.GetInputArgument(jobState, options, container);
            var inputArg = input.ArgumentLine.Trim();
            if (string.IsNullOrWhiteSpace(inputArg))
            {
                throw new InvalidOperationException("EncodingHelper returned empty input arguments.");
            }

            if (!allowHwAccel)
            {
                inputArg = "-threads " + threads + " " + inputArg; // HW accel args set a different input thread count, only set if disabled
            }

            if (options.HardwareAccelerationType == HardwareAccelerationType.videotoolbox && _isLowPriorityHwDecodeSupported)
            {
                // VideoToolbox supports low priority decoding, which is useful for trickplay
                inputArg = "-hwaccel_flags +low_priority " + inputArg;
            }

            var filterParam = encodingHelper.GetVideoProcessingFilterParam(jobState, options, vidEncoder).Trim();
            if (string.IsNullOrWhiteSpace(filterParam))
            {
                throw new InvalidOperationException("EncodingHelper returned empty or invalid filter parameters.");
            }

            try
            {
                return await ExtractVideoImagesOnIntervalInternal(
                    input with { ArgumentLine = (enableKeyFrameOnlyExtraction ? "-skip_frame nokey " : string.Empty) + inputArg },
                    filterParam,
                    vidEncoder,
                    threads,
                    qualityScale,
                    priority,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (FfmpegException ex)
            {
                if (!enableKeyFrameOnlyExtraction)
                {
                    throw;
                }

                _logger.LogWarning(ex, "I-frame trickplay extraction failed, will attempt standard way. Input: {InputFile}", inputFile);
            }

            return await ExtractVideoImagesOnIntervalInternal(input with { ArgumentLine = inputArg }, filterParam, vidEncoder, threads, qualityScale, priority, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> ExtractVideoImagesOnIntervalInternal(
            FfmpegArguments input,
            string filterParam,
            string vidEncoder,
            int? outputThreads,
            int? qualityScale,
            ProcessPriorityClass? priority,
            CancellationToken cancellationToken)
        {
            var inputArg = input.ArgumentLine;
            if (string.IsNullOrWhiteSpace(inputArg))
            {
                throw new InvalidOperationException("Empty or invalid input argument.");
            }

            // ffmpeg qscale is a value from 1-31, with 1 being best quality and 31 being worst
            // jpeg quality is a value from 0-100, with 0 being worst quality and 100 being best
            var encoderQuality = Math.Clamp(qualityScale ?? 4, 1, 31);
            var encoderQualityOption = "-qscale:v ";

            if (vidEncoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase)
                || vidEncoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            {
                // vaapi and qsv's mjpeg encoder use jpeg quality as input, instead of ffmpeg defined qscale
                encoderQuality = 100 - ((encoderQuality - 1) * (100 / 30));
                encoderQualityOption = "-global_quality:v ";
            }

            if (vidEncoder.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase))
            {
                // videotoolbox's mjpeg encoder uses jpeg quality scaled to QP2LAMBDA (118) instead of ffmpeg defined qscale
                encoderQuality = 118 - ((encoderQuality - 1) * (118 / 30));
            }

            if (vidEncoder.Contains("rkmpp", StringComparison.OrdinalIgnoreCase))
            {
                // rkmpp's mjpeg encoder uses jpeg quality as input (max is 99, not 100), instead of ffmpeg defined qscale
                encoderQuality = 99 - ((encoderQuality - 1) * (99 / 30));
                encoderQualityOption = "-qp_init:v ";
            }

            // Output arguments
            var targetDirectory = Path.Combine(_configurationManager.ApplicationPaths.TempDirectory, Guid.NewGuid().ToString("N"));
            var outputPath = Path.Combine(targetDirectory, "%08d.jpg");

            // Final command arguments
            var args = string.Format(
                CultureInfo.InvariantCulture,
                "-loglevel error {0} -an -sn {1} -threads {2} -c:v {3} {4}{5}{6}-f {7} \"{8}\"",
                inputArg,
                filterParam,
                outputThreads.GetValueOrDefault(_threads),
                vidEncoder,
                encoderQualityOption + encoderQuality + " ",
                vidEncoder.Contains("videotoolbox", StringComparison.InvariantCultureIgnoreCase) ? "-allow_sw 1 " : string.Empty, // allow_sw fallback for some intel macs
                EncodingHelper.GetVideoSyncOption("0", EncoderVersion).Trim() + " ", // passthrough timestamp
                "image2",
                outputPath);

            var timeoutMs = _configurationManager.Configuration.ImageExtractionTimeoutMs;
            timeoutMs = timeoutMs <= 0 ? DefaultHdrImageExtractionTimeout : timeoutMs;
            return await _images.ExtractIntervalAsync(
                FfmpegCommand.FromArgumentLine(_ffmpegPath, "-nostdin -y " + args, input.EnvironmentVariables),
                targetDirectory,
                TimeSpan.FromMilliseconds(timeoutMs),
                priority,
                cancellationToken).ConfigureAwait(false);
        }

        public string GetTimeParameter(long ticks)
        {
            var time = TimeSpan.FromTicks(ticks);

            return GetTimeParameter(time);
        }

        public string GetTimeParameter(TimeSpan time)
        {
            return time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        }

        public string EscapeSubtitleFilterPath(string path)
        {
            // https://ffmpeg.org/ffmpeg-filters.html#Notes-on-filtergraph-escaping
            // We need to double escape

            return path
                .Replace('\\', '/')
                .Replace(":", "\\:", StringComparison.Ordinal)
                .Replace("'", @"'\\\''", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                Task.WhenAll(_images.StopAsync(), _probes.StopAsync()).GetAwaiter().GetResult();
                _images.Dispose();
                _probes.Dispose();
            }
        }

        /// <inheritdoc />
        public Task ConvertImage(string inputPath, string outputPath)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetPrimaryPlaylistVobFiles(string path, uint? titleNumber)
        {
            // Eliminate menus and intros by omitting VIDEO_TS.VOB and all subsequent title .vob files ending with _0.VOB
            var allVobs = _fileSystem.GetFiles(path, true)
                .Where(file => string.Equals(file.Extension, ".VOB", StringComparison.OrdinalIgnoreCase))
                .Where(file => !string.Equals(file.Name, "VIDEO_TS.VOB", StringComparison.OrdinalIgnoreCase))
                .Where(file => !file.Name.EndsWith("_0.VOB", StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.FullName)
                .ToList();

            if (titleNumber.HasValue)
            {
                var prefix = string.Format(CultureInfo.InvariantCulture, "VTS_{0:D2}_", titleNumber.Value);
                var vobs = allVobs.Where(i => i.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

                if (vobs.Count > 0)
                {
                    return vobs.Select(i => i.FullName).ToList();
                }

                _logger.LogWarning("Could not determine .vob files for title {Title} of {Path}.", titleNumber, path);
            }

            // Check for multiple big titles (> 900 MB)
            var titles = allVobs
                .Where(vob => vob.Length >= 900 * 1024 * 1024)
                .Select(vob => _fileSystem.GetFileNameWithoutExtension(vob).AsSpan().RightPart('_').ToString())
                .Distinct()
                .ToList();

            // Fall back to first title if no big title is found
            if (titles.Count == 0)
            {
                titles.Add(_fileSystem.GetFileNameWithoutExtension(allVobs[0]).AsSpan().RightPart('_').ToString());
            }

            // Aggregate all .vob files of the titles
            return allVobs
                .Where(vob => titles.Contains(_fileSystem.GetFileNameWithoutExtension(vob).AsSpan().RightPart('_').ToString()))
                .Select(i => i.FullName)
                .Order()
                .ToList();
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetPrimaryPlaylistM2tsFiles(string path)
            => _blurayExaminer.GetDiscInfo(path).Files;

        /// <inheritdoc />
        public string GetInputPathArgument(EncodingJobInfo state)
            => GetInputPathArgument(state.MediaPath, state.MediaSource);

        /// <inheritdoc />
        public string GetInputPathArgument(string path, MediaSourceInfo mediaSource)
        {
            return mediaSource.VideoType switch
            {
                VideoType.Dvd => GetInputArgument(GetPrimaryPlaylistVobFiles(path, null), mediaSource),
                VideoType.BluRay => GetInputArgument(GetPrimaryPlaylistM2tsFiles(path), mediaSource),
                _ => GetInputArgument(path, mediaSource)
            };
        }

        /// <inheritdoc />
        public void GenerateConcatConfig(MediaSourceInfo source, string concatFilePath)
        {
            // Get all playable files
            IReadOnlyList<string> files;
            var videoType = source.VideoType;
            if (videoType == VideoType.Dvd)
            {
                files = GetPrimaryPlaylistVobFiles(source.Path, null);
            }
            else if (videoType == VideoType.BluRay)
            {
                files = GetPrimaryPlaylistM2tsFiles(source.Path);
            }
            else
            {
                return;
            }

            // Generate concat configuration entries for each file and write to file
            Directory.CreateDirectory(Path.GetDirectoryName(concatFilePath));
            using var sw = new FormattingStreamWriter(concatFilePath, CultureInfo.InvariantCulture);
            foreach (var path in files)
            {
                var mediaInfoResult = GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaType = DlnaProfileType.Video,
                        MediaSource = new MediaSourceInfo
                        {
                            Path = path,
                            Protocol = MediaProtocol.File,
                            VideoType = videoType
                        }
                    },
                    CancellationToken.None).GetAwaiter().GetResult();

                var duration = TimeSpan.FromTicks(mediaInfoResult.RunTimeTicks.Value).TotalSeconds;

                // Add file path stanza to concat configuration
                sw.WriteLine("file '{0}'", path.Replace("'", @"'\''", StringComparison.Ordinal));

                // Add duration stanza to concat configuration
                sw.WriteLine("duration {0}", duration);
            }
        }

        public bool CanExtractSubtitles(string codec)
        {
            return _configurationManager.GetEncodingOptions().EnableSubtitleExtraction;
        }
    }
}
