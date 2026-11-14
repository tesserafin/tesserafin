using System;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Playback.Shadow;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class PlaybackObservationBoundaryTests : IClassFixture<TesserafinApplicationFactory>
{
    private readonly TesserafinApplicationFactory _factory;

    public PlaybackObservationBoundaryTests(TesserafinApplicationFactory factory) => _factory = factory;

    [Fact]
    public void CompiledEncoding_DoesNotReferenceShadowComparison()
        => Assert.DoesNotContain(
            typeof(PlaybackSessionManager).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Tesserafin.Playback.Shadow", StringComparison.Ordinal));

    [Fact]
    public void SessionObservationPort_UsesTheSameStoreAsAdminDiagnostics()
    {
        var services = _factory.Services;
        var store = services.GetRequiredService<IShadowDiagnosticsStore>();
        Assert.Same(store, services.GetRequiredService<IPlaybackSessionDiagnostics>());
        Assert.Equal("Tesserafin.Playback.Shadow", store.GetType().Assembly.GetName().Name);
        Assert.Equal(typeof(ShadowComparer).Assembly, typeof(ShadowPlaybackSessionPlanner).Assembly);
    }
}
