using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
using Tesserafin.Server.Integration.Tests;
using Tesserafin.Server.ServerSetupApp;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// PR119: a real, booted <c>Startup</c> host - deliberately NOT <see cref="TesserafinApplicationFactory"/>
/// - wired to a REAL ffmpeg/ffprobe binary, so the PR117 URL contract
/// (<c>GET Playback/Sessions/{id}/Stream</c>) can be proven against a server that can actually serve
/// the bytes it promises (DirectPlay/remux static file, or a genuine transcode/HLS run), not just
/// plan one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TesserafinApplicationFactory"/> unconditionally sets the process-wide
/// <c>TESSERAFIN_FFMPEG__NOVALIDATION</c> environment variable, which makes
/// <c>Tesserafin.MediaEncoding.Encoder.MediaEncoder.SetFFmpegPath</c> return immediately without ever
/// resolving <c>_ffmpegPath</c> (see that method's early-return branch) - correct for the rest of
/// this project's tests, which never transcode, but fatal for this one: <c>TranscodeManager</c> would
/// be handed a null encoder path. This factory instead supplies a real path via the same
/// <c>--ffmpeg</c> CLI-switch precedence <c>StartupOptions.FFmpegPath</c> already exposes (top of
/// <c>SetFFmpegPath</c>'s CLI/env-var &gt; config &gt; $PATH precedence chain), and additionally pins
/// <c>FFmpeg:novalidation</c> to <c>false</c> as the LAST configuration provider composed - a deliberate
/// guard against a DIFFERENT xunit test class (in this same process/assembly) that already called
/// <see cref="TesserafinApplicationFactory"/> and left that environment variable set to <c>true</c> for the
/// remainder of the test run: without this override, ordering alone would decide whether this factory
/// gets real ffmpeg validation.
/// </para>
/// <para>
/// Not shared with the rest of this project on purpose: every other test here wants ffmpeg validation
/// skipped for speed/determinism. Only the PR119 end-to-end suite wants it real, so it gets its own
/// factory rather than a flag threaded through the shared one.
/// </para>
/// </remarks>
public class E2eApplicationFactory : WebApplicationFactory<Startup>
{
    private static readonly string _testPathRoot = Path.Combine(Path.GetTempPath(), "reefin-e2e-test-data");
    private readonly ConcurrentBag<IDisposable> _disposableComponents = new();
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private (string AccessToken, Guid UserId)? _cachedAuth;

