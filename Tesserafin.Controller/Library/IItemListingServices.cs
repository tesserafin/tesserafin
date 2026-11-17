using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// The services a folder's own listing logic needs (channel items, box-set collapsing, user views,
    /// next up and the sort), handed to the entity on its query instead of read from static properties.
    /// </summary>
    /// <remarks>
    /// The managers reach item listing themselves, so implementations resolve them on first use; callers
    /// that only list library children and must stay off those managers (the user-view catalog, the visible
    /// content probe) never take this service.
    /// </remarks>
    public interface IItemListingServices
    {
        /// <summary>Gets the channel manager.</summary>
        IChannelManager Channels { get; }

        /// <summary>Gets the collection manager.</summary>
        ICollectionManager Collections { get; }

        /// <summary>Gets the user view manager.</summary>
        IUserViewManager UserViews { get; }

        /// <summary>Gets the TV series manager.</summary>
        ITVSeriesManager TVSeries { get; }

        /// <summary>Gets the item sort service.</summary>
        IItemSortService Sort { get; }
    }
}
