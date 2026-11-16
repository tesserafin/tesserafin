using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Extensions;

namespace Tesserafin.Server.Core.Library;

/// <summary>Prepares the typed local-version graph without saving or publishing anything.</summary>
internal sealed class ItemVersionGraphPlanner(IItemResolutionService resolution, ILibraryItemOptions options, IItemLookupService lookup)
{
    public IEnumerable<Guid> GetLocalVersionIds(Video video)
        => video.LocalAlternateVersions.Where(path => !string.IsNullOrEmpty(path)).Select(path => resolution.GetNewItemId(path, video.GetType()));

    public List<BaseItem> Prepare(IReadOnlyList<BaseItem> items, BaseItem? parent, CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        var result = new List<BaseItem>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item);
            if (item.Id.IsEmpty() || !ids.Add(item.Id))
            {
                throw new ArgumentException("Write items must have distinct, nonempty identities.", nameof(items));
            }

            result.Add(item);
        }

        foreach (var item in items)
        {
            if (item is not Video video || video.LocalAlternateVersions.Length == 0)
            {
                continue;
            }

            var collectionType = options.GetTopFolderContentType(parent);
            foreach (var path in video.LocalAlternateVersions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var id = resolution.GetNewItemId(path, video.GetType());
                if (ids.Contains(id) || lookup.GetItemById(id) is not null)
                {
                    continue;
                }

                var alternate = resolution.ResolveAlternateVersion(path, video.GetType(), parent as Folder, collectionType);
                if (alternate is not null)
                {
                    if (!alternate.Id.Equals(id) || alternate.GetType() != video.GetType())
                    {
                        throw new InvalidOperationException("Resolved alternate identity/type does not match the primary.");
                    }

                    alternate.OwnerId = video.Id;
                    alternate.SetPrimaryVersionId(video.Id);
                    ids.Add(id);
                    result.Add(alternate);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
