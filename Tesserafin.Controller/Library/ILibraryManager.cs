#pragma warning disable CA1002, CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Resolvers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.Querying;
using Episode = Tesserafin.Controller.Entities.TV.Episode;
using Genre = Tesserafin.Controller.Entities.Genre;
using LinkedChildType = Tesserafin.Controller.Entities.LinkedChildType;
using Person = Tesserafin.Controller.Entities.Person;

namespace Tesserafin.Controller.Library
{
    /// <summary>
    /// Interface ILibraryManager.
    /// </summary>
    public interface ILibraryManager : IItemResolutionPolicy
    {
        /// <summary>Gets the shared application root.</summary>
        AggregateFolder RootFolder { get; }

        /// <summary>
        /// Resolves the path.
        /// </summary>
        /// <param name="fileInfo">The file information.</param>
        /// <param name="parent">The parent.</param>
        /// <param name="directoryService">An instance of <see cref="IDirectoryService"/>.</param>
        /// <param name="collectionType">The collection type of the library containing this item.</param>
        /// <returns>BaseItem.</returns>
        BaseItem? ResolvePath(
            FileSystemMetadata fileInfo,
            Folder? parent = null,
            IDirectoryService? directoryService = null,
            CollectionType? collectionType = null);

        /// <summary>
        /// Resolves a video file as an alternate version of a primary video, ensuring the result
        /// has the same concrete type as the primary (e.g. Movie instead of generic Video).
        /// Also cleans up any existing item with the wrong type from a previous scan.
        /// </summary>
        /// <param name="path">The file path of the alternate version.</param>
        /// <param name="expectedVideoType">The expected concrete type (same as the primary video).</param>
        /// <param name="parent">The parent folder.</param>
        /// <param name="collectionType">The collection type of the library.</param>
        /// <returns>A correctly-typed Video, or null if resolution fails.</returns>
        Video? ResolveAlternateVersion(
            string path,
            Type expectedVideoType,
            Folder? parent,
            CollectionType? collectionType);

        /// <summary>
        /// Finds the by path.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="isFolder"><c>true</c> is the path is a directory; otherwise <c>false</c>.</param>
        /// <returns>BaseItem.</returns>
        BaseItem? FindByPath(string path, bool? isFolder);

        /// <summary>
        /// Gets the item by id, as T, and validates user access.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <param name="userId">The user id to validate against. Empty validates nothing; an id that names no user sees nothing.</param>
        /// <typeparam name="T">The type of item.</typeparam>
        /// <returns>The item if found.</returns>
        public T? GetItemById<T>(Guid id, Guid userId)
            where T : BaseItem;

        /// <summary>
        /// Gets the item by id, as T, and validates user access.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <param name="user">The user to validate against.</param>
        /// <typeparam name="T">The type of item.</typeparam>
        /// <returns>The item if found.</returns>
        public T? GetItemById<T>(Guid id, User? user)
            where T : BaseItem;

        /// <summary>
        /// Gets the IDs of local alternate versions for a video.
        /// Local alternate versions are alternate quality versions at different file paths.
        /// </summary>
        /// <param name="video">The video item.</param>
        /// <returns>Enumerable of alternate version item IDs.</returns>
        IEnumerable<Guid> GetLocalAlternateVersionIds(Video video);

        /// <summary>
        /// Gets the linked alternate versions for a video.
        /// Linked alternate versions are different items representing the same content (e.g., Director's Cut).
        /// </summary>
        /// <param name="video">The video item.</param>
        /// <returns>Enumerable of linked Video items.</returns>
        IEnumerable<Video> GetLinkedAlternateVersions(Video video);

        /// <summary>
        /// Adds the parts.
        /// </summary>
        /// <param name="rules">The rules.</param>
        /// <param name="resolvers">The resolvers.</param>
        /// <param name="itemComparers">The item comparers.</param>
        void AddParts(
            IEnumerable<IResolverIgnoreRule> rules,
            IEnumerable<IItemResolver> resolvers,
            IEnumerable<IBaseItemComparer> itemComparers);

