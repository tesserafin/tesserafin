using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Controllers;
using Tesserafin.Common.Api;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.PlaybackCredentials;

/// <summary>
/// The route inventory for #153-A0-R1 item 2, read off the endpoint table the framework actually
/// dispatches on.
/// </summary>
/// <remarks>
/// WHY NOT REFLECTION OVER <c>MethodInfo</c>. A0's structural test reads attributes off controller
/// methods, which is a good approximation and not the thing. It cannot see a policy inherited from
/// the controller as the routing layer composes it, it cannot see filters added by convention, and
/// it cannot enumerate the endpoints a route template expands into — one method with a
/// <c>[HttpGet]</c> and a <c>[HttpHead]</c> is two endpoints, and only one of them may be gated.
/// <see cref="EndpointDataSource"/> is what the request pipeline consults, so it is what this
/// asserts against.
///
/// IT ALSO SEES THE HIDDEN HLS ROUTES FOR FREE. Both HLS controllers are
/// <c>[ApiExplorerSettings(IgnoreApi = true)]</c>, so none of their routes appear in
/// <c>openapi/openapi.json</c> and the OpenAPI diff is not the list of routes the capability
/// protects. <c>ApiExplorerSettings</c> hides an endpoint from the explorer; it does not hide it
/// from routing.
/// </remarks>
[Collection(MediaBoundarySuite.Name)]
public sealed class MediaRouteMetadataTests
{
    private readonly MediaBoundaryFixture _fixture;

