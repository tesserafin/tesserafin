using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.WebSocketListeners;
using Tesserafin.Api.WebSockets;
using Tesserafin.Controller;
using Tesserafin.Controller.Authentication;
using Tesserafin.Controller.BaseItemManager;
using Tesserafin.Controller.Devices;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Events;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Lyrics;
using Tesserafin.Controller.Net;
using Tesserafin.Controller.Security;
using Tesserafin.Controller.Trickplay;
using Tesserafin.Database.Implementations;
using Tesserafin.Drawing;
using Tesserafin.Drawing.Skia;
using Tesserafin.LiveTv;
using Tesserafin.Model.Activity;
using Tesserafin.Providers.Lyric;
using Tesserafin.Server.Core;
using Tesserafin.Server.Implementations.Activity;
using Tesserafin.Server.Implementations.Devices;
using Tesserafin.Server.Implementations.Events;
using Tesserafin.Server.Implementations.Extensions;
using Tesserafin.Server.Implementations.Security;
using Tesserafin.Server.Implementations.Trickplay;
using Tesserafin.Server.Implementations.Users;

namespace Tesserafin.Server
{
    /// <summary>
    /// Implementation of the abstract <see cref="ApplicationHost" /> class.
    /// </summary>
    public class CoreAppHost : ApplicationHost
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CoreAppHost" /> class.
        /// </summary>
        /// <param name="applicationPaths">The <see cref="ServerApplicationPaths" /> to be used by the <see cref="CoreAppHost" />.</param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory" /> to be used by the <see cref="CoreAppHost" />.</param>
        /// <param name="options">The <see cref="StartupOptions" /> to be used by the <see cref="CoreAppHost" />.</param>
        /// <param name="startupConfig">The <see cref="IConfiguration" /> to be used by the <see cref="CoreAppHost" />.</param>
        public CoreAppHost(
            IServerApplicationPaths applicationPaths,
            ILoggerFactory loggerFactory,
            IStartupOptions options,
            IConfiguration startupConfig)
            : base(
                applicationPaths,
                loggerFactory,
                options,
                startupConfig)
        {
        }

        /// <inheritdoc/>
        protected override void RegisterServices(IServiceCollection serviceCollection)
        {
            // Register an image encoder
            bool useSkiaEncoder = SkiaEncoder.IsNativeLibAvailable();
            Type imageEncoderType = useSkiaEncoder
                ? typeof(SkiaEncoder)
                : typeof(NullImageEncoder);
            serviceCollection.AddSingleton(typeof(IImageEncoder), imageEncoderType);

            // Log a warning if the Skia encoder could not be used
            if (!useSkiaEncoder)
            {
                Logger.LogWarning("Skia not available. Will fallback to {ImageEncoder}.", nameof(NullImageEncoder));
            }

            serviceCollection.AddEventServices();
            serviceCollection.AddSingleton<IBaseItemManager, BaseItemManager>();
            serviceCollection.AddSingleton<IEventManager, EventManager>();

            serviceCollection.AddSingleton<IActivityManager, ActivityManager>();
            serviceCollection.AddSingleton<IUserManager, UserManager>();
            serviceCollection.AddSingleton<IAuthenticationProvider, DefaultAuthenticationProvider>();
            serviceCollection.AddSingleton<IAuthenticationProvider, InvalidAuthProvider>();
            serviceCollection.AddSingleton<IPasswordResetProvider, DefaultPasswordResetProvider>();
            serviceCollection.AddSingleton<IDisplayPreferencesManager, DisplayPreferencesManager>();
            serviceCollection.AddSingleton<IDeviceManager, DeviceManager>();
            serviceCollection.AddSingleton<ITrickplayManager, TrickplayManager>();

            // HTTP delivery and WebSocket upgrades are composed by the executable, not the core.
            serviceCollection.AddSingleton<IWebSocketManager, WebSocketManager>();
            serviceCollection.AddSingleton<IHlsJobOwnershipAuthorizer, HlsJobOwnershipAuthorizer>();
            serviceCollection.AddSingleton<PlaybackSessionDeliveryGuard>();
            serviceCollection.AddScoped<AudioHelper>();
            serviceCollection.AddScoped<DynamicHlsHelper>();

            // TODO search the assemblies instead of adding them manually?
            serviceCollection.AddSingleton<IWebSocketListener, SessionWebSocketListener>();
            serviceCollection.AddSingleton<IWebSocketListener, ActivityLogWebSocketListener>();
            serviceCollection.AddSingleton<IWebSocketListener, ScheduledTasksWebSocketListener>();
            serviceCollection.AddSingleton<IWebSocketListener, SessionInfoWebSocketListener>();

            serviceCollection.AddSingleton<IAuthorizationContext, AuthorizationContext>();

            serviceCollection.AddScoped<IAuthenticationManager, AuthenticationManager>();

            foreach (var type in GetExportTypes<ILyricProvider>())
            {
                serviceCollection.AddSingleton(typeof(ILyricProvider), type);
            }

            foreach (var type in GetExportTypes<ILyricParser>())
            {
                serviceCollection.AddSingleton(typeof(ILyricParser), type);
            }

            base.RegisterServices(serviceCollection);
        }

        /// <inheritdoc />
        protected override IEnumerable<Assembly> GetAssembliesWithPartsInternal()
        {
            // Tesserafin.Server
            yield return typeof(CoreAppHost).Assembly;

            // Tesserafin.Database.Implementations
            yield return typeof(TesserafinDbContext).Assembly;

            // Tesserafin.Server.Implementations
            yield return typeof(ServiceCollectionExtensions).Assembly;

            // Tesserafin.LiveTv
            yield return typeof(LiveTvManager).Assembly;
        }
    }
}
