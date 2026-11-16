using System;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;

namespace Tesserafin.Server.Core.Library;

/// <summary>Shared notification ownership, independent of item writers and their construction.</summary>
internal sealed class ItemChangeEvents(ILogger<ItemChangeEvents> logger) : IItemChangeEvents, IItemChangePublisher
{
    public event EventHandler<ItemChangeEventArgs>? ItemAdded;

    public event EventHandler<ItemChangeEventArgs>? ItemUpdated;

    public event EventHandler<ItemChangeEventArgs>? ItemRemoved;

    public void Added(BaseItem item, BaseItem? parent)
        => Publish(ItemAdded, item, parent, default, nameof(ItemAdded));

    public void Updated(BaseItem item, BaseItem? parent, ItemUpdateType reason)
        => Publish(ItemUpdated, item, parent, reason, nameof(ItemUpdated));

    public void Removed(BaseItem item, BaseItem? parent)
        => Publish(ItemRemoved, item, parent, default, nameof(ItemRemoved));

    private void Publish(EventHandler<ItemChangeEventArgs>? subscribers, BaseItem item, BaseItem? parent, ItemUpdateType reason, string change)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (subscribers is null)
        {
            return;
        }

        var args = new ItemChangeEventArgs { Item = item, Parent = parent, UpdateReason = reason };
        foreach (EventHandler<ItemChangeEventArgs> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in {Change} subscriber for item {ItemId}", change, item.Id);
            }
        }
    }
}
