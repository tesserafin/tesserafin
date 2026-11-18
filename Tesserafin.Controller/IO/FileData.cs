using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.IO
{
    /// <summary>
    /// Provides low level File access that is much faster than the File/Directory api's.
    /// </summary>
    public static class FileData
    {
        /// <summary>Reads a library definition or the physical root and resolves its directory shortcuts.</summary>
        /// <param name="directory">Metadata for the directory being read.</param>
        /// <param name="directoryService">The directory service used by the scan.</param>
        /// <param name="fileSystem">Filesystem queries and path comparisons.</param>
        /// <param name="appHost">Virtual path expansion.</param>
        /// <param name="logger">Reports unreadable shortcuts and redundant scan paths.</param>
        /// <param name="physicalRoot">Whether to flatten definition directories and remove redundant scan paths.</param>
        /// <returns>The entries to scan (null when the directory is unavailable) and the configured physical locations.</returns>
        public static (FileSystemMetadata[]? Entries, string[] PhysicalLocations) ReadLibraryDirectory(
            FileSystemMetadata directory,
            IDirectoryService directoryService,
            IFileSystem fileSystem,
            IServerApplicationHost appHost,
            ILogger logger,
            bool physicalRoot)
        {
            var path = directory.FullName;
            var locations = new List<string>();
            if (!string.IsNullOrEmpty(path))
            {
                locations.Add(path);
            }

            if (!directory.IsDirectory)
            {
                return (null, locations.ToArray());
            }

            ArgumentException.ThrowIfNullOrEmpty(path);

            // The physical root contains the definition directories and their library subdirectories.
            var entries = ReadEntries(directoryService, path, fileSystem, appHost, logger, locations, physicalRoot ? 2 : 0);
            if (physicalRoot)
            {
                entries = NormalizeRootEntries(entries, fileSystem, logger);
            }

            return (entries, locations.ToArray());
        }

        private static FileSystemMetadata[] ReadEntries(
            IDirectoryService directoryService,
            string path,
            IFileSystem fileSystem,
            IServerApplicationHost appHost,
            ILogger logger,
            List<string> locations,
            int flattenFolderDepth)
        {
            var entries = directoryService.GetFileSystemEntries(path);
            var dict = new Dictionary<string, FileSystemMetadata>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var isDirectory = entry.IsDirectory;

                var fullName = entry.FullName;

                if (fileSystem.IsShortcut(fullName))
                {
                    try
                    {
                        var newPath = appHost.ExpandVirtualPath(fileSystem.ResolveShortcut(fullName));

                        if (string.IsNullOrEmpty(newPath))
                        {
                            // invalid shortcut - could be old or target could just be unavailable
                            logger.LogWarning("Encountered invalid shortcut: {Path}", fullName);
                            continue;
                        }

                        // Don't check if it exists here because that could return false for network shares.
                        var data = fileSystem.GetDirectoryInfo(newPath);

                        // add to our physical locations
                        locations.Add(newPath);

                        dict[newPath] = data;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "Error resolving shortcut from {Path}", fullName);
                    }
                }
                else if (flattenFolderDepth > 0 && isDirectory)
                {
                    foreach (var child in ReadEntries(directoryService, fullName, fileSystem, appHost, logger, locations, flattenFolderDepth - 1))
                    {
                        dict[child.FullName] = child;
                    }
                }
                else
                {
                    dict[fullName] = entry;
                }
            }

            var returnResult = new FileSystemMetadata[dict.Count];
            var index = 0;
            var values = dict.Values;
            foreach (var value in values)
            {
                returnResult[index] = value;
                index++;
            }

            return returnResult;
        }

        private static FileSystemMetadata[] NormalizeRootEntries(FileSystemMetadata[] entries, IFileSystem fileSystem, ILogger logger)
        {
            var directories = entries.Where(entry => entry.IsDirectory)
                .Select(entry => Path.TrimEndingDirectorySeparator(entry.FullName))
                .Distinct()
                .ToList();

            // Scanning a parent directory also covers its descendants.
            var redundant = directories.Where(path => !path.EndsWith(":\\", StringComparison.Ordinal)
                && directories.Any(parent => fileSystem.ContainsSubPath(parent, path))).ToList();
            foreach (var path in redundant)
            {
                logger.LogInformation("Found duplicate path: {Path}", path);
            }

            return directories.Except(redundant, StringComparer.Ordinal)
                .Select(fileSystem.GetDirectoryInfo)
                .Concat(entries.Where(entry => !entry.IsDirectory))
                .ToArray();
        }
    }
}
