using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>
/// Looks up catalogue items by their stored path.
/// </summary>
public interface IItemPathLookupService
{
    /// <summary>
    /// Finds the newest catalogue item at the specified path.
    /// </summary>
    /// <param name="path">The path to look up.</param>
    /// <param name="isFolder">Whether to look for a folder, a non-folder item, or either when null.</param>
    /// <returns>The matching item, or null when the path is absent from the catalogue.</returns>
    BaseItem? FindByPath(string path, bool? isFolder);
}
