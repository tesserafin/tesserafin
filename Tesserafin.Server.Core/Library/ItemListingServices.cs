using System;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production <see cref="IItemListingServices"/>: the managers are deferred because each of them lists
    /// folder children itself.
    /// </summary>
    internal sealed class ItemListingServices : IItemListingServices
    {
        private readonly Lazy<IChannelManager> _channels;
        private readonly Lazy<ICollectionManager> _collections;
        private readonly Lazy<IUserViewManager> _userViews;
        private readonly Lazy<ITVSeriesManager> _tvSeries;
        private readonly IItemListingReads _reads;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemListingServices"/> class.
        /// </summary>
        /// <param name="channels">The channel manager, deferred.</param>
        /// <param name="collections">The collection manager, deferred.</param>
        /// <param name="userViews">The user view manager, deferred.</param>
        /// <param name="tvSeries">The TV series manager, deferred.</param>
        /// <param name="userData">The user data manager.</param>
        /// <param name="reads">The catalogue reads used by listings.</param>
        /// <param name="queries">The catalogue queries for listings.</param>
        public ItemListingServices(
            Lazy<IChannelManager> channels,
            Lazy<ICollectionManager> collections,
            Lazy<IUserViewManager> userViews,
            Lazy<ITVSeriesManager> tvSeries,
            IUserDataManager userData,
            IItemListingReads reads,
            IItemCatalogueQuery queries)
        {
            _channels = channels;
            _collections = collections;
            _userViews = userViews;
            _tvSeries = tvSeries;
            UserData = userData;
            _reads = reads;
            Queries = queries;
        }

        /// <inheritdoc />
        public IChannelManager Channels => _channels.Value;

        /// <inheritdoc />
        public ICollectionManager Collections => _collections.Value;

        /// <inheritdoc />
        public IUserViewManager UserViews => _userViews.Value;

        /// <inheritdoc />
        public ITVSeriesManager TVSeries => _tvSeries.Value;

        /// <inheritdoc />
        public IUserDataManager UserData { get; }

        /// <inheritdoc />
        public IItemSortService Sort => _reads.Sort;

        /// <inheritdoc />
        public IItemLookupService Lookup => _reads.Lookup;

        /// <inheritdoc />
        public IItemCountService Counts => _reads.Counts;

        /// <inheritdoc />
        public IUserRootFolderProvider UserRoot => _reads.UserRoot;

        /// <inheritdoc />
        public ILibraryRootFolderProvider LibraryRoot => _reads.LibraryRoot;

        /// <inheritdoc />
        public IItemCatalogueQuery Queries { get; }
    }
}