        /// <summary>
        /// Sorts the specified items.
        /// </summary>
        /// <param name="items">The items.</param>
        /// <param name="user">The user.</param>
        /// <param name="sortBy">The sort by.</param>
        /// <param name="sortOrder">The sort order.</param>
        /// <returns>IEnumerable{BaseItem}.</returns>
        [Obsolete("Use IItemSortService.Sort instead. See docs/major-rewrite-plan-v13.md § PR48/N.")]
        IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<ItemSortBy> sortBy, SortOrder sortOrder);

        [Obsolete("Use IItemSortService.Sort instead. See docs/major-rewrite-plan-v13.md § PR48/N.")]
        IEnumerable<BaseItem> Sort(IEnumerable<BaseItem> items, User? user, IEnumerable<(ItemSortBy OrderBy, SortOrder SortOrder)> orderBy);

        /// <summary>
        /// Gets the user root folder.
        /// </summary>
        /// <returns>UserRootFolder.</returns>
        Folder GetUserRootFolder();

        /// <summary>
        /// Retrieves the item.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>BaseItem.</returns>
        BaseItem RetrieveItem(Guid id);

        /// <summary>
        /// Finds the type of the collection.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>System.String.</returns>
        CollectionType? GetContentType(BaseItem item);

        /// <summary>
        /// Gets the type of the inherited content.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>System.String.</returns>
        CollectionType? GetInheritedContentType(BaseItem item);

        /// <summary>
        /// Gets the type of the configured content.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>System.String.</returns>
        CollectionType? GetConfiguredContentType(BaseItem item);

        /// <summary>
        /// Normalizes the root path list.
        /// </summary>
        /// <param name="paths">The paths.</param>
        /// <returns>IEnumerable{System.String}.</returns>
        List<FileSystemMetadata> NormalizeRootPathList(IEnumerable<FileSystemMetadata> paths);

        /// <summary>
        /// Registers the item.
        /// </summary>
        /// <param name="item">The item.</param>
        void RegisterItem(BaseItem item);

        /// <summary>
        /// Gets the named view.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="name">The name.</param>
        /// <param name="parentId">The parent identifier.</param>
        /// <param name="viewType">Type of the view.</param>
        /// <param name="sortName">Name of the sort.</param>
        /// <returns>The named view.</returns>
        UserView GetNamedView(
            User user,
            string name,
            Guid parentId,
            CollectionType? viewType,
            string sortName);

        /// <summary>
        /// Gets the named view.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="name">The name.</param>
        /// <param name="viewType">Type of the view.</param>
        /// <param name="sortName">Name of the sort.</param>
        /// <returns>The named view.</returns>
        UserView GetNamedView(
            User user,
            string name,
            CollectionType? viewType,
            string sortName);

        /// <summary>
        /// Gets the named view.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="viewType">Type of the view.</param>
        /// <param name="sortName">Name of the sort.</param>
        /// <returns>The named view.</returns>
        UserView GetNamedView(
            string name,
            CollectionType viewType,
            string sortName);

        /// <summary>
        /// Gets the named view.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="parentId">The parent identifier.</param>
        /// <param name="viewType">Type of the view.</param>
        /// <param name="sortName">Name of the sort.</param>
        /// <param name="uniqueId">The unique identifier.</param>
        /// <returns>The named view.</returns>
        UserView GetNamedView(
            string name,
            Guid parentId,
            CollectionType? viewType,
            string sortName,
            string uniqueId);

        /// <summary>
        /// Gets the shadow view.
        /// </summary>
        /// <param name="parent">The parent.</param>
        /// <param name="viewType">Type of the view.</param>
        /// <param name="sortName">Name of the sort.</param>
        /// <returns>The shadow view.</returns>
        UserView GetShadowView(
            BaseItem parent,
            CollectionType? viewType,
            string sortName);

        /// <summary>
        /// Gets the season number from path.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="parentId">The parent id.</param>
        /// <returns>System.Nullable&lt;System.Int32&gt;.</returns>
        int? GetSeasonNumberFromPath(string path, Guid? parentId);

