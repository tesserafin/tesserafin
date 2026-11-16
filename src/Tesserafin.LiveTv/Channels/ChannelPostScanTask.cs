using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Enums;

namespace Tesserafin.LiveTv.Channels
{
    /// <summary>
    /// A task to remove all non-installed channels from the database.
    /// </summary>
    public class ChannelPostScanTask
    {
        private readonly IItemDeletionCommands _deletions;
        private readonly IChannelManager _channelManager;
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelPostScanTask"/> class.
        /// </summary>
        /// <param name="deletions">Item deletion operations.</param>
        /// <param name="channelManager">The channel manager.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="libraryManager">The library manager.</param>
        public ChannelPostScanTask(IItemDeletionCommands deletions, IChannelManager channelManager, ILogger logger, ILibraryManager libraryManager)
        {
            _deletions = deletions;
            _channelManager = channelManager;
            _logger = logger;
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Runs this task.
        /// </summary>
        /// <param name="progress">The progress.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The completed task.</returns>
        public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            await CleanDatabase(cancellationToken).ConfigureAwait(false);

            progress.Report(100);
        }

        private async Task CleanDatabase(CancellationToken cancellationToken)
        {
            var installedChannelIds = ((ChannelManager)_channelManager).GetInstalledChannelIds();

            var uninstalledChannels = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Channel },
                ExcludeItemIds = installedChannelIds.ToArray()
            });

            foreach (var channel in uninstalledChannels)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await CleanChannel((Channel)channel, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task CleanChannel(Channel channel, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Cleaning channel {0} from database", channel.Id);

            // Delete all channel items
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ChannelIds = new[] { channel.Id }
            });

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _deletions.DeleteAsync(
                    item,
                    new DeleteOptions
                    {
                        DeleteFileLocation = false
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            // Finally, delete the channel itself
            await _deletions.DeleteAsync(
                channel,
                new DeleteOptions
                {
                    DeleteFileLocation = false
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}
