using System;
using System.Collections.Generic;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Entities;

namespace Tesserafin.Server.Implementations.Item;

/// <summary>
/// Sole production <see cref="IItemReadServices"/>, over the item repository and the active recordings registry.
/// </summary>
/// <param name="repository">The item repository.</param>
/// <param name="recordings">The recordings in progress.</param>
/// <param name="linkedChildren">The deferred saved-link resolver.</param>
public sealed class ItemReadServices(IItemRepository repository, ActiveRecordings recordings, Lazy<LinkedChildResolver> linkedChildren) : IItemReadServices
{
    /// <inheritdoc />
    public LinkedChildResolver LinkedChildren => linkedChildren.Value;

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetChildren(Folder folder)
        => repository.GetItemList(new InternalItemsQuery
        {
            Parent = folder,
            GroupByPresentationUniqueKey = false,
            DtoOptions = new DtoOptions(true)
        });

    /// <inheritdoc />
    public bool IsPlayed(User user, Guid folderId) => repository.GetIsPlayed(user, folderId, true);

    /// <inheritdoc />
    public bool IsActiveRecording(string path) => recordings.GetActiveRecordingInfo(path) is not null;
}