        /// <summary>
        /// Fills the missing episode numbers from path.
        /// </summary>
        /// <param name="episode">Episode to use.</param>
        /// <param name="forceRefresh">Option to force refresh of episode numbers.</param>
        /// <returns>True if successful.</returns>
        bool FillMissingEpisodeNumbersFromPath(Episode episode, bool forceRefresh);

        /// <summary>
        /// Parses the name.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <returns>ItemInfo.</returns>
        ItemLookupInfo ParseName(string name);

        /// <summary>
        /// Gets the collection folders.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>The folders that contain the item.</returns>
        List<Folder> GetCollectionFolders(BaseItem item);

        /// <summary>
        /// Gets the collection folders.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="allUserRootChildren">The root folders to consider.</param>
        /// <returns>The folders that contain the item.</returns>
        List<Folder> GetCollectionFolders(BaseItem item, IEnumerable<Folder> allUserRootChildren);

        /// <summary>
        /// Gets the people.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>List&lt;PersonInfo&gt;.</returns>
        IReadOnlyList<PersonInfo> GetPeople(BaseItem item);

        /// <summary>
        /// Gets the people.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>List&lt;PersonInfo&gt;.</returns>
        IReadOnlyList<PersonInfo> GetPeople(InternalPeopleQuery query);

        /// <summary>
        /// Gets the item ids.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>List&lt;Guid&gt;.</returns>
        IReadOnlyList<Guid> GetItemIds(InternalItemsQuery query);

        /// <summary>
        /// Gets the people names.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>List&lt;System.String&gt;.</returns>
        IReadOnlyList<string> GetPeopleNames(InternalPeopleQuery query);

        /// <summary>
        /// Gets the distinct people names per item for multiple items.
        /// </summary>
        /// <param name="itemIds">The item IDs.</param>
        /// <param name="personTypes">The person types to include.</param>
        /// <returns>A dictionary mapping each item ID to its distinct people names. Items with no matching people are omitted.</returns>
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> GetPeopleNamesByItems(IReadOnlyList<Guid> itemIds, IReadOnlyList<string> personTypes);

        /// <summary>
        /// Queries the items.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>QueryResult&lt;BaseItem&gt;.</returns>
        QueryResult<BaseItem> QueryItems(InternalItemsQuery query);

        string GetPathAfterNetworkSubstitution(string path, BaseItem? ownerItem = null);

