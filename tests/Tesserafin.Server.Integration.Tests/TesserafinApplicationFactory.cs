using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Extensions.Logging;
using Tesserafin.Common;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Server.Core;
using Tesserafin.Server.Extensions;
using Tesserafin.Server.Helpers;
using Tesserafin.Server.ServerSetupApp;

namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// Factory for bootstrapping the Tesserafin application in memory for functional end to end tests.
    /// </summary>
    public class TesserafinApplicationFactory : WebApplicationFactory<Startup>
    {
        private static readonly string _testPathRoot = Path.Combine(Path.GetTempPath(), "reefin-test-data");
        private readonly ConcurrentBag<IDisposable> _disposableComponents = new ConcurrentBag<IDisposable>();

        /// <summary>
        /// Initializes static members of the <see cref="TesserafinApplicationFactory"/> class.
        /// </summary>
        static TesserafinApplicationFactory()
        {
            // Perform static initialization that only needs to happen once per test-run
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
                .CreateLogger();
            StartupHelpers.PerformStaticInitialization();
        }

        /// <inheritdoc/>
        protected override IHostBuilder CreateHostBuilder()
        {
            return new HostBuilder();
        }

        /// <inheritdoc/>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Skip ffmpeg check for testing
            Environment.SetEnvironmentVariable("TESSERAFIN_FFMPEG__NOVALIDATION", "true");
            // Specify the startup command line options
            var commandLineOpts = new StartupOptions();

            // Use a temporary directory for the application paths
            var webHostPathRoot = CreateWebHostPathRoot();
            Directory.CreateDirectory(Path.Combine(webHostPathRoot, "logs"));
            Directory.CreateDirectory(Path.Combine(webHostPathRoot, "config"));
            Directory.CreateDirectory(Path.Combine(webHostPathRoot, "cache"));
            Directory.CreateDirectory(Path.Combine(webHostPathRoot, "jellyfin-web"));
            var appPaths = new ServerApplicationPaths(
                webHostPathRoot,
                Path.Combine(webHostPathRoot, "logs"),
                Path.Combine(webHostPathRoot, "config"),
                Path.Combine(webHostPathRoot, "cache"),
                Path.Combine(webHostPathRoot, "jellyfin-web"));

            // Create the logging config file
            // TODO: We shouldn't need to do this since we are only logging to console
            StartupHelpers.InitLoggingConfigFile(appPaths).GetAwaiter().GetResult();

            // Create a copy of the application configuration to use for startup
            var startupConfig = Program.CreateAppConfiguration(commandLineOpts, appPaths);

            ILoggerFactory loggerFactory = new SerilogLoggerFactory();

            _disposableComponents.Add(loggerFactory);

            // Create the app host and initialize it
            var appHost = new TestAppHost(
                appPaths,
                loggerFactory,
                commandLineOpts,
                startupConfig);
            _disposableComponents.Add(appHost);

            builder.ConfigureServices(services => appHost.Init(services))
                .ConfigureWebHostBuilder(appHost, startupConfig, appPaths, NullLogger.Instance)
                .ConfigureAppConfiguration((context, builder) =>
                {
                    builder
                        .SetBasePath(appPaths.ConfigurationDirectoryPath)
                        .AddInMemoryCollection(ConfigurationOptions.DefaultConfiguration)
                        .AddEnvironmentVariables("TESSERAFIN_")
                        .AddInMemoryCollection(commandLineOpts.ConvertToConfig());
                })
                .ConfigureServices(e => e
                    .AddSingleton<IStartupLogger, NullStartupLogger<object>>()
                    .AddTransient(typeof(IStartupLogger<>), typeof(NullStartupLogger<>))
                    .AddSingleton(e));
        }

        /// <inheritdoc/>
        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = builder.Build();
            var appHost = (TestAppHost)host.Services.GetRequiredService<IApplicationHost>();
            appHost.ServiceProvider = host.Services;
            var applicationPaths = appHost.ServiceProvider.GetRequiredService<IApplicationPaths>();
            Program.ApplyStartupMigrationAsync((ServerApplicationPaths)applicationPaths, appHost.ServiceProvider.GetRequiredService<IConfiguration>(), new()).GetAwaiter().GetResult();
            Program.ApplyCoreMigrationsAsync(appHost.ServiceProvider, Migrations.Stages.TesserafinMigrationStageTypes.CoreInitialisation).GetAwaiter().GetResult();
            appHost.InitializeServices(Mock.Of<IConfiguration>()).GetAwaiter().GetResult();
            Program.ApplyCoreMigrationsAsync(appHost.ServiceProvider, Migrations.Stages.TesserafinMigrationStageTypes.AppInitialisation).GetAwaiter().GetResult();
            // Migrations seed an external catalog. These test hosts use no external plugins;
            // keep startup's scheduled updater from contacting that catalog. Catalog tests
            // must configure their own explicit repository fixtures after startup.
            appHost.ServiceProvider.GetRequiredService<IServerConfigurationManager>().Configuration.PluginRepositories = [];
            host.Start();

            appHost.RunStartupTasksAsync().GetAwaiter().GetResult();

            return host;
        }

        /// <summary>
        /// Gets the directory that holds the data of the server: a new one for each factory, unless a
        /// test restarts a server on the data of a previous one.
        /// </summary>
        /// <returns>The directory.</returns>
        protected virtual string CreateWebHostPathRoot()
            => Path.Combine(_testPathRoot, "test-host-" + Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            foreach (var disposable in _disposableComponents)
            {
                disposable.Dispose();
            }

            _disposableComponents.Clear();

            base.Dispose(disposing);
        }

        private sealed class NullStartupLogger<TCategory> : IStartupLogger<TCategory>
        {
            public StartupLogTopic? Topic => throw new NotImplementedException();

            public IStartupLogger BeginGroup(FormattableString logEntry)
            {
                return this;
            }

            public IStartupLogger<TCategory1> BeginGroup<TCategory1>(FormattableString logEntry)
            {
                return new NullStartupLogger<TCategory1>();
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return NullLogger.Instance.BeginScope(state);
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return NullLogger.Instance.IsEnabled(logLevel);
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                NullLogger.Instance.Log(logLevel, eventId, state, exception, formatter);
            }

            public Microsoft.Extensions.Logging.ILogger With(Microsoft.Extensions.Logging.ILogger logger)
            {
                return this;
            }

            public IStartupLogger<TCategory1> With<TCategory1>(Microsoft.Extensions.Logging.ILogger logger)
            {
                return new NullStartupLogger<TCategory1>();
            }

            IStartupLogger<TCategory> IStartupLogger<TCategory>.BeginGroup(FormattableString logEntry)
            {
                return new NullStartupLogger<TCategory>();
            }

            IStartupLogger IStartupLogger.With(Microsoft.Extensions.Logging.ILogger logger)
            {
                return this;
            }

            IStartupLogger<TCategory> IStartupLogger<TCategory>.With(Microsoft.Extensions.Logging.ILogger logger)
            {
                return this;
            }
        }
    }
}
