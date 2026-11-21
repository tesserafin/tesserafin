using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="IStandaloneVisibilityService"/> (and so of
    /// <see cref="ITagVisibilityService"/>), built on the id lookup (parents), the library membership
    /// resolver, the user root folder (libraries), the item repository (path search), the localization (rating
    /// scores), the server configuration (country code) and the active recordings only.
    /// </summary>
    /// <remarks>
    /// The combination and the filter are the entities' own (<see cref="BaseItem.CombineInheritedTags"/>,
    /// <see cref="BaseItem.EvaluateTagVisibility"/>); only where the parents and libraries come from
    /// differs from <see cref="BaseItem.GetInheritedTags()"/> (DEBT-SERVER-3). The same holds for the
    /// parent and library checks of the standalone visibility, which stay in the entities (DEBT-SERVER-4),
    /// and for the box set's library and linked-children checks (DEBT-SERVER-5, DEBT-SERVER-7), and the
    /// rating score (DEBT-SERVER-8) and a video's source type (DEBT-SERVER-9).
    /// </remarks>
    internal sealed class TagVisibilityService : IStandaloneVisibilityService
    {
        private readonly ILibraryItemOptions _libraryOptions;
        private readonly IItemLookupService _itemLookupService;
        private readonly ICollectionFolderResolver _collectionFolderResolver;
        private readonly IUserRootFolderProvider _userRootFolderProvider;
        private readonly IItemRepository _itemRepository;
        private readonly ILocalizationManager _localizationManager;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly ActiveRecordings _activeRecordings;

        /// <summary>
        /// Initializes a new instance of the <see cref="TagVisibilityService"/> class.
        /// </summary>
        /// <param name="itemLookupService">The cached id lookup (parents).</param>
        /// <param name="collectionFolderResolver">The library membership resolver.</param>
        /// <param name="userRootFolderProvider">The user root folder (its children are the libraries).</param>
        /// <param name="itemRepository">The item repository (path search).</param>
        /// <param name="localizationManager">The localization (rating scores).</param>
        /// <param name="configurationManager">The server configuration (country code).</param>
        /// <param name="activeRecordings">The recordings in progress.</param>
        /// <param name="libraryOptions">The options of an item's library.</param>
        /// <param name="logger">The logger for visibility diagnostics.</param>
        public TagVisibilityService(IItemLookupService itemLookupService, ICollectionFolderResolver collectionFolderResolver, IUserRootFolderProvider userRootFolderProvider, IItemRepository itemRepository, ILocalizationManager localizationManager, IServerConfigurationManager configurationManager, ActiveRecordings activeRecordings, ILibraryItemOptions libraryOptions, ILogger<TagVisibilityService> logger)
        {
            Logger = logger;
            _libraryOptions = libraryOptions;
            _itemLookupService = itemLookupService;
            _collectionFolderResolver = collectionFolderResolver;
            _userRootFolderProvider = userRootFolderProvider;
            _itemRepository = itemRepository;
            _localizationManager = localizationManager;
            _configurationManager = configurationManager;
            _activeRecordings = activeRecordings;
        }

        /// <inheritdoc />
        public ILogger Logger { get; }

        /// <inheritdoc />
        public IReadOnlyList<BaseItem> GetParents(BaseItem item)
        {
            return item.GetParents(_itemLookupService).ToList();
        }

        /// <inheritdoc />
        public BaseItem? GetDisplayParent(BaseItem item)
        {
            var id = item.DisplayParentId;
            return id.Equals(Guid.Empty) ? null : _itemLookupService.GetItemById(id);
        }

        /// <inheritdoc />
        public IReadOnlyList<Folder> GetCollectionFolders(BaseItem item)
        {
            return _collectionFolderResolver.GetCollectionFolders(item);
        }

        /// <inheritdoc />
        public LibraryOptions GetLibraryOptions(BaseItem item) => _libraryOptions.GetLibraryOptions(item);

        /// <inheritdoc />
        public IReadOnlyList<Guid> GetLibraryIds()
        {
            return CollectionFolderResolver.GetLibraries(_userRootFolderProvider.GetUserRootFolder(), _itemLookupService).Select(i => i.Id).ToList();
        }

        /// <inheritdoc />
        public BaseItem? GetItemById(Guid id)
        {
            return _itemLookupService.GetItemById(id);
        }

        /// <inheritdoc />
        public BaseItem? FindByPath(string path)
        {
            return ItemPathQuery.Find(_itemRepository, path, null);
        }

        /// <inheritdoc />
        public ParentalRatingScore? GetRatingScore(string rating, string? countryCode)
        {
            return _localizationManager.GetRatingScore(rating, countryCode);
        }

        /// <inheritdoc />
        public string GetServerMetadataCountryCode()
        {
            return _configurationManager.Configuration.MetadataCountryCode;
        }

        /// <inheritdoc />
        public bool IsActiveRecording(string path)
        {
            return _activeRecordings.GetActiveRecordingInfo(path) is not null;
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetInheritedTags(BaseItem item)
        {
            return BaseItem.CombineInheritedTags(item, item.GetParents(_itemLookupService), _collectionFolderResolver.GetCollectionFolders(item));
        }

        /// <inheritdoc />
        public bool IsVisibleViaTags(BaseItem item, User user, bool skipAllowedTagsCheck)
        {
            return BaseItem.EvaluateTagVisibility(user, skipAllowedTagsCheck, () => GetInheritedTags(item), () => item.GetParent(_itemLookupService) ?? item);
        }
    }
}
