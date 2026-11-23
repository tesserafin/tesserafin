using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Common;
using Tesserafin.Controller;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Collections;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.TV;
using Tesserafin.Server.Core;
using Tesserafin.Server.Helpers;
using Xunit;

namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// DEBT-SERVER-1: the user-view catalog's playlist/boxset probe no longer reaches the former
    /// <c>ILibraryManager</c>/<c>IUserViewManager</c>/<c>IChannelManager</c>/<c>ILiveTvManager</c> cycle
    /// nor <c>IItemQueryService</c> - not directly, not deferred, not moved elsewhere. DEBT-SERVER-2
    /// holds the library membership resolver extracted from <c>LibraryManager</c> to the same rule,
    /// DEBT-SERVER-3 the tag visibility service built on it, and DEBT-SERVER-4 the standalone visibility
    /// service and the access path that takes it.
    /// </summary>
    /// <remarks>
    /// The graph is built from the server's real registrations (<see cref="ApplicationHost.Init"/> on a
    /// fresh <see cref="ServiceCollection"/>). Factory registrations are opened up by reading their IL:
    /// every <c>GetService</c>/<c>GetRequiredService</c>/<c>GetServices</c> instantiation, <c>typeof</c>
    /// and constructor they reference (including in compiler-generated closures) becomes an edge, so a
    /// DI alias or a <c>Lazy</c>/<c>Func</c> factory cannot hide a dependency. An edge is deferred when it
    /// goes through <see cref="Lazy{T}"/>, <see cref="Func{TResult}"/> or a factory registered for one.
    /// </remarks>
    public sealed class UserViewCatalogDependencyTests
    {
        private static readonly Type[] _cycleTypes =
        [
            typeof(ILibraryManager),
            typeof(IUserViewManager),
            typeof(IChannelManager),
            typeof(ILiveTvManager),
            typeof(IItemQueryService),
        ];

        private static readonly Type[] _locatorTypes =
        [
            typeof(IServiceProvider),
            typeof(IServiceScopeFactory),
            typeof(IKeyedServiceProvider),
            typeof(IApplicationHost),
            typeof(IServerApplicationHost),
        ];

        // Entity statics that are service locators for the cycle members.
        private static readonly (Type Type, string Name)[] _staticLocators =
        [
            (typeof(BaseItem), nameof(BaseItem.LibraryManager)),
        ];

        private static readonly Lazy<IServiceCollection> _registrations = new(BuildRegistrations);

        [Fact]
        public void VisibleContentProbe_WholeGraph_ReachesNoCycleMemberNorLocator()
        {
            var graph = new DependencyGraph(_registrations.Value);

            var reached = graph.Walk(typeof(IVisibleContentProbe), followDeferred: true);

            AssertNone(reached.Where(n => IsCycleMember(n.Type)).Select(n => n.Path));

            // The repository holds the application host only to expand and reverse virtual paths
            // (BaseItemRepository / BaseItemMapper: ExpandVirtualPath, ReverseVirtualPath); the walk
            // stops there.
            AssertNone(reached
                .Where(n => _locatorTypes.Contains(n.Type))
                .Where(n => n.Path != "IVisibleContentProbe -> IItemRepository -> BaseItemRepository -> IServerApplicationHost")
                .Select(n => n.Path));
        }

        [Fact]
        public void CollectionFolderResolver_WholeGraph_ReachesNoCycleMemberNorLocator()
        {
            var graph = new DependencyGraph(_registrations.Value);

            var reached = graph.Walk(typeof(ICollectionFolderResolver), followDeferred: true);

            AssertNone(reached.Where(n => IsCycleMember(n.Type)).Select(n => n.Path));

            // Exactly one locator edge, pre-existing, through the user root folder leaf: the persistence
            // service holds the application host only to map virtual paths (BaseItemMapper.Map), and the
            // walk stops there. Equality, not a filter: any further locator path fails, and so does this
            // one disappearing, so the exception can neither widen nor outlive its reason.
            Assert.Equal(
                ["ICollectionFolderResolver -> IUserRootFolderProvider -> IItemStore -> IItemPersistenceService -> IServerApplicationHost"],
                reached.Where(n => _locatorTypes.Contains(n.Type)).Select(n => n.Path).Order(StringComparer.Ordinal));
        }

        [Fact]
        public void TagVisibilityService_WholeGraph_ReachesNoCycleMemberNorLocator()
        {
            var graph = new DependencyGraph(_registrations.Value);

            var reached = graph.Walk(typeof(ITagVisibilityService), followDeferred: true);

            AssertNone(reached.Where(n => IsCycleMember(n.Type)).Select(n => n.Path));

            // The walk reports each node once, on its shortest path: the application host, held only to map
            // virtual paths by the repository (taken directly for the path search since DEBT-SERVER-7, and
            // behind the lookup) and by the persistence service behind the user root folder (the
            // resolver's exception; the service reads the libraries through the root since DEBT-SERVER-4).
            // Equality, as for the resolver.
            Assert.Equal(
                ["ITagVisibilityService -> IItemRepository -> BaseItemRepository -> IServerApplicationHost"],
                reached.Where(n => _locatorTypes.Contains(n.Type)).Select(n => n.Path).Order(StringComparer.Ordinal));
        }

        [Theory]
        [InlineData(typeof(IStandaloneVisibilityService))]
        [InlineData(typeof(IItemAccessService))]
        public void StandaloneVisibilityAndAccess_WholeGraph_ReachNoCycleMemberNorLocator(Type service)
        {
            var graph = new DependencyGraph(_registrations.Value);

            var reached = graph.Walk(service, followDeferred: true);

            AssertNone(reached.Where(n => IsCycleMember(n.Type)).Select(n => n.Path));

            // As for the tag visibility service: the application host, held for virtual paths only, reported
            // once on its shortest path - behind the service's own repository (DEBT-SERVER-7), shorter than
            // behind the lookup. Equality, as above.
            var expected = service == typeof(IItemAccessService)
                ? "IItemAccessService -> IStandaloneVisibilityService -> IItemRepository -> BaseItemRepository -> IServerApplicationHost"
                : "IStandaloneVisibilityService -> IItemRepository -> BaseItemRepository -> IServerApplicationHost";
            Assert.Equal(
                [expected],
                reached.Where(n => _locatorTypes.Contains(n.Type)).Select(n => n.Path).Order(StringComparer.Ordinal));
        }

        [Fact]
        public void ItemAccessService_TakesTheStandaloneVisibilityService()
        {
            var implementation = _registrations.Value.Single(d => d.ServiceType == typeof(IItemAccessService)).ImplementationType!;

            Assert.Equal(
                [typeof(IItemLookupService), typeof(IStandaloneVisibilityService)],
                implementation.GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
        }

        [Theory]
        [InlineData("direct")]
        [InlineData("alias")]
        [InlineData("deferred")]
        public void Control_AReturnToTheLibraryManager_IsCaughtByTheWalk(string how)
        {
            // The walk itself, on a controlled failure: the real registrations plus one way back to the
            // library manager behind the root provider the standalone service depends on.
            var services = new ServiceCollection();
            foreach (var descriptor in _registrations.Value)
            {
                ((ICollection<ServiceDescriptor>)services).Add(descriptor);
            }

            switch (how)
            {
                case "direct":
                    services.AddSingleton<IUserRootFolderProvider, RootProviderOver<ILibraryManager>>();
                    break;
                case "alias":
                    var concrete = typeof(ApplicationHost).Assembly.GetType("Tesserafin.Server.Core.Library.LibraryManager", throwOnError: true)!;
                    services.AddSingleton(typeof(UserViewCatalogDependencyTests).GetMethod(nameof(AliasOnto), BindingFlags.NonPublic | BindingFlags.Static)!
                        .MakeGenericMethod(concrete)
                        .CreateDelegate<Func<IServiceProvider, IUserRootFolderProvider>>());
                    break;
                default:
                    services.AddSingleton<IUserRootFolderProvider, RootProviderOver<Lazy<ILibraryManager>>>();
                    break;
            }

            var reached = new DependencyGraph(services).Walk(typeof(IStandaloneVisibilityService), followDeferred: true);

            Assert.Contains(reached, n => IsCycleMember(n.Type));
        }

        // A factory alias onto the concrete, internal library manager.
        private static IUserRootFolderProvider AliasOnto<T>(IServiceProvider services)
            where T : notnull
            => new RootProviderOver<T>(services.GetRequiredService<T>());

        [Fact]
        public void TagVisibilityService_ImplementationAndEntityHelpers_ReachNoStaticBackedEntityMember()
        {
            // The service delegates the combination and the filter to two static entity helpers. Neither
            // they nor the service may reach the statics through the entity members that read them:
            // parameterless parent/owner walks and the static-backed tag members.
            var implementation = _registrations.Value.Single(d => d.ServiceType == typeof(ITagVisibilityService)).ImplementationType!;
            var helpers = typeof(BaseItem).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name is nameof(BaseItem.CombineInheritedTags) or nameof(BaseItem.EvaluateTagVisibility))
                .ToArray();
            Assert.Equal(2, helpers.Length);

            var offending = IlReader.MethodsOf(implementation)
                .Concat(helpers.SelectMany(IlReader.ReachableMethods))
                .SelectMany(m => IlReader.References(m).Select(r => (Method: m, Reference: r)))
                .Where(x => IsForbiddenReference(x.Reference) || IsStaticBackedEntityMember(x.Reference))
                .Select(x => $"{x.Method.DeclaringType!.Name}.{x.Method.Name} -> {x.Reference.DeclaringType?.Name}.{x.Reference.Name}")
                .ToArray();

            AssertNone(offending);
        }

        private static bool IsStaticBackedEntityMember(MemberInfo member) =>
            member is MethodInfo method
            && typeof(BaseItem).IsAssignableFrom(method.DeclaringType)
            && ((method.Name is nameof(BaseItem.GetParent) or nameof(BaseItem.GetParents) or nameof(BaseItem.GetOwner) && method.GetParameters().Length == 0)
                || method.Name is nameof(BaseItem.GetInheritedTags) or "IsVisibleViaTags");

        [Fact]
        public void UserViewCatalog_EagerGraph_ReachesNoCycleMember()
        {
            var graph = new DependencyGraph(_registrations.Value);

            var reached = graph.Walk(typeof(IUserViewCatalog), followDeferred: false);

            AssertNone(reached.Where(n => IsCycleMember(n.Type)).Select(n => n.Path));
        }

        [Fact]
        public void UserViewCatalog_EagerGraph_DefersOnlyThePreExistingExceptions()
        {
            var graph = new DependencyGraph(_registrations.Value);

            var deferred = graph.Walk(typeof(IUserViewCatalog), followDeferred: false)
                .SelectMany(n => n.DeferredEdges)
                .Distinct()
                .OrderBy(e => e, StringComparer.Ordinal)
                .ToArray();

            // docs/pr111-di-closure-audit.md exceptions 1 and 3; exception 4 (Lazy<IItemQueryService>) is gone.
            Assert.Equal(
                [
                    "IUserViewCatalog -> ILiveTvPresenceProvider =>(deferred) Lazy[IReadOnlyList[ILiveTvService]]",
                    "IUserViewCatalog -> IUserViewFactory =>(deferred) Lazy[IProviderManager]",
                ],
                deferred);
        }

        [Fact]
        public void Registrations_NoDeferredOrAliasedItemQueryService()
        {
            var services = _registrations.Value;

            Assert.DoesNotContain(services, d => d.ServiceType == typeof(Lazy<IItemQueryService>));
            Assert.DoesNotContain(services, d => d.ServiceType == typeof(Func<IItemQueryService>));
            Assert.All(
                services.Where(d => !d.IsKeyedService && d.ImplementationType is { } t && typeof(IItemQueryService).IsAssignableFrom(t)),
                d => Assert.Equal(typeof(IItemQueryService), d.ServiceType));

            foreach (var service in new[] { typeof(IUserViewCatalog), typeof(IVisibleContentProbe) })
            {
                var descriptor = Assert.Single(services, d => d.ServiceType == service);
                Assert.NotNull(descriptor.ImplementationType);
            }
        }

        [Theory]
        [InlineData(typeof(IUserViewCatalog))]
        [InlineData(typeof(IVisibleContentProbe))]
        [InlineData(typeof(ICollectionFolderResolver))]
        [InlineData(typeof(ITagVisibilityService))]
        [InlineData(typeof(IStandaloneVisibilityService))]
        [InlineData(typeof(IItemAccessService))]
        public void ImplementationCode_ReferencesNoCycleMemberLocatorOrEntityStaticLocator(Type service)
        {
            var implementation = _registrations.Value.Single(d => d.ServiceType == service).ImplementationType!;

            var offending = IlReader.MethodsOf(implementation)
                .SelectMany(m => IlReader.References(m).Select(r => (Method: m, Reference: r)))
                .Where(x => IsForbiddenReference(x.Reference))
                .Select(x => $"{x.Method.DeclaringType!.Name}.{x.Method.Name} -> {x.Reference.DeclaringType?.Name}.{x.Reference.Name}")
                .ToArray();

            AssertNone(offending);

            Assert.DoesNotContain(
                implementation.GetConstructors().Single().GetParameters(),
                p => IsForbiddenType(p.ParameterType));
        }

        private static void AssertNone(IEnumerable<string> findings)
        {
            var list = findings.ToArray();
            Assert.True(list.Length == 0, string.Join(Environment.NewLine, list));
        }

        private static bool IsForbiddenReference(MemberInfo member)
        {
            if (member is Type type)
            {
                return IsForbiddenType(type);
            }

            if (member.DeclaringType is { } declaringType && IsForbiddenType(declaringType))
            {
                return true;
            }

            if (member is MethodInfo method)
            {
                if (IsForbiddenType(method.ReturnType)
                    || method.GetGenericArguments().Any(IsForbiddenType)
                    || (method.IsSpecialName && _staticLocators.Any(s => s.Type == method.DeclaringType && method.Name == "get_" + s.Name)))
                {
                    return true;
                }

                // ServiceProviderServiceExtensions, ActivatorUtilities: locator calls whatever they resolve.
                return method.DeclaringType?.Namespace == "Microsoft.Extensions.DependencyInjection";
            }

            return member is FieldInfo field && IsForbiddenType(field.FieldType);
        }

        // A concrete implementation counts as its interface: an alias resolving LibraryManager itself
        // (which also implements the lookup) would otherwise read as an unknown leaf.
        private static bool IsCycleMember(Type type) => _cycleTypes.Any(c => c.IsAssignableFrom(type));

        private static bool IsForbiddenType(Type type)
        {
            if (type.IsGenericType)
            {
                return type.GetGenericArguments().Any(IsForbiddenType);
            }

            return IsCycleMember(type) || _locatorTypes.Contains(type);
        }

        private static IServiceCollection BuildRegistrations()
        {
            Environment.SetEnvironmentVariable("TESSERAFIN_FFMPEG__NOVALIDATION", "true");
            var root = Path.Combine(Path.GetTempPath(), "reefin-test-data", "di-graph-" + Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));
            var directories = new[] { "logs", "config", "cache", "jellyfin-web" }.Select(d => Path.Combine(root, d)).ToArray();
            foreach (var directory in directories)
            {
                Directory.CreateDirectory(directory);
            }

            var appPaths = new ServerApplicationPaths(root, directories[0], directories[1], directories[2], directories[3]);
            StartupHelpers.InitLoggingConfigFile(appPaths).GetAwaiter().GetResult();

            var options = new StartupOptions();
            var appHost = new TestAppHost(appPaths, NullLoggerFactory.Instance, options, Program.CreateAppConfiguration(options, appPaths));

            var services = new ServiceCollection();
            appHost.Init(services);
            return services;
        }

        private sealed class RootProviderOver<T> : IUserRootFolderProvider
        {
            public RootProviderOver(T dependency)
            {
                _ = dependency;
            }

            public Folder GetUserRootFolder() => throw new NotSupportedException();
        }

        private sealed record Node(Type Type, string Path, IReadOnlyList<string> DeferredEdges);

        private sealed class DependencyGraph
        {
            private readonly IServiceCollection _services;

            public DependencyGraph(IServiceCollection services)
            {
                _services = services;
            }

            public IReadOnlyList<Node> Walk(Type root, bool followDeferred)
            {
                var reached = new List<Node>();
                var seen = new HashSet<Type>();
                var queue = new Queue<(Type Type, string Path)>();
                queue.Enqueue((root, Name(root)));

                while (queue.Count > 0)
                {
                    var (type, path) = queue.Dequeue();
                    if (!seen.Add(type))
                    {
                        continue;
                    }

                    var deferredEdges = new List<string>();
                    reached.Add(new Node(type, path, deferredEdges));

                    // Locators are reported, not entered: anything is reachable through them.
                    if (_locatorTypes.Contains(type))
                    {
                        continue;
                    }

                    foreach (var (dependency, deferred) in DependenciesOf(type))
                    {
                        if (deferred)
                        {
                            deferredEdges.Add($"{path} =>(deferred) {Name(dependency)}");
                            if (!followDeferred)
                            {
                                continue;
                            }
                        }

                        queue.Enqueue((dependency, $"{path} -> {Name(dependency)}"));
                    }
                }

                return reached;
            }

            private static string Name(Type type) =>
                type.IsGenericType ? $"{type.Name.Split('`')[0]}[{string.Join(",", type.GetGenericArguments().Select(Name))}]" : type.Name;

            private static bool IsDeferral(Type type) =>
                type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Lazy<>) || type.GetGenericTypeDefinition() == typeof(Func<>));

            private IEnumerable<(Type Type, bool Deferred)> DependenciesOf(Type service)
            {
                var descriptors = _services.Where(d => !d.IsKeyedService && d.ServiceType == service).ToList();

                if (descriptors.Count == 0 && service.IsGenericType)
                {
                    var definition = service.GetGenericTypeDefinition();
                    if (IsDeferral(service))
                    {
                        yield return (service.GetGenericArguments()[0], true);
                        yield break;
                    }

                    if (definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>))
                    {
                        yield return (service.GetGenericArguments()[0], false);
                        yield break;
                    }

                    descriptors = _services
                        .Where(d => !d.IsKeyedService && d.ServiceType == definition && d.ImplementationType is not null)
                        .Select(d => ServiceDescriptor.Describe(service, d.ImplementationType!.MakeGenericType(service.GetGenericArguments()), d.Lifetime))
                        .ToList();
                }

                // Single resolution takes the last registration; IEnumerable<T> takes them all.
                foreach (var descriptor in descriptors)
                {
                    var deferredFactory = IsDeferral(service);
                    foreach (var (dependency, deferred) in DependenciesOf(descriptor))
                    {
                        yield return (dependency, deferred || deferredFactory);
                    }
                }
            }

            private static IEnumerable<(Type Type, bool Deferred)> DependenciesOf(ServiceDescriptor descriptor)
            {
                if (descriptor.ImplementationType is { } implementation)
                {
                    return ConstructorDependencies(implementation);
                }

                if (descriptor.ImplementationFactory is { } factory)
                {
                    return IlReader.ReachableMethods(factory.Method)
                        .SelectMany(IlReader.References)
                        .SelectMany(FactoryDependencies);
                }

                return [];
            }

            private static IEnumerable<(Type Type, bool Deferred)> ConstructorDependencies(Type implementation)
            {
                var constructor = implementation.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
                foreach (var parameter in constructor?.GetParameters() ?? [])
                {
                    // A Lazy<T>/Func<T> parameter resolves through its own registration when there is one.
                    yield return (parameter.ParameterType, IsDeferral(parameter.ParameterType));
                }
            }

            private static IEnumerable<(Type Type, bool Deferred)> FactoryDependencies(MemberInfo reference)
            {
                switch (reference)
                {
                    case MethodInfo { IsGenericMethod: true } method
                        when method.Name is "GetService" or "GetRequiredService" or "GetServices" or "GetKeyedService" or "GetRequiredKeyedService":
                        yield return (method.GetGenericArguments()[0], false);
                        break;
                    case ConstructorInfo { DeclaringType: { } constructed } when !IsDeferral(constructed)
                        && !constructed.IsDefined(typeof(CompilerGeneratedAttribute))
                        && constructed.Namespace?.StartsWith("Tesserafin", StringComparison.Ordinal) == true:
                        foreach (var dependency in ConstructorDependencies(constructed))
                        {
                            yield return dependency;
                        }

                        break;
                    case Type type when type.Namespace?.StartsWith("Tesserafin", StringComparison.Ordinal) == true:
                        yield return (type, false);
                        break;
                }
            }
        }

        private static class IlReader
        {
            private static readonly Dictionary<short, OpCode> _opCodes = typeof(OpCodes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => (OpCode)f.GetValue(null)!)
                .ToDictionary(o => o.Value);

            public static IEnumerable<MethodBase> MethodsOf(Type type)
            {
                const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                return type.GetMethods(All).Cast<MethodBase>()
                    .Concat(type.GetConstructors(All))
                    .Concat(type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).SelectMany(MethodsOf));
            }

            // The method plus every compiler-generated method (lambda bodies, closures) it points at.
            public static IEnumerable<MethodBase> ReachableMethods(MethodBase method)
            {
                var seen = new HashSet<MethodBase>();
                var stack = new Stack<MethodBase>([method]);
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    if (!seen.Add(current))
                    {
                        continue;
                    }

                    yield return current;
                    foreach (var next in References(current).OfType<MethodBase>())
                    {
                        if (next.DeclaringType?.IsDefined(typeof(CompilerGeneratedAttribute)) == true || next.Name.StartsWith('<'))
                        {
                            stack.Push(next);
                        }
                    }
                }
            }

            public static IEnumerable<MemberInfo> References(MethodBase method)
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null)
                {
                    yield break;
                }

                var typeArguments = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
                var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

                var position = 0;
                while (position < il.Length)
                {
                    short value = il[position++];
                    if (value == 0xFE)
                    {
                        value = unchecked((short)(0xFE00 | il[position++]));
                    }

                    var opCode = _opCodes[value];
                    switch (opCode.OperandType)
                    {
                        case OperandType.InlineMethod:
                        case OperandType.InlineField:
                        case OperandType.InlineType:
                        case OperandType.InlineTok:
                            var token = BitConverter.ToInt32(il, position);
                            position += 4;
                            MemberInfo? member = null;
                            try
                            {
                                member = method.Module.ResolveMember(token, typeArguments, methodArguments);
                            }
                            catch (ArgumentException)
                            {
                            }

                            if (member is not null)
                            {
                                yield return member;
                            }

                            break;
                        case OperandType.InlineSwitch:
                            position += 4 + (4 * BitConverter.ToInt32(il, position));
                            break;
                        case OperandType.InlineNone:
                            break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar:
                            position += 1;
                            break;
                        case OperandType.InlineVar:
                            position += 2;
                            break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR:
                            position += 8;
                            break;
                        default:
                            position += 4;
                            break;
                    }
                }
            }
        }
    }
}
