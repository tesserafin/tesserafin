using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Tesserafin.Server.Api.RemoteAccess;
using Tesserafin.Server.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.RemoteAccess;

public class RemoteAccessPublicationControllerTests
{
    [Fact]
    public async Task GetState_ReturnsOkWithState()
    {
        var caddy = new FakeCaddy(new ManagedState(true, "media.example.com", "/run/t.sock", "/etc/caddy/t.caddy", null));
        var sut = new RemoteAccessPublicationController(caddy);

        var result = await sut.GetState(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var state = Assert.IsType<ManagedState>(ok.Value);
        Assert.True(state.IsEnabled);
        Assert.Equal("media.example.com", state.Hostname);
    }

    [Fact]
    public async Task Apply_Success_ReturnsOk()
    {
        var caddy = new FakeCaddy(new ManagedState(true, null, null, null, null));
        var sut = new RemoteAccessPublicationController(caddy);
        var request = new ApplyRequest("media.example.com", "/run/t.sock", "127.0.0.1");

        var result = await sut.Apply(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Apply_Failure_ReturnsBadRequest()
    {
        var caddy = new FakeCaddy(new ManagedState(false, null, null, null, null), succeed: false);
        var sut = new RemoteAccessPublicationController(caddy);
        var request = new ApplyRequest("media.example.com", "/run/t.sock", "127.0.0.1");

        var result = await sut.Apply(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Disable_Success_ReturnsOk()
    {
        var caddy = new FakeCaddy(new ManagedState(false, null, null, null, null));
        var sut = new RemoteAccessPublicationController(caddy);

        var result = await sut.Disable(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    private sealed class FakeCaddy : IManagedCaddyService
    {
        private readonly ManagedState _state;
        private readonly bool _succeed;

        public FakeCaddy(ManagedState state, bool succeed = true)
        {
            _state = state;
            _succeed = succeed;
        }

        public Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ApplyResult(_succeed, _succeed ? null : "failed", _state));

        public Task<DisableResult> DisableAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DisableResult(_succeed, _succeed ? null : "failed"));

        public Task<ManagedState> GetStateAsync(CancellationToken cancellationToken)
            => Task.FromResult(_state);
    }
}
