using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Auth.HlsJobOwnership;
using Tesserafin.Api.Helpers;
using Tesserafin.Api.WebSocketListeners;
using Tesserafin.Api.WebSockets;
using Tesserafin.Controller.Net;
using Tesserafin.Server.Core;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class HttpCompositionBoundaryTests : IClassFixture<TesserafinApplicationFactory>
{
    private readonly TesserafinApplicationFactory _factory;

    public HttpCompositionBoundaryTests(TesserafinApplicationFactory factory) => _factory = factory;

    [Fact]
    public void CompiledCore_DoesNotReferenceTheApi()
        => Assert.DoesNotContain(
            typeof(ApplicationHost).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Tesserafin.Api", StringComparison.Ordinal));

    [Fact]
    public void TheExecutable_ComposesApiOwnedWebSocketServices()
    {
        var api = typeof(WebSocketManager).Assembly;
        var services = _factory.Services;
        Assert.Equal("Tesserafin.Api", api.GetName().Name);
        Assert.Equal(api, services.GetRequiredService<IWebSocketManager>().GetType().Assembly);
        Assert.Equal(api, typeof(WebSocketConnection).Assembly);
        Assert.Equal(api, typeof(WebSocketSessionTransport).Assembly);
        Assert.Equal(api, Assert.Single(services.GetServices<IWebSocketListener>().OfType<SessionWebSocketListener>()).GetType().Assembly);
    }

    [Fact]
    public void SessionTransport_IsNotExposedAsAnHttpController()
    {
        var feature = new ControllerFeature();
        _factory.Services.GetRequiredService<ApplicationPartManager>().PopulateFeature(feature);
        Assert.DoesNotContain(feature.Controllers, controller => controller.AsType() == typeof(WebSocketSessionTransport));
    }

    [Fact]
    public void HttpDeliveryServices_KeepTheirSingletonAndScopedLifetimes()
    {
        using var first = _factory.Services.CreateScope();
        using var second = _factory.Services.CreateScope();
        foreach (var type in new[] { typeof(IHlsJobOwnershipAuthorizer), typeof(PlaybackSessionDeliveryGuard), typeof(IWebSocketManager) })
        {
            Assert.Same(first.ServiceProvider.GetRequiredService(type), second.ServiceProvider.GetRequiredService(type));
        }

        foreach (var type in new[] { typeof(MediaInfoHelper), typeof(AudioHelper), typeof(DynamicHlsHelper) })
        {
            var service = first.ServiceProvider.GetRequiredService(type);
            Assert.Same(service, first.ServiceProvider.GetRequiredService(type));
            Assert.NotSame(service, second.ServiceProvider.GetRequiredService(type));
        }
    }
}
