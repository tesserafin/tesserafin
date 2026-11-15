using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tesserafin.Api.Models.LibraryStructureDto;
using Tesserafin.Controller.Library;
using Tesserafin.Model.Configuration;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class LibraryScanEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    [Fact]
    public async Task StructureChangesAndFullScan_UseSharedRoot_AndReturnAfterReconciliation()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var scans = factory.Services.GetRequiredService<ILibraryScanCoordinator>();
        Assert.Contains(factory.Services.GetServices<IHostedService>(), service => ReferenceEquals(service, scans));
        var roots = factory.Services.GetRequiredService<ILibraryRootFolderProvider>();
        var manager = factory.Services.GetRequiredService<ILibraryManager>();
        var name = "scan-" + Guid.NewGuid().ToString("N");
        var renamed = name + "-renamed";
        var body = new AddVirtualFolderDto { LibraryOptions = new LibraryOptions { Enabled = false, EnableRealtimeMonitor = false } };
        using var created = await client.PostAsJsonAsync("Library/VirtualFolders?name=" + name, body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        Assert.Contains(manager.GetVirtualFolders(), folder => folder.Name == name);
        Assert.Same(roots.GetRootFolder(), manager.RootFolder);

        var progress = new List<double>();
        await scans.ScanAsync(new InlineProgress(progress.Add), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(100, progress[^1]);
        Assert.False(scans.IsScanRunning);
        using var moved = await client.PostAsync($"Library/VirtualFolders/Name?name={name}&newName={renamed}", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        Assert.Contains(manager.GetVirtualFolders(), folder => folder.Name == renamed);
        Assert.DoesNotContain(manager.GetVirtualFolders(), folder => folder.Name == name);
        using var removed = await client.DeleteAsync("Library/VirtualFolders?name=" + renamed, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.DoesNotContain(manager.GetVirtualFolders(), folder => folder.Name == renamed);
        Assert.False(scans.IsScanRunning);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
