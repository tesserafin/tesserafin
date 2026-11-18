using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Model.IO;
using Tesserafin.Server.Core.Library;

namespace Tesserafin.Server.Core.IO
{
    /// <inheritdoc cref="ILibraryMonitor" />
    public sealed class LibraryMonitor : ILibraryMonitor, IDisposable
    {
        private readonly ILogger<LibraryMonitor> _logger;
        private readonly ILibraryRootFolderProvider _roots;
        private readonly ILibraryItemOptions _libraryOptions;
        private readonly IItemPathLookupService _itemPaths;
        private readonly ILibraryWriteTracker _writes;
        private readonly IItemChangeEvents _changes;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IFileSystem _fileSystem;
        private readonly DotIgnoreIgnoreRule _dotIgnoreIgnoreRule;

        /// <summary>
        /// The file system watchers.
        /// </summary>
        private readonly ConcurrentDictionary<string, FileSystemWatcher> _fileSystemWatchers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The affected paths.
        /// </summary>
        private readonly List<FileRefresher> _activeRefreshers = [];

        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryMonitor" /> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="roots">The physical library root provider.</param>
        /// <param name="libraryOptions">The effective library settings.</param>
        /// <param name="itemPaths">The catalogue path lookup service.</param>
        /// <param name="writes">Shared filesystem write suppression.</param>
        /// <param name="changes">The completed item change feed.</param>
        /// <param name="configurationManager">The configuration manager.</param>
        /// <param name="fileSystem">The filesystem.</param>
        /// <param name="appLifetime">The <see cref="IHostApplicationLifetime"/>.</param>
        /// <param name="dotIgnoreIgnoreRule">The .ignore rule handler.</param>
        public LibraryMonitor(
            ILogger<LibraryMonitor> logger,
            ILibraryRootFolderProvider roots,
            ILibraryItemOptions libraryOptions,
            IItemPathLookupService itemPaths,
            ILibraryWriteTracker writes,
            IItemChangeEvents changes,
            IServerConfigurationManager configurationManager,
            IFileSystem fileSystem,
            IHostApplicationLifetime appLifetime,
            DotIgnoreIgnoreRule dotIgnoreIgnoreRule)
        {
            _roots = roots;
            _libraryOptions = libraryOptions;
            _itemPaths = itemPaths;
            _writes = writes;
            _changes = changes;
            _logger = logger;
            _configurationManager = configurationManager;
            _fileSystem = fileSystem;
            _dotIgnoreIgnoreRule = dotIgnoreIgnoreRule;

            appLifetime.ApplicationStarted.Register(Start);
            appLifetime.ApplicationStopping.Register(Stop);
        }

        /// <inheritdoc />
        public void ReportFileSystemChangeBeginning(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            _writes.Begin(path);
        }

        /// <inheritdoc />
        public async void ReportFileSystemChangeComplete(string path, bool refreshPath)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            await _writes.CompleteAsync(path).ConfigureAwait(false);

            if (refreshPath)
            {
                try
                {
                    ReportFileSystemChanged(path);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in ReportFileSystemChanged for {Path}", path);
                }
            }
        }

        private bool IsLibraryMonitorEnabled(BaseItem item)
        {
            if (item is BasePluginFolder)
            {
                return false;
            }

            var options = _libraryOptions.GetLibraryOptions(item);

            return options is not null && options.EnableRealtimeMonitor;
        }

        /// <inheritdoc />
        public void Start()
        {
            _changes.ItemAdded += OnItemAdded;
            _changes.ItemRemoved += OnItemRemoved;

            var pathsToWatch = new List<string>();

            var paths = _roots
                .GetRootFolder()
                .Children
                .Where(IsLibraryMonitorEnabled)
                .OfType<Folder>()
                .SelectMany(f => f.PhysicalLocations)
                .Distinct()
                .Order();

            foreach (var path in paths)
            {
                if (!ContainsParentFolder(pathsToWatch, path))
                {
                    pathsToWatch.Add(path);
                }
            }

            foreach (var path in pathsToWatch)
            {
                StartWatchingPath(path);
            }
        }

        private void StartWatching(BaseItem item)
        {
            if (IsLibraryMonitorEnabled(item))
            {
                StartWatchingPath(item.Path);
            }
        }

