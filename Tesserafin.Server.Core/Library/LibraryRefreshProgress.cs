using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Events;

namespace Tesserafin.Server.Core.Library;

/// <summary>Tracks active recursive scans and publishes their progress.</summary>
internal sealed class LibraryRefreshProgress(ILogger<LibraryRefreshProgress> logger) : ILibraryRefreshProgress
{
    private readonly ConcurrentDictionary<Guid, double> _activeRefreshes = new();

    /// <inheritdoc />
    public event EventHandler<GenericEventArgs<BaseItem>>? RefreshStarted;

    /// <inheritdoc />
    public event EventHandler<GenericEventArgs<BaseItem>>? RefreshCompleted;

    /// <inheritdoc />
    public event EventHandler<GenericEventArgs<Tuple<BaseItem, double>>>? RefreshProgress;

    /// <inheritdoc />
    public void OnRefreshStart(BaseItem item)
    {
        logger.LogDebug("OnRefreshStart {Item:N}", item.Id);
        _activeRefreshes[item.Id] = 0;
        try
        {
            RefreshStarted?.Invoke(this, new GenericEventArgs<BaseItem>(item));
        }
        catch (Exception ex)
        {
            // EventHandlers should never propagate exceptions, but we have little control over plugins...
            logger.LogError(ex, "Invoking {RefreshEvent} event handlers failed", nameof(RefreshStarted));
        }
    }

    /// <inheritdoc />
    public void OnRefreshComplete(BaseItem item)
    {
        logger.LogDebug("OnRefreshComplete {Item:N}", item.Id);
        _activeRefreshes.TryRemove(item.Id, out _);

        try
        {
            RefreshCompleted?.Invoke(this, new GenericEventArgs<BaseItem>(item));
        }
        catch (Exception ex)
        {
            // EventHandlers should never propagate exceptions, but we have little control over plugins...
            logger.LogError(ex, "Invoking {RefreshEvent} event handlers failed", nameof(RefreshCompleted));
        }
    }

    /// <inheritdoc />
    public double? GetRefreshProgress(Guid id)
    {
        if (_activeRefreshes.TryGetValue(id, out double value))
        {
            return value;
        }

        return null;
    }

    /// <inheritdoc />
    public void OnRefreshProgress(BaseItem item, double progress)
    {
        var id = item.Id;
        logger.LogDebug("OnRefreshProgress {Id:N} {Progress}", id, progress);

        if (!_activeRefreshes.TryGetValue(id, out var current)
            || progress <= current
            || !_activeRefreshes.TryUpdate(id, progress, current))
        {
            // Item isn't currently refreshing, or update was received out-of-order, so don't trigger event.
            return;
        }

        try
        {
            RefreshProgress?.Invoke(this, new GenericEventArgs<Tuple<BaseItem, double>>(new Tuple<BaseItem, double>(item, progress)));
        }
        catch (Exception ex)
        {
            // EventHandlers should never propagate exceptions, but we have little control over plugins...
            logger.LogError(ex, "Invoking {RefreshEvent} event handlers failed", nameof(RefreshProgress));
        }
    }
}
