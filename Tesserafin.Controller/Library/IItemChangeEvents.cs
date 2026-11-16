using System;

namespace Tesserafin.Controller.Library;

/// <summary>
/// Synchronous, process-local notifications after item persistence and cache publication.
/// Subscribe during consumer startup and unsubscribe during shutdown. This is not a durable log.
/// </summary>
public interface IItemChangeEvents
{
    /// <summary>Raised after a library item is created.</summary>
    event EventHandler<ItemChangeEventArgs>? ItemAdded;

    /// <summary>Raised after an item or its images are updated.</summary>
    event EventHandler<ItemChangeEventArgs>? ItemUpdated;

    /// <summary>Raised after an item is removed.</summary>
    event EventHandler<ItemChangeEventArgs>? ItemRemoved;
}