        /// <summary>
        /// Handles the ItemRemoved event of the LibraryManager control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="ItemChangeEventArgs"/> instance containing the event data.</param>
        private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
        {
            if (e.Parent is AggregateFolder)
            {
                StopWatchingPath(e.Item.Path);
            }
        }

        /// <summary>
        /// Handles the ItemAdded event of the LibraryManager control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="ItemChangeEventArgs"/> instance containing the event data.</param>
        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            if (e.Parent is AggregateFolder)
            {
                StartWatching(e.Item);
            }
        }

        /// <summary>
        /// Examine a list of strings assumed to be file paths to see if it contains a parent of
        /// the provided path.
        /// </summary>
        /// <param name="lst">The LST.</param>
        /// <param name="path">The path.</param>
        /// <returns><c>true</c> if [contains parent folder] [the specified LST]; otherwise, <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <c>null</c>.</exception>
        private static bool ContainsParentFolder(IReadOnlyList<string> lst, ReadOnlySpan<char> path)
        {
            if (path.IsEmpty)
            {
                throw new ArgumentException("Path can't be empty", nameof(path));
            }

            path = path.TrimEnd(Path.DirectorySeparatorChar);

            foreach (var str in lst)
            {
                // this should be a little quicker than examining each actual parent folder...
                var compare = str.AsSpan().TrimEnd(Path.DirectorySeparatorChar);

                if (path.Equals(compare, StringComparison.OrdinalIgnoreCase)
                    || (path.StartsWith(compare, StringComparison.OrdinalIgnoreCase) && path[compare.Length] == Path.DirectorySeparatorChar))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Starts the watching path.
        /// </summary>
        /// <param name="path">The path.</param>
        private void StartWatchingPath(string path)
        {
            if (!Directory.Exists(path))
            {
                // Seeing a crash in the mono runtime due to an exception being thrown on a different thread
                _logger.LogInformation("Skipping realtime monitor for {Path} because the path does not exist", path);
                return;
            }

            // Already being watched
            if (_fileSystemWatchers.ContainsKey(path))
            {
                return;
            }

            // Creating a FileSystemWatcher over the LAN can take hundreds of milliseconds, so wrap it in a Task to do them all in parallel
            Task.Run(() =>
            {
                try
                {
                    var newWatcher = new FileSystemWatcher(path, "*")
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 65536,
                        NotifyFilter = NotifyFilters.CreationTime |
                                       NotifyFilters.DirectoryName |
                                       NotifyFilters.FileName |
                                       NotifyFilters.LastWrite |
                                       NotifyFilters.Size |
                                       NotifyFilters.Attributes
                    };

                    newWatcher.Created += OnWatcherChanged;
                    newWatcher.Deleted += OnWatcherChanged;
                    newWatcher.Renamed += OnWatcherChanged;
                    newWatcher.Changed += OnWatcherChanged;
                    newWatcher.Error += OnWatcherError;

                    if (_fileSystemWatchers.TryAdd(path, newWatcher))
                    {
                        newWatcher.EnableRaisingEvents = true;
                        _logger.LogInformation("Watching directory {Path}", path);
                    }
                    else
                    {
                        DisposeWatcher(newWatcher, false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error watching path: {Path}", path);
                }
            });
        }

        /// <summary>
        /// Stops the watching path.
        /// </summary>
        /// <param name="path">The path.</param>
        private void StopWatchingPath(string path)
        {
            if (_fileSystemWatchers.TryGetValue(path, out var watcher))
            {
                DisposeWatcher(watcher, true);
            }
        }

        /// <summary>
        /// Disposes the watcher.
        /// </summary>
        private void DisposeWatcher(FileSystemWatcher watcher, bool removeFromList)
        {
            try
            {
                using (watcher)
                {
                    _logger.LogInformation("Stopping directory watching for path {Path}", watcher.Path);

                    watcher.Created -= OnWatcherChanged;
                    watcher.Deleted -= OnWatcherChanged;
                    watcher.Renamed -= OnWatcherChanged;
                    watcher.Changed -= OnWatcherChanged;
                    watcher.Error -= OnWatcherError;

                    watcher.EnableRaisingEvents = false;
                }
            }
            finally
            {
                if (removeFromList)
                {
                    _fileSystemWatchers.TryRemove(watcher.Path, out _);
                }
            }
        }

        /// <summary>
        /// Handles the Error event of the watcher control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="ErrorEventArgs" /> instance containing the event data.</param>
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            var ex = e.GetException();
            var dw = (FileSystemWatcher)sender;

            if (ex is UnauthorizedAccessException unauthorizedAccessException)
            {
                _logger.LogError(unauthorizedAccessException, "Permission error for Directory watcher: {Path}", dw.Path);
                return;
            }

            _logger.LogError(ex, "Error in Directory watcher for: {Path}", dw.Path);

            DisposeWatcher(dw, true);
        }

        /// <summary>
        /// Handles the Changed event of the watcher control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="FileSystemEventArgs" /> instance containing the event data.</param>
        private void OnWatcherChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                ReportFileSystemChanged(e.FullPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in ReportFileSystemChanged. Path: {FullPath}", e.FullPath);
            }
        }

        /// <inheritdoc />
        public void ReportFileSystemChanged(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            if (IgnorePatterns.ShouldIgnore(path))
            {
                return;
            }

            var fileInfo = _fileSystem.GetFileSystemInfo(path);
            if (_dotIgnoreIgnoreRule.ShouldIgnore(fileInfo, null))
            {
                return;
            }

            // Ignore certain files, If the parent of an ignored path has a change event, ignore that too
            foreach (var i in _writes.Paths)
            {
                if (_fileSystem.AreEqual(i, path)
                    || _fileSystem.ContainsSubPath(i, path))
                {
                    _logger.LogDebug("Ignoring change to {Path}", path);
                    return;
                }

                // Go up a level
                var parent = Path.GetDirectoryName(i);
                if (!string.IsNullOrEmpty(parent) && _fileSystem.AreEqual(parent, path))
                {
                    _logger.LogDebug("Ignoring change to {Path}", path);
                    return;
                }
            }

            CreateRefresher(path);
        }

        private void CreateRefresher(string path)
        {
            var parentPath = Path.GetDirectoryName(path);

            lock (_activeRefreshers)
            {
                foreach (var refresher in _activeRefreshers)
                {
                    // Path is already being refreshed
                    if (_fileSystem.AreEqual(path, refresher.Path))
                    {
                        refresher.RestartTimer();
                        return;
                    }

                    // Parent folder is already being refreshed
                    if (_fileSystem.ContainsSubPath(refresher.Path, path))
                    {
                        refresher.AddPath(path);
                        return;
                    }

                    // New path is a parent
                    if (_fileSystem.ContainsSubPath(path, refresher.Path))
                    {
                        refresher.ResetPath(path, null);
                        return;
                    }

                    // They are siblings. Rebase the refresher to the parent folder.
                    if (parentPath is not null
                        && Path.GetDirectoryName(refresher.Path.AsSpan()).Equals(parentPath, StringComparison.Ordinal))
                    {
                        refresher.ResetPath(parentPath, path);
                        return;
                    }
                }

                var newRefresher = new FileRefresher(path, _configurationManager, _itemPaths, _logger);
                newRefresher.Completed += OnNewRefresherCompleted;
                _activeRefreshers.Add(newRefresher);
            }
        }

        private void OnNewRefresherCompleted(object? sender, EventArgs e)
        {
            if (sender is null)
            {
                return;
            }

            var refresher = (FileRefresher)sender;
            DisposeRefresher(refresher);
        }

        /// <summary>
        /// Stops this instance.
        /// </summary>
        public void Stop()
        {
            _changes.ItemAdded -= OnItemAdded;
            _changes.ItemRemoved -= OnItemRemoved;

            foreach (var watcher in _fileSystemWatchers.Values.ToList())
            {
                DisposeWatcher(watcher, false);
            }

            _fileSystemWatchers.Clear();
            DisposeRefreshers();
        }

        private void DisposeRefresher(FileRefresher refresher)
        {
            lock (_activeRefreshers)
            {
                refresher.Completed -= OnNewRefresherCompleted;
                refresher.Dispose();
                _activeRefreshers.Remove(refresher);
            }
        }

        private void DisposeRefreshers()
        {
            lock (_activeRefreshers)
            {
                foreach (var refresher in _activeRefreshers)
                {
                    refresher.Completed -= OnNewRefresherCompleted;
                    refresher.Dispose();
                }

                _activeRefreshers.Clear();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }
    }
}
