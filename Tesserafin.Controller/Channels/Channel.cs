#nullable disable

#pragma warning disable CS1591

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.TV;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Querying;

namespace Tesserafin.Controller.Channels
{
    public class Channel : Folder
    {
        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        [JsonIgnore]
        public override SourceType SourceType => SourceType.Channel;

        public override bool SupportsRawQueryItems => false;

        public override bool IsVisible(User user, bool skipAllowedTagsCheck, ITagVisibilityService tagVisibility)
        {
            var blockedChannelsPreference = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels);
            if (blockedChannelsPreference.Length != 0)
            {
                if (blockedChannelsPreference.Contains(Id))
                {
                    return false;
                }
            }
            else
            {
                if (!user.HasPermission(PermissionKind.EnableAllChannels)
                    && !user.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels).Contains(Id))
                {
                    return false;
                }
            }

            return base.IsVisible(user, skipAllowedTagsCheck, tagVisibility);
        }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            try
            {
                query.Parent = this;
                query.ChannelIds = new Guid[] { Id };

                // Don't blow up here because it could cause parent screens with other content to fail
                return channelManager.GetChannelItemsInternal(query, new Progress<double>(), CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                // Already logged at lower levels
                return new QueryResult<BaseItem>();
            }
        }

        public override string GetInternalMetadataPath(string basePath)
        {
            return GetInternalMetadataPath(basePath, Id);
        }

        public static string GetInternalMetadataPath(string basePath, Guid id)
        {
            return System.IO.Path.Combine(basePath, "channels", id.ToString("N", CultureInfo.InvariantCulture), "metadata");
        }

        public override bool CanDelete()
        {
            return false;
        }

        /// <summary>
        /// Determines if the channel of a channel item admits a given user.
        /// </summary>
        /// <param name="channelItem">The channel item, or the channel itself.</param>
        /// <param name="user">The user.</param>
        /// <param name="tagVisibility">Where the data comes from: an <see cref="IBoxSetVisibilityService"/> also supplies the channel by id; otherwise the channel comes through the static <see cref="BaseItem.ChannelManager"/>.</param>
        /// <returns><c>true</c> if the channel is visible to the user.</returns>
        internal static bool IsChannelVisible(BaseItem channelItem, User user, ITagVisibilityService tagVisibility)
        {
            var channel = tagVisibility is IBoxSetVisibilityService lookup
                ? lookup.GetItemById(channelItem.ChannelId) as Channel
                : ChannelManager.GetChannel(channelItem.ChannelId.ToString(string.Empty));

            return channel.IsVisible(user, false, tagVisibility);
        }
    }
}
