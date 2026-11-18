using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tesserafin.Api.Models.LibraryStructureDto;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Model.Configuration;
using Tesserafin.Server.Integration.Tests.EndToEnd;
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
        var reader = factory.Services.GetRequiredService<ILibraryStructureReader>();
        var name = "scan-" + Guid.NewGuid().ToString("N");
        var renamed = name + "-renamed";
        var catalogue = factory.Services.GetRequiredService<IItemRepository>();
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = Directory.CreateTempSubdirectory("library-scan-discovery-").FullName;
        try
        {
            var included = await EndToEndMediaFixtures.CreateH264AacMp4Async(source, "Discovery Journey (2026).mp4", durationSeconds: 1);
            var excluded = Path.Combine(source, "Ignored Journey (2026).mp4");
            File.Copy(included, excluded);
            await File.WriteAllTextAsync(Path.Combine(source, ".ignore"), "Ignored*.mp4\n", cancellationToken);
            var body = new AddVirtualFolderDto
            {
                LibraryOptions = new LibraryOptions
                {
                    EnableRealtimeMonitor = false,
                    PathInfos = [new MediaPathInfo(source)]
                }
            };
            using var created = await client.PostAsJsonAsync("Library/VirtualFolders?collectionType=movies&name=" + name, body, cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
            Assert.Contains(reader.GetVirtualFolders(), folder => folder.Name == name);
            Assert.Same(roots.GetRootFolder(), manager.RootFolder);

            var progress = new List<double>();
            await scans.ScanAsync(new InlineProgress(progress.Add), cancellationToken).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            Assert.Equal(100, progress[^1]);
            Assert.False(scans.IsScanRunning);
            var movie = Assert.IsType<Movie>(Assert.Single(catalogue.GetItemList(new InternalItemsQuery { Path = included })));
            Assert.Equal(included, movie.Path);
            Assert.Empty(catalogue.GetItemList(new InternalItemsQuery { Path = excluded }));

            using var moved = await client.PostAsync($"Library/VirtualFolders/Name?name={name}&newName={renamed}", null, cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
            Assert.Contains(reader.GetVirtualFolders(), folder => folder.Name == renamed);
            Assert.DoesNotContain(reader.GetVirtualFolders(), folder => folder.Name == name);
            using var removed = await client.DeleteAsync("Library/VirtualFolders?name=" + renamed, cancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            Assert.DoesNotContain(reader.GetVirtualFolders(), folder => folder.Name == renamed);
            Assert.False(scans.IsScanRunning);
            Assert.True(File.Exists(included));
            Assert.True(File.Exists(excluded));
        }
        finally
        {
            Directory.Delete(source, true);
        }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
