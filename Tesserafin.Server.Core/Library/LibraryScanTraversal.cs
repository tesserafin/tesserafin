using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

internal sealed class LibraryScanTraversal(
    LibraryScanContext context,
    ILibraryRootFolderProvider roots,
    IUserRootFolderProvider userRoots,
    IFileSystem fileSystem,
    IItemPersistenceService persistence,
    IItemCacheStore cache,
    DotIgnoreIgnoreRule ignore) : ILibraryScanTraversal
{
    public async Task RefreshTopFoldersAsync(CancellationToken cancellationToken, bool removeRoot)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = roots.GetRootFolder();
        root.Children = null;
        await root.RefreshMetadata(context.Deletions, context.Writes, cancellationToken).ConfigureAwait(false);
        await root.ValidateChildren(context, new Progress<double>(), new MetadataRefreshOptions(new DirectoryService(fileSystem)), recursive: false, allowRemoveRoot: removeRoot, cancellationToken: cancellationToken).ConfigureAwait(false);

        var userRoot = userRoots.GetUserRootFolder();
        userRoot.Children = null;
        await userRoot.RefreshMetadata(context.Deletions, context.Writes, cancellationToken).ConfigureAwait(false);
        await userRoot.ValidateChildren(context, new Progress<double>(), new MetadataRefreshOptions(new DirectoryService(fileSystem)), recursive: false, allowRemoveRoot: removeRoot, cancellationToken: cancellationToken).ConfigureAwait(false);

        var deleted = new List<Guid>();
        foreach (var child in userRoot.Children!.OfType<Folder>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child is CollectionFolder && !Directory.Exists(child.Path))
            {
                deleted.Add(child.Id);
            }
            else
            {
                await child.RefreshMetadata(context.Deletions, context.Writes, cancellationToken).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (deleted.Count > 0)
        {
            persistence.DeleteItem(deleted.ToArray());
            cache.RemoveRange(deleted);
        }
    }

    public Task RefreshAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => roots.GetRootFolder().ValidateChildren(context, progress, new MetadataRefreshOptions(new DirectoryService(fileSystem)), recursive: true, cancellationToken: cancellationToken);

    public void ClearIgnoreRuleCache() => ignore.ClearDirectoryCache();

    public void UpdateInheritedValues() => persistence.UpdateInheritedValues();
}
