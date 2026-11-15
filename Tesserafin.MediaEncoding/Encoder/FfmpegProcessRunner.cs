using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.MediaEncoding;

namespace Tesserafin.MediaEncoding.Encoder;

/// <inheritdoc cref="IFfmpegProcessRunner" />
public sealed class FfmpegProcessRunner : IFfmpegProcessRunner
{
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
    public async Task<FfmpegProcessResult> RunProbeAsync(FfmpegCommand command, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = CreateProcess(command, redirectOutput: true, redirectInput: standardInput is not null, raiseEvents: false);
        process.Start();

        // Both streams must be drained concurrently: reading one to completion before starting
        // the other can deadlock if the unread stream fills its OS pipe buffer first. They are
        // read with CancellationToken.None deliberately - killing the process below closes its
        // pipes, which is what ends these reads (with whatever was captured so far), rather than
        // having them throw and lose that partial output.
        var stdOutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stdErrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        bool timedOut = false;
        try
        {
            if (standardInput is not null)
            {
                using var writer = process.StandardInput;
                await writer.WriteAsync(standardInput.AsMemory(), linkedCts.Token).ConfigureAwait(false);
            }

            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
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

            timedOut = true;
        }
        catch
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdOutTask, stdErrTask).ConfigureAwait(false);
            throw;
        }

        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        return new FfmpegProcessResult(
            timedOut ? null : process.ExitCode,
            stdOut,
            stdErr,
            timedOut);
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
