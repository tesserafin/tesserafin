using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;
using Tesserafin.Api.Auth;
using Tesserafin.Api.Auth.AnonymousLanAccessPolicy;
using Tesserafin.Api.Auth.DefaultAuthorizationPolicy;
using Tesserafin.Api.Auth.FirstTimeSetupPolicy;
using Tesserafin.Api.Auth.LocalAccessOrRequiresElevationPolicy;
using Tesserafin.Api.Auth.MediaDeliveryPolicy;
using Tesserafin.Api.Auth.PlaybackCapabilityPolicy;
using Tesserafin.Api.Auth.SyncPlayAccessPolicy;
using Tesserafin.Api.Auth.UserPermissionPolicy;
using Tesserafin.Api.Constants;
using Tesserafin.Api.Controllers;
using Tesserafin.Api.Formatters;
using Tesserafin.Api.ModelBinders;
using Tesserafin.Common.Api;
using Tesserafin.Common.Net;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Entities;
using Tesserafin.Server.Api.RemoteAccess;
using Tesserafin.Server.Configuration;
using Tesserafin.Server.Core;
using Tesserafin.Server.Diagnostics.RemoteAccess;
using Tesserafin.Server.Filters;
using Tesserafin.Server.Implementations.Security.PlaybackCredentials;
using AuthenticationSchemes = Tesserafin.Api.Constants.AuthenticationSchemes;

namespace Tesserafin.Server.Extensions
{
    /// <summary>
    /// API specific extensions for the service collection.
    /// </summary>
    public static class ApiServiceCollectionExtensions
    {
        /// <summary>
        /// Adds reefin API authorization policies to the DI container.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddTesserafinApiAuthorization(this IServiceCollection serviceCollection)
        {
            // The default handler must be first so that it is evaluated first
            serviceCollection.AddSingleton<IAuthorizationHandler, DefaultAuthorizationHandler>();
            serviceCollection.AddSingleton<IAuthorizationHandler, UserPermissionHandler>();
            serviceCollection.AddSingleton<IAuthorizationHandler, FirstTimeSetupHandler>();
            serviceCollection.AddSingleton<IAuthorizationHandler, AnonymousLanAccessHandler>();
            serviceCollection.AddSingleton<IAuthorizationHandler, SyncPlayAccessHandler>();
            serviceCollection.AddSingleton<IAuthorizationHandler, LocalAccessOrRequiresElevationHandler>();
            serviceCollection.AddSingleton<IAuthorizationHandler, MediaDeliveryHandler>();

            return serviceCollection.AddAuthorizationCore(options =>
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                    .AddAuthenticationSchemes(AuthenticationSchemes.CustomAuthentication)
                    .AddRequirements(new DefaultAuthorizationRequirement())
                    .Build();

                options.AddPolicy(Policies.AnonymousLanAccessPolicy, new AnonymousLanAccessRequirement());
                options.AddPolicy(Policies.CollectionManagement, new UserPermissionRequirement(PermissionKind.EnableCollectionManagement));
                options.AddPolicy(Policies.Download, new UserPermissionRequirement(PermissionKind.EnableContentDownloading));
                options.AddPolicy(Policies.FirstTimeSetupOrDefault, new FirstTimeSetupRequirement(requireAdmin: false));
                options.AddPolicy(Policies.FirstTimeSetupOrElevated, new FirstTimeSetupRequirement());
                options.AddPolicy(Policies.FirstTimeSetupOrIgnoreParentalControl, new FirstTimeSetupRequirement(false, false));
                options.AddPolicy(Policies.IgnoreParentalControl, new DefaultAuthorizationRequirement(validateParentalSchedule: false));
                options.AddPolicy(Policies.LiveTvAccess, new UserPermissionRequirement(PermissionKind.EnableLiveTvAccess));
                options.AddPolicy(Policies.LiveTvManagement, new UserPermissionRequirement(PermissionKind.EnableLiveTvManagement));
                options.AddPolicy(Policies.LocalAccessOrRequiresElevation, new LocalAccessOrRequiresElevationRequirement());
                options.AddPolicy(Policies.SyncPlayHasAccess, new SyncPlayAccessRequirement(SyncPlayAccessRequirementType.HasAccess));
                options.AddPolicy(Policies.SyncPlayCreateGroup, new SyncPlayAccessRequirement(SyncPlayAccessRequirementType.CreateGroup));
                options.AddPolicy(Policies.SyncPlayJoinGroup, new SyncPlayAccessRequirement(SyncPlayAccessRequirementType.JoinGroup));
                options.AddPolicy(Policies.SyncPlayIsInGroup, new SyncPlayAccessRequirement(SyncPlayAccessRequirementType.IsInGroup));
                options.AddPolicy(Policies.SubtitleManagement, new UserPermissionRequirement(PermissionKind.EnableSubtitleManagement));
                options.AddPolicy(Policies.LyricManagement, new UserPermissionRequirement(PermissionKind.EnableLyricManagement));
                options.AddPolicy(Policies.ContentPackManagement, new UserPermissionRequirement(PermissionKind.EnableContentPackManagement));
                // The ONE policy that accepts a playback capability. Every other policy names a
                // single authentication scheme and will never select the capability scheme, which
                // is what makes "a capability cannot authenticate the general API" structural
                // rather than a rule somebody has to remember on each new controller.
                options.AddPolicy(
                    Policies.MediaDelivery,
                    policy => policy
                        .AddAuthenticationSchemes(
                            AuthenticationSchemes.CustomAuthentication,
                            AuthenticationSchemes.PlaybackCapability)
                        .AddRequirements(new MediaDeliveryRequirement()));

                options.AddPolicy(
                    Policies.RequiresElevation,
                    policy => policy.AddAuthenticationSchemes(AuthenticationSchemes.CustomAuthentication)
                        .RequireClaim(ClaimTypes.Role, UserRoles.Administrator));
            });
        }

