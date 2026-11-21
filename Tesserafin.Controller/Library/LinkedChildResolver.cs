using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Library;

/// <summary>
/// Resolves saved links to catalogue items in the order they appear in a folder.
/// </summary>
public sealed class LinkedChildResolver
{
    private readonly Func<InternalItemsQuery, IReadOnlyList<BaseItem>>? _query;
    private readonly Func<Guid, BaseItem?> _lookup;
    private readonly Func<string, BaseItem?> _findPath;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkedChildResolver"/> class.
    /// </summary>
    /// <param name="repository">The repository for batched item reads.</param>
    /// <param name="lookup">The item lookup service.</param>
    /// <param name="paths">The stored-path lookup service.</param>
    /// <param name="logger">The logger.</param>
    public LinkedChildResolver(
        IItemRepository repository,
        IItemLookupService lookup,
        IItemPathLookupService paths,
        ILogger<LinkedChildResolver> logger)
        : this(repository.GetItemList, lookup.GetItemById, path => paths.FindByPath(path, null), logger)
    {
    }

    internal LinkedChildResolver(
        Func<InternalItemsQuery, IReadOnlyList<BaseItem>>? query,
        Func<Guid, BaseItem?> lookup,
        Func<string, BaseItem?> findPath,
        ILogger logger)
    {
        _query = query;
        _lookup = lookup;
        _findPath = findPath;
        _logger = logger;
    }

    /// <summary>
    /// Gets the resolved links and their targets, keeping repeated entries.
    /// </summary>
    /// <param name="folder">The folder that owns the links.</param>
    /// <returns>The resolved entries in link order.</returns>
    public IReadOnlyList<(LinkedChild Link, BaseItem Item)> Resolve(Folder folder)
    {
        var links = folder.LinkedChildren;
        if (links.Length == 0)
        {
            return [];
        }

        var byId = new Dictionary<Guid, BaseItem>();
        if (_query is not null)
        {
            var ids = links.Select(link => link.ItemId.GetValueOrDefault())
                .Where(id => !id.Equals(Guid.Empty))
                .Distinct()
                .ToArray();
            if (ids.Length > 0)
            {
                foreach (var item in _query(new InternalItemsQuery { ItemIds = ids }))
                {
                    byId[item.Id] = item;
                }
            }
        }

        var result = new List<(LinkedChild Link, BaseItem Item)>(links.Length);
        foreach (var link in links)
        {
            var item = byId.TryGetValue(link.ItemId.GetValueOrDefault(), out var known) ? known : ResolveSingle(folder, link);

            if (item is not null)
            {
                result.Add((link, item));
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the resolved linked items in link order.
    /// </summary>
    /// <param name="folder">The folder that owns the links.</param>
    /// <returns>The linked items.</returns>
    public IReadOnlyList<BaseItem> GetItems(Folder folder)
        => Resolve(folder).Select(entry => entry.Item).ToArray();

    /// <summary>
    /// Checks whether the folder links to the given item.
    /// </summary>
    /// <param name="folder">The folder that owns the links.</param>
    /// <param name="itemId">The target item ID.</param>
    /// <returns>Whether a link targets the item.</returns>
    public bool Contains(Folder folder, Guid itemId)
    {
        foreach (var link in folder.LinkedChildren)
        {
            if (link.ItemId.HasValue ? link.ItemId.Value.Equals(itemId) : ResolveSingle(folder, link)?.Id.Equals(itemId) == true)
            {
                return true;
            }
        }

        return false;
    }

    internal BaseItem? ResolveSingle(Folder folder, LinkedChild link)
    {
        if (link.ItemId.HasValue)
        {
            var id = link.ItemId.Value;
            if (id.Equals(Guid.Empty))
            {
                return null;
            }

            var item = _lookup(id);
            if (item is not null)
            {
                return item;
            }

            _logger.LogWarning("Unable to find linked item by ItemId {ItemId}", id);
        }

        var target = FindSavedTarget(folder, link);
        link.ItemId = target?.Id ?? Guid.Empty;
        return target;
    }

#pragma warning disable CS0618 // Saved links may still contain a path or legacy ID.
    private BaseItem? FindSavedTarget(Folder folder, LinkedChild link)
    {
        if (!string.IsNullOrEmpty(link.Path))
        {
            var path = FilePath.MakeAbsolute(folder.ContainingFolderPath, link.Path);
            var item = _findPath(path);
            if (item is null)
            {
                _logger.LogWarning("Unable to find linked item at path {Path}", link.Path);
            }

            return item;
        }

        if (!string.IsNullOrEmpty(link.LibraryItemId))
        {
            var item = _lookup(new Guid(link.LibraryItemId));
            if (item is null)
            {
                _logger.LogWarning("Unable to find linked item by LibraryItemId {LibraryItemId}", link.LibraryItemId);
            }

            return item;
        }

        return null;
    }
#pragma warning restore CS0618
}
