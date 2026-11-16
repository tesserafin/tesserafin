#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LibraryTaskScheduler;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Querying;

namespace Tesserafin.Controller.Entities
{
    public class UserView : Folder, IHasCollectionType
    {
        private static readonly CollectionType?[] _viewTypesEligibleForGrouping =
        {
            Tesserafin.Data.Enums.CollectionType.movies,
            Tesserafin.Data.Enums.CollectionType.tvshows,
            null
        };

        private static readonly CollectionType?[] _originalFolderViewTypes =
        {
            Tesserafin.Data.Enums.CollectionType.books,
            Tesserafin.Data.Enums.CollectionType.musicvideos,
            Tesserafin.Data.Enums.CollectionType.homevideos,
            Tesserafin.Data.Enums.CollectionType.photos,
            Tesserafin.Data.Enums.CollectionType.music,
            Tesserafin.Data.Enums.CollectionType.boxsets
        };

        public static ITVSeriesManager TVSeriesManager { get; set; }

        /// <summary>
        /// Gets or sets the view type.
        /// </summary>
        public CollectionType? ViewType { get; set; }

        /// <summary>
        /// Gets or sets the display parent id.
        /// </summary>
        public new Guid DisplayParentId { get; set; }

        /// <summary>
        /// Gets or sets the user id.
        /// </summary>
        public Guid? UserId { get; set; }

        /// <inheritdoc />
        [JsonIgnore]
        public CollectionType? CollectionType => ViewType;

        /// <inheritdoc />
        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        /// <inheritdoc />
        [JsonIgnore]
        public override bool SupportsPlayedStatus => false;

        /// <inheritdoc />
        [JsonIgnore]
        public override bool SupportsPeople => false;

        /// <inheritdoc />
        public override bool SupportsRawQueryItems => false;

        /// <inheritdoc />
        public override IEnumerable<Guid> GetIdsForAncestorQuery()
        {
            if (!DisplayParentId.IsEmpty())
            {
                yield return DisplayParentId;
            }
            else if (!ParentId.IsEmpty())
            {
                yield return ParentId;
            }
            else
            {
                yield return Id;
            }
        }

        /// <inheritdoc />
        public override int GetChildCount(User user, IStandaloneVisibilityService scope)
        {
            return GetChildren(user, true, new InternalItemsQuery(user) { Visibility = scope }).Count;
        }

        /// <inheritdoc />
        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            var parent = this as Folder;

            if (!DisplayParentId.IsEmpty())
            {
                parent = LibraryManager.GetItemById(DisplayParentId) as Folder ?? parent;
            }
            else if (!ParentId.IsEmpty())
            {
                parent = LibraryManager.GetItemById(ParentId) as Folder ?? parent;
            }

            return new UserViewBuilder(userViewManager, LibraryManager, Logger, UserDataManager, tvSeriesManager, channelManager, collectionManager, null, query.RequireVisibility())
                .GetUserItems(parent, this, CollectionType, query);
        }

        /// <inheritdoc />
        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager, IItemSortService itemSortService)
        {
            var parent = this as Folder;

            if (!DisplayParentId.IsEmpty())
            {
                parent = LibraryManager.GetItemById(DisplayParentId) as Folder ?? parent;
            }
            else if (!ParentId.IsEmpty())
            {
                parent = LibraryManager.GetItemById(ParentId) as Folder ?? parent;
            }

            return new UserViewBuilder(userViewManager, LibraryManager, Logger, UserDataManager, tvSeriesManager, channelManager, collectionManager, itemSortService, query.RequireVisibility())
                .GetUserItems(parent, this, CollectionType, query);
        }

        /// <inheritdoc />
        public override List<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query)
        {
            query = PrepareChildrenQuery(user, query);

            // GetChildren isn't part of the GetItemsInternal/GetItems/GetItemList chain the
            // channelManager parameter was threaded through (out of scope: GetChildren is a much
            // wider virtual with many more overrides/callers) — falls back to the statics here.
            // Documented legitimate caller of the obsolete 5-parameter overload — see
            // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
            var result = GetItemList(query, ChannelManager, CollectionManager, UserViewManager, TVSeriesManager);
#pragma warning restore CS0618

            return result.ToList();
        }

        /// <inheritdoc />
        public override IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query, IItemSortService itemSortService)
        {
            query = PrepareChildrenQuery(user, query);

            var result = GetItemList(query, ChannelManager, CollectionManager, UserViewManager, TVSeriesManager, itemSortService);

            return result.ToList();
        }

        /// <inheritdoc />
        public override bool CanDelete()
        {
            return false;
        }

        /// <inheritdoc />
        public override bool IsSaveLocalMetadataEnabled()
        {
            return true;
        }

        /// <inheritdoc />
        public override IReadOnlyList<BaseItem> GetRecursiveChildren(User user, InternalItemsQuery query, out int totalCount)
        {
            query = PrepareRecursiveChildrenQuery(user, query);

            // Same scope boundary as GetChildren above: GetRecursiveChildren isn't part of the
            // threaded chain, falls back to the statics.
            // Documented legitimate caller of the obsolete 5-parameter overload — see
            // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
            var data = GetItemList(query, ChannelManager, CollectionManager, UserViewManager, TVSeriesManager);
#pragma warning restore CS0618
            totalCount = data.Count;

            return data;
        }

        /// <inheritdoc />
        public override IReadOnlyList<BaseItem> GetRecursiveChildren(User user, InternalItemsQuery query, out int totalCount, IItemSortService itemSortService)
        {
            query = PrepareRecursiveChildrenQuery(user, query);

            var data = GetItemList(query, ChannelManager, CollectionManager, UserViewManager, TVSeriesManager, itemSortService);
            totalCount = data.Count;

            return data;
        }

        private static InternalItemsQuery PrepareChildrenQuery(User user, InternalItemsQuery query)
        {
            query ??= new InternalItemsQuery(user);
            query.EnableTotalRecordCount = false;
            return query;
        }

        private static InternalItemsQuery PrepareRecursiveChildrenQuery(User user, InternalItemsQuery query)
        {
            query ??= new InternalItemsQuery(user);
            query.SetUser(user);
            query.Recursive = true;
            query.EnableTotalRecordCount = false;
            query.ForceDirect = true;
            return query;
        }

        /// <inheritdoc />
        protected override IReadOnlyList<BaseItem> GetEligibleChildrenForRecursiveChildren(User user, InternalItemsQuery query)
        {
            return GetChildren(user, false, new InternalItemsQuery(user) { Visibility = query.RequireVisibility() });
        }

        public static bool IsUserSpecific(Folder folder)
        {
            if (folder is not ICollectionFolder collectionFolder)
            {
                return false;
            }

            if (folder is ISupportsUserSpecificView supportsUserSpecific
                && supportsUserSpecific.EnableUserSpecificView)
            {
                return true;
            }

            return collectionFolder.CollectionType == Tesserafin.Data.Enums.CollectionType.playlists;
        }

        public static bool IsEligibleForGrouping(Folder folder)
        {
            return folder is ICollectionFolder collectionFolder
                    && IsEligibleForGrouping(collectionFolder.CollectionType);
        }

        public static bool IsEligibleForGrouping(CollectionType? viewType)
        {
            return _viewTypesEligibleForGrouping.Contains(viewType);
        }

        public static bool EnableOriginalFolder(CollectionType? viewType)
        {
            return _originalFolderViewTypes.Contains(viewType);
        }

        protected override Task ValidateChildrenInternal(IItemDeletionCommands deletions, IItemWriteCommands writes, IItemImageService images, ILimitedConcurrencyLibraryScheduler scheduler, IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