        /// <summary>
        /// Adds custom legacy authentication to the service collection.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static AuthenticationBuilder AddCustomAuthentication(this IServiceCollection serviceCollection)
        {
            return serviceCollection.AddAuthentication(AuthenticationSchemes.CustomAuthentication)
                .AddScheme<AuthenticationSchemeOptions, CustomAuthenticationHandler>(AuthenticationSchemes.CustomAuthentication, null)
                .AddScheme<AuthenticationSchemeOptions, PlaybackCapabilityAuthenticationHandler>(AuthenticationSchemes.PlaybackCapability, null);
        }

        /// <summary>
        /// Extension method for adding the Tesserafin API to the service collection.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="pluginAssemblies">An IEnumerable containing all plugin assemblies with API controllers.</param>
        /// <param name="config">The <see cref="NetworkConfiguration"/>.</param>
        /// <returns>The MVC builder.</returns>
        public static IMvcBuilder AddTesserafinApi(this IServiceCollection serviceCollection, IEnumerable<Assembly> pluginAssemblies, NetworkConfiguration config)
        {
            IMvcBuilder mvcBuilder = serviceCollection
                .AddCors()
                .AddTransient<ICorsPolicyProvider, CorsPolicyProvider>()
                .Configure<ForwardedHeadersOptions>(options =>
                {
                    ConfigureForwardHeaders(config, options);
                })
                .AddMvc(opts =>
                {
                    // Allow requester to change between camelCase and PascalCase
                    opts.RespectBrowserAcceptHeader = true;

                    opts.OutputFormatters.Insert(0, new CamelCaseJsonProfileFormatter());
                    opts.OutputFormatters.Insert(0, new PascalCaseJsonProfileFormatter());

                    opts.OutputFormatters.Add(new CssOutputFormatter());
                    opts.OutputFormatters.Add(new XmlOutputFormatter());

                    opts.ModelBinderProviders.Insert(0, new NullableEnumModelBinderProvider());
                })

                // Clear app parts to avoid other assemblies being picked up
                .ConfigureApplicationPartManager(a => a.ApplicationParts.Clear())
                .AddApplicationPart(typeof(StartupController).Assembly)

                // R1-P (#248): the remote-access diagnostics controller lives in THIS assembly,
                // not in Tesserafin.Api. The reference direction is
                // Tesserafin.Server -> Tesserafin.Server.Core -> Tesserafin.Api, and the R1-A
                // engine it calls lives here, so a controller in Tesserafin.Api would have to
                // reference back and close a cycle. Registering this assembly as an application
                // part is the same mechanism the plugin loop below already uses, and it is
                // required because ApplicationParts.Clear() above removes everything.
                .AddApplicationPart(typeof(RemoteAccessDiagnosticsController).Assembly)
                .AddJsonOptions(options =>
                {
                    // Update all properties that are set in JsonDefaults
                    var jsonOptions = JsonDefaults.PascalCaseOptions;

                    // From JsonDefaults
                    options.JsonSerializerOptions.ReadCommentHandling = jsonOptions.ReadCommentHandling;
                    options.JsonSerializerOptions.WriteIndented = jsonOptions.WriteIndented;
                    options.JsonSerializerOptions.DefaultIgnoreCondition = jsonOptions.DefaultIgnoreCondition;
                    options.JsonSerializerOptions.NumberHandling = jsonOptions.NumberHandling;

                    options.JsonSerializerOptions.Converters.Clear();
                    foreach (var converter in jsonOptions.Converters)
                    {
                        options.JsonSerializerOptions.Converters.Add(converter);
                    }

                    // From JsonDefaults.PascalCase
                    options.JsonSerializerOptions.PropertyNamingPolicy = jsonOptions.PropertyNamingPolicy;
                });

            foreach (Assembly pluginAssembly in pluginAssemblies)
            {
                mvcBuilder.AddApplicationPart(pluginAssembly);
            }

            return mvcBuilder.AddControllersAsServices();
        }

