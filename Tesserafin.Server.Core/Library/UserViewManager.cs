#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Channels;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Library;
using Tesserafin.Model.Querying;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Class UserViewManager.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>PR110</b>: <see cref="GetUserViews"/> now delegates entirely to <see cref="IUserViewCatalog"/>
    /// (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §9/§10.7) - <c>UserViewCatalog</c> is the sole
    /// real implementation of the listing logic; this class no longer duplicates it. The former
    /// per-folder named/shadow-view creation helpers that only <see cref="GetUserViews"/> called
    /// (<c>GetUserView(Folder, ...)</c>, the private list-grouping overload) were removed along with
    /// it - zero other callers, in or out of this class (verified by repo-wide grep before removal).
    /// </para>
    /// <para>
    /// <see cref="GetUserSubView"/>/<see cref="GetLatestItems"/> remain implemented here (out of
    /// <see cref="IUserViewCatalog"/>'s narrower scope, PR109) and still route named-view creation
    /// through <see cref="IUserViewFactory"/> instead of <c>ILibraryManager.GetNamedView</c>.
    /// <see cref="ILibraryManager"/> itself stays as a dependency: <see cref="GetLatestItems"/>'s
    /// <c>GetItemsForLatestItems</c> helper needs <c>GetItemById</c>, <c>GetLatestItemList</c> and
    /// <c>GetItemList(InternalItemsQuery, List&lt;BaseItem&gt;)</c>, none of which are exposed on any
    /// narrower leaf port - out of this RFC's scope. This is a one-way edge only
    /// (<c>UserViewManager -&gt; ILibraryManager</c>): <c>LibraryManager</c> no longer references
    /// <see cref="IUserViewManager"/> (direct or <see cref="Lazy{T}"/>) at all, so the SCC is broken
    /// even though this residual edge remains.
    /// </para>
    /// </remarks>
    public class UserViewManager : IUserViewManager
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILocalizationManager _localizationManager;
        private readonly IChannelManager _channelManager;
        private readonly IItemSortService _itemSortService;
        private readonly IUserViewCatalog _userViewCatalog;
        private readonly IUserViewFactory _userViewFactory;

        public UserViewManager(
            ILibraryManager libraryManager,
            ILocalizationManager localizationManager,
            IChannelManager channelManager,
            IItemSortService itemSortService,
            IUserViewCatalog userViewCatalog,
            IUserViewFactory userViewFactory)
        {
            _libraryManager = libraryManager;
            _localizationManager = localizationManager;
            _channelManager = channelManager;
            _itemSortService = itemSortService;
            _userViewCatalog = userViewCatalog;
            _userViewFactory = userViewFactory;
        }

        /// <inheritdoc/>
        public Folder[] GetUserViews(UserViewQuery query)
        {
            return _userViewCatalog.GetUserViews(query);
        }

        public UserView GetUserSubViewWithName(string name, Guid parentId, CollectionType? type, string sortName)
        {
            var uniqueId = parentId + "subview" + type;

            return _userViewFactory.GetNamedView(name, parentId, type, sortName, uniqueId);
        }

        public UserView GetUserSubView(Guid parentId, CollectionType? type, string localizationKey, string sortName)
        {
            var name = _localizationManager.GetLocalizedString(localizationKey);

            return GetUserSubViewWithName(name, parentId, type, sortName);
        }

        public List<Tuple<BaseItem, List<BaseItem>>> GetLatestItems(LatestItemsQuery request, DtoOptions options)
        {
            var libraryItems = GetItemsForLatestItems(request.User, request, options);

            var list = new List<Tuple<BaseItem, List<BaseItem>>>();
            var containerIndexMap = new Dictionary<Guid, int>();
            foreach (var item in libraryItems)
            {
                // Only grab the index container for media
                var container = item.IsFolder || !request.GroupItems ? null : item.LatestItemsIndexContainer;

                if (container is null)
                {
                    list.Add(new Tuple<BaseItem, List<BaseItem>>(null!, new List<BaseItem> { item }));
                }
                else if (containerIndexMap.TryGetValue(container.Id, out var existingIndex))
                {
                    list[existingIndex].Item2.Add(item);
                }
                else
                {
                    containerIndexMap[container.Id] = list.Count;
                    list.Add(new Tuple<BaseItem, List<BaseItem>>(container, new List<BaseItem> { item }));
                }

                if (list.Count >= request.Limit)
                {
                    break;
                }
            }

            return list;
        }

        private IReadOnlyList<BaseItem> GetItemsForLatestItems(User user, LatestItemsQuery request, DtoOptions options)
        {
            var parentId = request.ParentId;

            var includeItemTypes = request.IncludeItemTypes;
            var limit = request.Limit ?? 10;

            var parents = new List<BaseItem>();

            if (!parentId.IsEmpty())
            {
                var parentItem = _libraryManager.GetItemById(parentId);
                if (parentItem is Channel)
                {
                    return _channelManager.GetLatestChannelItemsInternal(
                        new InternalItemsQuery(user)
                        {
                            ChannelIds = [parentId],
                            IsPlayed = request.IsPlayed,
                            StartIndex = request.StartIndex,
                            Limit = request.Limit,
                            IncludeItemTypes = request.IncludeItemTypes,
                            EnableTotalRecordCount = false
                        },
                        CancellationToken.None).GetAwaiter().GetResult().Items;
                }

                if (parentItem is Folder parent)
                {
                    parents.Add(parent);
                }
            }

            var isPlayed = request.IsPlayed;

            if (parents.OfType<ICollectionFolder>().Any(i => i.CollectionType == CollectionType.music))
            {
                isPlayed = null;
            }

            if (parents.Count == 0)
            {
                parents = _libraryManager.GetUserRootFolder().GetChildren(user, true, null, _itemSortService)
                    .Where(i => i is Folder)
                    .Where(i => !user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes)
                        .Contains(i.Id))
                    .ToList();
            }

            if (parents.Count == 0)
            {
                return Array.Empty<BaseItem>();
            }

            if (includeItemTypes.Length == 0)
            {
                // Handle situations with the grouping setting, e.g. movies showing up in tv, etc.
                // Thanks to mixed content libraries included in the UserView
                var hasCollectionType = parents.OfType<UserView>().ToList();
                if (hasCollectionType.Count > 0)
                {
                    if (hasCollectionType.All(i => i.CollectionType == CollectionType.movies))
                    {
                        includeItemTypes = [BaseItemKind.Movie];
                    }
                    else if (hasCollectionType.All(i => i.CollectionType == CollectionType.tvshows))
                    {
                        includeItemTypes = [BaseItemKind.Episode];
                    }
                }
            }

            MediaType[] mediaTypes = [];

            if (includeItemTypes.Length == 0)
            {
                HashSet<MediaType> tmpMediaTypes = [];
                foreach (var parent in parents.OfType<ICollectionFolder>())
                {
                    switch (parent.CollectionType)
                    {
                        case CollectionType.books:
                            tmpMediaTypes.Add(MediaType.Book);
                            tmpMediaTypes.Add(MediaType.Audio);
                            break;
                        case CollectionType.music:
                            tmpMediaTypes.Add(MediaType.Audio);
                            break;
                        case CollectionType.photos:
                            tmpMediaTypes.Add(MediaType.Photo);
                            tmpMediaTypes.Add(MediaType.Video);
                            break;
                        case CollectionType.homevideos:
                            tmpMediaTypes.Add(MediaType.Photo);
                            tmpMediaTypes.Add(MediaType.Video);
                            break;
                        default:
                            tmpMediaTypes.Add(MediaType.Video);
                            break;
                    }
                }

                mediaTypes = tmpMediaTypes.ToArray();
            }

            var excludeItemTypes = includeItemTypes.Length == 0 && mediaTypes.Length == 0
                ?
                [
                    BaseItemKind.Person,
                    BaseItemKind.Studio,
                    BaseItemKind.Year,
                    BaseItemKind.MusicGenre,
                    BaseItemKind.Genre
                ]
                : Array.Empty<BaseItemKind>();

            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = includeItemTypes,
                OrderBy =
                [
                    (ItemSortBy.DateCreated, SortOrder.Descending),
                    (ItemSortBy.SortName, SortOrder.Descending),
                    (ItemSortBy.ProductionYear, SortOrder.Descending)
                ],
                IsFolder = includeItemTypes.Length == 0 ? false : null,
                ExcludeItemTypes = excludeItemTypes,
                IsVirtualItem = false,
                Limit = limit * 2,
                IsPlayed = isPlayed,
                DtoOptions = options,
                MediaTypes = mediaTypes
            };

            if (request.GroupItems)
            {
                var collectionType = parents
                    .Select(parent => parent switch
                    {
                        ICollectionFolder collectionFolder => collectionFolder.CollectionType,
                        UserView userView => userView.CollectionType,
                        _ => null
                    })
                    .FirstOrDefault(type => type is not null);

                if (collectionType == CollectionType.tvshows)
                {
                    query.Limit = limit;
                    return _libraryManager.GetLatestItemList(query, parents, CollectionType.tvshows);
                }

                if (collectionType == CollectionType.music)
                {
                    query.Limit = limit;
                    return _libraryManager.GetLatestItemList(query, parents, CollectionType.music);
                }

                if (collectionType == CollectionType.movies)
                {
                    query.Limit = limit;
                    return _libraryManager.GetLatestItemList(query, parents, CollectionType.movies);
                }
            }

            return _libraryManager.GetItemList(query, parents);
        }
    }
}
