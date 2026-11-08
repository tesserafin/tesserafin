using System;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Server.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.RemoteAccess;

public class GatedManagedCaddyServiceTests
{
    [Fact]
    public async Task ApplyAsync_GatesFail_DoesNotCallInner()
    {
        var inner = new FakeInner();
        var sut = new GatedManagedCaddyService(
            inner,
            () => new PublicationFacts(false, true, ["127.0.0.1"], true),
            () => "CorrectHorseBatteryStaple!42");

        var result = await sut.ApplyAsync(
            new ApplyRequest("h", "/run/t.sock", "127.0.0.1"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Publication invariant", result.Error);
        Assert.Equal(0, inner.ApplyCount);
    }

    [Fact]
    public async Task ApplyAsync_WeakPassword_DoesNotCallInner()
    {
        var inner = new FakeInner();
        var sut = new GatedManagedCaddyService(
            inner,
            () => new PublicationFacts(true, true, ["127.0.0.1"], true),
            () => "weak");

        var result = await sut.ApplyAsync(
            new ApplyRequest("h", "/run/t.sock", "127.0.0.1"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("minimum strength", result.Error);
        Assert.Equal(0, inner.ApplyCount);
    }

    [Fact]
    public async Task ApplyAsync_GatesPass_DelegatesToInner()
    {
        var inner = new FakeInner();
        var sut = new GatedManagedCaddyService(
            inner,
            () => new PublicationFacts(true, true, ["127.0.0.1"], true),
            () => "CorrectHorseBatteryStaple!42");

        var result = await sut.ApplyAsync(
            new ApplyRequest("h", "/run/t.sock", "127.0.0.1"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, inner.ApplyCount);
    }

    private sealed class FakeInner : IManagedCaddyService
    {
        public int ApplyCount { get; private set; }

        public Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
        {
            ApplyCount++;
            return Task.FromResult(new ApplyResult(true, null, new ManagedState(true, null, null, null, null)));
        }

        public Task<DisableResult> DisableAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DisableResult(true, null));

        public Task<ManagedState> GetStateAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ManagedState(false, null, null, null, null));
    }
}
