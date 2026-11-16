#pragma warning disable CS1591

using Tesserafin.Controller.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;

namespace Tesserafin.Controller.Library;

/// <summary>Library options and content type policy without a manager dependency.</summary>
public interface ILibraryItemOptions
{
    LibraryOptions GetLibraryOptions(BaseItem item);

    CollectionType? GetTopFolderContentType(BaseItem? item);

    CollectionType? GetConfiguredContentType(string path, bool inherit = false);
}
