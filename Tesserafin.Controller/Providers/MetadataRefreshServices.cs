using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Library;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Providers;

/// <summary>
/// Services supplied by the metadata provider when preparing an item for refresh.
/// </summary>
/// <param name="FileSystem">Access to the item's files.</param>
/// <param name="ApplicationPaths">The configured storage paths.</param>
/// <param name="ItemNamingService">The media naming and numbering rules.</param>
/// <param name="Logger">The provider's logger.</param>
public readonly record struct MetadataRefreshServices(
    IFileSystem FileSystem,
    IServerApplicationPaths ApplicationPaths,
    IItemNamingService ItemNamingService,
    ILogger Logger);
