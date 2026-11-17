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
using VideoResolver = Tesserafin.Naming.Video.VideoResolver;

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
        private readonly ILogger<LibraryManager> _logger;
        private readonly IUserManager _userManager;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly Lazy<IProviderManager> _providerManagerFactory;
        private readonly IUserViewCatalog _userViewCatalog;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;
        private readonly INextUpService _nextUpService;
        private readonly IItemCountService _countService;
        private readonly ILinkedChildrenService _linkedChildrenService;
        private readonly IItemSortService _itemSortService;
        private readonly IItemNamingService _itemNamingService;
        private readonly IItemPeopleService _itemPeopleService;
        private readonly NamingOptions _namingOptions;
        private readonly IPeopleRepository _peopleRepository;
        private readonly ExtraResolver _extraResolver;
        private readonly IItemLookupService _itemLookupService;
        private readonly IItemCacheStore _itemCacheStore;
        private readonly IItemAccessService _itemAccessService;
        private readonly IUserViewFactory _userViewFactory;
        private readonly IUserRootFolderProvider _userRootFolderProvider;
        private readonly ICollectionFolderResolver _collectionFolderResolver;
        private readonly IMediaStreamLanguageService _mediaStreamLanguageService;

        private readonly DotIgnoreIgnoreRule _dotIgnoreIgnoreRule;
        private readonly ILibraryRootFolderProvider _roots;

        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryManager" /> class.
        /// </summary>
        /// <param name="resolution">Filesystem resolution.</param>
        /// <param name="options">Library options and content policy.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="userManager">The user manager.</param>
        /// <param name="configurationManager">The configuration manager.</param>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="providerManagerFactory">The provider manager.</param>
        /// <param name="userViewCatalog">
        /// The user-views catalog leaf (sole real implementation of <c>GetUserViews</c>; see PR106-109).
        /// Replaces the former <c>Lazy&lt;IUserViewManager&gt;</c> - injected directly (not
        /// <see cref="Lazy{T}"/>), since <see cref="IUserViewCatalog"/>'s implementation
        /// (<c>UserViewCatalog</c>) never references <see cref="ILibraryManager"/>,
        /// <see cref="IUserViewManager"/>, <c>IChannelManager</c> or <c>ILiveTvManager</c> in its own
        /// eager construction graph (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §9/§10.7, PR110) -
        /// this is the edge whose removal breaks the SCC.
        /// </param>
        /// <param name="itemRepository">The item repository.</param>
        /// <param name="nextUpService">The next up service.</param>
        /// <param name="countService">The item count service.</param>
        /// <param name="linkedChildrenService">The linked children service.</param>
        /// <param name="itemSortService">The item sort service.</param>
        /// <param name="itemNamingService">The item naming service.</param>
        /// <param name="itemPeopleService">The item people service.</param>
        /// <param name="namingOptions">The naming options.</param>
        /// <param name="directoryService">The directory service.</param>
        /// <param name="peopleRepository">The people repository.</param>
        /// <param name="mediaStreamLanguageService">The media stream language service.</param>
        /// <param name="itemLookupService">The item lookup service (read side of the item lookup cache; see PR75/PR76).</param>
        /// <param name="itemCacheStore">The item cache lifecycle port (register/invalidate side of the same cache; see PR76).</param>
        /// <param name="itemAccessService">The item access service (user visibility on top of the item lookup service; see PR77).</param>
        /// <param name="userViewFactory">The user-view factory leaf (named/shadow view creation; see PR106b). <see cref="GetNamedView(User, string, CollectionType?, string)"/> and the other overloads delegate to it - a leaf dependency, not a cycle: <c>UserViewFactory</c> never references <see cref="ILibraryManager"/>.</param>
        /// <param name="userRootFolderProvider">The user root folder provider leaf (construction/cache of the per-server <see cref="UserRootFolder"/>; see PR107). <see cref="GetUserRootFolder"/> and this class's internal callers delegate to it - a leaf dependency, not a cycle: <c>UserRootFolderProvider</c> never references <see cref="ILibraryManager"/>. <see cref="LibraryManager"/> no longer implements <see cref="IUserRootFolderProvider"/> itself (it used to be aliased onto it via a DI factory cast - RFC <c>docs/rfc-di-query-user-views-v2.md</c> §0/§1's "piège n°1").</param>
        /// <param name="collectionFolderResolver">The library membership leaf (<see cref="GetCollectionFolders(BaseItem)"/> and its overload delegate to it; see DEBT-SERVER-2) - a leaf dependency, not a cycle: <c>CollectionFolderResolver</c> never references <see cref="ILibraryManager"/>.</param>
        /// <param name="dotIgnoreIgnoreRule">The ignore rules.</param>
        /// <param name="roots">Application aggregate root ownership.</param>
        public LibraryManager(
            IItemResolutionService resolution,
            ILibraryItemOptions options,
            ILoggerFactory loggerFactory,
            IUserManager userManager,
            IServerConfigurationManager configurationManager,
            IFileSystem fileSystem,
            Lazy<IProviderManager> providerManagerFactory,
            IUserViewCatalog userViewCatalog,
            IItemRepository itemRepository,
            INextUpService nextUpService,
            IItemCountService countService,
            ILinkedChildrenService linkedChildrenService,
            IItemSortService itemSortService,
            IItemNamingService itemNamingService,
            IItemPeopleService itemPeopleService,
            NamingOptions namingOptions,
            IDirectoryService directoryService,
            IPeopleRepository peopleRepository,
            IMediaStreamLanguageService mediaStreamLanguageService,
            IItemLookupService itemLookupService,
            IItemCacheStore itemCacheStore,
            IItemAccessService itemAccessService,
            IUserViewFactory userViewFactory,
            IUserRootFolderProvider userRootFolderProvider,
            ICollectionFolderResolver collectionFolderResolver,
            DotIgnoreIgnoreRule dotIgnoreIgnoreRule,
            ILibraryRootFolderProvider roots)
        {
            _resolution = resolution;
            _options = options;
            _logger = loggerFactory.CreateLogger<LibraryManager>();
            _userManager = userManager;
            _configurationManager = configurationManager;
            _fileSystem = fileSystem;
            _providerManagerFactory = providerManagerFactory;
            _userViewCatalog = userViewCatalog;
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
            _dotIgnoreIgnoreRule = dotIgnoreIgnoreRule;
            _roots = roots;

            _namingOptions = namingOptions;
            _peopleRepository = peopleRepository;
            _extraResolver = new ExtraResolver(loggerFactory.CreateLogger<ExtraResolver>(), namingOptions, directoryService);

            _mediaStreamLanguageService = mediaStreamLanguageService;
        }

        /// <summary>
        /// Gets the root folder.
        /// </summary>
        /// <value>The root folder.</value>
        public AggregateFolder RootFolder => _roots.GetRootFolder();

        private IProviderManager ProviderManager => _providerManagerFactory.Value;

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

        public List<FileSystemMetadata> NormalizeRootPathList(IEnumerable<FileSystemMetadata> paths)
            => _resolution.NormalizeRootPathList(paths);

        private BaseItem? ResolvePath(FileSystemMetadata fileInfo, IDirectoryService directoryService, IItemResolver[]? resolvers, Folder? parent = null, CollectionType? collectionType = null, LibraryOptions? libraryOptions = null)
            => _resolution.ResolvePath(fileInfo, directoryService, resolvers, parent, collectionType, libraryOptions);

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

        public IEnumerable<BaseItem> ResolvePaths(IEnumerable<FileSystemMetadata> files, IDirectoryService directoryService, Folder parent, LibraryOptions libraryOptions, CollectionType? collectionType = null)
            => _resolution.ResolvePaths(files, directoryService, parent, libraryOptions, collectionType);

        public IEnumerable<BaseItem> ResolvePaths(
            IEnumerable<FileSystemMetadata> files,
            IDirectoryService directoryService,
            Folder parent,
            LibraryOptions libraryOptions,
            CollectionType? collectionType,
            IItemResolver[] resolvers)
            => _resolution.ResolvePaths(files, directoryService, parent, libraryOptions, collectionType, resolvers);

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

        /// <inheritdoc />
        public BaseItem? FindByPath(string path, bool? isFolder)
        {
            // If this returns multiple items it could be tricky figuring out which one is correct.
            // In most cases, the newest one will be and the others obsolete but not yet cleaned up
            ArgumentException.ThrowIfNullOrEmpty(path);

            return GetItemList(ItemPathQuery.Create(path, isFolder))
                .FirstOrDefault();
        }

        /// <inheritdoc />
        public Person? GetPerson(string name)
        {
            var path = Person.GetPath(name);
            var id = GetItemByNameId<Person>(path);
            if (GetItemById(id) is Person item)
            {
                return item;
            }

            return null;
        }

        public IReadOnlyDictionary<string, MusicArtist[]> GetArtists(IReadOnlyList<string> names)
        {
            return _linkedChildrenService.FindArtists(names);
        }

        private Guid GetItemByNameId<T>(string path)
              where T : BaseItem, new()
        {
            var forceCaseInsensitiveId = _configurationManager.Configuration.EnableNormalizedItemByNameIds;
            return GetNewItemIdInternal(path, typeof(T), forceCaseInsensitiveId);
        }

        /// <inheritdoc />
        public Task ValidatePeopleAsync(IItemDeletionCommands deletions, IItemWriteCommands writes, IProgress<double> progress, CancellationToken cancellationToken)
        {
            // Ensure the location is available.
            Directory.CreateDirectory(_configurationManager.ApplicationPaths.PeoplePath);

            return new PeopleValidator(writes, deletions, ProviderManager, this, _itemPeopleService, _logger, _fileSystem).ValidatePeople(cancellationToken, progress);
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
                    SetTopParentIdsOrAncestors(query, [parent]);
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
                    SetTopParentIdsOrAncestors(query, [parent]);
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
                    SetTopParentIdsOrAncestors(query, [parent]);
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
            SetTopParentIdsOrAncestors(query, parents);

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
            SetTopParentIdsOrAncestors(query, parents);

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
            SetTopParentIdsOrAncestors(query, parents);

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

            SetTopParentOrAncestorIds(query);
            return _itemRepository.GetStudios(query);
        }

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetGenres(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            SetTopParentOrAncestorIds(query);
            return _itemRepository.GetGenres(query);
        }

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetMusicGenres(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            SetTopParentOrAncestorIds(query);
            return _itemRepository.GetMusicGenres(query);
        }

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAllArtists(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            SetTopParentOrAncestorIds(query);
            return _itemRepository.GetAllArtists(query);
        }

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetArtists(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            SetTopParentOrAncestorIds(query);
            return _itemRepository.GetArtists(query);
        }

        private void SetTopParentOrAncestorIds(InternalItemsQuery query)
        {
            var ancestorIds = query.AncestorIds;
            int len = ancestorIds.Length;
            if (len == 0)
            {
                return;
            }

            var parents = new BaseItem[len];
            for (int i = 0; i < len; i++)
            {
                parents[i] = GetItemById(ancestorIds[i]) ?? throw new ArgumentException($"Failed to find parent with id: {ancestorIds[i]}");

                // As in SetTopParentIdsOrAncestors: an ancestor its user may not see, or the root
                // of every library, scopes the query to nothing.
                if (query.User is not null
                    && (parents[i] is AggregateFolder || _itemAccessService.GetVisibleItemById<BaseItem>(ancestorIds[i], query.User) is null))
                {
                    query.TopParentIds = [Guid.NewGuid()];
                    query.AncestorIds = [];
                    return;
                }

                if (parents[i] is not (ICollectionFolder or UserView))
                {
                    return;
                }
            }

            // Optimize by querying against top level views
            query.TopParentIds = parents.SelectMany(i => GetTopParentIdsForQuery(i, query.User)).ToArray();
            query.AncestorIds = [];

            // Prevent searching in all libraries due to empty filter
            if (query.TopParentIds.Length == 0)
            {
                query.TopParentIds = [Guid.NewGuid()];
            }
        }

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAlbumArtists(InternalItemsQuery query)
        {
            if (query.User is not null)
            {
                AddUserToQuery(query, query.User);
            }

            SetTopParentOrAncestorIds(query);
            return _itemRepository.GetAlbumArtists(query);
        }

        public QueryResult<BaseItem> GetItemsResult(InternalItemsQuery query)
        {
            if (query.Recursive && !query.ParentId.IsEmpty())
            {
                var parent = GetItemById(query.ParentId);
                if (parent is not null)
                {
                    SetTopParentIdsOrAncestors(query, [parent]);
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

        private void SetTopParentIdsOrAncestors(InternalItemsQuery query, IReadOnlyCollection<BaseItem> parents)
        {
            // A parent stands in for scoping the query to its user's libraries, so it has to be one
            // that user may see: with none left, the query is scoped to nothing.
            if (query.User is not null && parents.Count != 0)
            {
                parents = parents.Where(i => _itemAccessService.GetVisibleItemById<BaseItem>(i.Id, query.User) is not null).ToList();
                if (parents.Count == 0)
                {
                    query.TopParentIds = [Guid.NewGuid()];
                    query.Parent = null;
                    return;
                }
            }

            if (parents.All(i => i is ICollectionFolder || i is UserView))
            {
                // Optimize by querying against top level views
                query.TopParentIds = parents.SelectMany(i => GetTopParentIdsForQuery(i, query.User)).ToArray();

                // Prevent searching in all libraries due to empty filter
                if (query.TopParentIds.Length == 0)
                {
                    query.TopParentIds = [Guid.NewGuid()];
                }
            }
            else if (parents.Count == 1 && parents.First() is Folder folder
                && (folder is Playlist || folder is BoxSet)
                && folder.LinkedChildren.Length > 0)
            {
                // Playlists and BoxSets store their contents in LinkedChildren and never
                // populate AncestorIds for those items, so a recursive AncestorIds query
                // would return zero rows. Resolve to the linked child IDs up front and
                // route through the existing indexed ItemIds filter.
                // These ids stand in for the scoping a parent gives, and a query that names ids is
                // not scoped to its user's libraries afterwards: each child is asked for here.
                query.ItemIds = folder.LinkedChildren
                    .Where(lc => lc.ItemId.HasValue && !lc.ItemId.Value.IsEmpty())
                    .Select(lc => lc.ItemId!.Value)
                    .Where(id => query.User is null || _itemAccessService.GetVisibleItemById<BaseItem>(id, query.User) is not null)
                    .ToArray();

                // Empty linked-children should still return empty rather than scanning everything.
                if (query.ItemIds.Length == 0)
                {
                    query.ItemIds = [Guid.NewGuid()];
                }
            }
            else
            {
                // We need to be able to query from any arbitrary ancestor up the tree
                query.AncestorIds = parents.SelectMany(i => i.GetIdsForAncestorQuery()).ToArray();

                // Prevent searching in all libraries due to empty filter
                if (query.AncestorIds.Length == 0)
                {
                    query.AncestorIds = [Guid.NewGuid()];
                }

                // An ancestor can hold more than its user's libraries - the root holds them all.
                if (query.User is not null)
                {
                    SetTopParentIdsToUserViews(query, query.User, true);
                }
            }

            query.Parent = null;
        }

        private void AddUserToQuery(InternalItemsQuery query, User user, bool allowExternalContent = true)
        {
            if (query.User is null)
            {
                query.SetUser(user);
            }

            if (query.AncestorIds.Length == 0 &&
                query.ParentId.IsEmpty() &&
                query.ChannelIds.Count == 0 &&
                query.TopParentIds.Length == 0 &&
                string.IsNullOrEmpty(query.AncestorWithPresentationUniqueKey) &&
                string.IsNullOrEmpty(query.SeriesPresentationUniqueKey) &&
                query.ItemIds.Length == 0 &&
                query.OwnerIds.Length == 0)
            {
                SetTopParentIdsToUserViews(query, user, allowExternalContent);
            }

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
                .Where(i => !_itemAccessService.IsVisibleStandalone(i, user))
                .Select(i => i.Id)
                .ToArray();

            if (refused.Length > 0)
            {
                query.ExcludeItemIds = [.. query.ExcludeItemIds.Except(refused), .. refused];
            }
        }

        private void SetTopParentIdsToUserViews(InternalItemsQuery query, User user, bool allowExternalContent)
        {
            var userViews = _userViewCatalog.GetUserViews(new UserViewQuery
            {
                User = user,
                IncludeHidden = true,
                IncludeExternalContent = allowExternalContent
            });

            query.TopParentIds = userViews.SelectMany(i => GetTopParentIdsForQuery(i, user)).ToArray();

            // Prevent searching in all libraries due to empty filter
            if (query.TopParentIds.Length == 0)
            {
                query.TopParentIds = [Guid.NewGuid()];
            }
        }

        /// <inheritdoc/>
        public void ConfigureUserAccess(InternalItemsQuery query, User user)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(user);

            AddUserToQuery(query, user);
        }

        private IEnumerable<Guid> GetTopParentIdsForQuery(BaseItem item, User? user)
        {
            if (item is UserView view)
            {
                if (view.ViewType == CollectionType.livetv)
                {
                    return [view.Id];
                }

                // Translate view into folders
                if (!view.DisplayParentId.IsEmpty())
                {
                    var displayParent = GetItemById(view.DisplayParentId);
                    if (displayParent is not null)
                    {
                        return GetTopParentIdsForQuery(displayParent, user);
                    }

                    return [];
                }

                if (!view.ParentId.IsEmpty())
                {
                    var displayParent = GetItemById(view.ParentId);
                    if (displayParent is not null)
                    {
                        return GetTopParentIdsForQuery(displayParent, user);
                    }

                    return [];
                }

                // Handle grouping
                if (user is not null && view.ViewType != CollectionType.unknown && UserView.IsEligibleForGrouping(view.ViewType)
                    && user.GetPreference(PreferenceKind.GroupedFolders).Length > 0)
                {
                    return _itemAccessService
                        .GetChildren(GetUserRootFolder(), user, _itemSortService)
                        .OfType<CollectionFolder>()
                        .Where(i => i.CollectionType is null || i.CollectionType == view.ViewType)
                        .Where(i => user.IsFolderGrouped(i.Id))
                        .SelectMany(i => GetTopParentIdsForQuery(i, user));
                }

                return [];
            }

            if (item is CollectionFolder collectionFolder)
            {
                return collectionFolder.PhysicalFolderIds;
            }

            var topParent = item.GetTopParent();
            if (topParent is not null)
            {
                return [topParent.Id];
            }

            return [];
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
        {
            var configuredContentType = GetConfiguredContentType(item, false);
            if (configuredContentType is not null)
            {
                return configuredContentType;
            }

            configuredContentType = GetConfiguredContentType(item, true);
            if (configuredContentType is not null)
            {
                return configuredContentType;
            }

            return GetInheritedContentType(item);
        }

        public CollectionType? GetInheritedContentType(BaseItem item)
        {
            var type = GetTopFolderContentType(item);

            if (type is not null)
            {
                return type;
            }

            return item.GetParents(this)
                .Select(GetConfiguredContentType)
                .LastOrDefault(i => i is not null);
        }

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

        private CollectionType? GetTopFolderContentType(BaseItem item)
            => _options.GetTopFolderContentType(item);

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

        public IEnumerable<BaseItem> FindExtras(BaseItem owner, IReadOnlyList<FileSystemMetadata> fileSystemChildren, IDirectoryService directoryService)
        {
            // Apply .ignore rules
            var filtered = fileSystemChildren.Where(c => !_dotIgnoreIgnoreRule.ShouldIgnore(c, owner)).ToList();
            var isFolder = owner.IsFolder || (owner is Video video && (video.VideoType == VideoType.BluRay || video.VideoType == VideoType.Dvd));
            var ownerVideoInfo = VideoResolver.Resolve(owner.Path, isFolder, _namingOptions, libraryRoot: owner.ContainingFolderPath);
            if (ownerVideoInfo is null)
            {
                yield break;
            }

            var count = filtered.Count;
            for (var i = 0; i < count; i++)
            {
                var current = filtered[i];
                if (current.IsDirectory && _namingOptions.AllExtrasTypesFolderNames.ContainsKey(current.Name))
                {
                    var filesInSubFolder = _fileSystem.GetFiles(current.FullName, null, false, false);
                    var filesInSubFolderList = filesInSubFolder.ToList();

                    bool subFolderIsMixedFolder = filesInSubFolderList.Count > 1;

                    foreach (var file in filesInSubFolderList)
                    {
                        if (!_extraResolver.TryGetExtraTypeForOwner(file.FullName, ownerVideoInfo, out var extraType))
                        {
                            continue;
                        }

                        var extra = GetExtra(file, extraType.Value, subFolderIsMixedFolder);
                        if (extra is not null)
                        {
                            yield return extra;
                        }
                    }
                }
                else if (!current.IsDirectory && _extraResolver.TryGetExtraTypeForOwner(current.FullName, ownerVideoInfo, out var extraType))
                {
                    var extra = GetExtra(current, extraType.Value, false);
                    if (extra is not null)
                    {
                        yield return extra;
                    }
                }
            }

            BaseItem? GetExtra(FileSystemMetadata file, ExtraType extraType, bool isInMixedFolder)
            {
                var extra = ResolvePath(_fileSystem.GetFileInfo(file.FullName), directoryService, _extraResolver.GetResolversForExtraType(extraType));
                if (extra is not Video && extra is not Audio)
                {
                    return null;
                }

                // Try to retrieve it from the db. If we don't find it, use the resolved version
                var itemById = GetItemById(extra.Id);
                if (itemById is not null)
                {
                    extra = itemById;
                }

                // Only update extra type if it is more specific then the currently known extra type
                if (extra.ExtraType is null or ExtraType.Unknown || extraType != ExtraType.Unknown)
                {
                    extra.ExtraType = extraType;
                }

                // Only return items that are actual extras (have ExtraType set)
                // Note: OwnerId and ParentId are set by RefreshExtras, not here,
                // so that RefreshExtras can detect when they need updating and set ForceSave.
                if (extra.ExtraType is not null)
                {
                    extra.IsInMixedFolder = isInMixedFolder;
                    return extra;
                }

                return null;
            }
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

        public QueryResult<BaseItem> GetPeopleItems(InternalPeopleQuery query)
        {
            var queryResult = _peopleRepository.GetPeople(query);
            var baseItems = queryResult.Items.Select(i =>
                {
                    try
                    {
                        return GetPerson(i.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "error retrieving BaseItem for person: {0}", i.Name);
                        return null;
                    }
                })
                .Where(i => i is not null)
                .Where(i => query.User is null || _itemAccessService.IsVisible(i!, query.User))
                .OfType<BaseItem>()
                .ToList()
                .AsReadOnly();

            return new QueryResult<BaseItem>
            {
                StartIndex = queryResult.StartIndex,
                TotalRecordCount = queryResult.TotalRecordCount,
                Items = baseItems,
            };
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

            SetTopParentOrAncestorIds(query);
            return _itemRepository.GetQueryFiltersLegacy(query);
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetMediaStreamLanguages(MediaStreamType mediaStreamType)
        {
            return _mediaStreamLanguageService.GetMediaStreamLanguages(mediaStreamType);
        }
    }
}