    public MediaRouteMetadataTests(MediaBoundaryFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Gets every endpoint a capability may reach, as "VERB /template", with the demand its
    /// attribute must declare.
    /// </summary>
    public static IReadOnlyDictionary<string, (PlaybackCapabilityScope Scope, string? ItemKey, string? MediaSourceKey, string? PlaySessionKey)> Roster { get; }
        = new Dictionary<string, (PlaybackCapabilityScope, string?, string?, string?)>(StringComparer.Ordinal)
        {
            ["GET /Videos/{itemId}/stream"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["HEAD /Videos/{itemId}/stream"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Videos/{itemId}/stream.{container}"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["HEAD /Videos/{itemId}/stream.{container}"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            // Consolidation step 1: a direct-play session's file route. The session id names the session;
            // item, media source and play session are query values the action also matches against it.
            ["GET /Playback/Sessions/{id}/File"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["HEAD /Playback/Sessions/{id}/File"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            // Consolidation step 1: a remux session's HLS routes, bound the same way.
            ["GET /Playback/Sessions/{id}/Hls/main.m3u8"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Playback/Sessions/{id}/Hls/hls1/{playlistId}/{segmentId}.{container}"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Audio/{itemId}/stream"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["HEAD /Audio/{itemId}/stream"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Audio/{itemId}/stream.{container}"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["HEAD /Audio/{itemId}/stream.{container}"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Audio/{itemId}/universal"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", null),
            ["HEAD /Audio/{itemId}/universal"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", null),
            ["GET /Videos/{itemId}/live.m3u8"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Videos/{itemId}/master.m3u8"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["HEAD /Videos/{itemId}/master.m3u8"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Audio/{itemId}/master.m3u8"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["HEAD /Audio/{itemId}/master.m3u8"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Videos/{itemId}/main.m3u8"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Audio/{itemId}/main.m3u8"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Videos/{itemId}/hls1/{playlistId}/{segmentId}.{container}"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Audio/{itemId}/hls1/{playlistId}/{segmentId}.{container}"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Videos/{itemId}/hls/{playlistId}/stream.m3u8"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", null),
            // #153-LTV-S1: the media source is read from the request here, as it already is on the
            // legacy playlist route above. A null demand agrees only with a capability bound to no
            // media source, which no Live TV transcode ever mints.
            // #153-LTV-R1: the play-session key was null here, which made ValidateCapability skip
            // its play-session comparison entirely. LTV-R0 reached a segment with a capability
            // minted under a play session the server had never issued.
            ["GET /Videos/{itemId}/hls/{playlistId}/{segmentId}.{segmentContainer}"] = (PlaybackCapabilityScope.Media, "itemId", "mediaSourceId", "playSessionId"),
            ["GET /Audio/{itemId}/hls/{segmentId}/stream.mp3"] = (PlaybackCapabilityScope.Media, "itemId", null, null),
            ["GET /Audio/{itemId}/hls/{segmentId}/stream.aac"] = (PlaybackCapabilityScope.Media, "itemId", null, null),
            ["GET /Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/Stream.{routeFormat}"] = (PlaybackCapabilityScope.Subtitles, "routeItemId", "routeMediaSourceId", null),
            ["GET /Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/{routeStartPositionTicks}/Stream.{routeFormat}"] = (PlaybackCapabilityScope.Subtitles, "routeItemId", "routeMediaSourceId", null),
            ["GET /Videos/{itemId}/{mediaSourceId}/Subtitles/{index}/subtitles.m3u8"] = (PlaybackCapabilityScope.Subtitles, "itemId", "mediaSourceId", null),
            ["GET /FallbackFont/Fonts"] = (PlaybackCapabilityScope.Fonts, null, null, null),
            ["GET /FallbackFont/Fonts/{name}"] = (PlaybackCapabilityScope.Fonts, null, null, null),
            ["GET /Videos/{videoId}/{mediaSourceId}/Attachments/{index}"] = (PlaybackCapabilityScope.Attachments, "videoId", "mediaSourceId", null),
            ["GET /Videos/{itemId}/Trickplay/{width}/tiles.m3u8"] = (PlaybackCapabilityScope.Trickplay, "itemId", "mediaSourceId", null),
            ["GET /Videos/{itemId}/Trickplay/{width}/{index}.jpg"] = (PlaybackCapabilityScope.Trickplay, "itemId", "mediaSourceId", null),
        };

    /// <summary>
    /// The whole point of item 2. An endpoint that narrows a capability but does not REQUIRE the
    /// policy is the A0 defect exactly: the attribute is inert when nothing is presented, so the
    /// route is anonymous, and it looks gated in every reflection-based inventory.
    /// </summary>
    [Fact]
    public void Every_endpoint_that_narrows_a_capability_also_requires_the_media_delivery_policy()
    {
        var offenders = Endpoints()
            .Where(endpoint => Capability(endpoint) is not null && !RequiresMediaDelivery(endpoint))
            .Select(Describe)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "these endpoints narrow a presented capability but require no policy, so they are anonymous:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The reverse direction. The media delivery policy is the only one naming the capability
    /// authentication scheme, so an endpoint carrying it that is not in the roster is an endpoint a
    /// capability can reach unnarrowed.
    /// </summary>
    [Fact]
    public void No_endpoint_outside_the_roster_requires_the_media_delivery_policy()
    {
        var offenders = Endpoints()
            .Where(RequiresMediaDelivery)
            .Select(Describe)
            .Where(description => !Roster.ContainsKey(description))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "these endpoints accept the capability scheme but are not classified as media:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The roster is exactly the set, in both directions, and each row's demand is what the
    /// attribute declares.
    /// </summary>
    [Fact]
    public void The_roster_is_exactly_the_set_of_capability_endpoints()
    {
        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var endpoint in Endpoints())
        {
            var capability = Capability(endpoint);
            if (capability is not null)
            {
                actual[Describe(endpoint)] = Demand(capability);
            }
        }

        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (description, demand) in Roster)
        {
            expected[description] = string.Create(
                CultureInfo.InvariantCulture,
                $"{demand.Scope}:{demand.ItemKey ?? "-"}:{demand.MediaSourceKey ?? "-"}:{demand.PlaySessionKey ?? "-"}");
        }

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// No media endpoint is anonymous, including the ones OpenAPI cannot see.
    /// </summary>
    [Fact]
    public void No_endpoint_of_the_hidden_hls_controllers_is_anonymous()
    {
        var hidden = HiddenHlsEndpoints().ToArray();

        // Enumerated by controller type, not by path substring: a keyword filter over route
        // templates finds thirteen and silently drops Videos/ActiveEncodings, which is an endpoint
        // of these controllers just as much as the segment routes are. Sixteen since the two
        // HLS routes of a remux session (Playback/Sessions/{id}/Hls/...) joined DynamicHlsController.
        Assert.Equal(16, hidden.Length);

        var anonymous = hidden
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0
                || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(Describe)
            .ToArray();

        Assert.True(
            anonymous.Length == 0,
            "these OpenAPI-hidden HLS endpoints require no authorization:\n  " + string.Join("\n  ", anonymous));
    }

    /// <summary>
    /// The finding that started R1, as a standing assertion: an endpoint that delivers media and
    /// requires nothing at all.
    /// </summary>
    [Fact]
    public void No_media_delivering_endpoint_is_anonymous()
    {
        var offenders = Endpoints()
            .Where(endpoint => LooksLikeMediaDelivery(endpoint)
                && endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0)
            .Select(Describe)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "these endpoints deliver media and require no authorization at all:\n  " + string.Join("\n  ", offenders));
    }

    private static string Demand(RequiresPlaybackCapabilityAttribute attribute)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{attribute.Scope}:{attribute.ItemRouteKey ?? "-"}:{attribute.MediaSourceRouteKey ?? "-"}:{attribute.PlaySessionRouteKey ?? "-"}");

    private static RequiresPlaybackCapabilityAttribute? Capability(Endpoint endpoint)
        => endpoint.Metadata.GetMetadata<RequiresPlaybackCapabilityAttribute>();

    private static bool RequiresMediaDelivery(Endpoint endpoint)
        => endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Any(data => string.Equals(data.Policy, Policies.MediaDelivery, StringComparison.Ordinal));

    private static string Describe(Endpoint endpoint)
    {
        var route = (RouteEndpoint)endpoint;
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
        var verb = methods is null ? "*" : string.Join('/', methods.HttpMethods);
        return string.Create(CultureInfo.InvariantCulture, $"{verb} /{route.RoutePattern.RawText}");
    }

    private static bool LooksLikeMediaDelivery(Endpoint endpoint)
    {
        var pattern = ((RouteEndpoint)endpoint).RoutePattern.RawText ?? string.Empty;
        return pattern.Contains("/stream", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains("/hls", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains("/universal", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains("/Subtitles/", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains("/Attachments/", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains("/Trickplay/", StringComparison.OrdinalIgnoreCase)
            || pattern.Contains("FallbackFont", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<RouteEndpoint> Endpoints()
        => _fixture.Factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>();

    private IEnumerable<RouteEndpoint> HiddenHlsEndpoints()
        => Endpoints().Where(endpoint =>
        {
            var descriptor = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            return descriptor is not null
                && (descriptor.ControllerTypeInfo.AsType() == typeof(DynamicHlsController)
                    || descriptor.ControllerTypeInfo.AsType() == typeof(HlsSegmentController));
        });
}
