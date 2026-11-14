using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Auth.UserPermissionPolicy;
using Tesserafin.Api.Controllers;
using Tesserafin.Common.Api;
using Tesserafin.Data.Enums;
using Tesserafin.Server.Extensions;
using Xunit;

namespace Tesserafin.Server.Tests.Authorization;

/// <summary>
/// An <c>[Authorize(Policy = …)]</c> attribute naming a policy nobody registered fails at request
/// time, not at build time. These tests close that gap for every controller in the API.
/// </summary>
public class AuthorizationPolicyRegistrationTests
{
    public static TheoryData<string> PolicyNamesUsedByControllers()
    {
        var data = new TheoryData<string>();
        foreach (var name in CollectPolicyNames())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PolicyNamesUsedByControllers))]
    public async Task EveryPolicyNamedByAnAttributeIsRegistered(string policyName)
    {
        var provider = BuildAuthorizationProvider();

        var policy = await provider.GetPolicyAsync(policyName);

        Assert.NotNull(policy);
    }

    [Fact]
    public async Task ContentPackManagementPolicyRequiresTheContentPackPermission()
    {
        var provider = BuildAuthorizationProvider();

        var policy = await provider.GetPolicyAsync(Policies.ContentPackManagement);

        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<UserPermissionRequirement>());
        Assert.Equal(PermissionKind.EnableContentPackManagement, requirement.RequiredPermission);
    }

    [Fact]
    public void ContentPacksControllerGuardsEveryWriteWithTheManagementPolicy()
    {
        var writes = new[]
        {
            nameof(ContentPacksController.CreateContentPack),
            nameof(ContentPacksController.UpdateContentPack),
            nameof(ContentPacksController.ReorderContentPacks),
            nameof(ContentPacksController.DeleteContentPack),
            nameof(ContentPacksController.AddContentPackItem),
            nameof(ContentPacksController.RemoveContentPackItem)
        };

        foreach (var name in writes)
        {
            var method = typeof(ContentPacksController).GetMethod(name);
            Assert.NotNull(method);

            var policies = method.GetCustomAttributes<AuthorizeAttribute>(true)
                .Select(a => a.Policy)
                .ToArray();

            Assert.Contains(Policies.ContentPackManagement, policies);
        }
    }

    [Fact]
    public void ContentPacksControllerReadsRequireOnlyAuthentication()
    {
        var reads = new[]
        {
            nameof(ContentPacksController.GetContentPacks),
            nameof(ContentPacksController.GetContentPack),
            nameof(ContentPacksController.GetContentPackItems),
            nameof(ContentPacksController.GetContentPacksForItem)
        };

        foreach (var name in reads)
        {
            var method = typeof(ContentPacksController).GetMethod(name);
            Assert.NotNull(method);

            var policies = method.GetCustomAttributes<AuthorizeAttribute>(true)
                .Select(a => a.Policy)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            Assert.Empty(policies);
        }

        // The class-level attribute still demands an authenticated caller: no anonymous endpoint.
        var classPolicies = typeof(ContentPacksController).GetCustomAttributes<AuthorizeAttribute>(true).ToArray();
        Assert.NotEmpty(classPolicies);
        Assert.Empty(typeof(ContentPacksController).GetCustomAttributes<AllowAnonymousAttribute>(true));
    }

    private static IEnumerable<string> CollectPolicyNames()
    {
        var controllerTypes = typeof(ContentPacksController).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in controllerTypes)
        {
            foreach (var attribute in type.GetCustomAttributes<AuthorizeAttribute>(true))
            {
                AddPolicy(names, attribute);
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributes<AuthorizeAttribute>(true))
                {
                    AddPolicy(names, attribute);
                }
            }
        }

        return names.OrderBy(n => n, StringComparer.Ordinal);
    }

    private static void AddPolicy(HashSet<string> names, AuthorizeAttribute attribute)
    {
        if (!string.IsNullOrEmpty(attribute.Policy))
        {
            names.Add(attribute.Policy);
        }
    }

    private static IAuthorizationPolicyProvider BuildAuthorizationProvider()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddTesserafinApiAuthorization();

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationPolicyProvider>();
    }
}
