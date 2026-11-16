using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Tasks;

namespace Tesserafin.LiveTv.Channels
{
    /// <summary>
    /// The "Refresh Channels" scheduled task.
    /// </summary>
    public class RefreshChannelsScheduledTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly IItemDeletionCommands _deletions;
        private readonly IChannelManager _channelManager;
        private readonly ILogger<RefreshChannelsScheduledTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ILocalizationManager _localization;

        /// <summary>
        /// Initializes a new instance of the <see cref="RefreshChannelsScheduledTask"/> class.
        /// </summary>
        /// <param name="deletions">Item deletion operations.</param>
        /// <param name="channelManager">The channel manager.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="localization">The localization manager.</param>
        public RefreshChannelsScheduledTask(
            IItemDeletionCommands deletions,
            IChannelManager channelManager,
            ILogger<RefreshChannelsScheduledTask> logger,
            ILibraryManager libraryManager,
            ILocalizationManager localization)
        {
            _deletions = deletions;
            _channelManager = channelManager;
            _logger = logger;
            _libraryManager = libraryManager;
            _localization = localization;
        }

        /// <inheritdoc />
        public string Name => _localization.GetLocalizedString("TaskRefreshChannels");

        /// <inheritdoc />
        public string Description => _localization.GetLocalizedString("TaskRefreshChannelsDescription");

        /// <inheritdoc />
        public string Category => _localization.GetLocalizedString("TasksChannelsCategory");

        /// <inheritdoc />
        public bool IsHidden => ((ChannelManager)_channelManager).Channels.Length == 0;

        /// <inheritdoc />
        public bool IsEnabled => true;

        /// <inheritdoc />
        public bool IsLogged => true;

        /// <inheritdoc />
        public string Key => "RefreshInternetChannels";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var manager = (ChannelManager)_channelManager;

            await manager.RefreshChannels(new Progress<double>(), cancellationToken).ConfigureAwait(false);

            await new ChannelPostScanTask(_deletions, _channelManager, _logger, _libraryManager).Run(progress, cancellationToken)
                    .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                // Every so often
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks
                }
            };
        }
    }
}
