#pragma warning disable CS1591

using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Library;

/// <summary>Explicit read context for filesystem resolution and ancestor lookup.</summary>
public interface IItemResolutionPolicy : IItemLookupService
{
    Guid GetNewItemId(string key, Type type);

    LibraryOptions GetLibraryOptions(BaseItem item);

    CollectionType? GetConfiguredContentType(string path);

    bool IgnoreFile(FileSystemMetadata file, BaseItem? parent);

    bool AreEqualPaths(string first, string second);
}
