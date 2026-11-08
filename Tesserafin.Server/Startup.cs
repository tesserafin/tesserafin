using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Prometheus;
using Tesserafin.Api.Middleware;
using Tesserafin.Common.Net;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Diagnostics;
using Tesserafin.Controller.Extensions;
using Tesserafin.Database.Implementations;
using Tesserafin.LiveTv.Extensions;
using Tesserafin.LiveTv.Recordings;
using Tesserafin.MediaEncoding.Hls.Extensions;
using Tesserafin.Networking;
using Tesserafin.Networking.HappyEyeballs;
using Tesserafin.Server.Core.EntryPoints;
using Tesserafin.Server.Core.Localization;
using Tesserafin.Server.Extensions;
using Tesserafin.Server.HealthChecks;
using Tesserafin.Server.Implementations.Extensions;
using Tesserafin.Server.RemoteAccess;
using Tesserafin.XbmcMetadata;

namespace Tesserafin.Server
{
    /// <summary>
    /// Startup configuration for the Kestrel webhost.
    /// </summary>
    public class Startup
    {
        private readonly CoreAppHost _serverApplicationHost;
        private readonly IConfiguration _configuration;
        private readonly IServerConfigurationManager _serverConfigurationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="Startup" /> class.
        /// </summary>
        /// <param name="appHost">The server application host.</param>
        /// <param name="configuration">The used Configuration.</param>
        public Startup(CoreAppHost appHost, IConfiguration configuration)
        {
            _serverApplicationHost = appHost;
            _configuration = configuration;
            _serverConfigurationManager = appHost.ConfigurationManager;
        }

        /// <summary>
        /// Configures the service collection for the webhost.
        /// </summary>
        /// <param name="services">The service collection.</param>
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddResponseCompression();
            services.AddHttpContextAccessor();

            // Issue #42: one correlation id per HTTP request, readable from layers below the
            // hosting stack (Tesserafin.MediaEncoding) through the Tesserafin.Controller-level abstraction.
            // Singleton is correct despite the per-request value: the accessor is stateless and
            // reads the ambient HttpContext through IHttpContextAccessor on every call.
            services.AddSingleton<IRequestCorrelationAccessor, HttpRequestCorrelationAccessor>();
            services.AddHttpsRedirection(options =>
            {
                options.HttpsPort = _serverApplicationHost.HttpsPort;
            });

            services.AddTesserafinApi(_serverApplicationHost.GetApiPluginAssemblies(), _serverConfigurationManager.GetNetworkConfiguration());
            services.AddTesserafinDbContext(_serverApplicationHost.ConfigurationManager, _configuration);
            services.AddTesserafinApiSwagger();

            // configure custom legacy authentication
            services.AddCustomAuthentication();

            services.AddTesserafinApiAuthorization();

            // R1-P (#248): the R1-A remote-access diagnostic engine and its production sources.
            // Registered here rather than inside AddTesserafinApi so that the HTTP surface and the
            // engine's own composition stay separable in review.
            services.AddPlaybackCredentials();
            services.AddRemoteAccessDiagnostics();

            // R2-L: managed Caddy lifecycle (skeleton). Concrete process runner and network
            // applier are registered so the service is constructible; production binding of
            // INetworkConfigurationStore remains a follow-up.
            services.AddManagedRemoteAccess();