        /// <summary>
        /// Gets the items.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>QueryResult&lt;BaseItem&gt;.</returns>
        IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query);

        IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query, bool allowExternalContent);

        /// <summary>
        /// Gets the items.
        /// </summary>
        /// <param name="query">The query to use.</param>
        /// <param name="parents">Items to use for query.</param>
        /// <returns>List of items.</returns>
        IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query, List<BaseItem> parents);

        /// <summary>
        /// Gets the TVShow/Album items for Latest api.
        /// </summary>
        /// <param name="query">The query to use.</param>
        /// <param name="parents">Items to use for query.</param>
        /// <param name="collectionType">Collection Type.</param>
        /// <returns>List of items.</returns>
        IReadOnlyList<BaseItem> GetLatestItemList(InternalItemsQuery query, IReadOnlyList<BaseItem> parents, CollectionType collectionType);

        /// <summary>
        /// Gets the list of series presentation keys for next up.
        /// </summary>
        /// <param name="query">The query to use.</param>
        /// <param name="parents">Items to use for query.</param>
        /// <param name="dateCutoff">The minimum date for a series to have been most recently watched.</param>
        /// <returns>List of series presentation keys.</returns>
        IReadOnlyList<string> GetNextUpSeriesKeys(InternalItemsQuery query, IReadOnlyCollection<BaseItem> parents, DateTime dateCutoff);

        /// <summary>
        /// Gets next up episodes for multiple series in a single batched query.
        /// </summary>
        /// <param name="query">The query filter.</param>
        /// <param name="seriesKeys">The series presentation unique keys to query.</param>
        /// <param name="includeSpecials">Whether to include specials for aired episode order sorting.</param>
        /// <param name="includeWatchedForRewatching">Whether to include watched episodes for rewatching mode.</param>
        /// <returns>A dictionary mapping series key to batch result.</returns>
        IReadOnlyDictionary<string, Tesserafin.Controller.Persistence.NextUpEpisodeBatchResult> GetNextUpEpisodesBatch(
            InternalItemsQuery query,
            IReadOnlyList<string> seriesKeys,
            bool includeSpecials,
            bool includeWatchedForRewatching);

        /// <summary>
        /// Gets the items result.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>QueryResult&lt;BaseItem&gt;.</returns>
        QueryResult<BaseItem> GetItemsResult(InternalItemsQuery query);

        QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetGenres(InternalItemsQuery query);

        QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetMusicGenres(InternalItemsQuery query);

        QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetStudios(InternalItemsQuery query);

        QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetArtists(InternalItemsQuery query);

        IReadOnlyDictionary<string, MusicArtist[]> GetArtists(IReadOnlyList<string> names);

        QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAlbumArtists(InternalItemsQuery query);

        QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAllArtists(InternalItemsQuery query);

        int GetCount(InternalItemsQuery query);

        ItemCounts GetItemCounts(InternalItemsQuery query);

        /// <summary>
        /// Gets item counts for a "by-name" item using an optimized query path.
        /// </summary>
        /// <param name="kind">The kind of the name item.</param>
        /// <param name="id">The ID of the name item.</param>
        /// <param name="relatedItemKinds">The item kinds to count.</param>
        /// <param name="user">The user for access filtering.</param>
        /// <returns>The item counts grouped by type.</returns>
        ItemCounts GetItemCountsForNameItem(BaseItemKind kind, Guid id, BaseItemKind[] relatedItemKinds, User? user);

        /// <summary>
        /// Batch-fetches child counts for multiple parent folders.
        /// Returns the count of immediate children (non-recursive) for each parent.
        /// </summary>
        /// <param name="parentIds">The list of parent folder IDs.</param>
        /// <param name="userId">The user ID for access filtering.</param>
        /// <returns>Dictionary mapping parent ID to child count.</returns>
        Dictionary<Guid, int> GetChildCountBatch(IReadOnlyList<Guid> parentIds, Guid? userId);

        /// <summary>
        /// Batch-fetches played and total counts for multiple folder items.
        /// Avoids N+1 queries when building DTOs for lists of folder items.
        /// </summary>
        /// <param name="folderIds">The list of folder item IDs.</param>
        /// <param name="user">The user for access filtering and played status.</param>
        /// <returns>Dictionary mapping folder ID to (Played count, Total count).</returns>
        Dictionary<Guid, (int Played, int Total)> GetPlayedAndTotalCountBatch(IReadOnlyList<Guid> folderIds, User user);

        /// <summary>
        /// Configures the query with user access settings including TopParentIds for library access.
        /// Call this before passing a query to methods that need user access filtering.
        /// </summary>
        /// <param name="query">The query to configure.</param>
        /// <param name="user">The user to configure access for.</param>
        void ConfigureUserAccess(InternalItemsQuery query, User user);

        BaseItem GetParentItem(Guid? parentId, Guid? userId);

        /// <summary>
        /// Gets legacy query filters for filtering UI.
        /// </summary>
        /// <param name="query">The query filter.</param>
        /// <returns>Aggregated filter values.</returns>
        QueryFiltersLegacy GetQueryFiltersLegacy(InternalItemsQuery query);

        /// <summary>
        /// Gets a list of all language codes of the provided stream type.
        /// </summary>
        /// <param name="mediaStreamType">The stream type.</param>
        /// <returns>List of language codes.</returns>
        [Obsolete("Use IMediaStreamLanguageService.GetMediaStreamLanguages instead. See docs/major-rewrite-plan-v13.md § PR47/N.")]
        IReadOnlyList<string> GetMediaStreamLanguages(MediaStreamType mediaStreamType);
    }
}
