using System;
using Tesserafin.Model.Dto;

namespace Tesserafin.Model.Dlna
{
    /// <summary>
    /// What the server resolved for a playback session's request: the item, its sources and the
    /// caller. The engine decides from the request's own capabilities and constraints.
    /// </summary>
    public class MediaOptions
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MediaOptions"/> class.
        /// </summary>
        public MediaOptions()
        {
            Context = EncodingContext.Streaming;
        }

        /// <summary>
        /// Gets or sets the item id.
        /// </summary>
        public Guid ItemId { get; set; }

        /// <summary>
        /// Gets or sets the requesting user id, stamped on the engine's <c>PlaybackRequestContext</c>.
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// Gets or sets the media sources.
        /// </summary>
        public MediaSourceInfo[] MediaSources { get; set; } = Array.Empty<MediaSourceInfo>();

        /// <summary>
        /// Gets or sets a media source id. Optional. Only needed if a specific AudioStreamIndex or SubtitleStreamIndex are requested.
        /// </summary>
        public string? MediaSourceId { get; set; }

        /// <summary>
        /// Gets or sets the device id.
        /// </summary>
        public string? DeviceId { get; set; }

        /// <summary>
        /// Gets or sets the context.
        /// </summary>
        /// <value>The context.</value>
        public EncodingContext Context { get; set; }
    }
}
