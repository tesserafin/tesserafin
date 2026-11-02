using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Microsoft.Extensions.Logging;
using Tesserafin.Common;
using Tesserafin.Common.Configuration;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Diagnostics;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Session;
using Tesserafin.Controller.Streaming;
using Tesserafin.Data;
using Tesserafin.Database.Implementations.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;
using Tesserafin.Model.Session;

namespace Tesserafin.MediaEncoding.Transcoding;

/// <inheritdoc cref="ITranscodeManager"/>
public sealed class TranscodeManager : ITranscodeManager, IHlsSegmentBindingRegistry, IProgressiveJobBindingRegistry, IHardwareTranscodeFallback, ITranscodeOutputStop, ITranscodeOwnedStop, ITranscodeOwnedPing, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TranscodeManager> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _appPaths;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly EncodingHelper _encodingHelper;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IAttachmentExtractor _attachmentExtractor;
    private readonly IRequestCorrelationAccessor _requestCorrelation;

    private readonly List<TranscodingJob> _activeTranscodingJobs = new();
    private readonly AsyncKeyedLocker<string> _transcodingLocks = new(o =>
    {
        o.PoolSize = 20;
        o.PoolInitialFill = 1;
    });

    private readonly Version _maxFFmpegCkeyPauseSupported = new Version(6, 1);

    // tesserafin#289. HLS outputs still holding what a failed attempt wrote before software took
    // over, by the attempt that wrote it. See RemoveFailedOutput.
    private readonly Dictionary<string, TranscodingJob> _failedOutputs = new(StringComparer.OrdinalIgnoreCase);

    // #153-LTV-R1. Monotonic across the process, so two jobs that reuse one playlist identifier
    // are still distinguishable and a stale binding cannot be mistaken for a live one.
    // tesserafin#119. What a hardware failure in THIS server process has shown cannot work.
    // In memory only: the stored configuration is never touched, and the next start's own
    // hardware verification decides afresh.
    private static readonly TimeSpan FailureAnswerLifetime = TimeSpan.FromSeconds(30);

    private readonly object _fallbackLock = new();
    private readonly HashSet<HardwareAccelerationType> _unavailableBackends = new();
    private readonly HashSet<string> _softwareOnlyMediaSources = new(StringComparer.OrdinalIgnoreCase);

    private long _jobGeneration;
    private EncodingOptions? _softwareOptionsSource;
    private EncodingOptions? _softwareOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeManager"/> class.
    /// </summary>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
    /// <param name="fileSystem">The <see cref="IFileSystem"/>.</param>
    /// <param name="appPaths">The <see cref="IApplicationPaths"/>.</param>
    /// <param name="serverConfigurationManager">The <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="userManager">The <see cref="IUserManager"/>.</param>
    /// <param name="sessionManager">The <see cref="ISessionManager"/>.</param>
    /// <param name="encodingHelper">The <see cref="EncodingHelper"/>.</param>
    /// <param name="mediaEncoder">The <see cref="IMediaEncoder"/>.</param>
    /// <param name="mediaSourceManager">The <see cref="IMediaSourceManager"/>.</param>
    /// <param name="attachmentExtractor">The <see cref="IAttachmentExtractor"/>.</param>
    /// <param name="requestCorrelation">
    /// Issue #42: supplies the correlation id of the HTTP request currently in flight, so the
    /// ping/kill log lines below carry it alongside the session-scoped <c>PlaySessionId</c> they
    /// already had. Defaults to <see cref="NullRequestCorrelationAccessor"/> when not supplied, so
    /// every existing call site — test constructors above all — keeps compiling and simply logs no
    /// request id. Reads <c>null</c> on purpose for the timer-driven kill path, which is caused by
    /// no request at all.
    /// </param>
    public TranscodeManager(
        ILoggerFactory loggerFactory,
        IFileSystem fileSystem,
        IApplicationPaths appPaths,
        IServerConfigurationManager serverConfigurationManager,
        IUserManager userManager,
        ISessionManager sessionManager,
        EncodingHelper encodingHelper,
        IMediaEncoder mediaEncoder,
        IMediaSourceManager mediaSourceManager,
        IAttachmentExtractor attachmentExtractor,
        IRequestCorrelationAccessor? requestCorrelation = null)
    {
        _requestCorrelation = requestCorrelation ?? NullRequestCorrelationAccessor.Instance;
        _loggerFactory = loggerFactory;
        _fileSystem = fileSystem;
        _appPaths = appPaths;
        _serverConfigurationManager = serverConfigurationManager;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _encodingHelper = encodingHelper;
        _mediaEncoder = mediaEncoder;
        _mediaSourceManager = mediaSourceManager;
        _attachmentExtractor = attachmentExtractor;

        _logger = loggerFactory.CreateLogger<TranscodeManager>();
        DeleteEncodedMediaCache();
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStart += OnPlaybackProgress;
    }

    /// <inheritdoc />
    public event EventHandler<TranscodingJob>? TranscodingJobEnded;

    /// <inheritdoc />
    public event EventHandler<TranscodingJob>? TranscodingJobStarted;

    /// <summary>
    /// Gets or sets the clock a failure is dated and aged with. The system clock, except in a test
    /// that has to stand on either side of <see cref="FailureAnswerLifetime"/>.
    /// </summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <inheritdoc />
    public TranscodingJob? GetTranscodingJob(string playSessionId)
    {
        lock (_activeTranscodingJobs)
        {
            return _activeTranscodingJobs.FirstOrDefault(j => string.Equals(j.PlaySessionId, playSessionId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type)
    {
        lock (_activeTranscodingJobs)
        {
            return _activeTranscodingJobs.FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public HlsSegmentBinding? ResolveByPlaylistId(string playlistId)
    {
        if (string.IsNullOrEmpty(playlistId))
        {
            return null;
        }

        // The playlist identifier IS the job's output file name without its extension: that is
        // what DynamicHlsController passes to ffmpeg as `-hls_base_url "hls/{name}/"` and as
        // the `-hls_segment_filename "{name}%d.{ext}"` prefix. Matching on it is therefore a
        // server-side fact, not a guess about what the caller meant.
        return Project(SelectHlsJob(j =>
            string.Equals(Path.GetFileNameWithoutExtension(j.Path), playlistId, StringComparison.Ordinal)));
    }

    /// <inheritdoc />
    public HlsSegmentBinding? ResolveBySegmentName(string segmentName)
    {
        if (string.IsNullOrEmpty(segmentName))
        {
            return null;
        }

        // #153-LTV-R3. The audio sibling route names no playlist, so the segment name is all there
        // is to select a job with — and selecting is ALL it is used for. ffmpeg writes a job's
        // segments as "{playlistId}%d.{ext}", so the owner is the job whose playlist identifier
        // prefixes the name. The file itself is then built from that job's own canonical root by
        // the caller of this method, never from the transcode folder plus this string.
        return Project(SelectHlsJob(j =>
        {
            var jobPlaylistId = Path.GetFileNameWithoutExtension(j.Path);
            return !string.IsNullOrEmpty(jobPlaylistId)
                   && segmentName.StartsWith(jobPlaylistId, StringComparison.Ordinal);
        }));
    }

    /// <inheritdoc />
    public HlsSegmentBinding? ResolveByOutputPath(string outputPath)
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            return null;
        }

        // #153-LTV-R3. DynamicHlsController already knows the path it would serve from; what it
        // does not know is whose job writes it. The comparison is on the canonicalised path, so a
        // job selected here is a job this server started, not a path a caller composed.
        var canonical = Path.GetFullPath(outputPath);
        return Project(SelectHlsJob(j =>
            string.Equals(Path.GetFullPath(j.Path!), canonical, StringComparison.Ordinal)));
    }

    /// <inheritdoc />
    public HlsSegmentBinding? ResolveProgressiveByOutputPath(string outputPath)
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            return null;
        }

        var canonical = Path.GetFullPath(outputPath);
        return Project(SelectJob(TranscodingJobType.Progressive, j =>
            string.Equals(Path.GetFullPath(j.Path!), canonical, StringComparison.Ordinal)));
    }

    private TranscodingJob? SelectHlsJob(Func<TranscodingJob, bool> predicate)
        => SelectJob(TranscodingJobType.Hls, predicate);

    private TranscodingJob? SelectJob(TranscodingJobType type, Func<TranscodingJob, bool> predicate)
    {
        lock (_activeTranscodingJobs)
        {
            return _activeTranscodingJobs.FirstOrDefault(j =>
                j.Type == type
                && j.Path is not null
                && predicate(j));
        }
    }

    private static HlsSegmentBinding? Project(TranscodingJob? job)
    {
        if (job?.Path is null)
        {
            return null;
        }

        var canonicalPlaylistPath = Path.GetFullPath(job.Path);
        var directory = Path.GetDirectoryName(canonicalPlaylistPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        return new HlsSegmentBinding(
            Path.GetFileNameWithoutExtension(job.Path),
            job.UserId,
            job.OwnerDeviceId,
            job.ItemId,
            job.MediaSourceId,
            job.PlaySessionId,
            directory,
            canonicalPlaylistPath,
            job.Generation);
    }

    /// <inheritdoc />
    public void PingTranscodingJob(string playSessionId, bool? isUserPaused)
        => PingTranscodingJobs(playSessionId, isUserPaused, static _ => true);

    /// <inheritdoc />
    public void PingTranscodingJobs(string playSessionId, bool? isUserPaused, Func<TranscodingJob, bool> isCallers)
    {
        ArgumentException.ThrowIfNullOrEmpty(playSessionId);
        ArgumentNullException.ThrowIfNull(isCallers);

        // Issue #42: named placeholders, so RequestId/PlaySessionId land as structured properties
        // rather than being baked into the message text. RequestId is the id of the HTTP ping that
        // caused this call and differs on every ping; PlaySessionId is the same for all of them.
        _logger.LogDebug(
            "PingTranscodingJob RequestId={RequestId} PlaySessionId={PlaySessionId} isUsedPaused: {IsUserPaused}",
            _requestCorrelation.CurrentRequestId,
            playSessionId.ToSingleLogLine(),
            isUserPaused);

        List<TranscodingJob> jobs;

        lock (_activeTranscodingJobs)
        {
            // This is really only needed for HLS.
            // Progressive streams can stop on their own reliably.
            jobs = _activeTranscodingJobs.Where(j => string.Equals(playSessionId, j.PlaySessionId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Asked outside the registry's lock, and of each job: a play session id is the client's to
        // send, so two users' jobs can carry the same one.
        foreach (var job in jobs.Where(isCallers))
        {
            if (isUserPaused.HasValue)
            {
                _logger.LogDebug("Setting job.IsUserPaused to {0}. jobId: {1}", isUserPaused, job.Id);
                job.IsUserPaused = isUserPaused.Value;
            }

            PingTimer(job, true);
        }
    }

    private void PingTimer(TranscodingJob job, bool isProgressCheckIn)
    {
        if (job.HasExited)
        {
            job.StopKillTimer();
            return;
        }

        var timerDuration = 10000;

        if (job.Type != TranscodingJobType.Progressive)
        {
            timerDuration = 60000;
        }

        job.PingTimeout = timerDuration;
        job.LastPingDate = DateTime.UtcNow;

        // Don't start the timer for playback checkins with progressive streaming
        if (job.Type != TranscodingJobType.Progressive || !isProgressCheckIn)
        {
            job.StartKillTimer(OnTranscodeKillTimerStopped);
        }
        else
        {
            job.ChangeKillTimerIfStarted();
        }
    }

    private async void OnTranscodeKillTimerStopped(object? state)
    {
        var job = state as TranscodingJob ?? throw new ArgumentException($"{nameof(state)} is not of type {nameof(TranscodingJob)}", nameof(state));
        if (!job.HasExited && job.Type != TranscodingJobType.Progressive)
        {
            var timeSinceLastPing = (DateTime.UtcNow - job.LastPingDate).TotalMilliseconds;

            if (timeSinceLastPing < job.PingTimeout)
            {
                job.StartKillTimer(OnTranscodeKillTimerStopped, job.PingTimeout);
                return;
            }
        }

        // Issue #42: this path is reached from a timer callback, not from a request, so RequestId is
        // normally null here. That is the honest value — nothing requested this kill — and it is
        // exactly what distinguishes a timeout-driven kill from a client-driven DELETE in the logs.
        _logger.LogInformation(
            "Transcoding kill timer stopped for RequestId={RequestId} JobId {JobId} PlaySessionId {PlaySessionId}. Killing transcoding",
            _requestCorrelation.CurrentRequestId,
            job.Id,
            job.PlaySessionId);

        try
        {
            await KillTranscodingJob(job, true, path => true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nobody awaits a timer callback: thrown from here, it would take the server down.
            // A job whose process did not end stays registered, and so does its owner.
            _logger.LogError(ex, "Error killing transcoding job {JobId} after its kill timer", job.Id);
        }
    }

    /// <inheritdoc />
    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles)
    {
        var jobs = new List<TranscodingJob>();

        lock (_activeTranscodingJobs)
        {
            // This is really only needed for HLS.
            // Progressive streams can stop on their own reliably.
            jobs.AddRange(_activeTranscodingJobs.Where(j => string.IsNullOrWhiteSpace(playSessionId)
                ? string.Equals(deviceId, j.DeviceId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(playSessionId, j.PlaySessionId, StringComparison.OrdinalIgnoreCase)));
        }

        return Task.WhenAll(GetKillJobs());

        IEnumerable<Task> GetKillJobs()
        {
            foreach (var job in jobs)
            {
                yield return KillTranscodingJob(job, false, deleteFiles);
            }
        }
    }

    /// <inheritdoc />
    public Task StopTranscodingJob(string path, TranscodingJobType type, long generation, Func<string, bool> deleteFiles)
    {
        TranscodingJob? job;

        lock (_activeTranscodingJobs)
        {
            job = _activeTranscodingJobs.FirstOrDefault(j => j.Type == type
                && j.Generation == generation
                && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        return job is null ? Task.CompletedTask : KillTranscodingJob(job, false, deleteFiles);
    }

    /// <inheritdoc />
    public Task StopTranscodingJobs(string playSessionId, Func<TranscodingJob, bool> isCallers)
    {
        // No play session id names no job - not every job that was started without one.
        if (string.IsNullOrWhiteSpace(playSessionId))
        {
            return Task.CompletedTask;
        }

        List<TranscodingJob> jobs;

        lock (_activeTranscodingJobs)
        {
            jobs = _activeTranscodingJobs.Where(j => string.Equals(playSessionId, j.PlaySessionId, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Asked outside the registry's lock - the answer comes from the session manager - and of
        // each job. What is stopped below is the job that was asked about, itself: nothing is
        // selected again by what the caller named.
        List<Exception>? errors = null;
        foreach (var job in jobs.Where(isCallers))
        {
            try
            {
                StopJob(job);

                // Not waited for: the process is what the caller asked to have stopped. An HLS
                // output's files are not served meanwhile - see DynamicHlsController.RemoveUnownedOutput.
                _ = DeletePartialStreamFiles(job.Path!, job.Type, 0, 1500);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping transcoding job {JobId}", job.Id);
                (errors ??= new List<Exception>()).Add(ex);
            }
        }

        return errors is null ? Task.CompletedTask : Task.FromException(new AggregateException("Error stopping transcoding jobs", errors));
    }

    private async Task KillTranscodingJob(TranscodingJob job, bool closeLiveStream, Func<string, bool> delete)
    {
        StopJob(job);

        if (delete(job.Path!))
        {
            await DeletePartialStreamFiles(job.Path!, job.Type, 0, 1500).ConfigureAwait(false);
        }

        if (closeLiveStream && !string.IsNullOrWhiteSpace(job.LiveStreamId))
        {
            await _sessionManager.CloseLiveStreamIfNeededAsync(job.LiveStreamId, job.PlaySessionId).ConfigureAwait(false);
        }
    }

    private void StopJob(TranscodingJob job)
    {
        job.DisposeKillTimer();
        job.CurrentAttempt?.MarkStopRequested();

        _logger.LogDebug(
            "KillTranscodingJob - RequestId={RequestId} JobId {JobId} PlaySessionId {PlaySessionId}. Killing transcoding",
            _requestCorrelation.CurrentRequestId,
            job.Id,
            job.PlaySessionId);

        lock (_activeTranscodingJobs)
        {
            if (job.CancellationTokenSource?.IsCancellationRequested == false)
            {
#pragma warning disable CA1849 // Can't await in lock block
                job.CancellationTokenSource.Cancel();
#pragma warning restore CA1849
            }
        }

        // The job stays registered until its process has been waited for. It used to leave first,
        // and for as long as the stop took the output had no owner while something was still
        // writing to it: a request for it was compared with nobody, and started a transcode beside
        // the dying one.
        job.Stop();

        // The wait is bounded, so it can end with the process still there. Then the stop has not
        // happened: the job keeps its place and its owner, nothing is removed, and the caller is
        // told. Leaving the registry is not what proves a process has ended.
        //
        // Asked under the registry's lock, where StartFfMpeg starts a process: a job that is
        // stopped before its process exists has nothing to wait for, and that process must not
        // be started once the job has left.
        lock (_activeTranscodingJobs)
        {
            if (!HasEnded(job))
            {
                throw new TimeoutException(string.Format(CultureInfo.InvariantCulture, "The FFmpeg process of transcoding job {0} did not end when stopped; its output is still its owner's.", job.Id));
            }

            _activeTranscodingJobs.Remove(job);
        }

        TranscodingJobEnded?.Invoke(this, job);
    }

    private static bool HasEnded(TranscodingJob job)
    {
        if (job.HasExited)
        {
            return true;
        }

        try
        {
            return job.Process?.HasExited ?? true;
        }
        catch (InvalidOperationException)
        {
            // Never started, or disposed of by the handling of its own exit: nothing is running.
            return true;
        }
    }

    private async Task DeletePartialStreamFiles(string path, TranscodingJobType jobType, int retryCount, int delayMs)
    {
        if (retryCount >= 10)
        {
            return;
        }

        _logger.LogInformation("Deleting partial stream file(s) {Path}", path);

        await Task.Delay(delayMs).ConfigureAwait(false);

        // Asked before the lock as well: a successor can hold the lock for as long as its own
        // start takes, and a stop that awaits this removal must not wait on it for nothing.
        if (GetTranscodingJob(path, jobType) is not null)
        {
            _logger.LogDebug("Not deleting {Path}: another transcode has started on it", path);
            return;
        }

        try
        {
            // The job was unregistered before the delay, and a request on the same play session
            // can have started another on this output since. Under the lock a start takes, so
            // that it is entirely before or after; and not at all if the output has a new owner.
            using (await _transcodingLocks.LockAsync(path).ConfigureAwait(false))
            {
                if (GetTranscodingJob(path, jobType) is not null)
                {
                    _logger.LogDebug("Not deleting {Path}: another transcode has started on it", path);
                    return;
                }

                if (jobType == TranscodingJobType.Progressive)
                {
                    DeleteProgressivePartialStreamFiles(path);
                }
                else
                {
                    DeleteHlsPartialStreamFiles(path);
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);

            await DeletePartialStreamFiles(path, jobType, retryCount + 1, 500).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);
        }
    }

    private void DeleteProgressivePartialStreamFiles(string outputFilePath)
    {
        if (File.Exists(outputFilePath))
        {
            _fileSystem.DeleteFile(outputFilePath);
        }
    }

    private void DeleteHlsPartialStreamFiles(string outputFilePath)
    {
        var directory = Path.GetDirectoryName(outputFilePath)
                        ?? throw new ArgumentException("Path can't be a root directory.", nameof(outputFilePath));

        var name = Path.GetFileNameWithoutExtension(outputFilePath);

        var filesToDelete = _fileSystem.GetFilePaths(directory)
            .Where(f => f.Contains(name, StringComparison.OrdinalIgnoreCase));

        List<Exception>? exs = null;
        foreach (var file in filesToDelete)
        {
            try
            {
                _logger.LogDebug("Deleting HLS file {0}", file);
                _fileSystem.DeleteFile(file);
            }
            catch (IOException ex)
            {
                (exs ??= new List<Exception>()).Add(ex);
                _logger.LogError(ex, "Error deleting HLS file {Path}", file);
            }
        }

        if (exs is not null)
        {
            throw new AggregateException("Error deleting HLS files", exs);
        }
    }

    /// <inheritdoc />
    public void ReportTranscodingProgress(
        TranscodingJob job,
        StreamState state,
        TimeSpan? transcodingPosition,
        float? framerate,
        double? percentComplete,
        long? bytesTranscoded,
        int? bitRate)
    {
        var ticks = transcodingPosition?.Ticks;

        if (job is not null)
        {
            job.Framerate = framerate;
            job.CompletionPercentage = percentComplete;
            job.TranscodingPositionTicks = ticks;
            job.BytesTranscoded = bytesTranscoded;
            job.BitRate = bitRate;
        }

        var deviceId = state.Request.DeviceId;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var audioCodec = state.ActualOutputAudioCodec;
            var videoCodec = state.ActualOutputVideoCodec;
            // What this job's own command was built for, when known: a session that is still
            // healthy on hardware keeps saying so after the backend was withheld from new ones.
            var hardwareAccelerationType = job?.CurrentAttempt?.Backend
                ?? GetEffectiveEncodingOptions(state.Request.MediaSourceId).HardwareAccelerationType;

            _sessionManager.ReportTranscodingInfo(deviceId, new TranscodingInfo
            {
                Bitrate = bitRate ?? state.TotalOutputBitrate,
                AudioCodec = audioCodec,
                VideoCodec = videoCodec,
                Container = state.OutputContainer,
                Framerate = framerate,
                CompletionPercentage = percentComplete,
                Width = state.OutputWidth,
                Height = state.OutputHeight,
                AudioChannels = state.OutputAudioChannels,
                IsAudioDirect = EncodingHelper.IsCopyCodec(state.OutputAudioCodec),
                IsVideoDirect = EncodingHelper.IsCopyCodec(state.OutputVideoCodec),
                HardwareAccelerationType = hardwareAccelerationType,
                TranscodeReasons = state.TranscodeReasons
            });
        }
    }

    /// <inheritdoc />
    public EncodingOptions GetEffectiveEncodingOptions(string? mediaSourceId)
    {
        var configured = _serverConfigurationManager.GetEncodingOptions();
        if (configured.HardwareAccelerationType == HardwareAccelerationType.none)
        {
            return configured;
        }

        lock (_fallbackLock)
        {
            var withheld = _unavailableBackends.Contains(configured.HardwareAccelerationType)
                || (!string.IsNullOrEmpty(mediaSourceId) && _softwareOnlyMediaSources.Contains(mediaSourceId));

            return withheld ? GetSoftwareOptions(configured) : configured;
        }
    }

    /// <inheritdoc />
    public TranscodeFailure? GetTranscodeFailure(string path, TranscodingJobType type)
    {
        lock (_activeTranscodingJobs)
        {
            // For a short while only. A client that knows the answer reloads within a second or
            // two and releases the job; one that does not must not be refused forever on a play
            // session it keeps asking for - after this it is served a fresh transcode, built
            // with the options in force by then.
            var notBefore = TimeProvider.GetUtcNow().UtcDateTime - FailureAnswerLifetime;

            return _activeTranscodingJobs
                .FirstOrDefault(j => j.Type == type
                    && j.Failure is not null
                    && j.Failure.OccurredAt >= notBefore
                    && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase))
                ?.Failure;
        }
    }

    /// <inheritdoc />
    public async Task<TranscodingJob> StartFfMpeg(
        StreamState state,
        string outputPath,
        string commandLineArguments,
        Guid userId,
        TranscodingJobType transcodingJobType,
        CancellationTokenSource cancellationTokenSource,
        string? workingDirectory = null)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath));
        Directory.CreateDirectory(directory);

        // tesserafin#289. Every caller holds LockAsync(outputPath). A failed attempt whose output
        // has not been removed yet - it was released before its own exit got that far - does not
        // leave it to the process started here.
        RemoveFailedOutput(outputPath, null);

        await AcquireResources(state, cancellationTokenSource).ConfigureAwait(false);

        if (state.VideoRequest is not null && !EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            var user = userId.IsEmpty() ? null : _userManager.GetUserById(userId);
            if (user is not null && !user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding))
            {
                // No job is registered for this start yet: there is none to stop.
                OnTranscodeFailedToStart(null, state);

                throw new ArgumentException("User does not have access to video transcoding.");
            }
        }

        ArgumentException.ThrowIfNullOrEmpty(_mediaEncoder.EncoderPath);

        // If subtitles get burned in fonts may need to be extracted from the media file
        if (state.SubtitleStream is not null && (state.SubtitleDeliveryMethod == SubtitleDeliveryMethod.Encode || state.BaseRequest.AlwaysBurnInSubtitleWhenTranscoding))
        {
            if (state.MediaSource.VideoType == VideoType.Dvd || state.MediaSource.VideoType == VideoType.BluRay)
            {
                var concatPath = Path.Join(_appPaths.CachePath, "concat", state.MediaSource.Id + ".concat");
                await _attachmentExtractor.ExtractAllAttachments(concatPath, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false);
            }
            else
            {
                await _attachmentExtractor.ExtractAllAttachments(state.MediaPath, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false);
            }

            if (state.SubtitleStream.IsExternal && Path.GetExtension(state.SubtitleStream.Path.AsSpan()).Equals(".mks", StringComparison.OrdinalIgnoreCase))
            {
                await _attachmentExtractor.ExtractAllAttachments(state.SubtitleStream.Path, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false);
            }
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,

                // Must consume both stdout and stderr or deadlocks may occur
                // RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                FileName = _mediaEncoder.EncoderPath,
                Arguments = commandLineArguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? string.Empty : workingDirectory,
                ErrorDialog = false
            },
            EnableRaisingEvents = true
        };

        var transcodingJob = OnTranscodeBeginning(
            outputPath,
            state.Request.PlaySessionId,
            state.MediaSource.LiveStreamId,
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            transcodingJobType,
            process,
            state.Request.DeviceId,
            userId,
            state,
            cancellationTokenSource);

        DescribeAttempt(transcodingJob.CurrentAttempt!, state, commandLineArguments);

        _logger.LogInformation("{Filename} {Arguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

        var logFilePrefix = "FFmpeg.Transcode-";
        if (state.VideoRequest is not null
            && EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            logFilePrefix = EncodingHelper.IsCopyCodec(state.OutputAudioCodec)
                ? "FFmpeg.Remux-"
                : "FFmpeg.DirectStream-";
        }

        if (state.VideoRequest is null && EncodingHelper.IsCopyCodec(state.OutputAudioCodec))
        {
            logFilePrefix = "FFmpeg.Remux-";
        }

        var logFilePath = Path.Combine(
            _serverConfigurationManager.ApplicationPaths.LogDirectoryPath,
            $"{logFilePrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{state.Request.MediaSourceId}_{Guid.NewGuid().ToString()[..8]}.log");

        // FFmpeg writes debug/error info to stderr. This is useful when debugging so let's put it in the log directory.
        Stream logStream = new FileStream(
            logFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            IODefaults.FileStreamBufferSize,
            FileOptions.Asynchronous);

        await JsonSerializer.SerializeAsync(logStream, state.MediaSource, cancellationToken: cancellationTokenSource.Token).ConfigureAwait(false);
        var commandLineLogMessageBytes = Encoding.UTF8.GetBytes(
            Environment.NewLine
            + Environment.NewLine
            + process.StartInfo.FileName + " " + process.StartInfo.Arguments
            + Environment.NewLine
            + Environment.NewLine);

        await logStream.WriteAsync(commandLineLogMessageBytes, cancellationTokenSource.Token).ConfigureAwait(false);

        process.Exited += (_, _) => OnFfMpegProcessExited(process, transcodingJob, state);

        try
        {
            // A job is registered before its process is started, and can be stopped in between.
            // There is no process to stop then, so the job leaves the registry - and a process
            // started after that would write to an output nobody owns. Decided under the lock a
            // job leaves the registry under.
            lock (_activeTranscodingJobs)
            {
                if (!_activeTranscodingJobs.Contains(transcodingJob) || cancellationTokenSource.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The transcoding job was stopped before its process was started.");
                }

                process.Start();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error starting FFmpeg");
            OnTranscodeFailedToStart(transcodingJob, state);

            throw;
        }

        _logger.LogDebug("Launched FFmpeg process");
        state.TranscodingJob = transcodingJob;

        if (state.DirectStreamProvider is not null)
        {
            // The server already holds this live stream open. Feed ffmpeg the bytes over its
            // standard input instead of letting it fetch the [Authorize]d LiveStreamFiles URL that
            // SharedHttpStream publishes as MediaSource.Path - a child process has no session and
            // would only ever be refused. EncodingHelper has already selected "-i pipe:0" for
            // exactly this state. This must start before the "wait for the output file" loop below,
            // or ffmpeg would be given nothing to read while that loop waits for its output.
            //
            // GetStream() is called once per ffmpeg process, so two concurrent consumers get two
            // independent readers over the tuner's temp file and never share a Stream instance.
            transcodingJob.CurrentAttempt!.StandardInputIsMediaPipe = true;
            transcodingJob.DirectStreamPump = DirectStreamPump.Start(
                new ProgressiveFileStream(state.DirectStreamProvider.GetStream()),
                process.StandardInput.BaseStream,
                _logger,
                cancellationTokenSource.Token);
        }

        // PR113b: raised only once Process.Start() has actually returned successfully - this is
        // the real, observed "ffmpeg launched" moment, not an approximation from some earlier step
        // (command-line build, resource acquisition) that could still fail before the process
        // exists.
        TranscodingJobStarted?.Invoke(this, transcodingJob);

        if (state.VideoRequest is not null)
        {
            // Diagnostic only - logs what EncodingHelper decided, does not feed back into the
            // command that was already built and started above. See TranscodePlanner remarks.
            var plan = TranscodePlanner.CreatePlan(_encodingHelper, state, _serverConfigurationManager.GetEncodingOptions());
            _logger.LogDebug(
                "Transcode plan: codec={VideoCodec} requestedHwAccel={RequestedHwAccel} selectedEncoder={SelectedEncoder} isHardware={IsHardware}",
                plan.VideoCodec,
                plan.RequestedHardwareAccelerationType,
                plan.SelectedVideoEncoder,
                plan.IsHardwareEncoder);
        }

        // Important - don't await the log task or we won't be able to kill FFmpeg when the user stops playback.
        // It is kept, though: what it recognised in stderr is the evidence a failure is judged on.
        var jobLogger = new JobLogger(_logger);
        transcodingJob.CurrentAttempt!.Diagnostics = jobLogger;
        transcodingJob.CurrentAttempt.DiagnosticsCompleted = jobLogger.StartStreamingLog(state, process.StandardError, logStream);

        // Wait for the file to exist before proceeding
        var ffmpegTargetFile = state.WaitForPath ?? outputPath;
        _logger.LogDebug("Waiting for the creation of {0}", ffmpegTargetFile);
        while (!File.Exists(ffmpegTargetFile) && !transcodingJob.HasExited)
        {
            // A producer that dies before ffmpeg's first output must fail the job by name. Left to
            // ffmpeg alone it looks like a slow start until the process happens to exit, and the
            // real cause - the tuner - never appears in the error.
            var pumpFault = transcodingJob.DirectStreamPump?.Fault;
            if (pumpFault is not null)
            {
                OnTranscodeFailedToStart(transcodingJob, state);

                throw new FfmpegException("The live stream feeding FFmpeg failed before any output was produced.", pumpFault);
            }

            await Task.Delay(100, cancellationTokenSource.Token).ConfigureAwait(false);
        }

        _logger.LogDebug("File {0} created or transcoding has finished", ffmpegTargetFile);

        if (state.IsInputVideo && transcodingJob.Type == TranscodingJobType.Progressive && !transcodingJob.HasExited)
        {
            await Task.Delay(1000, cancellationTokenSource.Token).ConfigureAwait(false);

            if (state.ReadInputAtNativeFramerate && !transcodingJob.HasExited)
            {
                await Task.Delay(1500, cancellationTokenSource.Token).ConfigureAwait(false);
            }
        }

        if (!transcodingJob.HasExited)
        {
            StartThrottler(state, transcodingJob);
            StartSegmentCleaner(state, transcodingJob);
        }
        else if (transcodingJob.ExitCode != 0)
        {
            throw new FfmpegException(string.Format(CultureInfo.InvariantCulture, "FFmpeg exited with code {0}", transcodingJob.ExitCode));
        }

        _logger.LogDebug("StartFfMpeg() finished successfully");

        return transcodingJob;
    }

    private void StartThrottler(StreamState state, TranscodingJob transcodingJob)
    {
        if (EnableThrottling(state)
            && (_mediaEncoder.IsPkeyPauseSupported
                || _mediaEncoder.EncoderVersion <= _maxFFmpegCkeyPauseSupported))
        {
            transcodingJob.TranscodingThrottler = new TranscodingThrottler(transcodingJob, _loggerFactory.CreateLogger<TranscodingThrottler>(), _serverConfigurationManager, _fileSystem, _mediaEncoder);
            transcodingJob.TranscodingThrottler.Start();
        }
    }

    private static bool EnableThrottling(StreamState state)
        => state.InputProtocol == MediaProtocol.File
           && state.RunTimeTicks.HasValue
           && state.RunTimeTicks.Value >= TimeSpan.FromMinutes(5).Ticks
           && state.IsInputVideo
           && state.VideoType == VideoType.VideoFile;

    private void StartSegmentCleaner(StreamState state, TranscodingJob transcodingJob)
    {
        if (EnableSegmentCleaning(state))
        {
            transcodingJob.TranscodingSegmentCleaner = new TranscodingSegmentCleaner(transcodingJob, _loggerFactory.CreateLogger<TranscodingSegmentCleaner>(), _serverConfigurationManager, _fileSystem, _mediaEncoder, state.SegmentLength);
            transcodingJob.TranscodingSegmentCleaner.Start();
        }
    }

    private static bool EnableSegmentCleaning(StreamState state)
        => state.InputProtocol is MediaProtocol.File or MediaProtocol.Http
           && state.IsInputVideo
           && state.TranscodingType == TranscodingJobType.Hls
           && state.RunTimeTicks.HasValue
           && state.RunTimeTicks.Value >= TimeSpan.FromMinutes(5).Ticks;

    private TranscodingJob OnTranscodeBeginning(
        string path,
        string? playSessionId,
        string? liveStreamId,
        string transcodingJobId,
        TranscodingJobType type,
        Process process,
        string? deviceId,
        Guid ownerUserId,
        StreamState state,
        CancellationTokenSource cancellationTokenSource)
    {
        lock (_activeTranscodingJobs)
        {
            var job = new TranscodingJob(_loggerFactory.CreateLogger<TranscodingJob>())
            {
                Type = type,
                Path = path,
                Process = process,
                ActiveRequestCount = 1,
                DeviceId = deviceId,
                CancellationTokenSource = cancellationTokenSource,
                Id = transcodingJobId,
                PlaySessionId = playSessionId,
                LiveStreamId = liveStreamId,
                MediaSource = state.MediaSource,

                // #153-LTV-R1. The binding the legacy HLS segment route compares against. Taken
                // from the request that started the job, which is the same shape a capability is
                // minted against, so the three-way comparison route/capability/job is meaningful.
                ItemId = state.Request.Id,
                MediaSourceId = state.Request.MediaSourceId,

                // #153-LTV-R3. The job's owner. `ownerUserId` is the `userId` argument
                // StartFfMpeg is already given, which every call site fills from the validated
                // principal; `state.OwnerDeviceId` is the token's own device claim. Neither is a
                // query parameter, and `DeviceId` above - which is one - stays where it was so
                // that session reporting and teardown keep their meaning.
                UserId = ownerUserId,
                OwnerDeviceId = HlsJobOwnerDevice.Resolve(state.OwnerDeviceId, state.OwnerCapabilitySessionId, _sessionManager),
                Generation = Interlocked.Increment(ref _jobGeneration)
            };

            // An attempt that ended on this output and was never released is still registered.
            // It leaves when its successor arrives: every lookup by output returns the first
            // job on it, and two would answer for each other. By output, never by device or play
            // session - those select other people's jobs when a client names neither.
            _activeTranscodingJobs.RemoveAll(j => j.Type == type && j.HasExited && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
            _activeTranscodingJobs.Add(job);

            ReportTranscodingProgress(job, state, null, null, null, null, null);

            return job;
        }
    }

    /// <inheritdoc />
    public void OnTranscodeEndRequest(TranscodingJob job)
    {
        job.ActiveRequestCount--;
        _logger.LogDebug("OnTranscodeEndRequest job.ActiveRequestCount={ActiveRequestCount}", job.ActiveRequestCount);
        if (job.ActiveRequestCount <= 0)
        {
            PingTimer(job, false);
        }
    }

    private void OnTranscodeFailedToStart(TranscodingJob? job, StreamState state)
    {
        // The job of the start that failed, itself - not whichever job is found on its output,
        // which a start that failed before it registered anything has no claim on.
        if (job is not null)
        {
            try
            {
                // A start can fail with its process already running - the live stream feeding it
                // died first. Stopped like any other job, so that it leaves the registry once
                // that process has ended and not before.
                StopJob(job);
            }
            catch (Exception ex)
            {
                // The start's own failure is what its caller is about to be told.
                _logger.LogError(ex, "Error stopping transcoding job {JobId} after its start failed", job.Id);
            }
        }

        if (!string.IsNullOrWhiteSpace(state.Request.DeviceId))
        {
            _sessionManager.ClearTranscodingInfo(state.Request.DeviceId);
        }
    }

    private void OnFfMpegProcessExited(Process process, TranscodingJob job, StreamState state)
    {
        job.ExitCode = process.ExitCode;

        // Judged BEFORE HasExited is published: a segment request waiting on this job wakes up on
        // HasExited and must find the reason already there. Nothing is removed yet - see
        // RemoveFailedOutput for why that comes after both.
        var removeOutput = false;
        if (process.ExitCode != 0)
        {
            var failure = EvaluateFailure(job, state, process.ExitCode);
            removeOutput = failure is { Decision.ShouldFallback: true } && job.Type == TranscodingJobType.Hls && !string.IsNullOrEmpty(job.Path);
            if (removeOutput)
            {
                lock (_failedOutputs)
                {
                    _failedOutputs[job.Path!] = job;
                }
            }

            job.Failure = failure;
        }

        job.HasExited = true;

        ReportTranscodingProgress(job, state, null, null, null, null, null);

        _logger.LogDebug("Disposing stream resources");
        state.Dispose();

        if (process.ExitCode == 0)
        {
            _logger.LogInformation("FFmpeg exited with code 0");
        }
        else
        {
            _logger.LogError("FFmpeg exited with code {0}", process.ExitCode);
        }

        TranscodingJobEnded?.Invoke(this, job);

        job.Dispose();

        // Last, because it can wait: a request may be holding this output's lock, and nothing
        // above may wait with it - the job's end is reported and the job disposed first.
        if (removeOutput)
        {
            using (_transcodingLocks.Lock(job.Path!))
            {
                RemoveFailedOutput(job.Path!, job);
            }
        }
    }

    /// <summary>
    /// A copy of the configured options with hardware acceleration off. A copy, because the
    /// configured object is the one the dashboard reads and saves.
    /// </summary>
    private EncodingOptions GetSoftwareOptions(EncodingOptions configured)
    {
        lock (_fallbackLock)
        {
            if (!ReferenceEquals(_softwareOptionsSource, configured) || _softwareOptions is null)
            {
                var copy = JsonSerializer.Deserialize<EncodingOptions>(JsonSerializer.Serialize(configured))!;
                copy.HardwareAccelerationType = HardwareAccelerationType.none;
                _softwareOptionsSource = configured;
                _softwareOptions = copy;
            }

            return _softwareOptions;
        }
    }

    /// <summary>
    /// Records, while the stream state is still alive, what this attempt is about to run.
    /// </summary>
    private void DescribeAttempt(TranscodeAttempt attempt, StreamState state, string commandLineArguments)
    {
        // Read off the command that will run, not off the configuration: a backend can be
        // configured and still not be used (stream copy, audio only, an input it cannot take),
        // and the options in force now may no longer be the ones this command was built with -
        // another session's failure can have withheld the backend in between. The flags are the
        // ones EncodingHelper emits for every backend that opens a device; a backend whose
        // command carries neither (V4L2 M2M) is treated as software and is never fallen back from.
        var configuredBackend = _serverConfigurationManager.GetEncodingOptions().HardwareAccelerationType;
        attempt.UsesHardwarePipeline = configuredBackend != HardwareAccelerationType.none
            && (commandLineArguments.Contains("-init_hw_device ", StringComparison.Ordinal)
                || commandLineArguments.Contains("-hwaccel ", StringComparison.Ordinal));
        attempt.Backend = attempt.UsesHardwarePipeline ? configuredBackend : HardwareAccelerationType.none;

        if (!attempt.UsesHardwarePipeline || state.VideoRequest is null)
        {
            return;
        }

        try
        {
            var softwareEncoder = _encodingHelper.GetVideoEncoder(state, GetSoftwareOptions(_serverConfigurationManager.GetEncodingOptions()));
            attempt.SoftwareAlternativeAvailable = !string.IsNullOrEmpty(softwareEncoder)
                && !EncodingHelper.IsCopyCodec(softwareEncoder)
                && _mediaEncoder.SupportsEncoder(softwareEncoder);
        }
        catch (Exception ex)
        {
            // Not knowing is not having: no alternative is assumed.
            _logger.LogDebug(ex, "Could not determine the software encoder for a hardware attempt");
        }
    }

    /// <summary>
    /// Judges an ffmpeg process that ended with a non-zero exit code, and applies the result.
    /// </summary>
    /// <remarks>
    /// Nothing is restarted from here. A granted fallback changes what the NEXT command is built
    /// with; the client reloads the stream, which is also what gives the software attempt its own
    /// output files and its own initialisation segment.
    /// </remarks>
    /// <returns>The failure, or <see langword="null"/> when the server stopped the process itself.</returns>
    private TranscodeFailure? EvaluateFailure(TranscodingJob job, StreamState state, int exitCode)
    {
        var attempt = job.CurrentAttempt;
        if (attempt is null || attempt.StopRequested)
        {
            return null;
        }

        // The stderr reader is attached just after the process starts, and the pipe closes when
        // the process exits, so both waits are normally immediate. They are bounded all the
        // same, and a verdict on evidence that is not known to be complete is a refusal: a
        // device line that was read says nothing about an input line that was not read yet.
        var evidenceComplete = SpinWait.SpinUntil(() => attempt.DiagnosticsCompleted is not null, TimeSpan.FromSeconds(2));
        if (evidenceComplete)
        {
            try
            {
                evidenceComplete = attempt.DiagnosticsCompleted!.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                evidenceComplete = false;
            }
        }

        IReadOnlyCollection<FfmpegErrorCategory> categories = evidenceComplete
            ? attempt.Diagnostics?.GetDetectedErrorCategories() ?? []
            : [];
        var decision = TranscodeFallbackPlanner.Evaluate(new TranscodeFallbackRequest(
            categories,
            attempt.Backend,
            attempt.UsesHardwarePipeline,
            attempt.SoftwareAlternativeAvailable,
            attempt.StopRequested,
            attempt.Diagnostics?.UnsupportedCodecName));

        if (!decision.ShouldFallback)
        {
            _logger.LogInformation(
                "Transcode failure not eligible for software fallback: Backend={Backend} Categories={Categories} Reason={Reason}",
                attempt.Backend,
                categories,
                decision.Reason);

            return new TranscodeFailure(exitCode, categories, decision, TimeProvider.GetUtcNow().UtcDateTime);
        }

        WithholdHardware(decision, attempt.Backend, state.Request.MediaSourceId);

        _logger.LogWarning(
            "Hardware transcode failed; software will be used: Backend={Backend} Scope={Scope} Categories={Categories} Reason={Reason}. The configured backend is unchanged and is verified again at the next start.",
            attempt.Backend,
            decision.Scope,
            categories,
            decision.Reason);

        return new TranscodeFailure(exitCode, categories, decision, TimeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Removes what an attempt wrote before a failure that software will take over from: its last
    /// segment may be truncated, and its initialisation segment belongs to another encoder.
    /// </summary>
    /// <remarks>
    /// tesserafin#289. ALWAYS UNDER THE OUTPUT'S OWN LOCK, the one a request holds while it starts
    /// a transcode there, and only once the failure is published. So a request that finds a file
    /// gone because of this removal also finds the reason, in that order; and a start on this
    /// output is entirely before the removal or entirely after it.
    ///
    /// It is done once, by whoever holds the lock first: the failed attempt's own exit, or the
    /// start of a successor when the client let go of the play session before that exit got this
    /// far. Whichever comes second finds nothing to do - and an exit that comes second, or that
    /// finds another attempt registered on the output, leaves it alone: it is no longer its own.
    ///
    /// That last case is a seek that restarted the attempt between its verdict and its
    /// publication. What the failed attempt wrote then stays beside its successor's output, its
    /// last file excepted (the restart deletes that one). Accepted: the window is the few
    /// milliseconds of the verdict, and removing here would take the successor's files too.
    /// </remarks>
    /// <param name="path">The output.</param>
    /// <param name="failedJob">The failed attempt, or <see langword="null"/> for a start on the output.</param>
    private void RemoveFailedOutput(string path, TranscodingJob? failedJob)
    {
        lock (_failedOutputs)
        {
            if (!_failedOutputs.TryGetValue(path, out var pending)
                || (failedJob is not null && !ReferenceEquals(pending, failedJob)))
            {
                return;
            }

            _failedOutputs.Remove(path);
        }

        if (failedJob is not null
            && GetTranscodingJob(path, failedJob.Type) is { } current
            && !ReferenceEquals(current, failedJob))
        {
            return;
        }

        try
        {
            DeleteHlsPartialStreamFiles(path);
        }
        catch (Exception ex) when (ex is IOException or AggregateException)
        {
            _logger.LogWarning(ex, "Could not remove the failed attempt's output");
        }
    }

    /// <summary>
    /// Applies a granted fallback: withholds the backend from every new command, or hardware from
    /// one media source, for the rest of this process. Nothing is stored and nothing running is touched.
    /// </summary>
    /// <param name="decision">The planner's decision.</param>
    /// <param name="backend">The backend the failed attempt used.</param>
    /// <param name="mediaSourceId">The media source the failed attempt was for.</param>
    internal void WithholdHardware(TranscodeFallbackDecision decision, HardwareAccelerationType backend, string? mediaSourceId)
    {
        if (!decision.ShouldFallback)
        {
            return;
        }

        lock (_fallbackLock)
        {
            if (decision.Scope == TranscodeFallbackScope.Backend)
            {
                _unavailableBackends.Add(backend);
            }
            else if (decision.Scope == TranscodeFallbackScope.MediaSource && !string.IsNullOrEmpty(mediaSourceId))
            {
                // Bounded: forgetting a source costs one more failed hardware start, never a wrong answer.
                if (_softwareOnlyMediaSources.Count >= 4096)
                {
                    _softwareOnlyMediaSources.Clear();
                }

                _softwareOnlyMediaSources.Add(mediaSourceId);
            }
        }
    }

    private async Task AcquireResources(StreamState state, CancellationTokenSource cancellationTokenSource)
    {
        if (state.MediaSource.RequiresOpening && string.IsNullOrWhiteSpace(state.Request.LiveStreamId))
        {
            // OpenLiveStreamInternal, not OpenLiveStream: this path opens the live stream itself
            // because the request carried no liveStreamId, so StreamingHelpers never resolved a
            // provider for it. Dropping the provider here would silently fall back to ffmpeg
            // fetching the [Authorize]d LiveStreamFiles URL with no credential.
            var (liveStreamResponse, directStreamProvider) = await _mediaSourceManager.OpenLiveStreamInternal(
                    new LiveStreamRequest { OpenToken = state.MediaSource.OpenToken },
                    cancellationTokenSource.Token)
                .ConfigureAwait(false);
            state.DirectStreamProvider = directStreamProvider;
            var encodingOptions = _serverConfigurationManager.GetEncodingOptions();

            _encodingHelper.AttachMediaSourceInfo(state, encodingOptions, liveStreamResponse.MediaSource, state.RequestedUrl);

            if (state.VideoRequest is not null)
            {
                _encodingHelper.TryStreamCopy(state, encodingOptions);
            }
        }

        if (state.MediaSource.BufferMs.HasValue)
        {
            await Task.Delay(state.MediaSource.BufferMs.Value, cancellationTokenSource.Token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type)
    {
        lock (_activeTranscodingJobs)
        {
            var job = _activeTranscodingJobs
                .FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));

            if (job is null)
            {
                return null;
            }

            job.ActiveRequestCount++;
            if (string.IsNullOrWhiteSpace(job.PlaySessionId) || job.Type == TranscodingJobType.Progressive)
            {
                job.StopKillTimer();
            }

            return job;
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PlaySessionId))
        {
            // The report's session is the reporter's own (the playstate routes overwrite whatever
            // session id the body named), so only that user's jobs are kept alive or paused by it.
            var reporter = e.Session?.UserId ?? Guid.Empty;
            PingTranscodingJobs(e.PlaySessionId, e.IsPaused, job => !reporter.IsEmpty() && reporter.Equals(job.UserId));
        }
    }

    private void DeleteEncodedMediaCache()
    {
        var path = _serverConfigurationManager.GetTranscodePath();
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in _fileSystem.GetFilePaths(path, true))
        {
            try
            {
                _fileSystem.DeleteFile(file);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting encoded media cache file {Path}", path);
            }
        }
    }

    /// <summary>
    /// Transcoding lock.
    /// </summary>
    /// <param name="outputPath">The output path of the transcoded file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An <see cref="IDisposable"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken)
    {
        return _transcodingLocks.LockAsync(outputPath, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStart -= OnPlaybackProgress;
        _transcodingLocks.Dispose();
    }
}
