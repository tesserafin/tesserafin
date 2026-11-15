using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Permit ownership is transferred to _operations; its joined shutdown disposes them exactly once after all attempts finish.")]
    private readonly AsyncNonKeyedLocker _permits;
    private readonly FfmpegOperationScope _operations;

    public FfmpegImageScheduler(IFfmpegProcessRunner runner, IFileSystem fileSystem, ILogger logger, int concurrency)
    {
        _runner = runner;
        _fileSystem = fileSystem;
        _logger = logger;
        _permits = new(concurrency);
        _operations = new FfmpegOperationScope(_permits.Dispose);
    }

    public Task<string> ExtractImageAsync(FfmpegCommand command, string output, TimeSpan timeout, CancellationToken cancellationToken)
        => _operations.RunAsync(token => ExecuteAsync(command, output, timeout, interval: false, ProcessPriorityClass.BelowNormal, token), cancellationToken);

    public Task<string> ExtractIntervalAsync(FfmpegCommand command, string outputDirectory, TimeSpan inactivityTimeout, ProcessPriorityClass? priority, CancellationToken cancellationToken)
        => _operations.RunAsync(token => ExecuteAsync(command, outputDirectory, inactivityTimeout, interval: true, priority ?? ProcessPriorityClass.BelowNormal, token), cancellationToken);

    private async Task<string> ExecuteAsync(FfmpegCommand command, string output, TimeSpan timeout, bool interval, ProcessPriorityClass priority, CancellationToken cancellationToken)
    {
        using (await _permits.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                var result = await _runner.RunImageAsync(command, timeout, interval ? HasProgress : null, priority, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
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

    public Task StopAsync() => _operations.StopAsync();

    public void Dispose() => _operations.Dispose();
}
