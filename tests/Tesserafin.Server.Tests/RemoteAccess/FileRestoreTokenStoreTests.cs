using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Server.RemoteAccess;
using Xunit;

namespace Tesserafin.Server.Tests.RemoteAccess;

public class FileRestoreTokenStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storePath;

    public FileRestoreTokenStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "restore-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storePath = Path.Combine(_tempDir, "restore.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsSnapshot()
    {
        var store = new FileRestoreTokenStore(_storePath);
        var snapshot = new NetworkSnapshot(["10.0.0.1"], "127.0.0.1");

        await store.SaveAsync(snapshot, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(snapshot.KnownProxies, loaded.KnownProxies);
        Assert.Equal(snapshot.BindAddress, loaded.BindAddress);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsNull()
    {
        var store = new FileRestoreTokenStore(_storePath);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task Clear_RemovesFile()
    {
        var store = new FileRestoreTokenStore(_storePath);
        await store.SaveAsync(new NetworkSnapshot(["127.0.0.1"], null), CancellationToken.None);

        store.Clear();

        Assert.False(File.Exists(_storePath));
        Assert.Null(await store.LoadAsync(CancellationToken.None));
    }
}
