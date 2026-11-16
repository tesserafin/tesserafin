using System.IO;
using System.Linq;
using System;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Class ResolverHelper.
    /// </summary>
    public static class ResolverHelper
    {
        /// <summary>
        /// Sets the initial item values.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="parent">The parent.</param>
        /// <param name="policy">The resolution policy.</param>
        /// <param name="directoryService">The directory service.</param>
        /// <param name="useFileCreationTime">Whether the added date uses file creation time.</param>
        /// <returns>True if initializing was successful.</returns>
        /// <exception cref="ArgumentException">Item must have a path.</exception>
        public static bool SetInitialItemValues(BaseItem item, Folder? parent, IItemResolutionPolicy policy, IDirectoryService directoryService, bool useFileCreationTime)
        {
            // This version of the below method has no ItemResolveArgs, so we have to require the path already being set
            ArgumentException.ThrowIfNullOrEmpty(item.Path);

            // If the resolver didn't specify this
            if (parent is not null)
            {
                item.SetParent(parent);
            }

            item.Id = policy.GetNewItemId(item.Path, item.GetType());

            item.IsLocked = item.Path.Contains("[dontfetchmeta]", StringComparison.OrdinalIgnoreCase) ||
                item.GetParents(policy).Any(i => i.IsLocked);

            // Make sure DateCreated and DateModified have values
            var fileInfo = directoryService.GetFileSystemEntry(item.Path);
            if (fileInfo is null)
            {
                return false;
            }

            SetDateCreated(item, fileInfo, useFileCreationTime);

            EnsureName(item, fileInfo);

            return true;
        }

        /// <summary>
        /// Sets the initial item values.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="args">The args.</param>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="policy">The resolution policy.</param>
        /// <param name="useFileCreationTime">Whether the added date uses file creation time.</param>
        public static void SetInitialItemValues(BaseItem item, ItemResolveArgs args, IFileSystem fileSystem, IItemResolutionPolicy policy, bool useFileCreationTime)
        {
            // If the resolver didn't specify this
            if (string.IsNullOrEmpty(item.Path))
            {
                item.Path = args.Path;
            }

            // If the resolver didn't specify this
            if (args.Parent is not null)
            {
                item.SetParent(args.Parent);
            }

            item.Id = policy.GetNewItemId(item.Path, item.GetType());

            // Make sure the item has a name
            EnsureName(item, args.FileInfo);

            item.IsLocked = item.Path.Contains("[dontfetchmeta]", StringComparison.OrdinalIgnoreCase) ||
                item.GetParents(policy).Any(i => i.IsLocked);

            // Make sure DateCreated and DateModified have values
            EnsureDates(fileSystem, item, args, useFileCreationTime);
        }

        /// <summary>
        /// Ensures the name.
        /// </summary>
        private static void EnsureName(BaseItem item, FileSystemMetadata fileInfo)
        {
            // If the subclass didn't supply a name, add it here
            if (string.IsNullOrEmpty(item.Name) && !string.IsNullOrEmpty(item.Path))
            {
                item.Name = fileInfo.IsDirectory ? fileInfo.Name : Path.GetFileNameWithoutExtension(fileInfo.Name);
            }
        }

        /// <summary>
        /// Ensures DateCreated and DateModified have values.
        /// </summary>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="item">The item.</param>
        /// <param name="args">The args.</param>
        /// <param name="useFileCreationTime">Whether the added date uses file creation time.</param>
        private static void EnsureDates(IFileSystem fileSystem, BaseItem item, ItemResolveArgs args, bool useFileCreationTime)
        {
            // See if a different path came out of the resolver than what went in
            if (!fileSystem.AreEqual(args.Path, item.Path))
            {
                var childData = args.IsDirectory ? args.GetFileSystemEntryByPath(item.Path) : null;

                if (childData is not null)
                {
                    SetDateCreated(item, childData, useFileCreationTime);
                }
                else
                {
                    var fileData = fileSystem.GetFileSystemInfo(item.Path);

                    if (fileData.Exists)
                    {
                        SetDateCreated(item, fileData, useFileCreationTime);
                    }
                }
            }
            else
            {
                SetDateCreated(item, args.FileInfo, useFileCreationTime);
            }
        }

        private static void SetDateCreated(BaseItem item, FileSystemMetadata? info, bool useFileCreationTime)
        {
            if (useFileCreationTime)
            {
                var fileCreationDate = info?.CreationTimeUtc;
                if (fileCreationDate is not null)
                {
                    var dateCreated = fileCreationDate;
                    if (dateCreated == DateTime.MinValue)
                    {
                        dateCreated = DateTime.UtcNow;
                    }

                    item.DateCreated = dateCreated.Value;
                }
            }
            else
            {
                item.DateCreated = DateTime.UtcNow;
            }

            if (info is not null && !info.IsDirectory)
            {
                item.Size = info.Length;
            }

            var fileModificationDate = info?.LastWriteTimeUtc;
            if (fileModificationDate.HasValue)
            {
                item.DateModified = fileModificationDate.Value;
            }
        }
    }
}
