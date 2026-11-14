using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Configuration;
using Tesserafin.Playback.Engine;
using Tesserafin.Playback.Shadow;
using Xunit;

namespace Tesserafin.Playback.Shadow.Tests.Runtime;

/// <summary>
/// Reproduces the exact ApplicationHost wiring (ApplicationHost.cs, PR104) for the shadow playback
/// registration: <see cref="ShadowMetrics"/> is its own singleton, and
/// <see cref="ShadowPlaybackSessionPlanner"/> receives it by injection instead of defaulting to a
/// privately-owned instance. Mirrors the precedent DI-wiring test pattern used for
/// ItemLookupService/IItemCacheStore (LibraryManagerItemLookupTests.DiWiring_...): a minimal
/// <see cref="ServiceCollection"/> built the same way as the real composition root, not a full
/// ApplicationHost spin-up.
/// </summary>
public sealed class ShadowMetricsDiWiringTests
{
    [Fact]
    public void DiWiring_ApplicationHostStyleRegistration_MetricsSingletonInjectedIntoDecorator()
    {
        // Mirrors ApplicationHost.cs exactly: the legacy "inner" planner is registered under its
        // own concrete type (there, PlaybackSessionPlanner), never under IPlaybackSessionPlanner
        // itself - only ShadowPlaybackSessionPlanner is exposed as IPlaybackSessionPlanner. Reusing
        // IPlaybackSessionPlanner for both would make the factory below resolve itself.
        var innerPlanner = Mock.Of<IPlaybackSessionPlanner>();

        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IPlaybackEngine>());
        services.AddSingleton(NullLogger<ShadowPlaybackSessionPlanner>.Instance);
        services.AddSingleton<ShadowMetrics>();
        services.AddSingleton<IPlaybackSessionPlanner>(provider => new ShadowPlaybackSessionPlanner(
            innerPlanner,
            provider.GetRequiredService<IPlaybackEngine>(),
            provider.GetRequiredService<NullLogger<ShadowPlaybackSessionPlanner>>(),
            static () => new PlaybackShadowOptions { Enabled = false },
            provider.GetRequiredService<ShadowMetrics>()));

        using var provider = services.BuildServiceProvider();

        var metrics = provider.GetRequiredService<ShadowMetrics>();
        var decorator = Assert.IsType<ShadowPlaybackSessionPlanner>(provider.GetRequiredService<IPlaybackSessionPlanner>());

        Assert.Same(metrics, decorator.Metrics);
    }

    [Fact]
    public void DiWiring_ShadowMetrics_ResolvesAsSameInstanceAcrossMultipleRequests()
    {
        // ShadowMetrics is a singleton specifically so a diagnostics endpoint or any other consumer
        // resolving it directly sees the SAME counters the shadow decorator is writing to - not a
        // second, disconnected instance.
        var services = new ServiceCollection();
        services.AddSingleton<ShadowMetrics>();

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<ShadowMetrics>();
        var second = provider.GetRequiredService<ShadowMetrics>();

        Assert.Same(first, second);
    }
}
