using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Tesserafin.Common.Configuration;
using Tesserafin.Common.Updates;
using Tesserafin.Controller;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Library;
using Tesserafin.Model.System;
using Tesserafin.Server.Implementations.StorageHelpers;

namespace Tesserafin.Server.Core;

/// <inheritdoc />
public class SystemManager : ISystemManager
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IServerApplicationPaths _applicationPaths;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IStartupOptions _startupOptions;
    private readonly IInstallationManager _installationManager;
    private readonly ILibraryStructureReader _libraryStructure;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemManager"/> class.
    /// </summary>
    /// <param name="applicationLifetime">Instance of <see cref="IHostApplicationLifetime"/>.</param>
    /// <param name="applicationHost">Instance of <see cref="IServerApplicationHost"/>.</param>
    /// <param name="applicationPaths">Instance of <see cref="IServerApplicationPaths"/>.</param>
    /// <param name="configurationManager">Instance of <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="startupOptions">Instance of <see cref="IStartupOptions"/>.</param>
    /// <param name="installationManager">Instance of <see cref="IInstallationManager"/>.</param>
    /// <param name="libraryStructure">Instance of <see cref="ILibraryStructureReader"/>.</param>
    public SystemManager(
        IHostApplicationLifetime applicationLifetime,
        IServerApplicationHost applicationHost,
        IServerApplicationPaths applicationPaths,
        IServerConfigurationManager configurationManager,
        IStartupOptions startupOptions,
        IInstallationManager installationManager,
        ILibraryStructureReader libraryStructure)
    {
        _applicationLifetime = applicationLifetime;
        _applicationHost = applicationHost;
        _applicationPaths = applicationPaths;
        _configurationManager = configurationManager;
        _startupOptions = startupOptions;
        _installationManager = installationManager;
        _libraryStructure = libraryStructure;
    }

    /// <inheritdoc />
    public SystemInfo GetSystemInfo(HttpRequest request)
    {
        return new SystemInfo
        {
            HasPendingRestart = _applicationHost.HasPendingRestart,
            IsShuttingDown = _applicationLifetime.ApplicationStopping.IsCancellationRequested,
            Version = _applicationHost.ApplicationVersionString,
            ProductName = _applicationHost.Name,
            WebSocketPortNumber = _applicationHost.HttpPort,
            CompletedInstallations = _installationManager.CompletedInstallations.ToArray(),
            Id = _applicationHost.SystemId,
#pragma warning disable CS0618 // Type or member is obsolete
            ProgramDataPath = _applicationPaths.ProgramDataPath,
            WebPath = _applicationPaths.WebPath,
            LogPath = _applicationPaths.LogDirectoryPath,
            ItemsByNamePath = _applicationPaths.InternalMetadataPath,
            InternalMetadataPath = _applicationPaths.InternalMetadataPath,
            CachePath = _applicationPaths.CachePath,
            TranscodingTempPath = _configurationManager.GetTranscodePath(),
#pragma warning restore CS0618 // Type or member is obsolete
            ServerName = _applicationHost.FriendlyName,
            LocalAddress = _applicationHost.GetSmartApiUrl(request),
            StartupWizardCompleted = _configurationManager.CommonConfiguration.IsStartupWizardCompleted,
            SupportsLibraryMonitor = true,
            PackageName = _startupOptions.PackageName,
            CastReceiverApplications = _configurationManager.Configuration.CastReceiverApplications
        };
    }

    /// <inheritdoc/>
    public SystemStorageInfo GetSystemStorageInfo()
    {
        var virtualFolderInfos = _libraryStructure
            .GetVirtualFolders()
            .Where(e => !string.IsNullOrWhiteSpace(e.ItemId)) // this should not be null but for some users it is.
            .Select(e => new LibraryStorageInfo()
            {
                Id = Guid.Parse(e.ItemId),
                Name = e.Name,
                Folders = e.Locations.Select(f => StorageHelper.GetFreeSpaceOf(f)).ToArray()
            });

        return new SystemStorageInfo()
        {
            ProgramDataFolder = StorageHelper.GetFreeSpaceOf(_applicationPaths.ProgramDataPath),
            WebFolder = StorageHelper.GetFreeSpaceOf(_applicationPaths.WebPath),
            LogFolder = StorageHelper.GetFreeSpaceOf(_applicationPaths.LogDirectoryPath),
            ImageCacheFolder = StorageHelper.GetFreeSpaceOf(_applicationPaths.ImageCachePath),
            InternalMetadataFolder = StorageHelper.GetFreeSpaceOf(_applicationPaths.InternalMetadataPath),
            CacheFolder = StorageHelper.GetFreeSpaceOf(_applicationPaths.CachePath),
            TranscodingTempFolder = StorageHelper.GetFreeSpaceOf(_configurationManager.GetTranscodePath()),
            Libraries = virtualFolderInfos.ToArray()
        };
    }

    /// <inheritdoc />
    public PublicSystemInfo GetPublicSystemInfo(HttpRequest request)
    {
        return new PublicSystemInfo
        {
            Version = _applicationHost.ApplicationVersionString,
            ProductName = _applicationHost.Name,
            Id = _applicationHost.SystemId,
            ServerName = _applicationHost.FriendlyName,
            LocalAddress = _applicationHost.GetSmartApiUrl(request),
            StartupWizardCompleted = _configurationManager.CommonConfiguration.IsStartupWizardCompleted
        };
    }

    /// <inheritdoc />
    public void Restart() => ShutdownInternal(true);

    /// <inheritdoc />
    public void Shutdown() => ShutdownInternal(false);

    private void ShutdownInternal(bool restart)
    {
        Task.Run(async () =>
        {
            await Task.Delay(100).ConfigureAwait(false);
            _applicationHost.ShouldRestart = restart;
            _applicationLifetime.StopApplication();
        });
    }
}
