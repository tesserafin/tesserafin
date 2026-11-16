#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Leaf item identity and save/register port for named and shadow user views.
    /// Persistence precedes cache registration and shared change publication. A failed save
    /// publishes nothing; a failed cache registration propagates without rolling back persistence.
    /// The eager dependency graph does not include the library, channel or live TV managers.
    /// </summary>
    internal sealed class ItemStore : IItemStore
    {
        private readonly IItemPersistenceService _persistenceService;
        private readonly IItemCacheStore _itemCacheStore;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IItemChangePublisher _changes;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemStore"/> class.
        /// </summary>
        /// <param name="persistenceService">The item persistence service (save side).</param>
        /// <param name="itemCacheStore">The item cache lifecycle port (register side).</param>
        /// <param name="configurationManager">The server configuration manager (id generation).</param>
        /// <param name="changes">Completed item change publication.</param>
        public ItemStore(
            IItemPersistenceService persistenceService,
            IItemCacheStore itemCacheStore,
            IServerConfigurationManager configurationManager,
            IItemChangePublisher changes)
        {
            _persistenceService = persistenceService;
            _itemCacheStore = itemCacheStore;
            _configurationManager = configurationManager;
            _changes = changes;
        }

        /// <inheritdoc />
        public Guid GetNewItemId(string key, Type type)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            ArgumentNullException.ThrowIfNull(type);

            string programDataPath = _configurationManager.ApplicationPaths.ProgramDataPath;
            if (key.StartsWith(programDataPath, StringComparison.Ordinal))
            {
                // Try to normalize paths located underneath program-data in an attempt to make them more portable
                key = key.Substring(programDataPath.Length)
                    .TrimStart('/', '\\')
                    .Replace('/', '\\');
            }

            if (!_configurationManager.Configuration.EnableCaseSensitiveItemIds)
            {
                key = key.ToLowerInvariant();
            }

            key = type.FullName + key;

            return key.GetMD5();
        }

        /// <inheritdoc />
        public void CreateItem(BaseItem item, BaseItem? parent)
        {
            ArgumentNullException.ThrowIfNull(item);

            _persistenceService.SaveItems(new List<BaseItem> { item }, CancellationToken.None);

            RegisterItem(item);

            if (parent is Folder folder)
            {
                folder.Children = null;
                folder.UserData = null;
            }

            if (item.SourceType == SourceType.Library)
            {
                _changes.Added(item, parent);
            }
        }

        /// <inheritdoc />
        public void RegisterItem(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            _itemCacheStore.Register(item);
        }
    }
}
