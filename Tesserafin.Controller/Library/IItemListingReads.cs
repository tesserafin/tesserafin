using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Sorting;

namespace Tesserafin.Controller.Library;

/// <summary>Catalogue reads that item listings need for scope, filtering and ordering.</summary>
public interface IItemListingReads
{
    /// <summary>Gets the item sort service.</summary>
    IItemSortService Sort { get; }

    /// <summary>Gets the catalogue identity lookup.</summary>
    IItemLookupService Lookup { get; }

    /// <summary>Gets aggregate item counts for list filters.</summary>
    IItemCountService Counts { get; }

    /// <summary>Gets the user-facing library root provider.</summary>
    IUserRootFolderProvider UserRoot { get; }

    /// <summary>Gets the physical library root provider.</summary>
    ILibraryRootFolderProvider LibraryRoot { get; }
}
