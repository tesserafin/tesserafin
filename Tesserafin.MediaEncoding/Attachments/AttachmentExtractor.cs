using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Encoder;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.MediaEncoding.Attachments
{
    /// <inheritdoc cref="IAttachmentExtractor"/>
    public sealed class AttachmentExtractor : IAttachmentExtractor, IDisposable
    {
        private readonly ILogger<AttachmentExtractor> _logger;
        private readonly FfmpegFileExecutor _executor;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IPathManager _pathManager;

        private readonly AsyncKeyedLocker<string> _semaphoreLocks = new(o =>
        {
            o.PoolSize = 20;
            o.PoolInitialFill = 1;
        });

        /// <summary>
        /// Initializes a new instance of the <see cref="AttachmentExtractor"/> class.
        /// </summary>
        /// <param name="logger">The <see cref="ILogger{AttachmentExtractor}"/>.</param>
        /// <param name="fileSystem">The <see cref="IFileSystem"/>.</param>
        /// <param name="mediaEncoder">The <see cref="IMediaEncoder"/>.</param>
        /// <param name="mediaSourceManager">The <see cref="IMediaSourceManager"/>.</param>
        /// <param name="pathManager">The <see cref="IPathManager"/>.</param>
        /// <param name="processRunner">The shared FFmpeg runner.</param>
        public AttachmentExtractor(
            ILogger<AttachmentExtractor> logger,
            IFileSystem fileSystem,
            IMediaEncoder mediaEncoder,
            IMediaSourceManager mediaSourceManager,
            IPathManager pathManager,
            IFfmpegProcessRunner processRunner)
        {
            _logger = logger;
            _executor = new FfmpegFileExecutor(processRunner, fileSystem, logger);
            _mediaEncoder = mediaEncoder;
            _mediaSourceManager = mediaSourceManager;
            _pathManager = pathManager;
        }

        /// <inheritdoc />
        public async Task<(MediaAttachment Attachment, Stream Stream)> GetAttachment(BaseItem item, string mediaSourceId, int attachmentStreamIndex, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (string.IsNullOrWhiteSpace(mediaSourceId))
            {
                throw new ArgumentNullException(nameof(mediaSourceId));
            }

            var mediaSources = await _mediaSourceManager.GetPlaybackMediaSources(item, null, true, false, cancellationToken).ConfigureAwait(false);
            var mediaSource = mediaSources
                .FirstOrDefault(i => string.Equals(i.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
            if (mediaSource is null)
            {
                throw new ResourceNotFoundException($"MediaSource {mediaSourceId} not found");
            }

            var mediaAttachment = mediaSource.MediaAttachments
                .FirstOrDefault(i => i.Index == attachmentStreamIndex);
            if (mediaAttachment is null)
            {
                throw new ResourceNotFoundException($"MediaSource {mediaSourceId} has no attachment with stream index {attachmentStreamIndex}");
            }

            if (string.Equals(mediaAttachment.Codec, "mjpeg", StringComparison.OrdinalIgnoreCase))
            {
                throw new ResourceNotFoundException($"Attachment with stream index {attachmentStreamIndex} can't be extracted for MediaSource {mediaSourceId}");
            }

            var output = AttachmentPath(mediaSource.Id, mediaAttachment);
            await ExtractAttachments(mediaSource.Path, mediaSource, [mediaAttachment], cancellationToken).ConfigureAwait(false);
            return (mediaAttachment, AsyncFile.OpenRead(output));
        }

        /// <inheritdoc />
        public Task ExtractAllAttachments(string inputFile, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
            => ExtractAttachments(inputFile, mediaSource, mediaSource.MediaAttachments, cancellationToken);

        private string AttachmentPath(string mediaSourceId, MediaAttachment attachment)
        {
            var index = attachment.Index.ToString(CultureInfo.InvariantCulture);
            var name = Path.GetFileName((attachment.FileName ?? index).Replace('\\', '/'));
            if (string.IsNullOrEmpty(name) || name is "." or "..")
            {
                name = index;
            }

            var safeName = new string(name.Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray());
            return _pathManager.GetAttachmentPath(mediaSourceId, index + "-" + safeName)
                ?? throw new ResourceNotFoundException($"MediaSource {mediaSourceId} has no attachment cache.");
        }

        private async Task ExtractAttachments(string inputFile, MediaSourceInfo mediaSource, IReadOnlyList<MediaAttachment> attachments, CancellationToken cancellationToken)
        {
            var outputFolder = _pathManager.GetAttachmentFolderPath(mediaSource.Id);
            if (outputFolder is null)
            {
                _logger.LogDebug("Skipping attachment extraction for input {InputFile}: MediaSource Id is not a GUID.", inputFile);
                return;
            }

            using (await _semaphoreLocks.LockAsync(outputFolder, cancellationToken).ConfigureAwait(false))
            {
                var missing = attachments
                    .Where(attachment => !string.Equals(attachment.Codec, "mjpeg", StringComparison.OrdinalIgnoreCase))
                    .Select(attachment => (Attachment: attachment, Path: AttachmentPath(mediaSource.Id, attachment)))
                    .Where(output => !File.Exists(output.Path) || new FileInfo(output.Path).Length == 0)
                    .ToList();
                if (missing.Count == 0)
                {
                    return;
                }

                var input = _mediaEncoder.GetInputArgument(inputFile, mediaSource);
                ArgumentException.ThrowIfNullOrEmpty(input);
                Directory.CreateDirectory(outputFolder);
                var hasAudioOrVideo = mediaSource.MediaStreams.Any(stream => stream.Type is MediaStreamType.Audio or MediaStreamType.Video);
                FfmpegCommand CreateCommand(IReadOnlyList<string> staged)
                {
                    var arguments = new StringBuilder("-nostdin -y ");
                    for (var index = 0; index < missing.Count; index++)
                    {
                        arguments.AppendFormat(CultureInfo.InvariantCulture, "-dump_attachment:{0} \"{1}\" ", missing[index].Attachment.Index, EncodingUtils.NormalizePath(staged[index]));
                    }

                    if (input.EndsWith(".concat\"", StringComparison.OrdinalIgnoreCase))
                    {
                        arguments.Append("-f concat -safe 0 ");
                    }

                    arguments.Append("-i ").Append(input);
                    if (hasAudioOrVideo)
                    {
                        arguments.Append(" -t 0 -f null null");
                    }

                    return FfmpegCommand.FromArgumentLine(_mediaEncoder.EncoderPath, arguments.ToString());
                }

                // An attachment-only MKS has no output stream; FFmpeg exits 1 after dumping it.
                // Accept that exit only if every requested attachment was actually produced.
                await _executor.ExecuteAsync(
                    CreateCommand,
                    missing.Select(output => output.Path).ToArray(),
                    Timeout.InfiniteTimeSpan,
                    cancellationToken,
                    hasAudioOrVideo ? [0] : [0, 1]).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public void Dispose() => _semaphoreLocks.Dispose();
    }
}
