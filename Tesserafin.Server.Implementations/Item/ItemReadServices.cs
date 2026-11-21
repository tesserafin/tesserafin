using System;
using System.Collections.Generic;
using System.Linq;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;
using Tesserafin.Model.LiveTv;
using LinkedChildType = Tesserafin.Controller.Entities.LinkedChildType;

namespace Tesserafin.Server.Implementations.Item;

/// <summary>
/// Sole production <see cref="IItemReadServices"/>, over the item repository and the active recordings registry.
/// </summary>
/// <param name="repository">The deferred item repository.</param>
/// <param name="recordings">The recordings in progress.</param>
/// <param name="linkedChildren">The deferred saved-link resolver.</param>
/// <param name="configuration">The server settings and storage paths.</param>
/// <param name="lookup">The deferred cached identity lookup.</param>
/// <param name="links">The persisted version links.</param>
/// <param name="collections">The deferred library membership resolver.</param>
/// <param name="options">The deferred effective library settings.</param>
public sealed class ItemReadServices(
    Lazy<IItemRepository> repository,
    ActiveRecordings recordings,
    Lazy<LinkedChildResolver> linkedChildren,
    IServerConfigurationManager configuration,
    Lazy<IItemLookupService> lookup,
    Lazy<ILinkedChildrenService> links,
    Lazy<ICollectionFolderResolver> collections,
    Lazy<ILibraryItemOptions> options) : IItemReadServices
{
    /// <inheritdoc />
    public LinkedChildResolver LinkedChildren => linkedChildren.Value;

    /// <inheritdoc />
    public bool DisplaySpecialsWithinSeasons => configuration.Configuration.DisplaySpecialsWithinSeasons;

    /// <inheritdoc />
    public ItemMetadataDefaults MetadataDefaults => ItemMetadataDefaults.FromConfiguration(configuration.Configuration);

    /// <inheritdoc />
    public bool IncludeLiveTvChannelNameInUserDataKeys => !configuration.Configuration.DisableLiveTvChannelUserDataName;

    /// <inheritdoc />
    public string? LiveTvMoviePrefix => configuration.GetConfiguration<LiveTvOptions>("livetv").ListingProviders
        .FirstOrDefault(provider => !string.IsNullOrEmpty(provider.MoviePrefix))?.MoviePrefix;

    /// <inheritdoc />
    public BaseItem? GetItemById(Guid id) => lookup.Value.GetItemById(id);

    /// <inheritdoc />
    public IReadOnlyList<Guid> GetVideoVersionIds(Video video, LinkedChildType versionType)
        => links.Value.GetLinkedChildrenIds(video.Id, (int)versionType);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, MusicArtist[]> FindArtists(IReadOnlyList<string> artistNames)
        => links.Value.FindArtists(artistNames);

    /// <inheritdoc />
    public IReadOnlyList<Folder> GetCollectionFolders(BaseItem item, IEnumerable<Folder>? candidates = null)
        => candidates is null
            ? collections.Value.GetCollectionFolders(item)
            : collections.Value.GetCollectionFolders(item, candidates);

    /// <inheritdoc />
    public LibraryOptions GetLibraryOptions(BaseItem item) => options.Value.GetLibraryOptions(item);

    /// <inheritdoc />
    public bool IsManagedLibraryPath(string path)
        => FilePath.ContainsSubPath(configuration.ApplicationPaths.DataPath, path);

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetChildren(Folder folder)
        => repository.Value.GetItemList(new InternalItemsQuery
        {
            Parent = folder,
            GroupByPresentationUniqueKey = false,
            DtoOptions = new DtoOptions(true)
        });

    /// <inheritdoc />
    public bool IsPlayed(User user, Guid folderId) => repository.Value.GetIsPlayed(user, folderId, true);

    /// <inheritdoc />
    public bool IsActiveRecording(string path) => recordings.GetActiveRecordingInfo(path) is not null;
}
