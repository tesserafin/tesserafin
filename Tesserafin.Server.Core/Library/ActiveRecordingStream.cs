using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Streaming;
using Tesserafin.Model.Dto;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;

namespace Tesserafin.Server.Core.Library;

/// <summary>
/// A Live TV recording still being written, opened as a stream the server holds for one playback
/// session. Its bytes are read from the recording file as it grows, from the start, and handed to
/// FFmpeg through <see cref="IDirectStreamProvider"/> (<c>pipe:0</c>) - never through the
/// <c>[Authorize]</c>d <c>LiveTv/LiveRecordings</c> route, which FFmpeg cannot authenticate to.
/// Closing it releases the session's hold only: the recording itself goes on.
/// </summary>
internal sealed class ActiveRecordingStream : ILiveStream, IDirectStreamProvider
{
    private readonly string _path;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActiveRecordingStream"/> class.
    /// </summary>
    /// <param name="recording">The active recording.</param>
    /// <param name="mediaSourceId">The id of the media source the recording's item lists for its file.</param>
    public ActiveRecordingStream(ActiveRecordingInfo recording, string mediaSourceId)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentException.ThrowIfNullOrEmpty(recording.Path);
        ArgumentException.ThrowIfNullOrEmpty(mediaSourceId);
        _path = recording.Path;
        UniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        OriginalStreamId = recording.Id;
        ConsumerCount = 1;
        MediaSource = new MediaSourceInfo
        {
            Id = mediaSourceId,
            LiveStreamId = UniqueId,
            Path = recording.Path,
            Protocol = MediaProtocol.File,
            Container = Path.GetExtension(recording.Path).TrimStart('.'),
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = true,
            IsInfiniteStream = true,
            RequiresOpening = false,
            RequiresClosing = false,
            BufferMs = 0,
            IgnoreDts = true,
            IgnoreIndex = true,
        };
    }

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string? TunerHostId => null;

    /// <inheritdoc />
    public bool EnableStreamSharing => false;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; }

    /// <inheritdoc />
    public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Close() => Task.CompletedTask;

    /// <summary>
    /// Opens the recording file from its start. Reads wait for the bytes the recorder has not
    /// written yet, and end once nothing new arrives for the stream's idle timeout.
    /// </summary>
    /// <returns>A stream over the growing recording.</returns>
    public Stream GetStream()
        => new ProgressiveFileStream(new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            IODefaults.FileStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan));

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
