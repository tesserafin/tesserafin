using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Tesserafin.Api.Attributes;
using Tesserafin.Common.Api;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Api.Tests.PlaybackCredentials;

/// <summary>
/// Proves the #153 media boundary is where it is claimed to be, route by route.
/// </summary>
/// <remarks>
/// WHY STRUCTURAL AND NOT END-TO-END. The claim under test is "a capability reaches media routes
/// and reaches nothing else". That is a property of the ROUTE TABLE, not of any one request, and a
/// request-level test can only ever sample it — it passes while a controller added next week
/// silently accepts capabilities, which is precisely the regression that matters. These assertions
/// read the attributes the framework itself dispatches on.
///
/// The companion request-level evidence lives in the integration suite; the primitive-level
/// evidence lives in PlaybackCredentialServiceTests. Neither replaces this one.
/// </remarks>
public class MediaBoundaryStructuralTests
{
    private static Assembly ApiAssembly => typeof(BaseTesserafinApiController).Assembly;

    /// <summary>Gets the routes the phase-0 inventory classified as playback media or auxiliary media.</summary>
    public static IReadOnlyList<(string Controller, string Action, PlaybackCapabilityScope Scope)> Declared { get; } = new[]
    {
        ("VideosController", "GetVideoStream", PlaybackCapabilityScope.Media),
        ("VideosController", "GetVideoStreamByContainer", PlaybackCapabilityScope.Media),
        // Consolidation step 1: the file route of a session the engine alone decided to direct play.
        ("PlaybackSessionFilesController", "GetPlaybackSessionFile", PlaybackCapabilityScope.Media),
        ("AudioController", "GetAudioStream", PlaybackCapabilityScope.Media),
        ("AudioController", "GetAudioStreamByContainer", PlaybackCapabilityScope.Media),
        ("UniversalAudioController", "GetUniversalAudioStream", PlaybackCapabilityScope.Media),
        ("DynamicHlsController", "GetLiveHlsStream", PlaybackCapabilityScope.Media),
        ("DynamicHlsController", "GetMasterHlsVideoPlaylist", PlaybackCapabilityScope.Media),
        ("DynamicHlsController", "GetMasterHlsAudioPlaylist", PlaybackCapabilityScope.Media),
        ("DynamicHlsController", "GetVariantHlsVideoPlaylist", PlaybackCapabilityScope.Media),
        ("DynamicHlsController", "GetVariantHlsAudioPlaylist", PlaybackCapabilityScope.Media),
        ("DynamicHlsController", "GetHlsVideoSegment", PlaybackCapabilityScope.Media),
        ("DynamicHlsController", "GetHlsAudioSegment", PlaybackCapabilityScope.Media),
        ("HlsSegmentController", "GetHlsPlaylistLegacy", PlaybackCapabilityScope.Media),
        ("HlsSegmentController", "GetHlsAudioSegmentLegacy", PlaybackCapabilityScope.Media),
        ("HlsSegmentController", "GetHlsVideoSegmentLegacy", PlaybackCapabilityScope.Media),
        ("SubtitleController", "GetSubtitle", PlaybackCapabilityScope.Subtitles),

        // A0's roster omitted this one. It carried [RequiresPlaybackCapability] and no policy, so
        // it never showed up as an offender below and the theory above never asked whether it had
        // the attribute at all — the omission was invisible in both directions. Gating it in R1 is
        // what made the gap visible.
        ("SubtitleController", "GetSubtitleWithTicks", PlaybackCapabilityScope.Subtitles),
        ("SubtitleController", "GetSubtitlePlaylist", PlaybackCapabilityScope.Subtitles),
        ("SubtitleController", "GetFallbackFontList", PlaybackCapabilityScope.Fonts),
        ("SubtitleController", "GetFallbackFont", PlaybackCapabilityScope.Fonts),
        ("VideoAttachmentsController", "GetAttachment", PlaybackCapabilityScope.Attachments),
        ("TrickplayController", "GetTrickplayHlsPlaylist", PlaybackCapabilityScope.Trickplay),
        ("TrickplayController", "GetTrickplayTileImage", PlaybackCapabilityScope.Trickplay)
    };

