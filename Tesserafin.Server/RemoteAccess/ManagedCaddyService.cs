using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.RemoteAccess;

/// <summary>
/// Production-oriented skeleton of the managed Caddy service.
/// Dependencies are injected so the atomic sequence can be unit-tested.
/// </summary>
public sealed class ManagedCaddyService : IManagedCaddyService
{
    private readonly string _fragmentPath;
    private readonly string _backupPath;
    private readonly IProcessRunner _processRunner;
    private readonly IServerNetworkConfigApplier _networkConfig;
    private readonly Func<ApplyRequest, bool>? _publicationGate;

    public ManagedCaddyService(
        string fragmentPath,
        IProcessRunner processRunner,
        IServerNetworkConfigApplier networkConfig,
        Func<ApplyRequest, bool>? publicationGate = null)
    {
        _fragmentPath = fragmentPath ?? throw new ArgumentNullException(nameof(fragmentPath));
        _backupPath = fragmentPath + ".bak";
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _networkConfig = networkConfig ?? throw new ArgumentNullException(nameof(networkConfig));
        _publicationGate = publicationGate;
    }

    public async Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_publicationGate is not null && !_publicationGate(request))
        {
            return new ApplyResult(false, "Publication invariant not satisfied", null);
        }

        await BackupFragmentAsync(cancellationToken).ConfigureAwait(false);
        IConfigRestoreToken? restoreToken = null;

        try
        {
            var fragment = BuildFragment(request);
            await WriteFragmentAtomicAsync(fragment, cancellationToken).ConfigureAwait(false);

            var validate = await _processRunner
                .RunAsync("caddy", "validate --config " + _fragmentPath, cancellationToken)
                .ConfigureAwait(false);

            if (validate.ExitCode != 0)
            {
                throw new InvalidOperationException("Caddy validate failed: " + validate.Output);
            }

            restoreToken = await _networkConfig
                .ApplyAsync(request.KnownProxyAddress, request.UnixSocketPath, cancellationToken)
                .ConfigureAwait(false);

            var reload = await _processRunner
                .RunAsync("systemctl", "reload caddy", cancellationToken)
                .ConfigureAwait(false);

            if (reload.ExitCode != 0)
            {
                throw new InvalidOperationException("Caddy reload failed: " + reload.Output);
            }

            var state = new ManagedState(
                true,
                request.Hostname,
                request.UnixSocketPath,
                _fragmentPath,
                DateTimeOffset.UtcNow);

            return new ApplyResult(true, null, state);
        }
        catch (Exception ex)
        {
            await RollbackAsync(restoreToken, cancellationToken).ConfigureAwait(false);
            return new ApplyResult(false, ex.Message, null);
        }
    }

    public async Task<DisableResult> DisableAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(_fragmentPath))
            {
                File.Delete(_fragmentPath);
            }

            await _processRunner.RunAsync("systemctl", "reload caddy", cancellationToken)
                .ConfigureAwait(false);

            return new DisableResult(true, null);
        }
        catch (Exception ex)
        {
            return new DisableResult(false, ex.Message);
        }
    }

    public Task<ManagedState> GetStateAsync(CancellationToken cancellationToken)
    {
        var enabled = File.Exists(_fragmentPath);
        return Task.FromResult(new ManagedState(
            enabled,
            null,
            null,
            enabled ? _fragmentPath : null,
            null));
    }

    internal static string BuildFragment(ApplyRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Tesserafin-managed fragment — do not edit by hand");
        sb.AppendLine($"{request.Hostname} {{");
        sb.AppendLine("    log {");
        sb.AppendLine("        output file /var/log/caddy/tesserafin-access.log");
        sb.AppendLine("        format filter {");
        sb.AppendLine("            request>uri query {");
        sb.AppendLine("                replace \".*\" \"\"");
        sb.AppendLine("            }");
        sb.AppendLine("            request>headers>Authorization delete");
        sb.AppendLine("            request>headers>X-Emby-Token delete");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    # Rate-limit auth endpoints (D-6)");
        sb.AppendLine("    rate_limit {");
        sb.AppendLine("        zone auth {");
        sb.AppendLine("            match path /Users/Authenticate*");
        sb.AppendLine("            events 10");
        sb.AppendLine("            window 1m");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    reverse_proxy unix//{request.UnixSocketPath} {{");
        sb.AppendLine("        header_up X-Forwarded-For {remote_host}");
        sb.AppendLine("        header_up X-Forwarded-Proto {scheme}");
        sb.AppendLine("        header_up X-Forwarded-Host {host}");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private async Task BackupFragmentAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_fragmentPath))
        {
            await using var src = File.OpenRead(_fragmentPath);
            await using var dst = File.Create(_backupPath);
            await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteFragmentAtomicAsync(string content, CancellationToken cancellationToken)
    {
        var temp = _fragmentPath + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken).ConfigureAwait(false);
        File.Move(temp, _fragmentPath, overwrite: true);
    }

    private async Task RollbackAsync(IConfigRestoreToken? restoreToken, CancellationToken cancellationToken)
    {
        if (File.Exists(_backupPath))
        {
            File.Move(_backupPath, _fragmentPath, overwrite: true);
        }
        else if (File.Exists(_fragmentPath))
        {
            File.Delete(_fragmentPath);
        }

        if (restoreToken is not null)
        {
            await restoreToken.RestoreAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
