using System;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Server.Core.Library;

namespace Tesserafin.Server.Implementations.Tests.Library.Scanning;

/// <summary>Public proxy hooks with a non-virtual implementation of the internal traversal port.</summary>
public class TraversalDouble : ILibraryScanTraversal
{
    public virtual Task RefreshTopFoldersAsync(bool removeRoot, CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task RefreshAllAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual void ClearIgnoreRuleCache()
    {
    }

    public virtual void UpdateInheritedValues()
    {
    }

    Task ILibraryScanTraversal.RefreshTopFoldersAsync(CancellationToken cancellationToken, bool removeRoot) => RefreshTopFoldersAsync(removeRoot, cancellationToken);

    Task ILibraryScanTraversal.RefreshAllAsync(IProgress<double> progress, CancellationToken cancellationToken) => RefreshAllAsync(progress, cancellationToken);

    void ILibraryScanTraversal.ClearIgnoreRuleCache() => ClearIgnoreRuleCache();

    void ILibraryScanTraversal.UpdateInheritedValues() => UpdateInheritedValues();
}
