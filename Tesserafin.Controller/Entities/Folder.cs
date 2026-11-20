#nullable disable

#pragma warning disable CA1002, CA1721, CA1819, CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using J2N.Collections.Generic.Extensions;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Controller.Sorting;
using Tesserafin.Controller.TV;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Dto;
using Tesserafin.Model.IO;
using Tesserafin.Model.Querying;
using Episode = Tesserafin.Controller.Entities.TV.Episode;
using MusicAlbum = Tesserafin.Controller.Entities.Audio.MusicAlbum;
using Season = Tesserafin.Controller.Entities.TV.Season;
using Series = Tesserafin.Controller.Entities.TV.Series;

namespace Tesserafin.Controller.Entities
{
    /// <summary>
    /// Class Folder.
    /// </summary>
    public class Folder : BaseItem
    {
        private IEnumerable<BaseItem> _children;

        public Folder()
        {
            LinkedChildren = Array.Empty<LinkedChild>();
        }

        public static IUserViewManager UserViewManager { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance is root.
        /// </summary>
        /// <value><c>true</c> if this instance is root; otherwise, <c>false</c>.</value>
        public bool IsRoot { get; set; }

        /// <summary>
        /// Gets or sets the linked children.
        /// </summary>
        [JsonIgnore]
        public LinkedChild[] LinkedChildren { get; set; }

        [JsonIgnore]
        public DateTime? DateLastMediaAdded { get; set; }

        [JsonIgnore]
        public override bool SupportsThemeMedia => true;

        [JsonIgnore]
        public virtual bool IsPreSorted => false;

        [JsonIgnore]
        public virtual bool IsPhysicalRoot => false;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => true;

        [JsonIgnore]
        public override bool SupportsPlayedStatus => true;

        /// <summary>
        /// Gets a value indicating whether this instance is folder.
        /// </summary>
        /// <value><c>true</c> if this instance is folder; otherwise, <c>false</c>.</value>
        [JsonIgnore]
        public override bool IsFolder => true;

        [JsonIgnore]
        public override bool IsDisplayedAsFolder => true;

        [JsonIgnore]
        public virtual bool SupportsCumulativeRunTimeTicks => false;

        [JsonIgnore]
        public virtual bool SupportsDateLastMediaAdded => false;

        // Whether GetRawQueryItems + PostFilterAndSort (the base GetItemsInternal's own routing,
        // cf. major rewrite plan PR24) is a correct substitute for calling GetItemsInternal through
        // full virtual dispatch, for THIS type - regardless of the query shape. ItemQueryService
        // (Tesserafin.Server.Core) reads this before taking its fast path (PR25) rather than
        // enumerating known subtypes itself: entity-hierarchy knowledge stays on the hierarchy, not
        // leaked into the service. Overridden to false by every type whose GetItemsInternal diverges
        // from the base routing (PlaylistsFolder, UserView, UserRootFolder, Series, Season, Channel -
        // the 6 known overrides, cf. PR11). Some of them (PlaylistsFolder, Season) are only unsafe
        // when query.User is set and would in principle be fine for query.User is null - kept false
        // unconditionally anyway: conservative per-type is simpler and safer than a per-query check
        // duplicated on both sides of the fast-path decision.
        [JsonIgnore]
        public virtual bool SupportsRawQueryItems => true;

        [JsonIgnore]
        public override string FileNameWithoutExtension
        {
            get
            {
                if (IsFileProtocol)
                {
                    return System.IO.Path.GetFileName(Path);
                }

                return null;
            }
        }

        /// <summary>
        /// Gets or Sets the actual children.
        /// </summary>
        /// <value>The actual children.</value>
        [JsonIgnore]
        public virtual IEnumerable<BaseItem> Children
        {
            get => GetCatalogueChildren(ItemRepository, LibraryManager);
            set => _children = value;
        }

        /// <summary>
        /// Gets thread-safe access to all recursive children of this folder - without regard to user.
        /// </summary>
        /// <value>The recursive children.</value>
        [JsonIgnore]
        public IEnumerable<BaseItem> RecursiveChildren => GetRecursiveChildren();

        [JsonIgnore]
        protected virtual bool SupportsShortcutChildren => false;

        protected virtual bool FilterLinkedChildrenPerUser => false;

        [JsonIgnore]
        protected override bool SupportsOwnedItems => base.SupportsOwnedItems || SupportsShortcutChildren;

        [JsonIgnore]
        public virtual bool SupportsUserDataFromChildren
        {
            get
            {
                // These are just far too slow.
                if (this is ICollectionFolder)
                {
                    return false;
                }

                if (this is UserView)
                {
                    return false;
                }

                if (this is UserRootFolder)
                {
                    return false;
                }

                if (this is Channel)
                {
                    return false;
                }

                if (SourceType != SourceType.Library)
                {
                    return false;
                }

                if (this is IItemByName)
                {
                    if (this is not IHasDualAccess hasDualAccess || hasDualAccess.IsAccessedByName)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public static ICollectionManager CollectionManager { get; set; }

        public override bool CanDelete()
        {
            if (IsRoot)
            {
                return false;
            }

            return base.CanDelete();
        }

        public override bool RequiresRefresh()
        {
            var baseResult = base.RequiresRefresh();

            if (SupportsCumulativeRunTimeTicks && !RunTimeTicks.HasValue)
            {
                baseResult = true;
            }

            return baseResult;
        }

        /// <summary>
        /// Adds the child.
        /// </summary>
        /// <param name="writes">Explicit item write/resolution ownership.</param>
        /// <param name="item">The item.</param>
        /// <exception cref="InvalidOperationException">Unable to add  + item.Name.</exception>
        public void AddChild(IItemWriteCommands writes, BaseItem item)
        {
            item.SetParent(this);

            if (item.Id.IsEmpty())
            {
                item.Id = LibraryManager.GetNewItemId(item.Path, item.GetType());
            }

            if (item.DateCreated == DateTime.MinValue)
            {
                item.DateCreated = DateTime.UtcNow;
            }

            if (item.DateModified == DateTime.MinValue)
            {
                item.DateModified = DateTime.UtcNow;
            }

            writes.CreateItem(item, this);
        }

        public override bool IsVisible(User user, bool skipAllowedTagsCheck, ITagVisibilityService tagVisibility)
        {
            if (this is ICollectionFolder && this is not BasePluginFolder)
            {
                var blockedMediaFolders = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders);
                if (blockedMediaFolders.Length > 0)
                {
                    if (blockedMediaFolders.Contains(Id))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!user.HasPermission(PermissionKind.EnableAllFolders)
                        && !user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders).Contains(Id))
                    {
                        return false;
                    }
                }
            }

            return base.IsVisible(user, skipAllowedTagsCheck, tagVisibility);
        }

        /// <summary>Gets the direct catalogue children using the supplied repository and identity lookup.</summary>
        /// <param name="repository">The item repository.</param>
        /// <param name="lookup">The identity lookup for folders that cache child identities.</param>
        /// <returns>The folder's catalogue children.</returns>
        public virtual IEnumerable<BaseItem> GetCatalogueChildren(IItemRepository repository, IItemLookupService lookup)
            => _children ??= LoadChildren(repository, lookup);

        /// <summary>Invalidates the cached direct children.</summary>
        /// <param name="lookup">The identity lookup for folders that delegate to physical folders.</param>
        public virtual void InvalidateCatalogueChildren(IItemLookupService lookup)
            => Children = null;

        /// <summary>Loads the folder's direct children from the catalogue.</summary>
        /// <param name="repository">The item repository.</param>
        /// <param name="lookup">The identity lookup for folders that cache child identities.</param>
        /// <returns>The folder's persisted children.</returns>
        protected virtual IReadOnlyList<BaseItem> LoadChildren(IItemRepository repository, IItemLookupService lookup)
        {
            return repository.GetItemList(new InternalItemsQuery
            {
                Parent = this,
                GroupByPresentationUniqueKey = false,
                DtoOptions = new DtoOptions(true)
            });
        }

        /// <summary>
        /// Validates that the children of the folder still exist.
        /// </summary>
        /// <param name="context">The services used throughout the scan.</param>
        /// <param name="progress">The progress.</param>
        /// <param name="metadataRefreshOptions">The metadata refresh options.</param>
        /// <param name="recursive">if set to <c>true</c> [recursive].</param>
        /// <param name="allowRemoveRoot">remove item even this folder is root.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public Task ValidateChildren(LibraryScanContext context, IProgress<double> progress, MetadataRefreshOptions metadataRefreshOptions, bool recursive = true, bool allowRemoveRoot = false, CancellationToken cancellationToken = default)
        {
            InvalidateCatalogueChildren(context.Lookup);
            return ValidateChildrenInternal(context, progress, recursive, true, allowRemoveRoot, metadataRefreshOptions, metadataRefreshOptions.DirectoryService, cancellationToken);
        }

        /// <summary>
        /// Validates the children internal.
        /// </summary>
        /// <param name="context">The services used throughout the scan.</param>
        /// <param name="progress">The progress.</param>
        /// <param name="recursive">if set to <c>true</c> [recursive].</param>
        /// <param name="refreshChildMetadata">if set to <c>true</c> [refresh child metadata].</param>
        /// <param name="allowRemoveRoot">remove item even this folder is root.</param>
        /// <param name="refreshOptions">The refresh options.</param>
        /// <param name="directoryService">The directory service.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        protected virtual async Task ValidateChildrenInternal(LibraryScanContext context, IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            if (recursive)
            {
                context.RefreshProgress.OnRefreshStart(this);
            }

            try
            {
                var ancestorIds = new HashSet<Guid> { Id };
                foreach (var parent in GetParents(context.Lookup))
                {
                    if (!ancestorIds.Add(parent.Id))
                    {
                        throw new InvalidOperationException("Recursive datastructure detected abort processing this item.");
                    }
                }

                await ReconcileAndRefreshChildren(context, progress, recursive, refreshChildMetadata, allowRemoveRoot, refreshOptions, directoryService, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (recursive)
                {
                    context.RefreshProgress.OnRefreshComplete(this);
                }
            }
        }

        private async Task ReconcileAndRefreshChildren(LibraryScanContext context, IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var validChildren = new List<BaseItem>();
            var accessibleChildren = new List<BaseItem>();
            var validChildrenNeedGeneration = false;

            if (IsFileProtocol)
            {
                IReadOnlyList<BaseItem> nonCachedChildren;

                try
                {
                    nonCachedChildren = GetNonCachedChildren(context.Resolution, directoryService, cancellationToken).ToArray();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (IOException ex)
                {
                    Logger.LogError(ex, "Error retrieving children from file system");
                    return;
                }
                catch (SecurityException ex)
                {
                    Logger.LogError(ex, "Error retrieving children from file system");
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error retrieving children");
                    return;
                }

                progress.Report(ProgressHelpers.RetrievedChildren);

                if (recursive)
                {
                    context.RefreshProgress.OnRefreshProgress(this, ProgressHelpers.RetrievedChildren);
                }

                InvalidateCatalogueChildren(context.Lookup);
                var reconciliation = await context.Writes.ReconcileFolderChildrenAsync(this, GetCatalogueChildren(context.Repository, context.Lookup).ToArray(), nonCachedChildren, directoryService, allowRemoveRoot, context.Deletions, context.Images, cancellationToken).ConfigureAwait(false);
                if (reconciliation.Skipped)
                {
                    return;
                }

                validChildren = reconciliation.ValidChildren.ToList();
                accessibleChildren = reconciliation.AccessibleChildren.ToList();
            }
            else
            {
                validChildrenNeedGeneration = true;
            }

            progress.Report(ProgressHelpers.UpdatedChildItems);

            if (recursive)
            {
                context.RefreshProgress.OnRefreshProgress(this, ProgressHelpers.UpdatedChildItems);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (recursive)
            {
                var folder = this;
                var innerProgress = new Progress<double>(innerPercent =>
                {
                    var percent = ProgressHelpers.GetProgress(ProgressHelpers.UpdatedChildItems, ProgressHelpers.ScannedSubfolders, innerPercent);

                    progress.Report(percent);

                    context.RefreshProgress.OnRefreshProgress(folder, percent);
                });

                if (validChildrenNeedGeneration)
                {
                    validChildren = GetCatalogueChildren(context.Repository, context.Lookup).ToList();
                    validChildrenNeedGeneration = false;
                }

                await ValidateSubFolders(context, accessibleChildren.OfType<Folder>().ToList(), directoryService, innerProgress, cancellationToken).ConfigureAwait(false);
            }

            if (refreshChildMetadata)
            {
                progress.Report(ProgressHelpers.ScannedSubfolders);

                if (recursive)
                {
                    context.RefreshProgress.OnRefreshProgress(this, ProgressHelpers.ScannedSubfolders);
                }

                var container = this as IMetadataContainer;

                var folder = this;
                var innerProgress = new Progress<double>(innerPercent =>
                {
                    var percent = ProgressHelpers.GetProgress(ProgressHelpers.ScannedSubfolders, ProgressHelpers.RefreshedMetadata, innerPercent);

                    progress.Report(percent);

                    if (recursive)
                    {
                        context.RefreshProgress.OnRefreshProgress(folder, percent);
                    }
                });

                if (container is not null)
                {
                    await RefreshAllMetadataForContainer(context, container, refreshOptions, innerProgress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (validChildrenNeedGeneration)
                    {
                        InvalidateCatalogueChildren(context.Lookup);
                        validChildren = GetCatalogueChildren(context.Repository, context.Lookup).ToList();
                    }

                    await RefreshMetadataRecursive(context, accessibleChildren, refreshOptions, recursive, innerProgress, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task RefreshMetadataRecursive(LibraryScanContext context, IList<BaseItem> children, MetadataRefreshOptions refreshOptions, bool recursive, IProgress<double> progress, CancellationToken cancellationToken)
        {
            await context.Scheduler.Enqueue(
                children.ToArray(),
                (baseItem, innerProgress) => RefreshChildMetadata(context, baseItem, refreshOptions, recursive && baseItem.IsFolder, innerProgress, cancellationToken),
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task RefreshAllMetadataForContainer(LibraryScanContext context, IMetadataContainer container, MetadataRefreshOptions refreshOptions, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (container is Series series)
            {
                await series.RefreshMetadata(context.Metadata.Value, context.Deletions, context.Writes, refreshOptions, cancellationToken).ConfigureAwait(false);
            }

            await container.RefreshAllMetadata(context, refreshOptions, progress, cancellationToken).ConfigureAwait(false);
        }

        private async Task RefreshChildMetadata(LibraryScanContext context, BaseItem child, MetadataRefreshOptions refreshOptions, bool recursive, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (child is IMetadataContainer container)
            {
                await RefreshAllMetadataForContainer(context, container, refreshOptions, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (refreshOptions.RefreshItem(child))
                {
                    await child.RefreshMetadata(context.Metadata.Value, context.Deletions, context.Writes, refreshOptions, cancellationToken).ConfigureAwait(false);
                }

                if (recursive && child is Folder folder)
                {
                    folder.InvalidateCatalogueChildren(context.Lookup);
                    await folder.RefreshMetadataRecursive(context, folder.GetCatalogueChildren(context.Repository, context.Lookup).Except([this, child]).ToList(), refreshOptions, true, progress, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Refreshes the children.
        /// </summary>
        /// <param name="context">The services used throughout the scan.</param>
        /// <param name="children">The children.</param>
        /// <param name="directoryService">The directory service.</param>
        /// <param name="progress">The progress.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        private async Task ValidateSubFolders(LibraryScanContext context, IList<Folder> children, IDirectoryService directoryService, IProgress<double> progress, CancellationToken cancellationToken)
        {
            await context.Scheduler.Enqueue(
                children.ToArray(),
                (folder, innerProgress) => folder.ValidateChildrenInternal(context, innerProgress, true, false, false, null, directoryService, cancellationToken),
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Discovers children for catalogue reconciliation.</summary>
        /// <param name="resolution">The filesystem candidate resolver.</param>
        /// <param name="directoryService">Access to filesystem entries.</param>
        /// <param name="cancellationToken">Cancels discovery.</param>
        /// <returns>The children resolved from the current filesystem entries.</returns>
        protected virtual IEnumerable<BaseItem> GetNonCachedChildren(IItemResolutionService resolution, IDirectoryService directoryService, CancellationToken cancellationToken)
            => resolution.ResolveFolderChildren(this, GetFileSystemChildren(directoryService), directoryService, cancellationToken);

        public virtual int GetChildCount(User user)
        {
            if (LinkedChildren.Length > 0)
            {
                if (this is not ICollectionFolder)
                {
                    return GetChildren(user, true).Count;
                }
            }

            // GetChildCount isn't part of the GetItemsInternal/GetItems/GetItemList chain the
            // channelManager parameter was threaded through (out of scope: a much wider virtual
            // with many more overrides/callers) — falls back to the static here.
            // Documented legitimate caller of the obsolete 5-parameter overload — see
            // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
            var result = GetItems(
                new InternalItemsQuery(user)
                {
                    Recursive = false,
                    Limit = 0,
                    Parent = this,
                    DtoOptions = new DtoOptions(false)
                    {
                        EnableImages = false
                    }
                },
                ChannelManager,
                CollectionManager,
                UserViewManager,
                UserView.TVSeriesManager);
#pragma warning restore CS0618

            return result.TotalRecordCount;
        }

        public virtual int GetRecursiveChildCount(User user)
        {
            // Same scope boundary as GetChildCount above: falls back to the static.
            // Documented legitimate caller of the obsolete 5-parameter overload — see
            // docs/major-rewrite-plan-v13.md § PR28/N.
#pragma warning disable CS0618
            return GetItems(
                new InternalItemsQuery(user)
                {
                    Recursive = true,
                    IsFolder = false,
                    IsVirtualItem = false,
                    EnableTotalRecordCount = true,
                    Limit = 0,
                    DtoOptions = new DtoOptions(false)
                    {
                        EnableImages = false
                    }
                },
                ChannelManager,
                CollectionManager,
                UserViewManager,
                UserView.TVSeriesManager).TotalRecordCount;
#pragma warning restore CS0618
        }

        public QueryResult<BaseItem> QueryRecursive(InternalItemsQuery query)
        {
            if (!query.ForceDirect && CollapseBoxSetItems(query, this, query.User, ConfigurationManager))
            {
                query.CollapseBoxSetItems = true;
                SetCollapseBoxSetItemTypes(query);
            }

            if (this is not UserRootFolder
                && this is not AggregateFolder
                && query.ParentId.IsEmpty())
            {
                query.Parent = this;
            }

            // BoxSets and Playlists can have per-user visibility (shares/open access) that is stored in the
            // serialized item data and cannot be evaluated by the database query, so filter them in memory.
            if (query.IncludeItemTypes.Length > 0
                && query.IncludeItemTypes.All(t => t == BaseItemKind.BoxSet || t == BaseItemKind.Playlist))
            {
                return QueryWithPostFiltering(query);
            }

            // LibraryManager excludes the box sets and playlists the user is refused by id (GHSA-c5pr-6h39-jp86).
            return LibraryManager.GetItemsResult(query);
        }

        protected QueryResult<BaseItem> QueryWithPostFiltering(InternalItemsQuery query)
        {
            var startIndex = query.StartIndex;
            var limit = query.Limit;

            query.StartIndex = null;
            query.Limit = null;

            IEnumerable<BaseItem> itemsList = LibraryManager.GetItemList(query);
            var user = query.User;

            if (user is not null)
            {
                // needed for boxsets and playlists
                itemsList = itemsList.Where(i => i.IsVisibleStandalone(query.User));
            }

            IEnumerable<BaseItem> returnItems;
            int totalCount = 0;

            if (query.EnableTotalRecordCount)
            {
                var itemArray = itemsList.ToArray();
                totalCount = itemArray.Length;
                returnItems = itemArray;
            }
            else
            {
                returnItems = itemsList;
            }

            if (limit.HasValue)
            {
                returnItems = returnItems.Skip(startIndex ?? 0).Take(limit.Value);
            }
            else if (startIndex.HasValue)
            {
                returnItems = returnItems.Skip(startIndex.Value);
            }

            return new QueryResult<BaseItem>(
                query.StartIndex,
                totalCount,
                returnItems.ToArray());
        }

        private static BaseItem[] SortItemsByRequest(InternalItemsQuery query, IReadOnlyList<BaseItem> items)
        {
            return items.OrderBy(i => Array.IndexOf(query.ItemIds, i.Id)).ToArray();
        }

        // PR28/N (major-rewrite-plan-v13.md): the 5-parameter signature was an intermediate
        // migration step (GetItemsInternal cluster, PR11/PR12), not a stable API. Marked obsolete
        // (warning) so no new application consumer picks it up; the remaining documented callers
        // (internal fallback sites here and in UserView, UserViewBuilder's relay, ItemQueryService's
        // own fallback path, UserViewManager's self-reference) suppress CS0618 locally with
        // a pointer back to that doc section.
        [Obsolete("Application code should use IItemQueryService instead of calling Folder.GetItems/GetItemList directly. See docs/major-rewrite-plan-v13.md § PR28/N.")]
        public QueryResult<BaseItem> GetItems(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            return GetItemsCore(query, q => GetItemsInternal(q, channelManager, collectionManager, userViewManager, tvSeriesManager));
        }

        public QueryResult<BaseItem> GetItems(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager, IItemSortService itemSortService)
        {
            return GetItemsCore(query, q => GetItemsInternal(q, channelManager, collectionManager, userViewManager, tvSeriesManager, itemSortService));
        }

        // PR28/N: same deprecation rationale as GetItems above.
        [Obsolete("Application code should use IItemQueryService instead of calling Folder.GetItems/GetItemList directly. See docs/major-rewrite-plan-v13.md § PR28/N.")]
        public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            return GetItemListCore(query, q => GetItemsInternal(q, channelManager, collectionManager, userViewManager, tvSeriesManager));
        }

        public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager, IItemSortService itemSortService)
        {
            return GetItemListCore(query, q => GetItemsInternal(q, channelManager, collectionManager, userViewManager, tvSeriesManager, itemSortService));
        }

        private QueryResult<BaseItem> GetItemsCore(InternalItemsQuery query, Func<InternalItemsQuery, QueryResult<BaseItem>> getItemsInternal)
        {
            if (query.ItemIds.Length > 0)
            {
                var result = LibraryManager.GetItemsResult(query);

                if (query.OrderBy.Count == 0 && query.ItemIds.Length > 1)
                {
                    result.Items = SortItemsByRequest(query, result.Items);
                }

                return result;
            }

            return getItemsInternal(query);
        }

        private IReadOnlyList<BaseItem> GetItemListCore(InternalItemsQuery query, Func<InternalItemsQuery, QueryResult<BaseItem>> getItemsInternal)
        {
            query.EnableTotalRecordCount = false;

            if (query.ItemIds.Length > 0)
            {
                var result = LibraryManager.GetItemList(query);

                if (query.OrderBy.Count == 0 && query.ItemIds.Length > 1)
                {
                    return SortItemsByRequest(query, result);
                }

                return result;
            }

            return getItemsInternal(query).Items;
        }

        // channelManager is needed only when this folder's SourceType is SourceType.Channel;
        // collectionManager only for PostFilterAndSort's BoxSet collapsing; userViewManager and
        // tvSeriesManager only by UserView's override. Threaded as parameters (replacing the
        // removed BaseItem statics) so the dependencies are explicit — most overrides never use
        // most of them.
        protected virtual QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            return GetItemsInternalCore(
                query,
                channelManager,
                collectionManager,
                () => GetRawQueryItems(query),
                PostFilterAndSort);
        }

        protected virtual QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager, IItemSortService itemSortService)
        {
            return GetItemsInternalCore(
                query,
                channelManager,
                collectionManager,
                () => GetRawQueryItems(query),
                (items, q, cm) => PostFilterAndSort(items, q, cm, itemSortService));
        }

        private QueryResult<BaseItem> GetItemsInternalCore(
            InternalItemsQuery query,
            IChannelManager channelManager,
            ICollectionManager collectionManager,
            Func<IEnumerable<BaseItem>> getRawQueryItems,
            Func<IEnumerable<BaseItem>, InternalItemsQuery, ICollectionManager, QueryResult<BaseItem>> postFilterAndSort)
        {
            if (SourceType == SourceType.Channel)
            {
                try
                {
                    query.Parent = this;
                    query.ChannelIds = new[] { ChannelId };

                    return channelManager.GetChannelItemsInternal(query, new Progress<double>(), CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    // Already logged at lower levels
                    return new QueryResult<BaseItem>();
                }
            }

            if (query.Recursive)
            {
                return QueryRecursive(query);
            }

            return postFilterAndSort(getRawQueryItems(), query, collectionManager);
        }

        // Raw filtered children before collapse/sort/pagination (PostFilterAndSort) - the block
        // GetItemsInternal used to inline directly. Stays on Folder (depends on this folder's own
        // children/overrides/hierarchy, per major rewrite plan PR24) rather than moving into
        // ItemQueryService. Public + virtual: overrides that build items differently (Season with a
        // user set, UserView's wholesale delegation to UserViewBuilder.GetUserItems, etc.) don't
        // route through this yet - they keep their own GetItemsInternal override untouched. Wiring
        // ItemQueryService.GetItems to call this directly (skipping GetItemsInternal's dispatch) is
        // deliberately deferred to a later PR: it first needs a way to know, per folder, whether
        // this base implementation is actually correct for it - that predicate belongs on the Folder
        // hierarchy, not as a type-check in the service.
        public virtual IEnumerable<BaseItem> GetRawQueryItems(InternalItemsQuery query)
        {
            return GetRawQueryItems(query, (user, includeLinkedChildren, childQuery) => GetChildren(user, includeLinkedChildren, out _, childQuery));
        }

        protected IEnumerable<BaseItem> GetRawQueryItems(
            InternalItemsQuery query,
            Func<User, bool, InternalItemsQuery, IReadOnlyList<BaseItem>> getChildren)
        {
            var user = query.User;

            if (user is null)
            {
                return UserViewBuilder.Filter(Children, user, query, UserDataManager, LibraryManager);
            }

            // need to pass this param to the children.
            // Note: Don't pass Limit/StartIndex here as pagination should happen after sorting in PostFilterAndSort
            var childQuery = new InternalItemsQuery
            {
                DisplayAlbumFolders = query.DisplayAlbumFolders,
                NameStartsWith = query.NameStartsWith,
                NameStartsWithOrGreater = query.NameStartsWithOrGreater,
                NameLessThan = query.NameLessThan
            };

            return UserViewBuilder.Filter(
                getChildren(user, true, childQuery),
                user,
                query,
                UserDataManager,
                LibraryManager);
        }

        protected QueryResult<BaseItem> PostFilterAndSort(IEnumerable<BaseItem> items, InternalItemsQuery query, ICollectionManager collectionManager)
        {
            return PostFilterAndSortCore(items, query, collectionManager, filteredItems =>
            {
#pragma warning disable CS0618
                return UserViewBuilder.SortAndPage(filteredItems, null, query, LibraryManager);
#pragma warning restore CS0618
            });
        }

        protected QueryResult<BaseItem> PostFilterAndSort(IEnumerable<BaseItem> items, InternalItemsQuery query, ICollectionManager collectionManager, IItemSortService itemSortService)
        {
            return PostFilterAndSortCore(
                items,
                query,
                collectionManager,
                filteredItems => UserViewBuilder.SortAndPage(filteredItems, null, query, itemSortService));
        }

        private QueryResult<BaseItem> PostFilterAndSortCore(
            IEnumerable<BaseItem> items,
            InternalItemsQuery query,
            ICollectionManager collectionManager,
            Func<IReadOnlyList<BaseItem>, QueryResult<BaseItem>> sortAndPage)
        {
            var user = query.User;

            if (user is not null)
            {
                items = CollapseBoxSetItemsIfNeeded(items, query, this, user, ConfigurationManager, collectionManager);

                // After collapse, BoxSets may have replaced items whose names matched the filter
                // but the BoxSet's own name may not match. Re-apply name filtering so BoxSets
                // appear under the correct letter (e.g. "Jump Street" under J, not under #).
                items = ApplyNameFilter(items, query);
            }

            var filteredItems = items as IReadOnlyList<BaseItem> ?? items.ToList();
            var result = sortAndPage(filteredItems);

            if (query.EnableTotalRecordCount)
            {
                result.TotalRecordCount = filteredItems.Count;
            }

            return result;
        }

        private static IEnumerable<BaseItem> ApplyNameFilter(IEnumerable<BaseItem> items, InternalItemsQuery query)
        {
            if (!string.IsNullOrWhiteSpace(query.NameStartsWith))
            {
                items = items.Where(i => i.SortName.StartsWith(query.NameStartsWith, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query.NameStartsWithOrGreater))
            {
                items = items.Where(i => string.Compare(i.SortName, query.NameStartsWithOrGreater, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (!string.IsNullOrWhiteSpace(query.NameLessThan))
            {
                items = items.Where(i => string.Compare(i.SortName, query.NameLessThan, StringComparison.OrdinalIgnoreCase) < 0);
            }

            return items;
        }

        private static IEnumerable<BaseItem> CollapseBoxSetItemsIfNeeded(
            IEnumerable<BaseItem> items,
            InternalItemsQuery query,
            BaseItem queryParent,
            User user,
            IServerConfigurationManager configurationManager,
            ICollectionManager collectionManager)
        {
            ArgumentNullException.ThrowIfNull(items);

            if (!CollapseBoxSetItems(query, queryParent, user, configurationManager))
            {
                return items;
            }

            var config = configurationManager.Configuration;

            bool collapseMovies = config.EnableGroupingMoviesIntoCollections;
            bool collapseSeries = config.EnableGroupingShowsIntoCollections;

            if (user is null || (collapseMovies && collapseSeries))
            {
                return collectionManager.CollapseItemsWithinBoxSets(items, user);
            }

            if (!collapseMovies && !collapseSeries)
            {
                return items;
            }

            var collapsibleItems = new List<BaseItem>();
            var remainingItems = new List<BaseItem>();

            foreach (var item in items)
            {
                if ((collapseMovies && item is Movie) || (collapseSeries && item is Series))
                {
                    collapsibleItems.Add(item);
                }
                else
                {
                    remainingItems.Add(item);
                }
            }

            if (collapsibleItems.Count == 0)
            {
                return remainingItems;
            }

            var collapsedItems = collectionManager.CollapseItemsWithinBoxSets(collapsibleItems, user);

            return collapsedItems.Concat(remainingItems);
        }

        private static bool CollapseBoxSetItems(
            InternalItemsQuery query,
            BaseItem queryParent,
            User user,
            IServerConfigurationManager configurationManager)
        {
            // Could end up stuck in a loop like this
            if (queryParent is BoxSet)
            {
                return false;
            }

            if (queryParent is Season)
            {
                return false;
            }

            if (queryParent is MusicAlbum)
            {
                return false;
            }

            if (queryParent is MusicArtist)
            {
                return false;
            }

            var param = query.CollapseBoxSetItems;
            if (param.HasValue)
            {
                return param.Value && AllowBoxSetCollapsing(query);
            }

            var config = configurationManager.Configuration;

            bool queryHasMovies = query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Contains(BaseItemKind.Movie);
            bool queryHasSeries = query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Contains(BaseItemKind.Series);

            bool collapseMovies = config.EnableGroupingMoviesIntoCollections;
            bool collapseSeries = config.EnableGroupingShowsIntoCollections;

            if (user is not null)
            {
                bool canCollapse = (queryHasMovies && collapseMovies) || (queryHasSeries && collapseSeries);
                return canCollapse && AllowBoxSetCollapsing(query);
            }

            return (queryHasMovies || queryHasSeries) && AllowBoxSetCollapsing(query);
        }

        private void SetCollapseBoxSetItemTypes(InternalItemsQuery query)
        {
            var config = ConfigurationManager.Configuration;
            bool collapseMovies = config.EnableGroupingMoviesIntoCollections;
            bool collapseSeries = config.EnableGroupingShowsIntoCollections;

            if (collapseMovies && collapseSeries)
            {
                // Empty means collapse all types
                query.CollapseBoxSetItemTypes = [];
                return;
            }

            var types = new List<BaseItemKind>();
            if (collapseMovies)
            {
                types.Add(BaseItemKind.Movie);
            }

            if (collapseSeries)
            {
                types.Add(BaseItemKind.Series);
            }

            query.CollapseBoxSetItemTypes = types.ToArray();
        }

        private static bool AllowBoxSetCollapsing(InternalItemsQuery request)
        {
            if (request.IsFavorite.HasValue)
            {
                return false;
            }

            if (request.IsFavoriteOrLiked.HasValue)
            {
                return false;
            }

            if (request.IsLiked.HasValue)
            {
                return false;
            }

            if (request.IsPlayed.HasValue)
            {
                return false;
            }

            if (request.IsResumable.HasValue)
            {
                return false;
            }

            if (request.IsFolder.HasValue)
            {
                return false;
            }

            if (request.Genres.Count > 0)
            {
                return false;
            }

            if (request.GenreIds.Count > 0)
            {
                return false;
            }

            if (request.HasImdbId.HasValue)
            {
                return false;
            }

            if (request.HasOfficialRating.HasValue)
            {
                return false;
            }

            if (request.HasOverview.HasValue)
            {
                return false;
            }

            if (request.HasParentalRating.HasValue)
            {
                return false;
            }

            if (request.HasSpecialFeature.HasValue)
            {
                return false;
            }

            if (request.HasSubtitles.HasValue)
            {
                return false;
            }

            if (request.HasThemeSong.HasValue)
            {
                return false;
            }

            if (request.HasThemeVideo.HasValue)
            {
                return false;
            }

            if (request.HasTmdbId.HasValue)
            {
                return false;
            }

            if (request.HasTrailer.HasValue)
            {
                return false;
            }

            if (request.ImageTypes.Length > 0)
            {
                return false;
            }

            if (request.Is3D.HasValue)
            {
                return false;
            }

            if (request.Is4K.HasValue)
            {
                return false;
            }

            if (request.IsHD.HasValue)
            {
                return false;
            }

            if (request.IsLocked.HasValue)
            {
                return false;
            }

            if (request.IsPlaceHolder.HasValue)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(request.Person))
            {
                return false;
            }

            if (request.PersonIds.Length > 0)
            {
                return false;
            }

            if (request.ItemIds.Length > 0)
            {
                return false;
            }

            if (request.StudioIds.Length > 0)
            {
                return false;
            }

            if (request.VideoTypes.Length > 0)
            {
                return false;
            }

            if (request.Years.Length > 0)
            {
                return false;
            }

            if (request.Tags.Length > 0)
            {
                return false;
            }

            if (request.OfficialRatings.Length > 0)
            {
                return false;
            }

            if (request.MinIndexNumber.HasValue)
            {
                return false;
            }

            if (request.OrderBy.Any(o =>
                o.OrderBy == ItemSortBy.CommunityRating ||
                o.OrderBy == ItemSortBy.CriticRating ||
                o.OrderBy == ItemSortBy.Runtime))
            {
                return false;
            }

            return true;
        }

        public virtual IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, out int totalItemCount, InternalItemsQuery query = null)
        {
            ArgumentNullException.ThrowIfNull(user);
            query ??= new InternalItemsQuery();
            query.User = user;

            // the true root should return our users root folder children
            if (IsPhysicalRoot)
            {
                return LibraryManager.GetUserRootFolder().GetChildren(user, includeLinkedChildren, out totalItemCount);
            }

            var result = new Dictionary<Guid, BaseItem>();

            totalItemCount = AddChildren(user, includeLinkedChildren, result, false, query);

            return result.Values.ToArray();
        }

        public virtual IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, out int totalItemCount, InternalItemsQuery query, IItemSortService itemSortService)
        {
            return GetChildren(user, includeLinkedChildren, out totalItemCount, query);
        }

        public virtual IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query = null)
        {
            return GetChildren(user, includeLinkedChildren, out _, query);
        }

        public virtual IReadOnlyList<BaseItem> GetChildren(User user, bool includeLinkedChildren, InternalItemsQuery query, IItemSortService itemSortService)
        {
            return GetChildren(user, includeLinkedChildren, query);
        }

        protected virtual IEnumerable<BaseItem> GetEligibleChildrenForRecursiveChildren(User user)
        {
            return Children;
        }

        /// <summary>
        /// Adds the children to list.
        /// </summary>
        private int AddChildren(User user, bool includeLinkedChildren, Dictionary<Guid, BaseItem> result, bool recursive, InternalItemsQuery query, HashSet<Folder> visitedFolders = null)
        {
            // Prevent infinite recursion of nested folders
            visitedFolders ??= new HashSet<Folder>();
            if (!visitedFolders.Add(this))
            {
                return 0;
            }

            // If Query.AlbumFolders is set, then enforce the format as per the db in that it permits sub-folders in music albums.
            IEnumerable<BaseItem> children = null;
            if ((query?.DisplayAlbumFolders ?? false) && (this is MusicAlbum))
            {
                children = Children;
                query = null;
            }

            // If there are not sub-folders, proceed as normal.
            if (children is null)
            {
                children = GetEligibleChildrenForRecursiveChildren(user);
            }

            if (includeLinkedChildren)
            {
                children = children.Concat(GetLinkedChildren(user)).ToArray();
            }

            return AddChildrenFromCollection(children, user, includeLinkedChildren, result, recursive, query, visitedFolders);
        }

        private int AddChildrenFromCollection(IEnumerable<BaseItem> children, User user, bool includeLinkedChildren, Dictionary<Guid, BaseItem> result, bool recursive, InternalItemsQuery query, HashSet<Folder> visitedFolders)
        {
            query ??= new InternalItemsQuery();
            var limit = query.Limit > 0 ? query.Limit : int.MaxValue;
            query.Limit = 0;

            var visibleChildren = children
                .Where(e => e.IsVisible(user))
                .ToArray();

            var realChildren = UserViewBuilder.Filter(visibleChildren, query.User, query, UserDataManager, LibraryManager)
                .ToArray();

            var childCount = realChildren.Length;
            if (result.Count < limit)
            {
                var remainingCount = (int)(limit - result.Count);
                foreach (var child in realChildren
                    .Skip(query.StartIndex ?? 0)
                    .Take(remainingCount))
                {
                    result[child.Id] = child;
                }
            }

            if (recursive)
            {
                foreach (var child in visibleChildren
                    .Where(e => e.IsFolder)
                    .OfType<Folder>())
                {
                    childCount += child.AddChildren(user, includeLinkedChildren, result, true, query, visitedFolders);
                }
            }

            return childCount;
        }

        public virtual IReadOnlyList<BaseItem> GetRecursiveChildren(User user, InternalItemsQuery query, out int totalCount)
        {
            ArgumentNullException.ThrowIfNull(user);

            var result = new Dictionary<Guid, BaseItem>();

            totalCount = AddChildren(user, true, result, true, query);

            return result.Values.ToArray();
        }

        public virtual IReadOnlyList<BaseItem> GetRecursiveChildren(User user, InternalItemsQuery query, out int totalCount, IItemSortService itemSortService)
        {
            return GetRecursiveChildren(user, query, out totalCount);
        }

        /// <summary>
        /// Gets the recursive children.
        /// </summary>
        /// <returns>IList{BaseItem}.</returns>
        public IReadOnlyList<BaseItem> GetRecursiveChildren()
        {
            return GetRecursiveChildren(true);
        }

        public IReadOnlyList<BaseItem> GetRecursiveChildren(bool includeLinkedChildren)
        {
            return GetRecursiveChildren(i => true, includeLinkedChildren);
        }

        public IReadOnlyList<BaseItem> GetRecursiveChildren(Func<BaseItem, bool> filter)
        {
            return GetRecursiveChildren(filter, true);
        }

        public IReadOnlyList<BaseItem> GetRecursiveChildren(Func<BaseItem, bool> filter, bool includeLinkedChildren)
            => ReadRecursiveChildren(filter, static folder => folder.Children, includeLinkedChildren ? static folder => folder.GetLinkedChildren() : null);

        /// <summary>Reads the catalogue descendants and optionally the root folder's saved links.</summary>
        /// <param name="repository">The item repository.</param>
        /// <param name="lookup">The item identity lookup.</param>
        /// <param name="links">The saved link resolver, or null to read only physical descendants.</param>
        /// <param name="filter">An optional filter for items returned by the traversal.</param>
        /// <returns>The distinct items in catalogue traversal order.</returns>
        public IReadOnlyList<BaseItem> GetRecursiveCatalogueChildren(
            IItemRepository repository,
            IItemLookupService lookup,
            LinkedChildResolver links = null,
            Func<BaseItem, bool> filter = null)
            => ReadRecursiveChildren(
                filter,
                folder => folder.GetCatalogueChildren(repository, lookup),
                links is null ? null : links.GetItems);

        private IReadOnlyList<BaseItem> ReadRecursiveChildren(
            Func<BaseItem, bool> filter,
            Func<Folder, IEnumerable<BaseItem>> readChildren,
            Func<Folder, IEnumerable<BaseItem>> readRootLinks)
        {
            var result = new Dictionary<Guid, BaseItem>();

            AddPhysicalChildren(this);
            if (readRootLinks is not null)
            {
                // Only follow links from the starting folder, where they contribute items but no new branches.
                foreach (var child in readRootLinks(this))
                {
                    AddMatching(child);
                }
            }

            return result.Values.ToArray();

            void AddPhysicalChildren(Folder folder)
            {
                foreach (var child in readChildren(folder))
                {
                    AddMatching(child);
                    if (child.IsFolder)
                    {
                        AddPhysicalChildren((Folder)child);
                    }
                }
            }

            void AddMatching(BaseItem child)
            {
                if (filter is null || filter(child))
                {
                    result[child.Id] = child;
                }
            }
        }

        /// <summary>
        /// Gets the linked children.
        /// </summary>
        /// <returns>IEnumerable{BaseItem}.</returns>
        public List<BaseItem> GetLinkedChildren()
            => CreateLinkedChildResolver().Resolve(this).Select(entry => entry.Item).ToList();

        protected static LinkedChildResolver CreateLinkedChildResolver(IBoxSetVisibilityService scope = null)
        {
            return new LinkedChildResolver(
                scope is null ? query => LibraryManager.GetItemList(query) : null,
                scope is null ? id => LibraryManager.GetItemById(id) : scope.GetItemById,
                scope is ILinkedChildVisibilityService paths
                    ? paths.FindByPath
                    : path => LibraryManager.GetItemList(ItemPathQuery.Create(path, null)).FirstOrDefault(),
                FileSystem,
                Logger);
        }

        public List<BaseItem> GetLinkedChildren(User user)
        {
            if (!FilterLinkedChildrenPerUser || user is null)
            {
                return GetLinkedChildren();
            }

            var linkedChildren = LinkedChildren;
            var list = new List<BaseItem>(linkedChildren.Length);

            if (linkedChildren.Length == 0)
            {
                return list;
            }

            var allUserRootChildren = LibraryManager.GetUserRootFolder()
                .GetChildren(user, true)
                .OfType<Folder>()
                .ToList();

            var collectionFolderIds = allUserRootChildren
                .Select(i => i.Id)
                .ToList();

            var resolver = CreateLinkedChildResolver();
            foreach (var i in linkedChildren)
            {
                var child = resolver.ResolveSingle(this, i);

                if (child is null)
                {
                    continue;
                }

                var childOwner = child.GetOwner() ?? child;

                if (child is not IItemByName)
                {
                    var childProtocol = childOwner.PathProtocol;
                    if (!childProtocol.HasValue || childProtocol.Value != Tesserafin.Model.MediaInfo.MediaProtocol.File)
                    {
                        if (!childOwner.IsVisibleStandalone(user))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        var itemCollectionFolderIds =
                            LibraryManager.GetCollectionFolders(childOwner, allUserRootChildren).Select(f => f.Id);

                        if (!itemCollectionFolderIds.Any(collectionFolderIds.Contains))
                        {
                            continue;
                        }
                    }
                }

                list.Add(child);
            }

            return list;
        }

        protected override async Task<bool> RefreshedOwnedItems(IItemMetadataDispatcher metadata, IItemDeletionCommands deletions, IItemWriteCommands writes, MetadataRefreshOptions options, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken)
        {
            var changesFound = false;

            if (IsFileProtocol)
            {
                if (await RefreshLinkedChildrenAsync(writes, fileSystemChildren, cancellationToken).ConfigureAwait(false))
                {
                    changesFound = true;
                }
            }

            var baseHasChanges = await base.RefreshedOwnedItems(metadata, deletions, writes, options, fileSystemChildren, cancellationToken).ConfigureAwait(false);

            return baseHasChanges || changesFound;
        }

        /// <summary>Refreshes links through their explicit write owner.</summary>
        /// <param name="writes">The item write owner.</param>
        /// <param name="fileSystemChildren">Completed filesystem discovery.</param>
        /// <param name="cancellationToken">The refresh cancellation token.</param>
        /// <returns>Whether linked children changed.</returns>
        protected virtual Task<bool> RefreshLinkedChildrenAsync(IItemWriteCommands writes, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken)
            => SupportsShortcutChildren
                ? writes.RefreshShortcutLinksAsync(this, fileSystemChildren, cancellationToken)
                : Task.FromResult(false);

        public override bool IsPlayed(User user, UserItemData userItemData)
        {
            return ItemRepository.GetIsPlayed(user, Id, true);
        }

        public override bool IsUnplayed(User user, UserItemData userItemData)
        {
            return !IsPlayed(user, userItemData);
        }

        public override void FillUserDataDtoValues(
            UserItemDataDto dto,
            UserItemData userData,
            BaseItemDto itemDto,
            User user,
            DtoOptions fields,
            IItemCountService itemCountService,
            (int Played, int Total)? precomputedCounts = null)
        {
            if (!SupportsUserDataFromChildren)
            {
                return;
            }

            if (SupportsPlayedStatus || (itemDto is not null && fields.ContainsField(ItemFields.RecursiveItemCount)))
            {
                int playedCount;
                int totalCount;

                if (precomputedCounts.HasValue)
                {
                    // Use batch-fetched counts (avoids N+1 queries)
                    (playedCount, totalCount) = precomputedCounts.Value;
                }
                else
                {
                    // Fall back to per-item query when no batch data is available
                    var query = new InternalItemsQuery(user);

                    if (LinkedChildren.Length > 0)
                    {
                        (playedCount, totalCount) = itemCountService.GetPlayedAndTotalCountFromLinkedChildren(query, Id);
                    }
                    else
                    {
                        (playedCount, totalCount) = itemCountService.GetPlayedAndTotalCount(query, Id);
                    }
                }

                if (itemDto is not null && fields.ContainsField(ItemFields.RecursiveItemCount))
                {
                    itemDto.RecursiveItemCount = totalCount;
                }

                if (SupportsPlayedStatus)
                {
                    var unplayedCount = totalCount - playedCount;
                    dto.UnplayedItemCount = unplayedCount;

                    if (totalCount > 0)
                    {
                        dto.PlayedPercentage = playedCount / (double)totalCount * 100;
                        dto.Played = playedCount >= totalCount;
                    }
                    else
                    {
                        dto.Played = true;
                    }
                }
            }
        }

        /// <summary>
        /// Contains constants used when reporting scan progress.
        /// </summary>
        private static class ProgressHelpers
        {
            /// <summary>
            /// Reported after the folders immediate children are retrieved.
            /// </summary>
            public const int RetrievedChildren = 5;

            /// <summary>
            /// Reported after add, updating, or deleting child items from the LibraryManager.
            /// </summary>
            public const int UpdatedChildItems = 10;

            /// <summary>
            /// Reported once subfolders are scanned.
            /// When scanning subfolders, the progress will be between [UpdatedItems, ScannedSubfolders].
            /// </summary>
            public const int ScannedSubfolders = 50;

            /// <summary>
            /// Reported once metadata is refreshed.
            /// When refreshing metadata, the progress will be between [ScannedSubfolders, MetadataRefreshed].
            /// </summary>
            public const int RefreshedMetadata = 100;

            /// <summary>
            /// Gets the current progress given the previous step, next step, and progress in between.
            /// </summary>
            /// <param name="previousProgressStep">The previous progress step.</param>
            /// <param name="nextProgressStep">The next progress step.</param>
            /// <param name="currentProgress">The current progress step.</param>
            /// <returns>The progress.</returns>
            public static double GetProgress(int previousProgressStep, int nextProgressStep, double currentProgress)
            {
                return previousProgressStep + ((nextProgressStep - previousProgressStep) * (currentProgress / 100));
            }
        }
    }
}
