#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Tesserafin.Common;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Playlists;
using Tesserafin.Controller.TV;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Querying;

namespace Tesserafin.Server.Core.Playlists
{
    [RequiresSourceSerialisation]
    public class PlaylistsFolder : BasePluginFolder
    {
        public PlaylistsFolder()
        {
            Name = "Playlists";
        }

        [JsonIgnore]
        public override bool IsHidden => true;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => false;

        [JsonIgnore]
        public override CollectionType? CollectionType => Tesserafin.Data.Enums.CollectionType.playlists;

        // Only unsafe when query.User is set (the base routing is used as-is for query.User is
        // null, cf. the base.GetItemsInternal call in GetItemsInternal below) - kept false
        // unconditionally anyway.
        [JsonIgnore]
        public override bool SupportsRawQueryItems => false;

        protected override IEnumerable<BaseItem> GetEligibleChildrenForRecursiveChildren(User user, InternalItemsQuery query)
        {
            return base.GetEligibleChildrenForRecursiveChildren(user, query).OfType<Playlist>();
        }

        protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query, IChannelManager channelManager, ICollectionManager collectionManager, IUserViewManager userViewManager, ITVSeriesManager tvSeriesManager)
        {
            if (query.User is null)
            {
                query.Recursive = false;
                return base.GetItemsInternal(query, channelManager, collectionManager, userViewManager, tvSeriesManager);
            }

            query.Recursive = true;
            query.IncludeItemTypes = [BaseItemKind.Playlist];

            return QueryWithPostFiltering(query);
        }

        public override string GetClientTypeName()
        {
            return "ManualPlaylistsFolder";
        }
    }
}
