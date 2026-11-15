using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Encoder;

/// <inheritdoc cref="IFfmpegProcessRunner" />
public sealed class FfmpegProcessRunner : IFfmpegProcessRunner
{
    private readonly ILogger<FfmpegProcessRunner> _logger;

    /// <summary>Initializes a new instance of the <see cref="FfmpegProcessRunner"/> class.</summary>
    /// <param name="logger">Diagnostics for optional native process settings.</param>
    public FfmpegProcessRunner(ILogger<FfmpegProcessRunner> logger) => _logger = logger;

    /// <inheritdoc />
    public Process CreateTranscodeProcess(FfmpegCommand command)
        => CreateProcess(command, redirectOutput: false, redirectInput: true, raiseEvents: true);

    /// <inheritdoc />
    public void StartTranscode(Process process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
    }

    /// <inheritdoc />
    public Task<FfmpegProcessResult> RunProbeAsync(FfmpegCommand command, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null)
        => RunAsync(command, timeout, standardInput, outputCaptureLimit: null, cancellationToken);

    /// <inheritdoc />
    public Task<FfmpegProcessResult> RunFileAsync(FfmpegCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        => RunAsync(command, timeout, standardInput: string.Empty, outputCaptureLimit: 64 * 1024, cancellationToken);

    /// <inheritdoc />
    public Task<FfmpegProcessResult> RunImageAsync(FfmpegCommand command, TimeSpan timeout, Func<bool>? hasProgress, ProcessPriorityClass priority, CancellationToken cancellationToken)
        => RunAsync(command, timeout, standardInput: string.Empty, outputCaptureLimit: 64 * 1024, cancellationToken, hasProgress, priority);

    /// <inheritdoc />
    public Task<FfmpegProcessResult> RunMediaProbeAsync(FfmpegCommand command, TimeSpan timeout, Func<Stream>? openInput, CancellationToken cancellationToken)
        => RunAsync(command, timeout, standardInput: openInput is null ? string.Empty : null, outputCaptureLimit: null, cancellationToken, priority: ProcessPriorityClass.BelowNormal, openInput: openInput, errorCaptureLimit: 64 * 1024);

    private async Task<FfmpegProcessResult> RunAsync(FfmpegCommand command, TimeSpan timeout, string? standardInput, int? outputCaptureLimit, CancellationToken cancellationToken, Func<bool>? hasProgress = null, ProcessPriorityClass? priority = null, Func<Stream>? openInput = null, int? errorCaptureLimit = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var process = CreateProcess(command, redirectOutput: true, redirectInput: standardInput is not null || openInput is not null, raiseEvents: false);
        process.Start();

        // Both streams must be drained concurrently: reading one to completion before starting
        // the other can deadlock if the unread stream fills its OS pipe buffer first. They are
        // read with CancellationToken.None deliberately - killing the process below closes its
        // pipes, which is what ends these reads (with whatever was captured so far), rather than
        // having them throw and lose that partial output.
        var stdOutTask = ReadOutputAsync(process.StandardOutput, outputCaptureLimit);
        var stdErrTask = ReadOutputAsync(process.StandardError, errorCaptureLimit ?? outputCaptureLimit);

        bool timedOut = false;
        DirectStreamPump? pump = null;
        try
        {
            if (priority is { } requestedPriority)
            {
                try
                {
                    process.PriorityClass = requestedPriority;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unable to set child priority to {Priority} for {Executable}", requestedPriority, command.Executable);
                }
            }

            if (openInput is not null)
            {
                var source = openInput() ?? throw new IOException("The media probe input factory returned null.");
                try
                {
                    pump = DirectStreamPump.Start(source, process.StandardInput.BaseStream, _logger, linkedCts.Token);
                }
                catch
                {
                    await source.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            if (standardInput is not null)
            {
                using var writer = process.StandardInput;
                await writer.WriteAsync(standardInput.AsMemory(), linkedCts.Token).ConfigureAwait(false);
            }

            if (hasProgress is null)
            {
                var exit = process.WaitForExitAsync(linkedCts.Token);
                if (pump is not null)
                {
                    var first = await Task.WhenAny(exit, pump.Completion).ConfigureAwait(false);
                    if (ReferenceEquals(first, pump.Completion) && pump.Fault is not null)
                    {
                        // Wake the exit wait on producer failure, including a consumer ignoring EOF.
                        await linkedCts.CancelAsync().ConfigureAwait(false);
                    }
                }

                await exit.ConfigureAwait(false);
            }
            else
            {
                await WaitForObservedExitAsync(process, timeout, hasProgress, cancellationToken).ConfigureAwait(false);
            }

            if (pump is not null)
            {
                await pump.StopAsync().ConfigureAwait(false);
                if (pump.Fault is { } fault)
                {
                    throw new IOException("The media probe input failed.", fault);
                }
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdOutTask, stdErrTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (pump?.Fault is { } fault)
            {
                throw new IOException("The media probe input failed.", fault);
            }

            timedOut = true;
        }
        catch
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdOutTask, stdErrTask).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (pump is not null)
            {
                await pump.DisposeAsync().ConfigureAwait(false);
            }
        }

        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        return new FfmpegProcessResult(
            timedOut ? null : process.ExitCode,
            stdOut,
            stdErr,
            timedOut);
    }

    private static async Task WaitForObservedExitAsync(Process process, TimeSpan timeout, Func<bool> hasProgress, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(window.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Invoke the observer in the handler: exceptions thrown inside an exception filter
                // are swallowed by the runtime and would hide an output-directory I/O failure.
                if (!hasProgress())
                {
                    throw;
                }

                // A new nonempty image buys another window, never immunity to cancellation.
            }
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, int? captureLimit)
    {
        if (captureLimit is null)
        {
            return await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var tail = new char[captureLimit.Value];
        var buffer = new char[4096];
        var position = 0;
        var length = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) != 0)
        {
            var first = Math.Min(read, tail.Length - position);
            Array.Copy(buffer, 0, tail, position, first);
            Array.Copy(buffer, first, tail, 0, read - first);
            position = (position + read) % tail.Length;
            length = Math.Min(length + read, tail.Length);
        }

        return length < tail.Length
            ? new string(tail, 0, length)
            : string.Concat(new string(tail, position, tail.Length - position), new string(tail, 0, position));
    }

    private static Process CreateProcess(FfmpegCommand command, bool redirectOutput, bool redirectInput, bool raiseEvents)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrEmpty(command.Executable);
        ArgumentNullException.ThrowIfNull(command.ArgumentLine);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(command.Executable, command.ArgumentLine)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false,
                RedirectStandardOutput = redirectOutput,
                RedirectStandardError = true,
                RedirectStandardInput = redirectInput,
                WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? string.Empty : command.WorkingDirectory,
            },
            EnableRaisingEvents = raiseEvents
        };
        foreach (var (key, value) in command.EnvironmentVariables)
        {
            process.StartInfo.Environment[key] = value;
        }

        return process;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the HasExited check and Kill.
        }
    }
}
