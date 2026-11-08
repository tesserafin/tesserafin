using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.RemoteAccess;

/// <summary>
/// Persists the previous network configuration so a restore can survive a process restart.
/// </summary>
/// <remarks>
/// Skeleton. The real IServerConfigurationManager will own the authoritative values;
/// this store only keeps a JSON snapshot next to the Caddy fragment so the rollback
/// path remains possible after a crash.
/// </remarks>
public sealed class FileRestoreTokenStore
{
    private readonly string _path;

    public FileRestoreTokenStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public async Task SaveAsync(NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(snapshot);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
    }

    public async Task<NetworkSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<NetworkSnapshot>(json);
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

public sealed record NetworkSnapshot(string[] KnownProxies, string? BindAddress);