        /// <summary>
        /// Adds the #153 playback capability and WebSocket ticket store to the service collection.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddPlaybackCredentials(this IServiceCollection serviceCollection)
        {
            serviceCollection.TryAddSingleton(TimeProvider.System);
            serviceCollection.AddSingleton<IRandomSecretSource, CryptoRandomSecretSource>();

            // Singleton because the store IS the state. A scoped or transient service would give
            // every request its own empty dictionary and validate nothing at all — the same shape
            // of mistake as a per-request semaphore that serialises nothing.
            serviceCollection.AddSingleton<IPlaybackCredentialService, PlaybackCredentialService>();
            return serviceCollection;
        }

        /// <summary>
        /// Registers the R1-A remote-access diagnostic engine and its production observation
        /// sources (R1-P, #248).
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        /// <remarks>
        /// THE COLLECTOR IS A SINGLETON, AND THAT IS THE POINT. Its one-at-a-time invariant is a
        /// <c>SemaphoreSlim</c> field on the instance, so a scoped or transient registration would
        /// hand every request its own semaphore and serialise nothing — the invariant would still
        /// be there in the source and mean nothing at runtime. Collection reads interface tables,
        /// listening sockets and configuration; running several at once is exactly what the
        /// invariant exists to prevent.
        ///
        /// A singleton is legitimate here because every dependency below is itself a singleton:
        /// the two system sources are stateless, the resolver holds only a timeout, and
        /// <see cref="ServerNetworkPostureSource"/> takes configuration and network managers that
        /// are already registered as singletons. Nothing scoped is captured.
        /// </remarks>
        public static IServiceCollection AddRemoteAccessDiagnostics(this IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<ILocalAddressSource, SystemLocalAddressSource>();
            serviceCollection.AddSingleton<ITcpListenerSource, SystemTcpListenerSource>();

            // The single bounded lookup R1-A defines. The timeout is the engine's own budget, not
            // a request timeout: the caller's cancellation token is propagated separately and
            // stops the lookup earlier if the request goes away.
            serviceCollection.AddSingleton<IHostnameResolver>(
                _ => new SystemHostnameResolver(TimeSpan.FromSeconds(5)));

            serviceCollection.AddSingleton<INetworkPostureSource, ServerNetworkPostureSource>();
            serviceCollection.TryAddSingleton(TimeProvider.System);
            serviceCollection.AddSingleton<RemoteAccessDiagnosticCollector>();

            return serviceCollection;
        }

        internal static void ConfigureForwardHeaders(NetworkConfiguration config, ForwardedHeadersOptions options)
        {
            // https://github.com/dotnet/aspnetcore/blob/master/src/Middleware/HttpOverrides/src/ForwardedHeadersMiddleware.cs
            // Enable debug logging on Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware to help investigate issues.

            if (config.KnownProxies.Length == 0)
            {
                options.ForwardedHeaders = ForwardedHeaders.None;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            }
            else
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                AddProxyAddresses(config, config.KnownProxies, options);
            }

