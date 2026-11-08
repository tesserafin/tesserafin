using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.RemoteAccess;

/// <summary>
/// Production IProcessRunner that starts a process and captures combined output.
/// </summary>
/// <remarks>
/// Used for `caddy validate` and `systemctl reload`. Timeouts and cancellation
/// are honoured; the process is killed on cancellation or timeout.
/// </remarks>
public sealed class SystemProcessRunner : IProcessRunner
{
    private readonly TimeSpan _timeout;

    public SystemProcessRunner(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.AppendLine(e.Data);
        };

        if (!process.Start())
        {
            return (-1, "Failed to start process");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, output.Length == 0 ? "Process cancelled or timed out" : output.ToString());
        }

        return (process.ExitCode, output.ToString());
    }
}
