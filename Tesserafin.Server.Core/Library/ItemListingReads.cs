using System;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Sorting;

namespace Tesserafin.Server.Core.Library;

/// <inheritdoc />
internal sealed class ItemListingReads(
    IItemSortService sort,
    IItemLookupService lookup,
    IItemCountService counts,
    Lazy<IUserRootFolderProvider> userRoot,
    Lazy<ILibraryRootFolderProvider> libraryRoot) : IItemListingReads
{
    public IItemSortService Sort { get; } = sort;

    public IItemLookupService Lookup { get; } = lookup;

    public IItemCountService Counts { get; } = counts;

    public IUserRootFolderProvider UserRoot => userRoot.Value;

    public ILibraryRootFolderProvider LibraryRoot => libraryRoot.Value;
}
