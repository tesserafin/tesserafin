using System;
using Tesserafin.Model.Dto;

namespace Tesserafin.Model.Dlna
{
    /// <summary>
    /// Class MediaOptions.
    /// </summary>
    public class MediaOptions
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MediaOptions"/> class.
        /// </summary>
        public MediaOptions()
        {
            Context = EncodingContext.Streaming;

            EnableDirectPlay = true;
            EnableDirectStream = true;
        }

        /// <summary>
        /// Gets or sets a value indicating whether direct playback is allowed.
        /// </summary>
        public bool EnableDirectPlay { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether direct streaming is allowed.
        /// </summary>
        public bool EnableDirectStream { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the plan has to be a delivery the client declared,
        /// or no plan at all. Set by <c>Playback/Sessions</c>; <c>PlaybackInfo</c> reads capability
        /// flags off whatever plan it gets and keeps its own contract (#374).
        /// </summary>
        public bool RequireDeclaredDelivery { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether another engine decides this session, leaving the
        /// legacy plan as the fallback only. A legacy refusal then does not refuse the session (#374).
        /// </summary>
        public bool LegacyPlanIsFallbackOnly { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether direct playback is forced.
        /// </summary>
        public bool ForceDirectPlay { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether direct streaming is forced.
        /// </summary>
        public bool ForceDirectStream { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether audio stream copy is allowed.
        /// </summary>
        public bool AllowAudioStreamCopy { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether video stream copy is allowed.
        /// </summary>
        public bool AllowVideoStreamCopy { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether always burn in subtitles when transcoding.
        /// </summary>
        public bool AlwaysBurnInSubtitleWhenTranscoding { get; set; }

        /// <summary>
        /// Gets or sets the item id.
        /// </summary>
        public Guid ItemId { get; set; }

        /// <summary>
        /// Gets or sets the requesting user id. PR113b: carried through so a shadow v2 decision
        /// engine run (<c>Tesserafin.Playback.Shadow.ShadowPlaybackSessionPlanner</c>) can stamp
        /// its <c>PlaybackRequestContext.UserId</c> with the real requester instead of
        /// <see cref="Guid.Empty"/>. Defaults to <see cref="Guid.Empty"/>, matching the pre-PR113b
        /// behavior for every caller that does not set it.
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// Gets or sets the media sources.
        /// </summary>
        public MediaSourceInfo[] MediaSources { get; set; } = Array.Empty<MediaSourceInfo>();

        /// <summary>
        /// Gets or sets the device profile.
        /// </summary>
        public required DeviceProfile Profile { get; set; }

        /// <summary>
        /// Gets or sets a media source id. Optional. Only needed if a specific AudioStreamIndex or SubtitleStreamIndex are requested.
        /// </summary>
        public string? MediaSourceId { get; set; }

        /// <summary>
        /// Gets or sets the device id.
        /// </summary>
        public string? DeviceId { get; set; }

        /// <summary>
        /// Gets or sets an override of supported number of audio channels
        /// Example: DeviceProfile supports five channel, but user only has stereo speakers.
        /// </summary>
        public int? MaxAudioChannels { get; set; }

        /// <summary>
        /// Gets or sets the application's configured maximum bitrate.
        /// </summary>
        public int? MaxBitrate { get; set; }

        /// <summary>
        /// Gets or sets the context.
        /// </summary>
        /// <value>The context.</value>
        public EncodingContext Context { get; set; }

        /// <summary>
        /// Gets or sets the audio transcoding bitrate.
        /// </summary>
        /// <value>The audio transcoding bitrate.</value>
        public int? AudioTranscodingBitrate { get; set; }

        /// <summary>
        /// Gets or sets an override for the audio stream index.
        /// </summary>
        public int? AudioStreamIndex { get; set; }

        /// <summary>
        /// Gets or sets an override for the subtitle stream index.
        /// </summary>
        public int? SubtitleStreamIndex { get; set; }

        /// <summary>
        /// Gets the maximum bitrate.
        /// </summary>
        /// <param name="isAudio">Whether or not this is audio.</param>
        /// <returns>System.Nullable&lt;System.Int32&gt;.</returns>
        public int? GetMaxBitrate(bool isAudio)
        {
            if (MaxBitrate.HasValue)
            {
                return MaxBitrate;
            }

            if (Profile is null)
            {
                return null;
            }

            if (Context == EncodingContext.Static)
            {
                if (isAudio && Profile.MaxStaticMusicBitrate.HasValue)
                {
                    return Profile.MaxStaticMusicBitrate;
                }

                return Profile.MaxStaticBitrate;
            }

            return Profile.MaxStreamingBitrate;
        }
    }
}
