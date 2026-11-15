using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Common;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Model.IO;

namespace Tesserafin.MediaEncoding.Encoder;

/// <summary>
/// Validates file-producing attempts and cleans their owned outputs before the caller releases
/// its cache lock. Process termination and diagnostic draining belong to the runner.
/// </summary>
internal sealed class FfmpegFileExecutor(IFfmpegProcessRunner runner, IFileSystem fileSystem, ILogger logger)
{
    public async Task ExecuteAsync(
        FfmpegCommand command,
        IReadOnlyCollection<string> outputPaths,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? acceptedExitCodes = null)
    {
        try
        {
            logger.LogInformation("{File} {Arguments}", command.Executable, command.ArgumentLine);
            var result = await runner.RunFileAsync(command, timeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.TimedOut || result.ExitCode is not { } code || !(acceptedExitCodes?.Contains(code) ?? code == 0))
            {
                logger.LogError("FFmpeg extraction failed: timeout={TimedOut}, exit={ExitCode}, stderr={StandardError}", result.TimedOut, result.ExitCode, result.StandardError);
                throw new FfmpegException(result.TimedOut ? "FFmpeg extraction timed out." : $"FFmpeg extraction exited with code {result.ExitCode}.");
            }

            foreach (var path in outputPaths)
            {
                var file = fileSystem.GetFileInfo(path);
                if (!file.Exists || file.Length == 0)
                {
                    throw new FfmpegException($"FFmpeg extraction produced no content at {path}.");
                }
            }
        }
        catch
        {
            foreach (var path in outputPaths)
            {
                try
                {
                    fileSystem.DeleteFile(path);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error deleting failed extraction output {Path}", path);
                }
            }

            throw;
        }
    }
}
