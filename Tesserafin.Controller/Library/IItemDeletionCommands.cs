using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Owns media deletion, alternate promotion, persistence, caches and removal notifications.</summary>
public interface IItemDeletionCommands
{
    /// <summary>Deletes an item and its owned descendants using the current owner/parent.</summary>
    /// <param name="item">The item.</param>
    /// <param name="options">Deletion policy; never mutated.</param>
    /// <param name="cancellationToken">Cancellation before destructive work starts.</param>
    /// <returns>Deletion and reconciliation.</returns>
    Task DeleteAsync(BaseItem item, DeleteOptions options, CancellationToken cancellationToken);

    /// <summary>Deletes an item with its already captured parent.</summary>
    /// <param name="item">The item.</param>
    /// <param name="options">Deletion policy; never mutated.</param>
    /// <param name="parent">The captured parent.</param>
    /// <param name="cancellationToken">Cancellation before destructive work starts.</param>
    /// <returns>Deletion and reconciliation.</returns>
    Task DeleteAsync(BaseItem item, DeleteOptions options, BaseItem? parent, CancellationToken cancellationToken);

    /// <summary>Removes unreferenced leaf entities without recursive promotion or notifications.</summary>
    /// <param name="items">The unreferenced leaves.</param>
    /// <param name="deleteSourceFiles">Whether to delete their files.</param>
    /// <param name="cancellationToken">Cancellation before destructive work starts.</param>
    /// <returns>Deletion and cache invalidation.</returns>
    Task DeleteUnreferencedAsync(IReadOnlyCollection<BaseItem> items, bool deleteSourceFiles, CancellationToken cancellationToken);

    /// <summary>Removes an obsolete generic video before resolving its correctly typed alternate.</summary>
    /// <param name="path">The alternate's source path.</param>
    /// <param name="expectedType">The correct item type.</param>
    /// <param name="cancellationToken">Cancellation before destructive work starts.</param>
    /// <returns>Obsolete-entry cleanup.</returns>
    Task DeleteObsoleteAlternateAsync(string path, Type expectedType, CancellationToken cancellationToken);
}
