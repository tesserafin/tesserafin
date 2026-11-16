using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Library;

namespace Tesserafin.Server.Core.IO;

/// <summary>Shared write suppression without library, provider or monitor callbacks.</summary>
internal sealed class LibraryWriteTracker(TimeProvider time) : ILibraryWriteTracker
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, PendingWrites> _paths = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Paths
    {
        get
        {
            lock (_sync)
            {
                return _paths.Keys.ToArray();
            }
        }
    }

    public void Begin(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_sync)
        {
            if (_paths.TryGetValue(path, out var pending))
            {
                pending.Count++;
            }
            else
            {
                _paths.Add(path, new PendingWrites());
            }
        }
    }

    public Task CompleteAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        PendingWrites? pending;
        lock (_sync)
        {
            _paths.TryGetValue(path, out pending);
        }

        return ReleaseAsync(path, pending);
    }

    private async Task ReleaseAsync(string path, PendingWrites? pending)
    {
        // Network filesystems can report events well after the write has finished.
        await Task.Delay(TimeSpan.FromSeconds(45), time, CancellationToken.None).ConfigureAwait(false);
        lock (_sync)
        {
            if (pending is not null
                && _paths.TryGetValue(path, out var current)
                && ReferenceEquals(current, pending)
                && --pending.Count == 0)
            {
                _paths.Remove(path);
            }
        }
    }

    private sealed class PendingWrites
    {
        public int Count { get; set; } = 1;
    }
}