    static E2eApplicationFactory()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
        StartupHelpers.PerformStaticInitialization();
    }

    /// <summary>
    /// Gets the real ffmpeg binary path this factory wires up, resolved once so every fixture/test in
    /// the same run agrees on which binary produced their media (diagnostic value in failure output).
    /// </summary>
    public static string FfmpegPath { get; } = ResolveFfmpegPath();

    /// <summary>
    /// Gets the ffmpeg the server is started with: <see cref="FfmpegPath"/>, unless a factory puts
    /// something in front of it.
    /// </summary>
    protected virtual string ServerFfmpegPath => FfmpegPath;

    /// <summary>
    /// Gets the directory the server keeps its data, config and cache under: a new one per factory,
    /// unless a factory boots again on what an earlier one left behind.
    /// </summary>
    protected virtual string DataRoot { get; } = Path.Combine(_testPathRoot, "test-host-" + Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));

    /// <summary>
    /// Completes the startup wizard exactly once for this factory's single booted server instance,
    /// caching the resulting admin token/user id for every subsequent caller. Needed because
    /// <see cref="IClassFixture{TFixture}"/> shares ONE <see cref="E2eApplicationFactory"/> (one real
    /// boot) across every <c>[Fact]</c> in the test class, while xunit runs those facts concurrently
    /// by default - a second, concurrent <c>AuthHelper.CompleteStartupAsync</c> call against an
    /// already-wizard-completed server 401s (<c>/Startup/User</c> requires elevation once
    /// <c>IsStartupWizardCompleted</c> is true). Discovered empirically: running this class's tests
    /// together (not filtered to one at a time) surfaced exactly this race.
    /// </summary>
    /// <returns>The shared admin access token and user id.</returns>
    /// <remarks>
    /// Deliberately does NOT take (or mutate) a caller-supplied <see cref="HttpClient"/>: it runs the
    /// one-time wizard completion through its OWN scoped, throwaway client, so every caller - whether
    /// it wins the race and actually performs the completion, or hits the cache - always follows the
    /// exact same "add the returned token to my own client's headers" step afterward, with no risk of
    /// a winning caller's client ending up with the auth header added twice.
    /// </remarks>
    public async Task<(string AccessToken, Guid UserId)> EnsureAuthenticatedAsync()
    {
        if (_cachedAuth is { } cached)
        {
            return cached;
        }

        await _authLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedAuth is { } cachedAfterLock)
            {
                return cachedAfterLock;
            }

            using var probeClient = CreateClient();
            var accessToken = await AuthHelper.CompleteStartupAsync(probeClient).ConfigureAwait(false);
            probeClient.DefaultRequestHeaders.AddAuthHeader(accessToken);
            var userDto = await AuthHelper.GetUserDtoAsync(probeClient).ConfigureAwait(false);

            _cachedAuth = (accessToken, userDto.Id);
            return _cachedAuth.Value;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <inheritdoc/>
    protected override IHostBuilder CreateHostBuilder()
    {
        return new HostBuilder();
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Deliberately NOT setting TESSERAFIN_FFMPEG__NOVALIDATION here (contrast with
        // TesserafinApplicationFactory) - this factory wants the real validation/probing path to run
        // against FfmpegPath below.
        var commandLineOpts = new StartupOptions
        {
            FFmpegPath = ServerFfmpegPath,
        };

        var webHostPathRoot = DataRoot;
        Directory.CreateDirectory(Path.Combine(webHostPathRoot, "logs"));
        var configDir = Path.Combine(webHostPathRoot, "config");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(Path.Combine(webHostPathRoot, "cache"));
        Directory.CreateDirectory(Path.Combine(webHostPathRoot, "jellyfin-web"));

        // Pre-seed encoding.xml with hardware encoding disabled, BEFORE MediaEncoder.SetFFmpegPath
        // runs during CreateHost() below. Discovered empirically: this sandbox has a /dev/dri render
        // node, so MediaEncoder's startup hardware-backend auto-select (EncodingOptions.EnableHardwareEncoding
        // defaults to true) picks HardwareAccelerationType=vaapi - its own startup TRIAL probe passes,
        // but a REAL transcode job later actually using vaapi hangs indefinitely in this environment
        // (confirmed: the DirectPlay/Remux/subtitle scenarios never transcode at all and are
        // unaffected; only the HLS transcode scenario, which does, hung past 150s until forced software
        // encoding was pinned here). This suite only needs to prove the URL contract serves real bytes
        // via a real encode - not validate hardware acceleration - so software x264/aac is pinned
        // unconditionally, regardless of what accelerators happen to be present on whatever machine
        // runs this suite (local dev box or ci/smoke-e2e.sh's Docker image).
        File.WriteAllText(
            Path.Combine(configDir, "encoding.xml"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <EncodingOptions>
              <EnableHardwareEncoding>false</EnableHardwareEncoding>
              <HardwareAccelerationType>none</HardwareAccelerationType>
            </EncodingOptions>
            """);

        var appPaths = new ServerApplicationPaths(
            webHostPathRoot,
            Path.Combine(webHostPathRoot, "logs"),
            Path.Combine(webHostPathRoot, "config"),
            Path.Combine(webHostPathRoot, "cache"),
            Path.Combine(webHostPathRoot, "jellyfin-web"));

        StartupHelpers.InitLoggingConfigFile(appPaths).GetAwaiter().GetResult();

        var startupConfig = Program.CreateAppConfiguration(commandLineOpts, appPaths);

        ILoggerFactory loggerFactory = new SerilogLoggerFactory();
        _disposableComponents.Add(loggerFactory);

        var appHost = new TestAppHost(
            appPaths,
            loggerFactory,
            commandLineOpts,
            startupConfig);
        _disposableComponents.Add(appHost);

        builder.ConfigureServices(services => appHost.Init(services))
            .ConfigureWebHostBuilder(appHost, startupConfig, appPaths, NullLogger.Instance)
            .ConfigureAppConfiguration((context, configBuilder) =>
            {
                configBuilder
                    .SetBasePath(appPaths.ConfigurationDirectoryPath)
                    .AddInMemoryCollection(ConfigurationOptions.DefaultConfiguration)
                    .AddEnvironmentVariables("TESSERAFIN_")
                    .AddInMemoryCollection(commandLineOpts.ConvertToConfig())
                    // Last, highest-precedence: guards against a sibling TesserafinApplicationFactory-based
                    // test in the same process having left TESSERAFIN_FFMPEG__NOVALIDATION=true behind it.
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [Tesserafin.Controller.Extensions.ConfigurationExtensions.FfmpegSkipValidationKey] = bool.FalseString,
                    });
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

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        foreach (var disposable in _disposableComponents)
        {
            disposable.Dispose();
        }

        _disposableComponents.Clear();

        if (disposing)
        {
            _authLock.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Resolves a real ffmpeg binary for this factory to hand the booted server (CLI-switch
    /// precedence, §<c>MediaEncoder.SetFFmpegPath</c>). Prefers <c>FFMPEG_PATH</c> if a caller sets it
    /// (parity with how <c>ci/smoke.sh</c>'s Docker image guarantees ffmpeg on <c>$PATH</c>), otherwise
    /// falls back to the bare command name and lets the OS resolve it via <c>$PATH</c> - the same
    /// posture <c>HlsSmokeTests</c> already relies on for its own real ffmpeg process.
    /// </summary>
    private static string ResolveFfmpegPath() => Environment.GetEnvironmentVariable("FFMPEG_PATH") is { Length: > 0 } explicitPath
        ? explicitPath
        : "ffmpeg";

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
