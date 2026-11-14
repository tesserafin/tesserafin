#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Sorting;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Channels;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Library;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="IUserViewCatalog"/> - <c>GetUserViews</c>,
    /// ported off <c>UserViewManager.cs:54-175</c> with behavior preserved exactly, built entirely on
    /// the PR106-108 leaves (RFC <c>docs/rfc-di-query-user-views-v2.md</c> §9, PR109).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Depends on <see cref="IUserRootFolderProvider"/> (PR107, root folder + children),
    /// <see cref="IUserViewFactory"/> (PR106b, named/shadow view creation), <see cref="IChannelCatalog"/>
    /// and <see cref="ILiveTvPresenceProvider"/> (PR108, external catalogs), <see cref="IItemSortService"/>
    /// (already a leaf, RFC §6), <see cref="IServerConfigurationManager"/> (<c>EnableFolderView</c>),
    /// <see cref="ILocalizationManager"/> (view labels) and <see cref="IVisibleContentProbe"/> (the
    /// playlist/boxset probe, RFC §3). None of them reaches <c>ILibraryManager</c>,
    /// <c>IUserViewManager</c>, <c>IChannelManager</c>, <c>ILiveTvManager</c> or
    /// <c>IItemQueryService</c>, directly or deferred - RFC invariant I1 (eager construction graph) holds
    /// for this leaf, and since DEBT-SERVER-1 so does the runtime path of the probe (formerly the
    /// <c>Lazy&lt;IItemQueryService&gt;</c> exception 4 of <c>docs/pr111-di-closure-audit.md</c>). Both
    /// are verified by architectural tests (<c>UserViewCatalogTests.DiWiring_...</c>).
    /// </para>
    /// </remarks>
    internal sealed class UserViewCatalog : IUserViewCatalog
    {
        private readonly IUserRootFolderProvider _userRootFolderProvider;
        private readonly IUserViewFactory _userViewFactory;
        private readonly IChannelCatalog _channelCatalog;
        private readonly ILiveTvPresenceProvider _liveTvPresenceProvider;
        private readonly IItemSortService _itemSortService;
        private readonly IServerConfigurationManager _config;
        private readonly ILocalizationManager _localizationManager;
        private readonly IVisibleContentProbe _visibleContentProbe;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserViewCatalog"/> class.
        /// </summary>
        /// <param name="userRootFolderProvider">The user root folder provider leaf (PR107).</param>
        /// <param name="userViewFactory">The user view factory leaf (PR106b).</param>
        /// <param name="channelCatalog">The channel catalog leaf (PR108).</param>
        /// <param name="liveTvPresenceProvider">The Live TV presence provider leaf (PR108).</param>
        /// <param name="itemSortService">The item sort service (already a leaf, RFC §6).</param>
        /// <param name="config">The server configuration manager (<c>EnableFolderView</c>).</param>
        /// <param name="localizationManager">The localization manager (view labels).</param>
        /// <param name="visibleContentProbe">The playlist/boxset visible-content probe.</param>
        public UserViewCatalog(
            IUserRootFolderProvider userRootFolderProvider,
            IUserViewFactory userViewFactory,
            IChannelCatalog channelCatalog,
            ILiveTvPresenceProvider liveTvPresenceProvider,
            IItemSortService itemSortService,
            IServerConfigurationManager config,
            ILocalizationManager localizationManager,
            IVisibleContentProbe visibleContentProbe)
        {
            _userRootFolderProvider = userRootFolderProvider;
            _userViewFactory = userViewFactory;
            _channelCatalog = channelCatalog;
            _liveTvPresenceProvider = liveTvPresenceProvider;
            _itemSortService = itemSortService;
            _config = config;
            _localizationManager = localizationManager;
            _visibleContentProbe = visibleContentProbe;
        }

        /// <inheritdoc />
        public Folder[] GetUserViews(UserViewQuery query)
        {
            var user = query.User;

            var folders = _userRootFolderProvider.GetUserRootFolder()
                .GetChildren(user, true, null, _itemSortService)
                .OfType<Folder>()
                .ToList();

            var groupedFolders = new List<ICollectionFolder>();
            var list = new List<Folder>();

            foreach (var folder in folders)
            {
                var collectionFolder = folder as ICollectionFolder;
                var folderViewType = collectionFolder?.CollectionType;

                // Playlist and BoxSet libraries require special handling because the folder only references linked items
                if ((folderViewType == CollectionType.playlists || folderViewType == CollectionType.boxsets)
                    && !_visibleContentProbe.HasVisibleContent(folder, user))
                {
                    continue;
                }

                if (UserView.IsUserSpecific(folder))
                {
                    list.Add(_userViewFactory.GetNamedView(user, folder.Name, folder.Id, folderViewType, null!));
                    continue;
                }

                if (collectionFolder is not null && UserView.IsEligibleForGrouping(folder) && user.IsFolderGrouped(folder.Id))
                {
                    groupedFolders.Add(collectionFolder);
                    continue;
                }

                if (query.PresetViews.Contains(folderViewType))
                {
                    list.Add(GetUserView(folder, folderViewType, string.Empty));
                }
                else
                {
                    list.Add(folder);
                }
            }

            foreach (var viewType in new[] { CollectionType.movies, CollectionType.tvshows })
            {
                var parents = groupedFolders.Where(i => i.CollectionType == viewType || i.CollectionType is null)
                    .ToList();

                if (parents.Count > 0)
                {
                    var localizationKey = viewType == CollectionType.tvshows
                        ? "TvShows"
                        : "Movies";

                    list.Add(GetUserView(parents, viewType, localizationKey, string.Empty, user, query.PresetViews));
                }
            }

            if (_config.Configuration.EnableFolderView)
            {
                var name = _localizationManager.GetLocalizedString("Folders");
                list.Add(_userViewFactory.GetNamedView(name, CollectionType.folders, string.Empty));
            }

            if (query.IncludeExternalContent)
            {
                var channelResult = _channelCatalog.GetChannelsAsync(new ChannelQuery
                {
                    UserId = user.Id
                }).GetAwaiter().GetResult();

                var channels = channelResult.Items;

                list.AddRange(channels);

                if (_liveTvPresenceProvider.GetEnabledUsers().Select(i => i.Id).Contains(user.Id))
                {
                    list.Add(_liveTvPresenceProvider.GetLiveTvFolder(CancellationToken.None));
                }
            }

            if (!query.IncludeHidden)
            {
                list = list.Where(i => !user.GetPreferenceValues<Guid>(PreferenceKind.MyMediaExcludes).Contains(i.Id)).ToList();
            }

            var sorted = _itemSortService.Sort(list, user, [ItemSortBy.SortName], SortOrder.Ascending).ToList();
            var orders = user.GetPreferenceValues<Guid>(PreferenceKind.OrderedViews);

            return list
                .OrderBy(i =>
                {
                    var index = Array.IndexOf(orders, i.Id);
                    if (index == -1
                        && i is UserView view
                        && !view.DisplayParentId.IsEmpty())
                    {
                        index = Array.IndexOf(orders, view.DisplayParentId);
                    }

                    return index == -1 ? int.MaxValue : index;
                })
                .ThenBy(sorted.IndexOf)
                .ThenBy(i => i.SortName)
                .ToArray();
        }

        /// <summary>
        /// Mirrors <c>UserViewManager</c>'s private <c>GetUserView(List&lt;ICollectionFolder&gt;, ...)</c>
        /// grouping helper.
        /// </summary>
        private Folder GetUserView(
            List<ICollectionFolder> parents,
            CollectionType? viewType,
            string localizationKey,
            string sortName,
            User user,
            CollectionType?[] presetViews)
        {
            if (parents.Count == 1 && parents.All(i => i.CollectionType == viewType))
            {
                if (!presetViews.Contains(viewType))
                {
                    return (Folder)parents[0];
                }

                return GetUserView((Folder)parents[0], viewType, string.Empty);
            }

            var name = _localizationManager.GetLocalizedString(localizationKey);
            return _userViewFactory.GetNamedView(user, name, viewType, sortName);
        }

        /// <summary>
        /// Mirrors <c>UserViewManager</c>'s public shadow-view <c>GetUserView(Folder, ...)</c>
        /// overload - out of scope on <see cref="IUserViewCatalog"/> itself (PR109 covers only
        /// <c>GetUserViews</c>), kept private here purely as an internal helper for
        /// <see cref="GetUserViews"/>.
        /// </summary>
        private UserView GetUserView(Folder parent, CollectionType? viewType, string sortName)
        {
            return _userViewFactory.GetShadowView(parent, viewType, sortName);
        }
    }
}
