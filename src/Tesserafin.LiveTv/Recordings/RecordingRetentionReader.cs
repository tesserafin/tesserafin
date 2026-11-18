using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Enums;
using Tesserafin.Model.MediaInfo;

namespace Tesserafin.LiveTv.Recordings;

/// <summary>
/// Reads catalogue candidates for recording retention.
/// </summary>
/// <param name="paths">The stored-path lookup service.</param>
/// <param name="repository">The item repository.</param>
/// <param name="mediaSources">The media source manager used to identify local files.</param>
public sealed class RecordingRetentionReader(
    IItemPathLookupService paths,
    IItemRepository repository,
    IMediaSourceManager mediaSources)
{
    /// <summary>
    /// Gets existing catalogue files beyond the number of newest recordings to keep.
    /// </summary>
    /// <param name="seriesPath">The recording series directory.</param>
    /// <param name="keepCount">The number of newest existing files to keep.</param>
    /// <returns>The deletion candidates in descending creation order.</returns>
    public IEnumerable<BaseItem> GetExcessFiles(string seriesPath, int keepCount)
    {
        if (paths.FindByPath(seriesPath, true) is not Folder folder
            || folder is AggregateFolder or UserRootFolder
            || folder.SourceType == SourceType.Channel)
        {
            return [];
        }

        // Retention is per recording folder: everything below this folder, newest first.
        var query = new InternalItemsQuery
        {
            AncestorIds = [folder.Id],
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            IsVirtualItem = false,
            IsFolder = false,
            Recursive = true,
            EnableTotalRecordCount = false,
            DtoOptions = new DtoOptions(true)
        };

        return repository.GetItemList(query)
            .Where(item => !string.IsNullOrEmpty(item.Path) && mediaSources.GetPathProtocol(item.Path) == MediaProtocol.File && File.Exists(item.Path))
            .Skip(keepCount);
    }
}
