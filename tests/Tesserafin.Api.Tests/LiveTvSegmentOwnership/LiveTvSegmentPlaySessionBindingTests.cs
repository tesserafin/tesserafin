using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Attributes;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller.Net.PlaybackCredentials;
using Tesserafin.Server.Implementations.Security.PlaybackCredentials;
using Xunit;

namespace Tesserafin.Api.Tests.LiveTvSegmentOwnership;

/// <summary>
/// The legacy HLS video segment route must be bound to the play session the capability belongs to
/// (#153-LTV-R1, LTV-R0 finding 5).
/// </summary>
/// <remarks>
/// WHAT LTV-R0 MEASURED, AND PREDICTED BEFORE MEASURING IT.
/// <c>GetHlsVideoSegmentLegacy</c> carries
/// <c>[RequiresPlaybackCapability(Media, "itemId", "mediaSourceId")]</c> — the play-session key is
/// <see langword="null"/>. <c>ValidateCapability</c> guards its play-session comparison with
/// <c>if (demand.PlaySessionId is not null &amp;&amp; …)</c>, so a null demand skips the check
/// entirely. R0 minted a capability under a play session id the server had never issued and reached
/// a segment with it: <b>200, 387 468 bytes</b>.
///
/// These tests use the REAL <see cref="PlaybackCredentialService"/> and the REAL attribute
/// declared on the route, read off the route by reflection rather than restated here — a test that
/// restated the demand would keep passing after the route stopped matching it.
/// </remarks>
public sealed class LiveTvSegmentPlaySessionBindingTests
{
    private const string MediaSourceId = "6d5da76e3955fd1005f75c496c371521";
    private const string MintedUnder = "aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa";
    private const string PresentedOn = "bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb";

    private static readonly Guid _itemId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// The route must name a play session, or no demand can ever be built for one.
    /// </summary>
    [Fact]
    public void TheLiveTvVideoSegmentRoute_NamesAPlaySession()
    {
        var declared = SegmentRouteDemand();

        Assert.NotNull(declared.PlaySessionRouteKey);
    }

    /// <summary>
    /// A capability minted under one play session, presented on a request that names another, must
    /// be refused — same user, same item, same media source.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task ACapabilityFromAnotherPlaySession_IsRefusedOnTheSegmentRoute()
    {
        var service = new PlaybackCredentialService(TimeProvider.System, new CryptoRandomSecretSource());
        var grant = service.MintCapability(new PlaybackCapabilityRequest(
            Guid.NewGuid(),
            "session-a",
            "device-a",
            MintedUnder,
            _itemId,
            MediaSourceId,
            new[] { PlaybackCapabilityScope.Media }));

        var context = SegmentRequest(service, grant.Value, presentedPlaySessionId: PresentedOn);

        await SegmentRouteDemand().OnAuthorizationAsync(context);

        Assert.IsType<UnauthorizedResult>(context.Result);
    }

    /// <summary>
    /// The negative direction, so the refusal above cannot be a blanket one: the capability's own
    /// play session still reaches the route.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous test.</returns>
    [Fact]
    public async Task ACapabilityPresentedOnItsOwnPlaySession_IsStillAccepted()
    {
        var service = new PlaybackCredentialService(TimeProvider.System, new CryptoRandomSecretSource());
        var grant = service.MintCapability(new PlaybackCapabilityRequest(
            Guid.NewGuid(),
            "session-a",
            "device-a",
            MintedUnder,
            _itemId,
            MediaSourceId,
            new[] { PlaybackCapabilityScope.Media }));

        var context = SegmentRequest(service, grant.Value, presentedPlaySessionId: MintedUnder);

        await SegmentRouteDemand().OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    /// <summary>
    /// The attribute exactly as the production route declares it. Read from the route so the test
    /// cannot drift away from what ships.
    /// </summary>
    private static RequiresPlaybackCapabilityAttribute SegmentRouteDemand()
    {
        var action = typeof(HlsSegmentController)
            .GetMethod(nameof(HlsSegmentController.GetPlaybackSessionLiveSegment), BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(action);
        var attribute = action!.GetCustomAttribute<RequiresPlaybackCapabilityAttribute>();
        Assert.NotNull(attribute);
        return attribute!;
    }

    private static AuthorizationFilterContext SegmentRequest(
        IPlaybackCredentialService service,
        string presented,
        string presentedPlaySessionId)
    {
        var services = new ServiceCollection();
        services.AddSingleton(service);

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Request.QueryString = QueryString.Create(new Dictionary<string, string?>
        {
            ["playbackCapability"] = presented,
            ["mediaSourceId"] = MediaSourceId,
            // Exactly one spelling. Request.Query is case-INSENSITIVE, so setting both
            // `playSessionId` and `PlaySessionId` yields one comma-joined value and the route
            // refuses for a reason that has nothing to do with the binding under test.
            ["playSessionId"] = presentedPlaySessionId
        });

        var routeData = new RouteData();
        routeData.Values["itemId"] = _itemId.ToString("N");

        return new AuthorizationFilterContext(
            new ActionContext(httpContext, routeData, new ActionDescriptor()),
            new List<IFilterMetadata>());
    }
}
