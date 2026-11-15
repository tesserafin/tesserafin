using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Server.Core.Library;

/// <summary>The filesystem/metadata work performed under the scan coordinator's traversal slot.</summary>
internal interface ILibraryScanTraversal
{
    Task RefreshTopFoldersAsync(CancellationToken cancellationToken, bool removeRoot);

    Task RefreshAllAsync(IProgress<double> progress, CancellationToken cancellationToken);

    void ClearIgnoreRuleCache();

    void UpdateInheritedValues();
}
