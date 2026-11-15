using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Controller.Library;

/// <summary>Owns library scan scheduling, execution and status independently of item management.</summary>
public interface ILibraryScanCoordinator
{
    /// <summary>Gets a value indicating whether a full scan owns the traversal slot, including its cleanup.</summary>
    bool IsScanRunning { get; }

    /// <summary>Queues a visible scheduled scan, replacing a currently running scheduled attempt.</summary>
    void RequestScan();

    /// <summary>Runs one full scan. Concurrent traversals are serialized; cancellation while queued does no work.</summary>
    /// <param name="progress">Progress from zero to one hundred.</param>
    /// <param name="cancellationToken">Cancellation for admission and traversal.</param>
    /// <returns>The scan, including monitor restoration and cleanup.</returns>
    Task ScanAsync(IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>Refreshes top-level folders under the same traversal lock as a full scan.</summary>
    /// <param name="cancellationToken">Cancellation for admission and traversal.</param>
    /// <param name="removeRoot">Whether missing root libraries may be removed.</param>
    /// <returns>The top-level refresh.</returns>
    Task RefreshTopFoldersAsync(CancellationToken cancellationToken, bool removeRoot = false);

    /// <summary>Applies a library structure edit while scans and filesystem monitoring are suspended.</summary>
    /// <param name="change">The filesystem edit, invoked only after exclusive admission.</param>
    /// <param name="refreshLibrary">Whether to queue a full scan after reconciliation.</param>
    /// <param name="removeRoot">Whether reconciliation may remove missing root libraries.</param>
    /// <param name="cancellationToken">Cancellation for admission and reconciliation.</param>
    /// <returns>The edit, root reconciliation and monitor restoration.</returns>
    Task ApplyLibraryChangeAsync(Action change, bool refreshLibrary, bool removeRoot, CancellationToken cancellationToken);

    /// <summary>Clears directory lookups so changed ignore rules are observed by the next refresh.</summary>
    void ClearIgnoreRuleCache();

    /// <summary>Registers post-scan tasks after application composition, avoiding eager dependency cycles.</summary>
    /// <param name="tasks">Tasks in application export order.</param>
    void SetPostScanTasks(IEnumerable<ILibraryPostScanTask> tasks);
}
