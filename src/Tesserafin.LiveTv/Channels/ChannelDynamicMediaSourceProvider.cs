using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Model.Dto;

namespace Tesserafin.LiveTv.Channels
{
    /// <summary>
    /// A media source provider for channels.
    /// </summary>
    public class ChannelDynamicMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ChannelManager _channelManager;
        private readonly ActiveRecordings _recordings;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChannelDynamicMediaSourceProvider"/> class.
        /// </summary>
        /// <param name="channelManager">The channel manager.</param>
        /// <param name="recordings">The recordings in progress.</param>
        public ChannelDynamicMediaSourceProvider(IChannelManager channelManager, ActiveRecordings recordings)
        {
            _channelManager = (ChannelManager)channelManager;
            _recordings = recordings;
        }

        /// <inheritdoc />
        public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            return item.GetSourceType(_recordings) == SourceType.Channel
                ? _channelManager.GetDynamicMediaSources(item, cancellationToken)
                : Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        /// <inheritdoc />
        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