            var productHeader = new ProductInfoHeaderValue(
                _serverApplicationHost.Name.Replace(' ', '-'),
                _serverApplicationHost.ApplicationVersionString);
            var acceptJsonHeader = new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json, 1.0);
            var acceptXmlHeader = new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Xml, 0.9);
            var acceptAnyHeader = new MediaTypeWithQualityHeaderValue("*/*", 0.8);
            Func<IServiceProvider, HttpMessageHandler> eyeballsHttpClientHandlerDelegate = (_) => new SocketsHttpHandler()
            {
                AutomaticDecompression = DecompressionMethods.All,
                RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8,
                ConnectCallback = HttpClientExtension.OnConnect
            };

            Func<IServiceProvider, HttpMessageHandler> defaultHttpClientHandlerDelegate = (_) => new SocketsHttpHandler()
            {
                AutomaticDecompression = DecompressionMethods.All,
                RequestHeaderEncodingSelector = (_, _) => Encoding.UTF8
            };

            services.AddHttpClient(NamedClient.Default, c =>
                {
                    c.DefaultRequestHeaders.UserAgent.Add(productHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptJsonHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptXmlHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptAnyHeader);
                })
                .ConfigurePrimaryHttpMessageHandler(eyeballsHttpClientHandlerDelegate);

            services.AddHttpClient(NamedClient.MusicBrainz, c =>
                {
                    c.DefaultRequestHeaders.UserAgent.Add(productHeader);
                    c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"({_serverApplicationHost.ApplicationUserAgentAddress})"));
                    c.DefaultRequestHeaders.Accept.Add(acceptXmlHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptAnyHeader);
                })
                .ConfigurePrimaryHttpMessageHandler(eyeballsHttpClientHandlerDelegate);

            services.AddHttpClient(NamedClient.DirectIp, c =>
                {
                    c.DefaultRequestHeaders.UserAgent.Add(productHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptJsonHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptXmlHeader);
                    c.DefaultRequestHeaders.Accept.Add(acceptAnyHeader);
                })
                .ConfigurePrimaryHttpMessageHandler(defaultHttpClientHandlerDelegate);

            // #91 / [A5]: the database check is a real bounded `SELECT 1` behind a one-method
            // interface. The interface is what makes the endpoint's 503 branch testable over HTTP
            // without a production failpoint — see IDatabaseHealthProbe.
            services.AddSingleton<IDatabaseHealthProbe, DatabaseHealthProbe>();
            services.AddHealthChecks()
                .AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name);

            services.AddHlsPlaylistGenerator();
            services.AddLiveTvServices();

            var serverUICulture = _serverConfigurationManager.Configuration.UICulture;
            if (string.IsNullOrEmpty(serverUICulture))
            {
                serverUICulture = "en-US";
            }

            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo(serverUICulture);

            services.Configure<RequestLocalizationOptions>(options =>
            {
                var supportedUICultures = LocalizationManager.GetSupportedUICultures();
                options.SupportedCultures = supportedUICultures;
                options.SupportedUICultures = supportedUICultures;
                options.DefaultRequestCulture = new RequestCulture(serverUICulture);
                options.ApplyCurrentCultureToResponseHeaders = true;
                options.FallBackToParentCultures = true;
                options.FallBackToParentUICultures = true;
            });

            services.AddHostedService<RecordingsHost>();
            services.AddHostedService<AutoDiscoveryHost>();
            services.AddHostedService<NfoUserDataSaver>();
            services.AddHostedService<LibraryChangedNotifier>();
            services.AddHostedService<UserDataChangeNotifier>();
            services.AddHostedService<RecordingNotifier>();
        }

        /// <summary>
        /// Configures the app builder for the webhost.
        /// </summary>
        /// <param name="app">The application builder.</param>
        /// <param name="env">The webhost environment.</param>
        /// <param name="appConfig">The application config.</param>
        public void Configure(
            IApplicationBuilder app,
            IWebHostEnvironment env,
            IConfiguration appConfig)
        {
            app.UseBaseUrlRedirection();

            // Wrap rest of configuration so everything only listens on BaseUrl.
            var config = _serverConfigurationManager.GetNetworkConfiguration();
            app.Map(config.BaseUrl, mainApp =>
            {
                if (env.IsDevelopment())
                {
                    mainApp.UseDeveloperExceptionPage();
                }

                mainApp.UseForwardedHeaders();

                // Issue #42: before ExceptionMiddleware, so that a request which ends in a handled
                // exception still gets its correlation id — both in the log scope covering the
                // exception log line and on the error response's X-Request-Id header.
                mainApp.UseMiddleware<RequestCorrelationMiddleware>();

                mainApp.UseMiddleware<ExceptionMiddleware>();

                mainApp.UseMiddleware<ResponseTimeMiddleware>();

                mainApp.UseWebSockets();

                mainApp.UseResponseCompression();

                mainApp.UseCors();

                mainApp.UseRequestLocalization();

                if (config.RequireHttps && _serverApplicationHost.ListenWithHttps)
                {
                    mainApp.UseHttpsRedirection();
                }

                if (appConfig.HostWebClient())
                {
                    var extensionProvider = new FileExtensionContentTypeProvider();

                    // subtitles octopus requires .data, .mem files.
                    extensionProvider.Mappings.Add(".data", MediaTypeNames.Application.Octet);
                    extensionProvider.Mappings.Add(".mem", MediaTypeNames.Application.Octet);
                    mainApp.UseDefaultFiles(new DefaultFilesOptions
                    {
                        FileProvider = new PhysicalFileProvider(_serverConfigurationManager.ApplicationPaths.WebPath),
                        RequestPath = "/web"
                    });
                    mainApp.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = new PhysicalFileProvider(_serverConfigurationManager.ApplicationPaths.WebPath),
                        RequestPath = "/web",
                        ContentTypeProvider = extensionProvider,
                        OnPrepareResponse = (context) =>
                        {
                            if (Path.GetFileName(context.File.Name).Equals("index.html", StringComparison.Ordinal))
                            {
                                context.Context.Response.Headers.CacheControl = new StringValues("no-cache");
                            }
                        }
                    });

                    mainApp.UseRobotsRedirection();
                }

                mainApp.UseStaticFiles();
                mainApp.UseAuthentication();
                mainApp.UseTesserafinApiSwagger(_serverConfigurationManager);
                mainApp.UseQueryStringDecoding();
                mainApp.UseRouting();
                mainApp.UseAuthorization();

                mainApp.UseIPBasedAccessValidation();
                mainApp.UseWebSocketHandler();
                mainApp.UseServerStartupMessage();

                if (_serverConfigurationManager.Configuration.EnableMetrics)
                {
                    // Must be registered after any middleware that could change HTTP response codes or the data will be bad
                    mainApp.UseHttpMetrics();
                }

                mainApp.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    if (_serverConfigurationManager.Configuration.EnableMetrics)
                    {
                        endpoints.MapMetrics();
                    }

                    // #91 / [A5]. Anonymous on purpose: a container runtime, an orchestrator probe
                    // or a reverse proxy has no credentials. The body carries no server detail
                    // beyond the version already published by /System/Info/Public.
                    endpoints.MapHealthChecks("/health", new HealthCheckOptions
                    {
                        ResponseWriter = HealthResponseWriter.WriteAsync,
                        ResultStatusCodes =
                        {
                            [HealthStatus.Healthy] = StatusCodes.Status200OK,
                            [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
                        }
                    }).AllowAnonymous();
                });
            });
        }
    }
}
