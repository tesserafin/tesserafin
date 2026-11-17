#pragma warning disable CS1591

using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="IUserRootFolderProvider"/> - autonomous
    /// owner of the per-server <see cref="UserRootFolder"/> construction and cache (RFC
    /// <c>docs/rfc-di-query-user-views-v2.md</c> §1, §8, §9, PR107). Replaces the historical
    /// <c>ApplicationHost.cs:568</c> factory lambda that cast <c>ILibraryManager</c> to this port
    /// (RFC §0's "piège n°1") - <see cref="LibraryManager"/> is now a <em>consumer</em> of this
    /// class, never the other way around.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ported off</b> <c>LibraryManager.GetUserRootFolder</c> (<c>LibraryManager.cs:1171-1216</c>),
    /// behavior preserved exactly: lazy double-checked-lock cache (<see cref="_userRootFolder"/>,
    /// <c>volatile</c>), same lookup-then-resolve-fallback shape, same "reset path if program data
    /// moved" tail.
    /// </para>
    /// <para>
    /// <b>Why <c>ResolvePath</c> is replicated, not called (RFC PR107 finding)</b>: the historical
    /// fallback branch (item not yet in the repository) calls
    /// <c>LibraryManager.ResolvePath(fileInfo).DeepCopy&lt;Folder, UserRootFolder&gt;()</c>. Tracing
    /// that call for <em>this exact path</em> (<c>DefaultUserViewsPath</c>, no parent, no collection
    /// type) shows it is structurally impossible to call from here without an <c>ILibraryManager</c>
    /// reference: <c>ItemResolveArgs</c>'s constructor requires one (used internally by
    /// <c>GetActualFileSystemChildren</c>'s per-child <c>IgnoreFile</c> check and by
    /// <c>GetConfiguredContentType</c>), and <c>ResolveItem</c> walks the full, DI-pluggable
    /// <c>IItemResolver[]</c>/<c>IResolverIgnoreRule[]</c> collections (which can include
    /// plugin-registered resolvers/rules) rather than a fixed, bounded set. Injecting
    /// <c>ILibraryManager</c> here would violate RFC invariant I1; extracting the general resolver
    /// pipeline into a leaf is out of scope for this PR (would recreate the very machinery PR106-111
    /// carve around, for a single call site).
    /// </para>
    /// <para>
    /// What <em>is</em> bounded, and what this method reproduces instead: for a plain, existing
    /// directory with no parent (<c>args.Parent is null</c>, <c>args.IsPhysicalRoot</c> is
    /// <c>false</c> for <c>DefaultUserViewsPath</c> - it is not <c>RootFolderPath</c>), every
    /// higher-priority resolver in the standard set only claims files/directories that look like
    /// media (movies, series, seasons, episodes, etc.); an internal, app-owned "user-views" directory
    /// is never claimed by any of them, so resolution deterministically falls through to
    /// <c>FolderResolver</c> (<c>Tesserafin.Server.Core/Library/Resolvers/FolderResolver.cs</c>,
    /// <c>ResolverPriority.Last</c>), which returns a bare <c>new Folder()</c> for any directory -
    /// the child-enumeration and ignore-rule machinery run but are inert for this outcome (the
    /// resolver never inspects <c>args.FileSystemChildren</c>). <see cref="ResolveUserRootFolder"/>
    /// reproduces the deterministic composition of that outcome with
    /// <c>ResolverHelper.SetInitialItemValues</c> (<c>ResolverHelper.cs:62-86</c>) and
    /// <c>GenericFolderResolver&lt;Folder&gt;.SetInitialItemValues</c>'s <c>IsRoot</c> assignment
    /// (<c>GenericFolderResolver.cs:21-26</c>), verified field-by-field against the source, including
    /// one preserved quirk: <c>ResolverHelper</c> computes the item's id from <c>item.GetType()</c>
    /// <em>before</em> the outer <c>DeepCopy&lt;Folder, UserRootFolder&gt;()</c> call, i.e. with
    /// <c>typeof(Folder)</c>, not <c>typeof(UserRootFolder)</c> - different from the id used for the
    /// cache lookup a few lines above it (computed with <c>typeof(UserRootFolder)</c>). This is
    /// reproduced verbatim (not "fixed") for parity with the historical behavior; see
    /// <c>UserRootFolderProviderTests</c> / <c>LibraryManagerUserRootFolderProviderTests</c> for the
    /// parity assertion.
    /// </para>
    /// <para>
    /// <b>Accepted deviation</b>: a plugin registering an <c>IItemResolver</c> with higher priority
    /// than <c>FolderResolver</c> that specifically claims <c>DefaultUserViewsPath</c> (an internal,
    /// non-media, application-owned directory) would change the historical outcome in a way this
    /// replication does not reproduce. This is treated as an extreme, unsupported edge case (no
    /// plugin has a legitimate reason to target this path) rather than a blocking gap - see the PR107
    /// report for the full reasoning.
    /// </para>
    /// <para>
    /// <b>Dependencies (RFC I1)</b>: <see cref="IServerConfigurationManager"/> (path + metadata
    /// configuration), <see cref="IFileSystem"/> (directory metadata), <see cref="IItemStore"/>
    /// (id generation only - <c>GetNewItemId</c>; this port never creates/registers an item, matching
    /// the historical method, which never persists the constructed <c>UserRootFolder</c> either) and
    /// <see cref="IItemLookupService"/> (cache lookup). None of these reference
    /// <c>ILibraryManager</c>, <c>IUserViewManager</c>, <c>IChannelManager</c> or
    /// <c>ILiveTvManager</c>.
    /// </para>
    /// <para>
    /// <b>Lifecycle / absence of invalidation (intentional, matches historical behavior)</b>: once
    /// resolved, <see cref="_userRootFolder"/> is cached for the lifetime of this singleton and is
    /// <em>never</em> invalidated or recomputed - no <c>InvalidateUserRootFolder</c>/
    /// <c>ResetUserRootFolder</c>/<c>_userRootFolder = null</c> exists anywhere, exactly as in the
    /// historical <c>LibraryManager</c> field it replaces (RFC §1: "jamais invalidé" - verified by
    /// repo-wide grep, none found outside tests). If the program-data path moves after the first
    /// resolution, only the cached instance's <c>Path</c> is patched in place (the "reset path if
    /// program data folder was moved" branch below) - the cache itself is not rebuilt.
    /// </para>
    /// </remarks>
    internal sealed class UserRootFolderProvider : IUserRootFolderProvider
    {
        private readonly IItemReadServices _readServices;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemStore _itemStore;
        private readonly IItemLookupService _itemLookupService;
        private readonly ILogger<UserRootFolderProvider> _logger;

        private readonly Lock _userRootFolderSyncLock = new();

        private volatile UserRootFolder? _userRootFolder;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserRootFolderProvider"/> class.
        /// </summary>
        /// <param name="configurationManager">The server configuration manager (path + metadata configuration).</param>
        /// <param name="fileSystem">The file system.</param>
        /// <param name="itemStore">The item store leaf (id generation only; see PR106a).</param>
        /// <param name="itemLookupService">The item lookup service (cache lookup; see PR75/PR76).</param>
        /// <param name="logger">The logger.</param>
        /// <param name="readServices">The catalogue reads attached to a root folder created before it is first saved.</param>
        public UserRootFolderProvider(
            IServerConfigurationManager configurationManager,
            IFileSystem fileSystem,
            IItemStore itemStore,
            IItemLookupService itemLookupService,
            ILogger<UserRootFolderProvider> logger,
            IItemReadServices readServices)
        {
            _readServices = readServices;
            _configurationManager = configurationManager;
            _fileSystem = fileSystem;
            _itemStore = itemStore;
            _itemLookupService = itemLookupService;
            _logger = logger;
        }

        /// <inheritdoc />
        public Folder GetUserRootFolder()
        {
            if (_userRootFolder is null)
            {
                lock (_userRootFolderSyncLock)
                {
                    if (_userRootFolder is null)
                    {
                        var userRootPath = _configurationManager.ApplicationPaths.DefaultUserViewsPath;

                        _logger.LogDebug("Creating userRootPath at {Path}", userRootPath);
                        Directory.CreateDirectory(userRootPath);

                        var newItemId = _itemStore.GetNewItemId(userRootPath, typeof(UserRootFolder));
                        UserRootFolder? tmpItem = null;
                        try
                        {
                            tmpItem = _itemLookupService.GetItemById(newItemId) as UserRootFolder;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error creating UserRootFolder {Path}", newItemId);
                        }

                        if (tmpItem is null)
                        {
                            _logger.LogDebug("Creating new userRootFolder with DeepCopy");
                            tmpItem = ResolveUserRootFolder(userRootPath);
                        }

                        tmpItem.ReadServices ??= _readServices;

                        // In case program data folder was moved
                        if (!string.Equals(tmpItem.Path, userRootPath, StringComparison.Ordinal))
                        {
                            _logger.LogInformation("Resetting user root folder path to {0}", userRootPath);
                            tmpItem.Path = userRootPath;
                        }

                        _userRootFolder = tmpItem;
                        _logger.LogDebug("Setting userRootFolder: {Folder}", _userRootFolder);
                    }
                }
            }

            return _userRootFolder;
        }

        /// <summary>
        /// Reproduces the deterministic, bounded subset of
        /// <c>LibraryManager.ResolvePath(fileInfo).DeepCopy&lt;Folder, UserRootFolder&gt;()</c>
        /// actually exercised when resolving <paramref name="userRootPath"/> - see the type-level
        /// remarks for the full trace and the accepted deviation.
        /// </summary>
        /// <param name="userRootPath">The (already-created) user root directory path.</param>
        /// <returns>The resolved <see cref="UserRootFolder"/>, never persisted (matches historical behavior).</returns>
        private UserRootFolder ResolveUserRootFolder(string userRootPath)
        {
            var directoryInfo = _fileSystem.GetDirectoryInfo(userRootPath);

            // FolderResolver.Resolve(args) -> new Folder() (ResolverPriority.Last; the only resolver
            // that ever claims an internal, non-media directory like this one).
            var folder = new Folder { ReadServices = _readServices };

            // GenericFolderResolver<Folder>.SetInitialItemValues: item.IsRoot = args.Parent is null.
            // The historical call site (LibraryManager.cs:1198) never passes a parent.
            folder.IsRoot = true;

            // ResolverHelper.SetInitialItemValues(item, args, fileSystem, libraryManager), in order:
            folder.Path = userRootPath;

            // NB: computed with typeof(Folder), not typeof(UserRootFolder) - item.GetType() at this
            // point in the historical code is still the intermediate Folder, before the outer
            // DeepCopy<Folder, UserRootFolder>() call. Preserved verbatim - see type remarks.
            folder.Id = _itemStore.GetNewItemId(folder.Path, typeof(Folder));

            // EnsureName: item.Name is empty, args.FileInfo.IsDirectory is true -> fileInfo.Name.
            folder.Name = directoryInfo.Name;

            // item.Path never contains "[dontfetchmeta]" here, and item.GetParents() is empty (no
            // parent was ever set on this freshly-constructed item) - both historical disjuncts are
            // false for this path, so this always evaluates to false. Spelled out rather than
            // hardcoded so a future change to the historical shape (e.g. a parent being set) is easy
            // to notice against this comment.
            folder.IsLocked = folder.Path.Contains("[dontfetchmeta]", StringComparison.OrdinalIgnoreCase);

            // EnsureDates: args.Path == item.Path here (resolver never changes the path), so
            // SetDateCreated(item, args.FileInfo) - i.e. from the directory's own file-system metadata.
            var metadataConfiguration = _configurationManager.GetMetadataConfiguration();
            if (metadataConfiguration.UseFileCreationTimeForDateAdded)
            {
                var creationTimeUtc = directoryInfo.CreationTimeUtc;
                folder.DateCreated = creationTimeUtc == DateTime.MinValue ? DateTime.UtcNow : creationTimeUtc;
            }
            else
            {
                folder.DateCreated = DateTime.UtcNow;
            }

            folder.DateModified = directoryInfo.LastWriteTimeUtc;

            return folder.DeepCopy<Folder, UserRootFolder>();
        }
    }
}
