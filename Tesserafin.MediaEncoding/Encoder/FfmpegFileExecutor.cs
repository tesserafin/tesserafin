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
/// Executes into private staging files, validates/postprocesses the complete attempt, then
/// publishes artifacts under the caller's cache locks. Failed staging is never a cache hit.
/// </summary>
internal sealed class FfmpegFileExecutor(IFfmpegProcessRunner runner, IFileSystem fileSystem, ILogger logger)
{
    public async Task ExecuteAsync(
        Func<IReadOnlyList<string>, FfmpegCommand> createCommand,
        IReadOnlyList<string> outputPaths,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? acceptedExitCodes = null,
        Func<AtomicCachePublication, CancellationToken, Task>? prepare = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var publication = new AtomicCachePublication(outputPaths, fileSystem, logger);
        var command = createCommand(publication.StagedPaths);
        logger.LogInformation("{File} {Arguments}", command.Executable, command.ArgumentLine);
        var result = await runner.RunFileAsync(command, timeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.TimedOut || result.ExitCode is not { } code || !(acceptedExitCodes?.Contains(code) ?? code == 0))
        {
            logger.LogError("FFmpeg extraction failed: timeout={TimedOut}, exit={ExitCode}, stderr={StandardError}", result.TimedOut, result.ExitCode, result.StandardError);
            throw new FfmpegException(result.TimedOut ? "FFmpeg extraction timed out." : $"FFmpeg extraction exited with code {result.ExitCode}.");
        }

        publication.Validate();
        if (prepare is not null)
        {
            await prepare(publication, cancellationToken).ConfigureAwait(false);
        }

        publication.Publish(cancellationToken);
    }
}
