using System;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;

namespace Tesserafin.Controller.Providers;

/// <summary>Selects and runs the registered metadata service for a single catalogue item.</summary>
public interface IItemMetadataDispatcher
{
    /// <summary>Reports a failure while refreshing an item's owned media.</summary>
    /// <param name="item">The item whose owned media failed to refresh.</param>
    /// <param name="exception">The refresh error.</param>
    void ReportOwnedItemRefreshFailure(BaseItem item, Exception exception);

    /// <summary>Refreshes the item with its registered metadata service.</summary>
    /// <param name="item">The item to refresh.</param>
    /// <param name="options">The metadata and image refresh options.</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>The kinds of changes made by the refresh.</returns>
    Task<ItemUpdateType> RefreshSingleItem(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken);
}
