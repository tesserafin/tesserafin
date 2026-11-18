#pragma warning disable CS1591
#pragma warning disable CA5394

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.IO;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Playlists;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Drawing;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.Library;
using Tesserafin.Model.Querying;
using Tesserafin.Naming.Common;
using Tesserafin.Naming.Video;
using Tesserafin.Server.Core.Library.Resolvers;
using Tesserafin.Server.Core.Library.Validators;
using Episode = Tesserafin.Controller.Entities.TV.Episode;
using Genre = Tesserafin.Controller.Entities.Genre;
using Person = Tesserafin.Controller.Entities.Person;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Class LibraryManager.
    /// </summary>
    /// <remarks>
    /// Internal since PR76: the constructor depends on the internal <see cref="IItemCacheStore"/>
    /// lifecycle port, and no other assembly ever referenced the concrete type - consumers go
    /// through <see cref="ILibraryManager"/> (or <see cref="IItemLookupService"/>), wired up in
    /// <c>ApplicationHost</c> (same assembly). Tests reach it via <c>InternalsVisibleTo</c>.
    /// </remarks>
    internal class LibraryManager : ILibraryManager, IItemLookupService
    {
        private readonly IItemResolutionService _resolution;
        private readonly ILibraryItemOptions _options;
        private readonly IUserManager _userManager;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IItemQueryScopeService _queryScope;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;
        private readonly INextUpService _nextUpService;
        private readonly IItemCountService _countService;
        private readonly ILinkedChildrenService _linkedChildrenService;
        private readonly IItemSortService _itemSortService;
        private readonly IItemNamingService _itemNamingService;
        private readonly IItemPeopleService _itemPeopleService;
        private readonly IItemLookupService _itemLookupService;
        private readonly IItemCacheStore _itemCacheStore;
        private readonly IItemAccessService _itemAccessService;
        private readonly IUserViewFactory _userViewFactory;
        private readonly IUserRootFolderProvider _userRootFolderProvider;
        private readonly ICollectionFolderResolver _collectionFolderResolver;
        private readonly IMediaStreamLanguageService _mediaStreamLanguageService;

        private readonly ILibraryRootFolderProvider _roots;

        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryManager" /> class.
        /// </summary>
        /// <param name="resolution">Filesystem resolution.</param>
        /// <param name="options">Library options and content policy.</param>
        /// <param name="userManager">The user manager.</param>
        /// <param name="configurationManager">The configuration manager.</param>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="queryScope">Shared parent and user query scoping policy.</param>
        /// <param name="itemRepository">The item repository.</param>
        /// <param name="nextUpService">The next up service.</param>
        /// <param name="countService">The item count service.</param>
        /// <param name="linkedChildrenService">The linked children service.</param>
        /// <param name="itemSortService">The item sort service.</param>
        /// <param name="itemNamingService">The item naming service.</param>
        /// <param name="itemPeopleService">The item people service.</param>
        /// <param name="mediaStreamLanguageService">The media stream language service.</param>
        /// <param name="itemLookupService">The item lookup service (read side of the item lookup cache; see PR75/PR76).</param>
        /// <param name="itemCacheStore">The item cache lifecycle port (register/invalidate side of the same cache; see PR76).</param>
        /// <param name="itemAccessService">The item access service (user visibility on top of the item lookup service; see PR77).</param>
        /// <param name="userViewFactory">The user-view factory leaf (named/shadow view creation; see PR106b). <see cref="GetNamedView(User, string, CollectionType?, string)"/> and the other overloads delegate to it - a leaf dependency, not a cycle: <c>UserViewFactory</c> never references <see cref="ILibraryManager"/>.</param>
        /// <param name="userRootFolderProvider">The user root folder provider leaf (construction/cache of the per-server <see cref="UserRootFolder"/>; see PR107). <see cref="GetUserRootFolder"/> and this class's internal callers delegate to it - a leaf dependency, not a cycle: <c>UserRootFolderProvider</c> never references <see cref="ILibraryManager"/>. <see cref="LibraryManager"/> no longer implements <see cref="IUserRootFolderProvider"/> itself (it used to be aliased onto it via a DI factory cast - RFC <c>docs/rfc-di-query-user-views-v2.md</c> §0/§1's "piège n°1").</param>
        /// <param name="collectionFolderResolver">The library membership leaf (<see cref="GetCollectionFolders(BaseItem)"/> and its overload delegate to it; see DEBT-SERVER-2) - a leaf dependency, not a cycle: <c>CollectionFolderResolver</c> never references <see cref="ILibraryManager"/>.</param>
        /// <param name="roots">Application aggregate root ownership.</param>
        public LibraryManager(
            IItemResolutionService resolution,
            ILibraryItemOptions options,
            IUserManager userManager,
            IServerConfigurationManager configurationManager,
            IFileSystem fileSystem,
            IItemQueryScopeService queryScope,
            IItemRepository itemRepository,
            INextUpService nextUpService,
            IItemCountService countService,
            ILinkedChildrenService linkedChildrenService,
            IItemSortService itemSortService,
            IItemNamingService itemNamingService,
            IItemPeopleService itemPeopleService,
            IMediaStreamLanguageService mediaStreamLanguageService,
            IItemLookupService itemLookupService,
            IItemCacheStore itemCacheStore,
            IItemAccessService itemAccessService,
            IUserViewFactory userViewFactory,
            IUserRootFolderProvider userRootFolderProvider,
            ICollectionFolderResolver collectionFolderResolver,
            ILibraryRootFolderProvider roots)
        {
            _resolution = resolution;
            _options = options;
            _userManager = userManager;
            _configurationManager = configurationManager;
            _fileSystem = fileSystem;
            _queryScope = queryScope;
            _itemRepository = itemRepository;
            _nextUpService = nextUpService;
            _countService = countService;
            _linkedChildrenService = linkedChildrenService;
            _itemSortService = itemSortService;
            _itemNamingService = itemNamingService;
            _itemPeopleService = itemPeopleService;

            _itemLookupService = itemLookupService;
            _itemCacheStore = itemCacheStore;
            _itemAccessService = itemAccessService;

            _userViewFactory = userViewFactory;
            _userRootFolderProvider = userRootFolderProvider;
            _collectionFolderResolver = collectionFolderResolver;
            _roots = roots;

            _mediaStreamLanguageService = mediaStreamLanguageService;
        }

        /// <summary>
        /// Gets the root folder.
        /// </summary>
        /// <value>The root folder.</value>
        public AggregateFolder RootFolder => _roots.GetRootFolder();

        /// <summary>
        /// Adds the parts.
        /// </summary>
        /// <param name="rules">The rules.</param>
        /// <param name="resolvers">The resolvers.</param>
        /// <param name="itemComparers">The item comparers.</param>
        public void AddParts(
            IEnumerable<IResolverIgnoreRule> rules,
            IEnumerable<IItemResolver> resolvers,
            IEnumerable<IBaseItemComparer> itemComparers)
        {
            _resolution.AddParts(rules, resolvers);
            _itemSortService.AddParts(itemComparers);
        }

        public Video? ResolveAlternateVersion(string path, Type expectedVideoType, Folder? parent, CollectionType? collectionType)
            => _resolution.ResolveAlternateVersion(path, expectedVideoType, parent, collectionType);

        public bool AreEqualPaths(string first, string second) => _fileSystem.AreEqual(first, second);

        public void RegisterItem(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            _itemCacheStore.Register(item);
        }

        public Guid GetNewItemId(string key, Type type)
        {
            return GetNewItemIdInternal(key, type, false);
        }

        private Guid GetNewItemIdInternal(string key, Type type, bool forceCaseInsensitive)
            => ItemIdentity.Create(_configurationManager, key, type, forceCaseInsensitive);

        public BaseItem? ResolvePath(
            FileSystemMetadata fileInfo,
            Folder? parent = null,
            IDirectoryService? directoryService = null,
            CollectionType? collectionType = null)
            => _resolution.ResolvePath(fileInfo, parent, directoryService, collectionType);

        public bool IgnoreFile(FileSystemMetadata file, BaseItem? parent)
            => _resolution.IgnoreFile(file, parent);

        /// <inheritdoc />
        /// <remarks>
        /// Delegates to <see cref="IUserRootFolderProvider"/> (PR107) - construction, caching and the
        /// (intentional) absence of invalidation now live solely in <c>UserRootFolderProvider</c>,
        /// not here. This facade member is kept for API compatibility with <see cref="ILibraryManager"/>
        /// consumers; <see cref="LibraryManager"/> no longer implements <see cref="IUserRootFolderProvider"/>
        /// itself.
        /// </remarks>
        public Folder GetUserRootFolder()
        {
            return _userRootFolderProvider.GetUserRootFolder();
        }

        public IReadOnlyDictionary<string, MusicArtist[]> GetArtists(IReadOnlyList<string> names)
        {
            return _linkedChildrenService.FindArtists(names);
        }

        /// <inheritdoc />
        public BaseItem? GetItemById(Guid id)
        {
            return _itemLookupService.GetItemById(id);
        }

        /// <inheritdoc />
        public T? GetItemById<T>(Guid id)
            where T : BaseItem
        {
            return _itemLookupService.GetItemById<T>(id);
        }

        /// <inheritdoc />
        public T? GetItemById<T>(Guid id, Guid userId)
            where T : BaseItem
        {
            if (userId.IsEmpty())
            {
                return GetItemById<T>(id);
            }

            // An id that names no user is not "no user": nothing is visible to it.
            var user = _userManager.GetUserById(userId);
            return user is null ? null : GetItemById<T>(id, user);
        }

        /// <inheritdoc />
        /// <remarks>
        /// PR77: <see cref="IItemLookupService"/> no longer exposes a user-aware overload - a null
        /// user historically meant "no visibility filtering", not "no user", so a null user routes
        /// to the plain <see cref="IItemLookupService.GetItemById{T}(Guid)"/> lookup, and a non-null
        /// user routes to <see cref="IItemAccessService.GetVisibleItemById{T}(Guid, User)"/>. This
        /// preserves the exact pre-PR77 behavior of this overload.
        /// </remarks>
        public T? GetItemById<T>(Guid id, User? user)
            where T : BaseItem
        {
            return user is null
                ? _itemLookupService.GetItemById<T>(id)
                : _itemAccessService.GetVisibleItemById<T>(id, user);
        }

        public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query, bool allowExternalContent)
        {
            if (query.Recursive && !query.ParentId.IsEmpty())
            {
                var parent = GetItemById(query.ParentId);
                if (parent is not null)
                {
                    _queryScope.SetTopParentIdsOrAncestors(query, [parent]);
                }
            }

            if (query.User is not null)
            {
                AddUserToQuery(query, query.User, allowExternalContent);
            }

            return _itemRepository.GetItemList(query);
        }

        public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query)
        {
            return GetItemList(query, true);
        }

        public int GetCount(InternalItemsQuery query)
        {
            if (query.Recursive && !query.ParentId.IsEmpty())
            {
                var parent = GetItemById(query.ParentId);
                if (parent is not null)
                {
                    _queryScope.SetTopParentIdsOrAncestors(query, [parent]);
                }
            }

            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            return _countService.GetCount(query);
        }

        public ItemCounts GetItemCounts(InternalItemsQuery query)
        {
            if (query.Recursive && !query.ParentId.IsEmpty())
            {
                var parent = GetItemById(query.ParentId);
                if (parent is not null)
                {
                    _queryScope.SetTopParentIdsOrAncestors(query, [parent]);
                }
            }

            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            return _countService.GetItemCounts(query);
        }

        /// <inheritdoc/>
        public ItemCounts GetItemCountsForNameItem(BaseItemKind kind, Guid id, BaseItemKind[] relatedItemKinds, User? user)
        {
            var query = new InternalItemsQuery(user);
            if (user is not null)
            {
                AddUserToQuery(query, user);
            }

            return _countService.GetItemCountsForNameItem(kind, id, relatedItemKinds, query);
        }

        public Dictionary<Guid, int> GetChildCountBatch(IReadOnlyList<Guid> parentIds, Guid? userId)
        {
            return _countService.GetChildCountBatch(parentIds, userId);
        }

        /// <inheritdoc/>
        public Dictionary<Guid, (int Played, int Total)> GetPlayedAndTotalCountBatch(IReadOnlyList<Guid> folderIds, User user)
        {
            return _countService.GetPlayedAndTotalCountBatch(folderIds, user);
        }

        public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query, List<BaseItem> parents)
        {
            _queryScope.SetTopParentIdsOrAncestors(query, parents);

            if (query.AncestorIds.Length == 0 && query.TopParentIds.Length == 0)
            {
                if (query.User is not null)
                {
                    AddUserToQuery(query, query.User);
                }
            }

            return _itemRepository.GetItemList(query);
        }

        public IReadOnlyList<BaseItem> GetLatestItemList(InternalItemsQuery query, IReadOnlyList<BaseItem> parents, CollectionType collectionType)
        {
            _queryScope.SetTopParentIdsOrAncestors(query, parents);

            if (query.AncestorIds.Length == 0 && query.TopParentIds.Length == 0)
            {
                if (query.User is not null)
                {
                    AddUserToQuery(query, query.User);
                }
            }

            return _itemRepository.GetLatestItemList(query, collectionType);
        }

        public IReadOnlyList<string> GetNextUpSeriesKeys(InternalItemsQuery query, IReadOnlyCollection<BaseItem> parents, DateTime dateCutoff)
        {
            _queryScope.SetTopParentIdsOrAncestors(query, parents);

            if (query.AncestorIds.Length == 0 && query.TopParentIds.Length == 0)
            {
                if (query.User is not null)
                {
                    AddUserToQuery(query, query.User);
                }
            }

            return _nextUpService.GetNextUpSeriesKeys(query, dateCutoff);
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, Tesserafin.Controller.Persistence.NextUpEpisodeBatchResult> GetNextUpEpisodesBatch(
            InternalItemsQuery query,
            IReadOnlyList<string> seriesKeys,
            bool includeSpecials,
            bool includeWatchedForRewatching)
        {
            return _nextUpService.GetNextUpEpisodesBatch(query, seriesKeys, includeSpecials, includeWatchedForRewatching);
        }

        public QueryResult<BaseItem> QueryItems(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            if (query.EnableTotalRecordCount)
            {
                return _itemRepository.GetItems(query);
            }

            return new QueryResult<BaseItem>(
                query.StartIndex,
                null,
                _itemRepository.GetItemList(query));
        }

        public IReadOnlyList<Guid> GetItemIds(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            return _itemRepository.GetItemIdsList(query);
        }

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetStudios(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            _queryScope.SetTopParentOrAncestorIds(query);
            return _itemRepository.GetStudios(query);
        }

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetGenres(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            _queryScope.SetTopParentOrAncestorIds(query);
            return _itemRepository.GetGenres(query);
        }

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetMusicGenres(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            _queryScope.SetTopParentOrAncestorIds(query);
            return _itemRepository.GetMusicGenres(query);
        }

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAllArtists(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            _queryScope.SetTopParentOrAncestorIds(query);
            return _itemRepository.GetAllArtists(query);
        }

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetArtists(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            _queryScope.SetTopParentOrAncestorIds(query);
            return _itemRepository.GetArtists(query);
        }

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAlbumArtists(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            _queryScope.SetTopParentOrAncestorIds(query);
            return _itemRepository.GetAlbumArtists(query);
        }

        public QueryResult<BaseItem> GetItemsResult(InternalItemsQuery query)
        {
            if (query.Recursive && !query.ParentId.IsEmpty())
            {
                var parent = GetItemById(query.ParentId);
                if (parent is not null)
                {
                    _queryScope.SetTopParentIdsOrAncestors(query, [parent]);
                }
            }

            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            if (query.EnableTotalRecordCount)
            {
                return _itemRepository.GetItems(query);
            }

            return new QueryResult<BaseItem>(
                query.StartIndex,
                null,
                _itemRepository.GetItemList(query));
        }

        private void AddUserToQuery(InternalItemsQuery query, User user, bool allowExternalContent = true)
        {
            _queryScope.AddUserToQuery(query, user, allowExternalContent);
            ExcludeBoxSetsAndPlaylistsRefusedToTheUser(query, user);
        }

        /// <summary>
        /// A query that can hold box sets or playlists (names them, names no type, or groups its items into their
        /// box sets) excludes, in the query itself, those the user is refused by id: the libraries above scope only
        /// the folders, so its items, pages, totals, counts and aggregated values stay those access by id admits
        /// (GHSA-c5pr-6h39-jp86).
        /// </summary>
        // ponytail: decides every box set and playlist of the server per such query; scope the candidates
        // to the query if their number makes listings slow.
        private void ExcludeBoxSetsAndPlaylistsRefusedToTheUser(InternalItemsQuery query, User user)
        {
            if (query.CollapseBoxSetItems != true
                && query.IncludeItemTypes.Length > 0
                && !query.IncludeItemTypes.Any(t => t == BaseItemKind.BoxSet || t == BaseItemKind.Playlist))
            {
                return;
            }

            var refused = GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.BoxSet, BaseItemKind.Playlist],
                Recursive = true,
                DtoOptions = new DtoOptions(false) { EnableImages = false }
            })
                .Where(i => !i.IsVisibleStandalone(user))
                .Select(i => i.Id)
                .ToArray();

            if (refused.Length > 0)
            {
                query.ExcludeItemIds = [.. query.ExcludeItemIds.Except(refused), .. refused];
            }
        }

        /// <inheritdoc/>
        public void ConfigureUserAccess(InternalItemsQuery query, User user)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(user);

            AddUserToQuery(query, user);
        }

        /// <inheritdoc />
        public IEnumerable<Guid> GetLocalAlternateVersionIds(Video video)
        {
            ArgumentNullException.ThrowIfNull(video);

            var linkedIds = _linkedChildrenService.GetLinkedChildrenIds(video.Id, (int)Tesserafin.Controller.Entities.LinkedChildType.LocalAlternateVersion);
            if (linkedIds.Count > 0)
            {
                return linkedIds;
            }

            return [];
        }

        /// <inheritdoc />
        public IEnumerable<Video> GetLinkedAlternateVersions(Video video)
        {
            ArgumentNullException.ThrowIfNull(video);

            var linkedIds = _linkedChildrenService.GetLinkedChildrenIds(video.Id, (int)Tesserafin.Controller.Entities.LinkedChildType.LinkedAlternateVersion);
            if (linkedIds.Count > 0)
            {
                return linkedIds
                    .Select(id => GetItemById(id))
                    .Where(i => i is not null)
                    .OfType<Video>()
                    .OrderBy(i => i.SortName);
            }

            return [];
        }

        /// <inheritdoc />
        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder)
        {
            return _itemSortService.Sort(items, user, sortBy, sortOrder);
        }

        /// <inheritdoc />
        public IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<(ItemSortBy OrderBy, SortOrder SortOrder)> orderBy)
        {
            return _itemSortService.Sort(items, user, orderBy);
        }

        /// <summary>
        /// Retrieves the item.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>BaseItem.</returns>
        public BaseItem RetrieveItem(Guid id)
        {
            return _itemRepository.RetrieveItem(id);
        }

        /// <inheritdoc />
        /// <remarks>Delegates to <see cref="ICollectionFolderResolver"/> (DEBT-SERVER-2).</remarks>
        public List<Folder> GetCollectionFolders(BaseItem item)
            => _collectionFolderResolver.GetCollectionFolders(item);

        /// <inheritdoc />
        /// <remarks>Delegates to <see cref="ICollectionFolderResolver"/> (DEBT-SERVER-2).</remarks>
        public List<Folder> GetCollectionFolders(BaseItem item, IEnumerable<Folder> allUserRootChildren)
            => _collectionFolderResolver.GetCollectionFolders(item, allUserRootChildren);

        public LibraryOptions GetLibraryOptions(BaseItem item)
            => _options.GetLibraryOptions(item);

        public CollectionType? GetContentType(BaseItem item)
            => _options.GetContentType(item);

        public CollectionType? GetInheritedContentType(BaseItem item)
            => _options.GetInheritedContentType(item);

        public CollectionType? GetConfiguredContentType(BaseItem item)
        {
            return GetConfiguredContentType(item, false);
        }

        public CollectionType? GetConfiguredContentType(string path)
        {
            return GetContentTypeOverride(path, false);
        }

        public CollectionType? GetConfiguredContentType(BaseItem item, bool inheritConfiguredPath)
        {
            if (item is ICollectionFolder collectionFolder)
            {
                return collectionFolder.CollectionType;
            }

            return GetContentTypeOverride(item.ContainingFolderPath, inheritConfiguredPath);
        }

        private CollectionType? GetContentTypeOverride(string path, bool inherit)
            => _options.GetConfiguredContentType(path, inherit);

        /// <inheritdoc />
        /// <remarks>
        /// Delegates to <see cref="IUserViewFactory"/> (PR106b) - <see cref="ILibraryManager"/> keeps
        /// this member for API compatibility, but no longer owns the logic (RFC
        /// <c>docs/rfc-di-query-user-views-v2.md</c> §9/PR106b).
        /// </remarks>
        public UserView GetNamedView(
            User user,
            string name,
            CollectionType? viewType,
            string sortName)
        {
            return _userViewFactory.GetNamedView(user, name, viewType, sortName);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Delegates to <see cref="IUserViewFactory"/> (PR106b) - see remarks on the other
        /// <see cref="GetNamedView(User, string, CollectionType?, string)"/> overload.
        /// </remarks>
        public UserView GetNamedView(
            string name,
            CollectionType viewType,
            string sortName)
        {
            return _userViewFactory.GetNamedView(name, viewType, sortName);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Delegates to <see cref="IUserViewFactory"/> (PR106b) - see remarks on the other
        /// <see cref="GetNamedView(User, string, CollectionType?, string)"/> overload.
        /// </remarks>
        public UserView GetNamedView(
            User user,
            string name,
            Guid parentId,
            CollectionType? viewType,
            string sortName)
        {
            return _userViewFactory.GetNamedView(user, name, parentId, viewType, sortName);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Delegates to <see cref="IUserViewFactory"/> (PR106b) - see remarks on the
        /// <see cref="GetNamedView(User, string, CollectionType?, string)"/> overload.
        /// </remarks>
        public UserView GetShadowView(
            BaseItem parent,
            CollectionType? viewType,
            string sortName)
        {
            return _userViewFactory.GetShadowView(parent, viewType, sortName);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Delegates to <see cref="IUserViewFactory"/> (PR106b) - see remarks on the
        /// <see cref="GetNamedView(User, string, CollectionType?, string)"/> overload.
        /// </remarks>
        public UserView GetNamedView(
            string name,
            Guid parentId,
            CollectionType? viewType,
            string sortName,
            string uniqueId)
        {
            return _userViewFactory.GetNamedView(name, parentId, viewType, sortName, uniqueId);
        }

        public BaseItem GetParentItem(Guid? parentId, Guid? userId)
        {
            if (parentId.HasValue)
            {
                // For a user, a parent it may not see is a parent that does not exist - and so is
                // the root of every library, which is no user's. Both answer 404, as access by id does;
                // a malformed id never gets here (400 from binding).
                var parent = userId.IsNullOrEmpty()
                    ? GetItemById(parentId.Value)
                    : GetItemById<BaseItem>(parentId.Value, userId.Value);
                return parent is null || (parent is AggregateFolder && !userId.IsNullOrEmpty())
                    ? throw new ResourceNotFoundException($"Invalid parent id: {parentId.Value}")
                    : parent;
            }

            if (!userId.IsNullOrEmpty())
            {
                return GetUserRootFolder();
            }

            return RootFolder;
        }

        /// <inheritdoc />
        public int? GetSeasonNumberFromPath(string path, Guid? parentId)
        {
            var parentPath = parentId.HasValue ? GetItemById(parentId.Value)?.ContainingFolderPath : null;
            return _itemNamingService.GetSeasonNumberFromPath(path, parentPath);
        }

        /// <inheritdoc />
        public bool FillMissingEpisodeNumbersFromPath(Episode episode, bool forceRefresh)
        {
            return _itemNamingService.FillMissingEpisodeNumbersFromPath(episode, forceRefresh);
        }

        public ItemLookupInfo ParseName(string name)
        {
            return _itemNamingService.ParseName(name);
        }

        public string GetPathAfterNetworkSubstitution(string path, BaseItem? ownerItem)
        {
            foreach (var map in _configurationManager.Configuration.PathSubstitutions)
            {
                if (path.TryReplaceSubPath(map.From, map.To, out var newPath))
                {
                    return newPath;
                }
            }

            return path;
        }

        public IReadOnlyList<PersonInfo> GetPeople(InternalPeopleQuery query)
        {
            return _itemPeopleService.GetPeople(query);
        }

        public IReadOnlyList<PersonInfo> GetPeople(BaseItem item)
        {
            return _itemPeopleService.GetPeople(item);
        }

        public IReadOnlyList<string> GetPeopleNames(InternalPeopleQuery query)
        {
            return _itemPeopleService.GetPeopleNames(query);
        }

        /// <inheritdoc/>
        public IReadOnlyDictionary<Guid, IReadOnlyList<string>> GetPeopleNamesByItems(IReadOnlyList<Guid> itemIds, IReadOnlyList<string> personTypes)
        {
            return _itemPeopleService.GetPeopleNamesByItems(itemIds, personTypes);
        }

        /// <inheritdoc />
        public QueryFiltersLegacy GetQueryFiltersLegacy(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            _queryScope.SetTopParentOrAncestorIds(query);
            return _itemRepository.GetQueryFiltersLegacy(query);
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetMediaStreamLanguages(MediaStreamType mediaStreamType)
        {
            return _mediaStreamLanguageService.GetMediaStreamLanguages(mediaStreamType);
        }
    }
}
