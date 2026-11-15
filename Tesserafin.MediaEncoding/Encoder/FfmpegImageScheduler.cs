using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Microsoft.Extensions.Logging;
using Tesserafin.Common;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.IO;

namespace Tesserafin.MediaEncoding.Encoder;

/// <summary>
/// Owns image permits and shutdown. Every queued or running attempt is registered before it
/// can enter the runner; shutdown cancels them and waits for cleanup before disposing permits.
/// </summary>
internal sealed class FfmpegImageScheduler : IDisposable
{
    private readonly IFfmpegProcessRunner _runner;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly AsyncNonKeyedLocker _permits;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _attempts = [];
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;

    public FfmpegImageScheduler(IFfmpegProcessRunner runner, IFileSystem fileSystem, ILogger logger, int concurrency)
    {
        _runner = runner;
        _fileSystem = fileSystem;
        _logger = logger;
        _permits = new(concurrency);
    }

    public Task<string> ExtractImageAsync(FfmpegCommand command, string output, TimeSpan timeout, CancellationToken cancellationToken)
        => Register(command, output, timeout, interval: false, ProcessPriorityClass.BelowNormal, cancellationToken);

    public Task<string> ExtractIntervalAsync(FfmpegCommand command, string outputDirectory, TimeSpan inactivityTimeout, ProcessPriorityClass? priority, CancellationToken cancellationToken)
        => Register(command, outputDirectory, inactivityTimeout, interval: true, priority ?? ProcessPriorityClass.BelowNormal, cancellationToken);

    private Task<string> Register(FfmpegCommand command, string output, TimeSpan timeout, bool interval, ProcessPriorityClass priority, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            _attempts.Add(completed.Task);
        }

        return ExecuteAsync(command, output, timeout, interval, priority, cancellation, completed);
    }

    private async Task<string> ExecuteAsync(FfmpegCommand command, string output, TimeSpan timeout, bool interval, ProcessPriorityClass priority, CancellationTokenSource cancellation, TaskCompletionSource completed)
    {
        try
        {
            using (await _permits.LockAsync(cancellation.Token).ConfigureAwait(false))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(interval ? output : Path.GetDirectoryName(output)!);
                var previousCount = 0;
                bool HasProgress()
                {
                    var count = Images(output).Count();
                    var advanced = count > previousCount;
                    previousCount = count;
                    return advanced;
                }

                _logger.LogDebug("Image extraction: {Executable} {Arguments}", command.Executable, command.ArgumentLine);
                try
                {
                    var result = await _runner.RunImageAsync(command, timeout, interval ? HasProgress : null, priority, cancellation.Token).ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (result.TimedOut || result.ExitCode != 0)
                    {
                        _logger.LogError("Image extraction failed: timeout={TimedOut}, exit={ExitCode}, stderr={StandardError}", result.TimedOut, result.ExitCode, result.StandardError);
                        throw new FfmpegException(result.TimedOut ? "FFmpeg image extraction timed out." : $"FFmpeg image extraction exited with code {result.ExitCode}.");
                    }

                    if (interval ? !Images(output).Any() : !Nonempty(output))
                    {
                        throw new FfmpegException($"FFmpeg image extraction produced no images at {output}.");
                    }

                    return output;
                }
                catch
                {
                    // The runner has already killed/waited/drained. Keep the permit until cleanup
                    // finishes: the next image must never overlap a child that is still stopping.
                    Cleanup(output, interval);
                    throw;
                }
            }
        }
        finally
        {
            cancellation.Dispose();
            lock (_gate)
            {
                _attempts.Remove(completed.Task);
                completed.SetResult();
            }
        }
    }

    private IEnumerable<string> Images(string directory)
        => _fileSystem.GetFilePaths(directory, false).Where(path => string.Equals(Path.GetExtension(path), ".jpg", StringComparison.OrdinalIgnoreCase) && Nonempty(path));

    private bool Nonempty(string path)
    {
        var file = _fileSystem.GetFileInfo(path);
        return file.Exists && file.Length > 0;
    }

    private void Cleanup(string output, bool directory)
    {
        try
        {
            if (directory)
            {
                Directory.Delete(output, recursive: true);
            }
            else
            {
                _fileSystem.DeleteFile(output);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to delete failed image extraction output {Output}", output);
        }
    }

    public void Dispose()
    {
        Task[]? attempts = null;
        lock (_gate)
        {
            if (!_stopping)
            {
                _stopping = true;
                attempts = _attempts.ToArray();
            }
        }

        if (attempts is null)
        {
            _disposed.Task.GetAwaiter().GetResult();
            return;
        }

        try
        {
            try
            {
                _shutdown.Cancel();
            }
            finally
            {
                Task.WhenAll(attempts).GetAwaiter().GetResult();
                _permits.Dispose();
                _shutdown.Dispose();
            }
        }
        finally
        {
            _disposed.SetResult();
        }
    }
}
