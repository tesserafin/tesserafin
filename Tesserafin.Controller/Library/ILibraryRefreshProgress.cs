using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Events;

namespace Tesserafin.Controller.Library;

/// <summary>
/// The state and notifications of active recursive library refreshes.
/// </summary>
public interface ILibraryRefreshProgress
{
    /// <summary>Raised after an item starts refreshing.</summary>
    event EventHandler<GenericEventArgs<BaseItem>>? RefreshStarted;

    /// <summary>Raised after an item finishes refreshing.</summary>
    event EventHandler<GenericEventArgs<BaseItem>>? RefreshCompleted;

    /// <summary>Raised when an active item's progress advances.</summary>
    event EventHandler<GenericEventArgs<Tuple<BaseItem, double>>>? RefreshProgress;

    /// <summary>Marks an item as active at zero percent.</summary>
    /// <param name="item">The item being refreshed.</param>
    void OnRefreshStart(BaseItem item);

    /// <summary>Reports a progress update for an active item.</summary>
    /// <param name="item">The item being refreshed.</param>
    /// <param name="progress">The percentage complete.</param>
    void OnRefreshProgress(BaseItem item, double progress);

    /// <summary>Removes an item from the active set.</summary>
    /// <param name="item">The item whose refresh ended.</param>
    void OnRefreshComplete(BaseItem item);

    /// <summary>Gets the current progress of an active item.</summary>
    /// <param name="id">The item identifier.</param>
    /// <returns>The percentage complete, or null when the item is inactive.</returns>
    double? GetRefreshProgress(Guid id);
}
