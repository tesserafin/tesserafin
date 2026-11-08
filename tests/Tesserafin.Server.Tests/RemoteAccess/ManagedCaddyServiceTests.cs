using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Server.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.RemoteAccess;

public class ManagedCaddyServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fragmentPath;

    public ManagedCaddyServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "caddy-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _fragmentPath = Path.Combine(_tempDir, "tesserafin.caddy");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_ValidateFails_RollsBackFragment()
    {
        var runner = new FakeProcessRunner(validateExitCode: 1);
        var network = new FakeNetworkApplier();
        var sut = new ManagedCaddyService(_fragmentPath, runner, network);

        // Pre-existing fragment that must be restored
        await File.WriteAllTextAsync(_fragmentPath, "# original");

        var request = new ApplyRequest("media.example.com", "/run/tesserafin.sock", "127.0.0.1");
        var result = await sut.ApplyAsync(request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("validate failed", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("# original", await File.ReadAllTextAsync(_fragmentPath));
        Assert.Equal(0, network.ApplyCount);
    }

    [Fact]
    public async Task ApplyAsync_ReloadFails_RollsBackFragmentAndNetwork()
    {
        var runner = new FakeProcessRunner(validateExitCode: 0, reloadExitCode: 1);
        var network = new FakeNetworkApplier();
        var sut = new ManagedCaddyService(_fragmentPath, runner, network);

        await File.WriteAllTextAsync(_fragmentPath, "# original");

        var request = new ApplyRequest("media.example.com", "/run/tesserafin.sock", "127.0.0.1");
        var result = await sut.ApplyAsync(request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("reload failed", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("# original", await File.ReadAllTextAsync(_fragmentPath));
        Assert.Equal(1, network.ApplyCount);
        Assert.Equal(1, network.RestoreCount);
    }

    [Fact]
    public async Task ApplyAsync_Success_WritesFragmentAndAppliesNetwork()
    {
        var runner = new FakeProcessRunner(validateExitCode: 0, reloadExitCode: 0);
        var network = new FakeNetworkApplier();
        var sut = new ManagedCaddyService(_fragmentPath, runner, network);

        var request = new ApplyRequest("media.example.com", "/run/tesserafin.sock", "127.0.0.1");
        var result = await sut.ApplyAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.True(File.Exists(_fragmentPath));
        var content = await File.ReadAllTextAsync(_fragmentPath);
        Assert.Contains("media.example.com", content);
        Assert.Contains("unix//", content);
        Assert.Equal(1, network.ApplyCount);
        Assert.Equal(0, network.RestoreCount);
    }

    [Fact]
    public async Task ApplyAsync_PublicationGateFails_DoesNotMutate()
    {
        var runner = new FakeProcessRunner(validateExitCode: 0);
        var network = new FakeNetworkApplier();
        var sut = new ManagedCaddyService(
            _fragmentPath,
            runner,
            network,
            _ => false);

        var request = new ApplyRequest("media.example.com", "/run/tesserafin.sock", "127.0.0.1");
        var result = await sut.ApplyAsync(request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(_fragmentPath));
        Assert.Equal(0, network.ApplyCount);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly int _validateExitCode;
        private readonly int _reloadExitCode;

        public FakeProcessRunner(int validateExitCode, int reloadExitCode = 0)
        {
            _validateExitCode = validateExitCode;
            _reloadExitCode = reloadExitCode;
        }

        public Task<(int ExitCode, string Output)> RunAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken)
        {
            if (fileName == "caddy" && arguments.Contains("validate"))
            {
                return Task.FromResult((_validateExitCode, _validateExitCode == 0 ? "" : "validate failed"));
            }

            if (fileName == "systemctl" && arguments.Contains("reload"))
            {
                return Task.FromResult((_reloadExitCode, _reloadExitCode == 0 ? "" : "reload failed"));
            }

            return Task.FromResult((0, ""));
        }
    }

    private sealed class FakeNetworkApplier : IServerNetworkConfigApplier
    {
        public int ApplyCount { get; private set; }
        public int RestoreCount { get; private set; }

        public Task<IConfigRestoreToken> ApplyAsync(
            string knownProxyAddress,
            string unixSocketPath,
            CancellationToken cancellationToken)
        {
            ApplyCount++;
            return Task.FromResult<IConfigRestoreToken>(new FakeRestoreToken(this));
        }

        private sealed class FakeRestoreToken : IConfigRestoreToken
        {
            private readonly FakeNetworkApplier _owner;

            public FakeRestoreToken(FakeNetworkApplier owner) => _owner = owner;

            public Task RestoreAsync(CancellationToken cancellationToken)
            {
                _owner.RestoreCount++;
                return Task.CompletedTask;
            }
        }
    }
}
