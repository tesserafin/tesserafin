using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>
/// Command-side publication after persistence and cache reconciliation. Each subscriber is
/// invoked synchronously and independently; a failing observer cannot fail a completed write
/// or prevent another observer from receiving the notification. Producers own source filtering.
/// </summary>
public interface IItemChangePublisher
{
    /// <summary>Reports a completed creation.</summary>
    /// <param name="item">Created item.</param>
    /// <param name="parent">Current parent, if any.</param>
    void Added(BaseItem item, BaseItem? parent);

    /// <summary>Reports a completed update.</summary>
    /// <param name="item">Updated item.</param>
    /// <param name="parent">Current parent, if any.</param>
    /// <param name="reason">The kind of update.</param>
    void Updated(BaseItem item, BaseItem? parent, ItemUpdateType reason);

    /// <summary>Reports a completed removal.</summary>
    /// <param name="item">Removed item.</param>
    /// <param name="parent">Previous parent, if any.</param>
    void Removed(BaseItem item, BaseItem? parent);
}