            // Only set forward limit if we have some known proxies or some known networks.
            if (options.KnownProxies.Count != 0 || options.KnownIPNetworks.Count != 0)
            {
                options.ForwardLimit = null;
            }
        }

        /// <summary>
        /// Adds Swagger to the service collection.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddTesserafinApiSwagger(this IServiceCollection serviceCollection)
        {
            return serviceCollection.AddSwaggerGen(c =>
            {
                var version = typeof(ApplicationHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.1";
                c.SwaggerDoc("api-docs", new OpenApiInfo
                {
                    Title = "Tesserafin API",
                    Version = version,
                    Extensions = new Dictionary<string, IOpenApiExtension>
                    {
                        {
                            "x-tesserafin-version",
                            new JsonNodeExtension(JsonValue.Create(version))
                        }
                    }
                });

                c.AddSecurityDefinition(AuthenticationSchemes.CustomAuthentication, new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "Authorization",
                    Description = "API key header parameter"
                });

                // Add all xml doc files to swagger generator, in one canonical order.
                // See XmlDocumentationFiles for why the order is load-bearing.
                foreach (var xmlFile in XmlDocumentationFiles(AppContext.BaseDirectory))
                {
                    c.IncludeXmlComments(xmlFile);
                }

                // Order actions by route path, then by http method.
                c.OrderActionsBy(description =>
                    $"{description.ActionDescriptor.RouteValues["controller"]}_{description.RelativePath}");

                // Use method name as operationId
                c.CustomOperationIds(
                    description =>
                    {
                        description.TryGetMethodInfo(out MethodInfo methodInfo);
                        // Attribute name, method name, none.
                        return description?.ActionDescriptor.AttributeRouteInfo?.Name
                               ?? methodInfo?.Name
                               ?? null;
                    });

                // Allow parameters to properly be nullable.
                c.UseAllOfToExtendReferenceSchemas();
                c.SupportNonNullableReferenceTypes();

                // Disambiguate the PR91 playback decision vocabulary (Tesserafin.Playback.Decision),
                // first exposed via PlaybackSessionResponse (PR112). Its short type names collide
                // with existing schemas under the default (short-name) schemaId strategy — e.g.
                // Tesserafin.Playback.Decision.SubtitleDeliveryMethod vs the already-exposed
                // Tesserafin.Model.Dlna.SubtitleDeliveryMethod — which throws during OpenAPI generation.
                // Scope the override to that one namespace and delegate to the captured framework
                // default for everything else, so generic schema ids (e.g. QueryResultOfBaseItemDto)
                // are preserved unchanged. The "PlaybackDecision" prefix (not a bare "Playback") is
                // deliberate: it avoids colliding the vocabulary's own MediaKind with the internal
                // Tesserafin.Controller.MediaEncoding.PlaybackMediaKind that the admin diagnostics
                // endpoint exposes. These types are new to the contract, so prefixing costs nothing.
                var defaultSchemaIdSelector = c.SchemaGeneratorOptions.SchemaIdSelector;
                c.CustomSchemaIds(type =>
                    type.Namespace is not null && type.Namespace.StartsWith("Tesserafin.Playback.Decision", StringComparison.Ordinal)
                        ? "PlaybackDecision" + defaultSchemaIdSelector(type)
                        : defaultSchemaIdSelector(type));

                // TODO - remove when all types are supported in System.Text.Json
                c.AddSwaggerTypeMappings();

                c.SchemaFilter<IgnoreEnumSchemaFilter>();
                c.SchemaFilter<FlagsEnumSchemaFilter>();

                // Issue #51: emit `required` for the members MVC's implicit [Required] already
                // rejects a request for omitting. Scoped to the Tesserafin.Playback.Decision namespace,
                // where the rule is provable from metadata alone - see the filter's remarks and
                // docs/pr-openapi-required-audit.md.
                c.SchemaFilter<PlaybackDecisionRequiredSchemaFilter>();
                c.OperationFilter<RetryOnTemporarilyUnavailableFilter>();
                c.OperationFilter<SecurityRequirementsOperationFilter>();
                c.OperationFilter<FileResponseFilter>();
                c.OperationFilter<FileRequestFilter>();
                c.OperationFilter<ParameterObsoleteFilter>();

                // Issue #226: `style: deepObject` without `explode` is the one style/explode
                // combination OpenAPI 3.0.4 leaves undefined. See the filter's remarks.
                c.ParameterFilter<DeepObjectExplodeParameterFilter>();

                c.DocumentFilter<AdditionalModelFilter>();
                c.DocumentFilter<SecuritySchemeReferenceFixupFilter>();
            })
            .Replace(ServiceDescriptor.Transient<ISwaggerProvider, CachingOpenApiProvider>());
        }

        /// <summary>
        /// The XML documentation files Swashbuckle must be given, in the one order that makes the
        /// generated contract reproducible across machines.
        ///
        /// <para>
        /// The order is load-bearing, not tidiness. <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>
        /// returns entries in filesystem order, which is unspecified and differs between hosts.
        /// Swashbuckle registers one comment filter per file and, for a property whose type is a
        /// <c>$ref</c>, the LAST registered file carrying a comment for that member wins — so an
        /// unsorted enumeration lets the property's own summary or the referenced type's summary
        /// win depending on which machine generated the document.
        /// </para>
        ///
        /// <para>
        /// That is not hypothetical. It is the whole of the divergence recorded on #94: the same
        /// commit produced one document inside the tesserafin-ci container and a different one on a
        /// GitHub-hosted runner. The documents were structurally identical — same paths, same
        /// schemas, same enums, same <c>required</c> arrays — and differed only in
        /// <c>description</c> strings, each flipping between a property's own summary and its
        /// referenced type's summary.
        /// </para>
        ///
        /// <para>
        /// <c>OpenApiContractTests.Contract_IsByteIdentical_AcrossColdGenerations</c> cannot catch
        /// this: it reboots the application on one filesystem, where the enumeration order is
        /// stable. The guard that can is <c>OpenApiXmlDocumentationOrderTests</c>, which drives
        /// <see cref="CanonicaliseXmlDocumentationOrder"/> with explicit permutations, plus the
        /// hosted <c>ci-tests.yml</c> job running the equality assertion on a different machine.
        /// </para>
        /// </summary>
        /// <param name="baseDirectory">The directory the server's XML documentation is emitted to.</param>
        /// <returns>The documentation files, ordinally ordered.</returns>
        internal static IReadOnlyList<string> XmlDocumentationFiles(string baseDirectory)
            => CanonicaliseXmlDocumentationOrder(
                Directory.EnumerateFiles(baseDirectory, "*.xml", SearchOption.TopDirectoryOnly));

        /// <summary>
        /// Puts an arbitrary enumeration of XML documentation files into the canonical registration
        /// order. Ordinal, so the result cannot depend on the ambient culture either.
        /// </summary>
        /// <param name="xmlFiles">The files as the filesystem happened to enumerate them.</param>
        /// <returns>The same files, ordinally ordered.</returns>
        internal static IReadOnlyList<string> CanonicaliseXmlDocumentationOrder(IEnumerable<string> xmlFiles)
            => xmlFiles.OrderBy(static xmlFile => xmlFile, StringComparer.Ordinal).ToArray();

        private static void AddPolicy(this AuthorizationOptions authorizationOptions, string policyName, IAuthorizationRequirement authorizationRequirement)
        {
            authorizationOptions.AddPolicy(policyName, policy =>
            {
                policy.AddAuthenticationSchemes(AuthenticationSchemes.CustomAuthentication).AddRequirements(authorizationRequirement);
            });
        }

        /// <summary>
        /// Sets up the proxy configuration based on the addresses/subnets in <paramref name="allowedProxies"/>.
        /// </summary>
        /// <param name="config">The <see cref="NetworkConfiguration"/> containing the config settings.</param>
        /// <param name="allowedProxies">The string array to parse.</param>
        /// <param name="options">The <see cref="ForwardedHeadersOptions"/> instance.</param>
        internal static void AddProxyAddresses(NetworkConfiguration config, string[] allowedProxies, ForwardedHeadersOptions options)
        {
            for (var i = 0; i < allowedProxies.Length; i++)
            {
                if (IPAddress.TryParse(allowedProxies[i], out var addr))
                {
                    AddIPAddress(config, options, addr, addr.AddressFamily == AddressFamily.InterNetwork ? NetworkConstants.MinimumIPv4PrefixSize : NetworkConstants.MinimumIPv6PrefixSize);
                }
                else if (NetworkUtils.TryParseToSubnet(allowedProxies[i], out var subnet))
                {
                    AddIPAddress(config, options, subnet.Address, subnet.Subnet.PrefixLength);
                }
                else if (NetworkUtils.TryParseHost(allowedProxies[i], out var addresses, config.EnableIPv4, config.EnableIPv6))
                {
                    foreach (var address in addresses)
                    {
                        AddIPAddress(config, options, address, address.AddressFamily == AddressFamily.InterNetwork ? NetworkConstants.MinimumIPv4PrefixSize : NetworkConstants.MinimumIPv6PrefixSize);
                    }
                }
            }
        }

        private static void AddIPAddress(NetworkConfiguration config, ForwardedHeadersOptions options, IPAddress addr, int prefixLength)
        {
            if (addr.IsIPv4MappedToIPv6)
            {
                addr = addr.MapToIPv4();
            }

            if ((!config.EnableIPv4 && addr.AddressFamily == AddressFamily.InterNetwork) || (!config.EnableIPv6 && addr.AddressFamily == AddressFamily.InterNetworkV6))
            {
                return;
            }

            if ((addr.AddressFamily == AddressFamily.InterNetwork && prefixLength == NetworkConstants.MinimumIPv4PrefixSize) || (addr.AddressFamily == AddressFamily.InterNetworkV6 && prefixLength == NetworkConstants.MinimumIPv6PrefixSize))
            {
                options.KnownProxies.Add(addr);
            }
            else
            {
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(addr, prefixLength));
            }
        }

        private static void AddSwaggerTypeMappings(this SwaggerGenOptions options)
        {
            /*
             * TODO remove when System.Text.Json properly supports non-string keys.
             * Used in BaseItemDto.ImageBlurHashes
             */
            options.MapType<Dictionary<ImageType, string>>(() =>
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    AdditionalProperties = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String
                    }
                });

            // Support dictionary with nullable string value.
            options.MapType<Dictionary<string, string?>>(() =>
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    AdditionalProperties = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String | JsonSchemaType.Null
                    }
                });

            // Swashbuckle doesn't use JsonOptions to describe responses, so we need to manually describe it.
            options.MapType<Version>(() => new OpenApiSchema
            {
                Type = JsonSchemaType.String
            });

            /*
             * Issue #226: PlaybackSessionId is a `readonly record struct PlaybackSessionId(Guid Value)`
             * that implements IParsable<T>, which is why ASP.NET Core binds it as a SIMPLE type from a
             * single route/query string value. Swashbuckle's schema generator has no such notion: it
             * reflects over the type's properties and emitted `{ type: object, properties: { Value:
             * {string/uuid} } }`. The two descriptions disagreed, and the contract recorded the CLR
             * shape. Every object serialization the emitted schema implied — `Value,<uuid>` under
             * `simple`/explode:false, `Value=<uuid>` under `simple`/explode:true, a literal JSON object
             * — is answered 400 by the running server; only the bare scalar binds. A generated Kotlin
             * client that transcribed it faithfully produced
             * `/Playback/Sessions/PlaybackSessionId(value=<uuid>)`, the analogue of the `[object
             * Object]` that tesserafin-web's scripts/generate-tesserafin-sdk.mjs already post-processes
             * away.
             *
             * `format: uuid` stays accurate: the binder accepts both the dashed form and the
             * 32-character "N" form PlaybackSessionId.ToString() emits, and `format` is an annotation,
             * not a validator.
             *
             * Deliberately scoped to this one CLR type rather than to record structs or IParsable<T> in
             * general — no other such type was measured, and widening it needs its own evidence.
             * Nothing about the runtime representation, parsing, ToString(), routes or response bodies
             * changes; this is what the document says, not what the server does.
             */
            options.MapType<PlaybackSessionId>(() => new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
                Description = "Opaque identifier for a Tesserafin.Controller.MediaEncoding.PlaybackSession."
            });
        }
    }
}