    /// <summary>The same roster, as xunit theory rows.</summary>
    /// <returns>One row per media route.</returns>
    public static TheoryData<string, string, PlaybackCapabilityScope> MediaRoutes()
    {
        var data = new TheoryData<string, string, PlaybackCapabilityScope>();
        foreach (var (controller, action, scope) in Declared)
        {
            data.Add(controller, action, scope);
        }

        return data;
    }

    private static MethodInfo FindAction(string controller, string action)
    {
        var type = ApiAssembly.GetTypes().SingleOrDefault(t => t.Name == controller);
        Assert.True(type is not null, $"controller {controller} not found — the inventory named a route that no longer exists");
        var method = type!.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(m => m.Name == action);
        Assert.True(method is not null, $"{controller}.{action} not found — the inventory named a route that no longer exists");
        return method!;
    }

    [Theory]
    [MemberData(nameof(MediaRoutes))]
    public void Every_media_route_narrows_a_presented_capability_to_its_own_scope(
        string controller, string action, PlaybackCapabilityScope expected)
    {
        var attribute = FindAction(controller, action)
            .GetCustomAttributes<RequiresPlaybackCapabilityAttribute>(inherit: false)
            .SingleOrDefault();

        Assert.True(
            attribute is not null,
            $"{controller}.{action} carries no [RequiresPlaybackCapability]; a capability presented there would be unconstrained");
        Assert.Equal(expected, attribute!.Scope);
    }

    [Fact]
    public void No_route_outside_the_inventory_carries_the_media_delivery_policy()
    {
        // The policy is the ONLY one that names the capability authentication scheme. If it appears
        // on a route the inventory never classified as media, a capability can reach that route.
        var declared = Declared.Select(d => (d.Controller, d.Action)).ToHashSet();

        var offenders = new List<string>();
        foreach (var type in ApiAssembly.GetTypes().Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal)))
        {
            var controllerPolicy = type.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
                .Any(a => string.Equals(a.Policy, Policies.MediaDelivery, StringComparison.Ordinal));

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var isRoute = method.GetCustomAttributes<HttpMethodAttribute>(inherit: false).Any();
                if (!isRoute)
                {
                    continue;
                }

                var hasPolicy = controllerPolicy
                    || method.GetCustomAttributes<AuthorizeAttribute>(inherit: false)
                        .Any(a => string.Equals(a.Policy, Policies.MediaDelivery, StringComparison.Ordinal));

                if (hasPolicy && !declared.Contains((type.Name, method.Name)))
                {
                    offenders.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "these routes accept the capability scheme but are not classified as media: " + string.Join(", ", offenders));
    }

    [Fact]
    public void A_representative_general_api_controller_does_not_accept_the_capability_scheme()
    {
        // ItemsController is the shape the inventory called out: an ordinary [Authorize] that reads
        // the same query credential media does. It must keep the default policy.
        var items = ApiAssembly.GetTypes().Single(t => t.Name == "ItemsController");

        var authorize = items.GetCustomAttributes<AuthorizeAttribute>(inherit: false).ToArray();

        Assert.NotEmpty(authorize);
        Assert.All(authorize, a => Assert.NotEqual(Policies.MediaDelivery, a.Policy));
        Assert.Empty(items.GetCustomAttributes<RequiresPlaybackCapabilityAttribute>(inherit: false));
    }

    [Fact]
    public void The_minting_endpoints_are_not_reachable_with_a_capability()
    {
        // "Never minted or renewed through a URL credential" has to be structural, or a later edit
        // could quietly let a capability mint its own successor and make expiry meaningless.
        foreach (var name in new[] { "PlaybackCredentialsController", "WebSocketTicketsController" })
        {
            var type = ApiAssembly.GetTypes().Single(t => t.Name == name);
            var authorize = type.GetCustomAttributes<AuthorizeAttribute>(inherit: false).ToArray();

            Assert.NotEmpty(authorize);
            Assert.All(authorize, a => Assert.NotEqual(Policies.MediaDelivery, a.Policy));
            Assert.All(authorize, a => Assert.Null(a.AuthenticationSchemes));
        }
    }
}
