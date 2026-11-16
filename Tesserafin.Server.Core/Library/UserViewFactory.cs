#pragma warning disable CS1591

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="IUserViewFactory"/> - named/shadow user-view
    /// creation, ported off <c>LibraryManager.cs:2756-2980</c> with behavior preserved exactly (RFC
    /// <c>docs/rfc-di-query-user-views-v2.md</c> §2, §8, §9, PR106b).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Depends only on <see cref="IItemLookupService"/> (item lookup), <see cref="IItemStore"/>
    /// (id generation plus save+register, PR106a), <see cref="IServerConfigurationManager"/>,
    /// <see cref="IFileSystem"/> and <see cref="Lazy{IProviderManager}"/>. None of these concrete
    /// implementations (<c>ItemLookupService</c>, <c>ItemStore</c>) reference <c>ILibraryManager</c>,
    /// <c>IUserViewManager</c>, <c>IChannelManager</c> or <c>ILiveTvManager</c> - this satisfies RFC
    /// invariant I1 (eager construction graph) for this leaf, verified by a dedicated architectural
    /// test (<c>UserViewFactoryTests.DiWiring_...</c>).
    /// </para>
    /// <para>
    /// <b>Why <c>Lazy&lt;IProviderManager&gt;</c>, not a direct injection</b>: <c>ProviderManager</c>
    /// injects <c>ILibraryManager</c> directly in its own constructor
    /// (<c>Tesserafin.Providers/Manager/ProviderManager.cs:117</c>). Since <c>LibraryManager</c> now
    /// delegates <c>GetNamedView</c>/<c>GetShadowView</c> to this class, a direct
    /// <c>IProviderManager</c> injection here would recreate the construction cycle
    /// <c>LibraryManager -&gt; UserViewFactory -&gt; IProviderManager -&gt; ILibraryManager</c>. The
    /// <see cref="Lazy{T}"/> wrapper is excluded from the eager construction graph (I1) while still
    /// providing a real runtime dependency on <c>ProviderManager</c> for <c>QueueRefresh</c> (the
    /// single RFC-assumed I2 exception) - identical in shape to how <c>LibraryManager</c> itself
    /// already holds <c>IProviderManager</c> (<c>LibraryManager.cs:76/258</c>).
    /// </para>
    /// <para>
    /// <b>I2 - when <c>Lazy&lt;IProviderManager&gt;.Value</c> is evaluated</b>: only inside the
    /// <c>if (refresh) { ... ProviderManager.QueueRefresh(...) ... }</c> branches below, which always
    /// run after the item has already been looked up and, for a new view, persisted+registered via
    /// <see cref="IItemStore.CreateItem"/>. For an existing view that does not need a refresh (the
    /// common case), <c>.Value</c> is never evaluated at all. See
    /// <c>UserViewFactoryTests.LazyProviderManager_...</c> for the empirical assertion.
    /// </para>
    /// </remarks>
    internal sealed class UserViewFactory : IUserViewFactory
    {
        private readonly IItemWriteCommands _writes;
        private readonly IItemLookupService _itemLookupService;
        private readonly IItemStore _itemStore;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IFileSystem _fileSystem;
        private readonly Lazy<IProviderManager> _providerManagerFactory;

        private readonly TimeSpan _viewRefreshInterval = TimeSpan.FromHours(24);

        /// <summary>
        /// Initializes a new instance of the <see cref="UserViewFactory"/> class.
        /// </summary>
        /// <param name="writes">Explicit item write/resolution ownership.</param>
        /// <param name="itemLookupService">The item lookup service (read side; see PR75/PR76).</param>
        /// <param name="itemStore">The item store leaf (id generation, save+register; see PR106a).</param>
        /// <param name="configurationManager">The server configuration manager.</param>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="providerManagerFactory">
        /// The provider manager, lazily resolved - <b>must</b> stay <see cref="Lazy{T}"/>, never a
        /// direct <see cref="IProviderManager"/> injection (see type-level remarks: RFC I1, PR106b).
        /// </param>
        public UserViewFactory(
            IItemWriteCommands writes,
            IItemLookupService itemLookupService,
            IItemStore itemStore,
            IServerConfigurationManager configurationManager,
            IFileSystem fileSystem,
            Lazy<IProviderManager> providerManagerFactory)
        {
            _writes = writes;
            _itemLookupService = itemLookupService;
            _itemStore = itemStore;
            _configurationManager = configurationManager;
            _fileSystem = fileSystem;
            _providerManagerFactory = providerManagerFactory;
        }

        private IProviderManager ProviderManager => _providerManagerFactory.Value;

        /// <inheritdoc />
        public UserView GetNamedView(
            User user,
            string name,
            Guid parentId,
            CollectionType? viewType,
            string sortName)
        {
            var parentIdString = parentId.IsEmpty()
                ? null
                : parentId.ToString("N", CultureInfo.InvariantCulture);
            var idValues = "38_namedview_" + name + user.Id.ToString("N", CultureInfo.InvariantCulture) + (parentIdString ?? string.Empty) + (viewType?.ToString() ?? string.Empty);

            var id = _itemStore.GetNewItemId(idValues, typeof(UserView));

            var path = Path.Combine(_configurationManager.ApplicationPaths.InternalMetadataPath, "views", id.ToString("N", CultureInfo.InvariantCulture));

            var item = _itemLookupService.GetItemById(id) as UserView;

            var isNew = false;

            if (item is null)
            {
                var info = Directory.CreateDirectory(path);
                item = new UserView
                {
                    Path = path,
                    Id = id,
                    DateCreated = info.CreationTimeUtc,
                    DateModified = info.LastWriteTimeUtc,
                    Name = name,
                    ViewType = viewType,
                    ForcedSortName = sortName,
                    UserId = user.Id,
                    DisplayParentId = parentId
                };

                _itemStore.CreateItem(item, null);

                isNew = true;
            }

            var lastRefreshedUtc = item.DateLastRefreshed;
            var refresh = isNew || DateTime.UtcNow - lastRefreshedUtc >= _viewRefreshInterval;

            if (!refresh && !item.DisplayParentId.IsEmpty())
            {
                var displayParent = _itemLookupService.GetItemById(item.DisplayParentId);
                refresh = displayParent is not null && displayParent.DateLastSaved > lastRefreshedUtc;
            }

            if (refresh)
            {
                ProviderManager.QueueRefresh(
                    item.Id,
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        // Need to force save to increment DateLastSaved
                        ForceSave = true
                    },
                    RefreshPriority.Normal);
            }

            return item;
        }

        /// <inheritdoc />
        public UserView GetNamedView(
            User user,
            string name,
            CollectionType? viewType,
            string sortName)
        {
            return GetNamedView(user, name, Guid.Empty, viewType, sortName);
        }

        /// <inheritdoc />
        public UserView GetNamedView(
            string name,
            CollectionType viewType,
            string sortName)
        {
            var path = Path.Combine(
                _configurationManager.ApplicationPaths.InternalMetadataPath,
                "views",
                _fileSystem.GetValidFilename(viewType.ToString()));

            var id = _itemStore.GetNewItemId(path + "_namedview_" + name, typeof(UserView));

            var item = _itemLookupService.GetItemById(id) as UserView;

            var refresh = false;

            if (item is null || !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                var info = Directory.CreateDirectory(path);
                item = new UserView
                {
                    Path = path,
                    Id = id,
                    DateCreated = info.CreationTimeUtc,
                    DateModified = info.LastWriteTimeUtc,
                    Name = name,
                    ViewType = viewType,
                    ForcedSortName = sortName
                };

                _itemStore.CreateItem(item, null);

                refresh = true;
            }

            if (refresh)
            {
                item.UpdateToRepositoryAsync(_writes, ItemUpdateType.MetadataImport, CancellationToken.None).GetAwaiter().GetResult();
                ProviderManager.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.Normal);
            }

            return item;
        }

        /// <inheritdoc />
        public UserView GetShadowView(
            BaseItem parent,
            CollectionType? viewType,
            string sortName)
        {
            ArgumentNullException.ThrowIfNull(parent);

            var name = parent.Name;
            var parentId = parent.Id;

            var idValues = "38_namedview_" + name + parentId + (viewType?.ToString() ?? string.Empty);

            var id = _itemStore.GetNewItemId(idValues, typeof(UserView));

            var path = parent.Path;

            var item = _itemLookupService.GetItemById(id) as UserView;

            var isNew = false;

            if (item is null)
            {
                var info = Directory.CreateDirectory(path);
                item = new UserView
                {
                    Path = path,
                    Id = id,
                    DateCreated = info.CreationTimeUtc,
                    DateModified = info.LastWriteTimeUtc,
                    Name = name,
                    ViewType = viewType,
                    ForcedSortName = sortName,
                    DisplayParentId = parentId
                };

                _itemStore.CreateItem(item, null);

                isNew = true;
            }

            var lastRefreshedUtc = item.DateLastRefreshed;
            var refresh = isNew || DateTime.UtcNow - lastRefreshedUtc >= _viewRefreshInterval;

            if (!refresh && !item.DisplayParentId.IsEmpty())
            {
                var displayParent = _itemLookupService.GetItemById(item.DisplayParentId);
                refresh = displayParent is not null && displayParent.DateLastSaved > lastRefreshedUtc;
            }

            if (refresh)
            {
                ProviderManager.QueueRefresh(
                    item.Id,
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        // Need to force save to increment DateLastSaved
                        ForceSave = true
                    },
                    RefreshPriority.Normal);
            }

            return item;
        }

        /// <inheritdoc />
        public UserView GetNamedView(
            string name,
            Guid parentId,
            CollectionType? viewType,
            string sortName,
            string uniqueId)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            var parentIdString = parentId.IsEmpty()
                ? null
                : parentId.ToString("N", CultureInfo.InvariantCulture);
            var idValues = "37_namedview_" + name + (parentIdString ?? string.Empty) + (viewType?.ToString() ?? string.Empty);
            if (!string.IsNullOrEmpty(uniqueId))
            {
                idValues += uniqueId;
            }

            var id = _itemStore.GetNewItemId(idValues, typeof(UserView));

            var path = Path.Combine(_configurationManager.ApplicationPaths.InternalMetadataPath, "views", id.ToString("N", CultureInfo.InvariantCulture));

            var item = _itemLookupService.GetItemById(id) as UserView;

            var isNew = false;

            if (item is null)
            {
                var info = Directory.CreateDirectory(path);
                item = new UserView
                {
                    Path = path,
                    Id = id,
                    DateCreated = info.CreationTimeUtc,
                    DateModified = info.LastWriteTimeUtc,
                    Name = name,
                    ViewType = viewType,
                    ForcedSortName = sortName,
                    DisplayParentId = parentId
                };

                _itemStore.CreateItem(item, null);

                isNew = true;
            }

            if (viewType != item.ViewType)
            {
                item.ViewType = viewType;
                item.UpdateToRepositoryAsync(_writes, ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
            }

            var lastRefreshedUtc = item.DateLastRefreshed;
            var refresh = isNew || DateTime.UtcNow - lastRefreshedUtc >= _viewRefreshInterval;

            if (!refresh && !item.DisplayParentId.IsEmpty())
            {
                var displayParent = _itemLookupService.GetItemById(item.DisplayParentId);
                refresh = displayParent is not null && displayParent.DateLastSaved > lastRefreshedUtc;
            }

            if (refresh)
            {
                ProviderManager.QueueRefresh(
                    item.Id,
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                    {
                        // Need to force save to increment DateLastSaved
                        ForceSave = true
                    },
                    RefreshPriority.Normal);
            }

            return item;
        }
    }
}
