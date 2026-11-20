using System;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Extensions;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Controller.BaseItemManager
{
    /// <inheritdoc />
    public class BaseItemManager : IBaseItemManager
    {
        private readonly IServerConfigurationManager _serverConfigurationManager;
        private readonly Lazy<IChannelManager> _channels;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseItemManager"/> class.
        /// </summary>
        /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
        /// <param name="channels">The channel manager, resolved only for channel items.</param>
        public BaseItemManager(IServerConfigurationManager serverConfigurationManager, Lazy<IChannelManager> channels)
        {
            _serverConfigurationManager = serverConfigurationManager;
            _channels = channels;
        }

        /// <inheritdoc />
        public bool IsMetadataFetcherEnabled(BaseItem baseItem, TypeOptions? libraryTypeOptions, string name)
        {
            if (baseItem is Channel)
            {
                // Hack alert.
                return true;
            }

            if (baseItem.SourceType == SourceType.Channel)
            {
                return !_channels.Value.EnableMediaSourceDisplay(baseItem);
            }

            if (libraryTypeOptions is not null)
            {
                return libraryTypeOptions.MetadataFetchers.Contains(name, StringComparison.OrdinalIgnoreCase);
            }

            var itemConfig = _serverConfigurationManager.GetMetadataOptionsForType(baseItem.GetType().Name);
            return itemConfig is null || !itemConfig.DisabledMetadataFetchers.Contains(name, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public bool IsImageFetcherEnabled(BaseItem baseItem, TypeOptions? libraryTypeOptions, string name)
        {
            if (baseItem is Channel)
            {
                // Hack alert.
                return true;
            }

            if (baseItem.SourceType == SourceType.Channel)
            {
                return !_channels.Value.EnableMediaSourceDisplay(baseItem);
            }

            if (libraryTypeOptions is not null)
            {
                return libraryTypeOptions.ImageFetchers.Contains(name, StringComparison.OrdinalIgnoreCase);
            }

            var itemConfig = _serverConfigurationManager.GetMetadataOptionsForType(baseItem.GetType().Name);
            return itemConfig is null || !itemConfig.DisabledImageFetchers.Contains(name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
