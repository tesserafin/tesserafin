using System;
using Tesserafin.Controller;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Maps data and internal-metadata paths to and from the virtual prefixes persisted in configuration files.
    /// </summary>
    internal static class VirtualPaths
    {
        /// <summary>
        /// Replaces the virtual prefixes with the real data and internal-metadata paths.
        /// </summary>
        /// <param name="paths">The application paths.</param>
        /// <param name="path">The stored path.</param>
        /// <returns>The real path.</returns>
        public static string Expand(IServerApplicationPaths paths, string path)
            => path.Replace(paths.VirtualDataPath, paths.DataPath, StringComparison.OrdinalIgnoreCase)
                .Replace(paths.VirtualInternalMetadataPath, paths.InternalMetadataPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Replaces the real data and internal-metadata paths with their virtual prefixes.
        /// </summary>
        /// <param name="paths">The application paths.</param>
        /// <param name="path">The real path.</param>
        /// <returns>The path to store.</returns>
        public static string Reverse(IServerApplicationPaths paths, string path)
            => path.Replace(paths.DataPath, paths.VirtualDataPath, StringComparison.OrdinalIgnoreCase)
                .Replace(paths.InternalMetadataPath, paths.VirtualInternalMetadataPath, StringComparison.OrdinalIgnoreCase);
    }
}
