using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Api.Models.LibraryStructureDto;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class LibraryStructureCommandsEndToEndTests(TesserafinApplicationFactory factory) : IClassFixture<TesserafinApplicationFactory>
{
    private static string? _accessToken;

    [Fact]
    public async Task HttpRead_UsesTheStructureReader_ReturnsOptionsAndLocations_AndPreservesDefinitionAndSources()
    {
        using var client = await ClientAsync();
        using var sources = new Sources();
        var commands = factory.Services.GetRequiredService<ILibraryStructureCommands>();
        var reader = factory.Services.GetRequiredService<ILibraryStructureReader>();
        var name = "read-structure-" + Guid.NewGuid().ToString("N");
        var options = Options(sources.First);
        options.SaveLocalMetadata = true;
        await commands.AddVirtualFolderAsync(name, CollectionTypeOptions.movies, options, false, TestContext.Current.CancellationToken);
        try
        {
            var direct = reader.GetVirtualFolders().Single(f => f.Name == name);
            Assert.Null(direct.RefreshStatus);
            var root = factory.Services.GetRequiredService<IServerConfigurationManager>().ApplicationPaths.DefaultUserViewsPath;
            var definition = Path.Combine(root, name, "options.xml");
            var bytes = await File.ReadAllBytesAsync(definition, TestContext.Current.CancellationToken);
            for (var i = 0; i < 2; i++)
            {
                var folders = await client.GetFromJsonAsync<VirtualFolderInfo[]>("Library/VirtualFolders", JsonDefaults.Options, TestContext.Current.CancellationToken);
                var folder = Assert.Single(Assert.IsType<VirtualFolderInfo[]>(folders), f => f.Name == name);
                Assert.Equal(direct.ItemId, folder.ItemId);
                Assert.Equal(CollectionTypeOptions.movies, folder.CollectionType);
                Assert.Equal(sources.First, Assert.Single(folder.Locations));
                Assert.True(folder.LibraryOptions.SaveLocalMetadata);
                Assert.NotNull(folder.RefreshStatus);
            }

            Assert.Equal(bytes, await File.ReadAllBytesAsync(definition, TestContext.Current.CancellationToken));
            Assert.Null(reader.GetVirtualFolders().Single(f => f.Name == name).RefreshStatus);
            sources.AssertMediaUntouched();
        }
        finally
        {
            await commands.RemoveVirtualFolderAsync(name, false, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CompleteJourney_ChangesDefinitionAndPaths_ButNeverDeletesSourceMedia()
    {
        using var client = await ClientAsync();
        using var sources = new Sources();
        var name = "structure-" + Guid.NewGuid().ToString("N");
        var renamed = name + "-renamed";
        var created = new AddVirtualFolderDto { LibraryOptions = Options(sources.First) };
        using (var response = await client.PostAsJsonAsync("Library/VirtualFolders?name=" + name, created, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var response = await client.PostAsJsonAsync("Library/VirtualFolders/Paths", new MediaPathDto { Name = name, PathInfo = new MediaPathInfo(sources.Second) }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var response = await client.PostAsJsonAsync("Library/VirtualFolders/Paths/Update", new UpdateMediaPathRequestDto { Name = name, PathInfo = new MediaPathInfo(sources.Second) }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var folder = factory.Services.GetRequiredService<ILibraryStructureReader>().GetVirtualFolders().Single(folder => folder.Name == name);
        Assert.Equal(2, folder.Locations.Length);
        var options = Options(sources.First, sources.Second);
        options.EnableRealtimeMonitor = false;
        options.SaveLocalMetadata = true;
        using (var response = await client.PostAsJsonAsync("Library/VirtualFolders/LibraryOptions", new UpdateLibraryOptionsDto { Id = Guid.Parse(folder.ItemId), LibraryOptions = options }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var response = await client.PostAsync($"Library/VirtualFolders/Name?name={name}&newName={renamed}", null, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var response = await client.DeleteAsync($"Library/VirtualFolders/Paths?name={renamed}&path={Uri.EscapeDataString(sources.Second)}", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        folder = factory.Services.GetRequiredService<ILibraryStructureReader>().GetVirtualFolders().Single(folder => folder.Name == renamed);
        Assert.Equal(sources.First, Assert.Single(folder.Locations));
        Assert.True(folder.LibraryOptions.SaveLocalMetadata);
        using (var response = await client.DeleteAsync("Library/VirtualFolders?name=" + renamed, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        Assert.DoesNotContain(factory.Services.GetRequiredService<ILibraryStructureReader>().GetVirtualFolders(), folder => folder.Name == name || folder.Name == renamed);
        sources.AssertMediaUntouched();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCreates_ChooseDistinctNamesAfterAdmission_AndCanBeRemovedIndependently(bool normalizeNames)
    {
        using var client = await ClientAsync();
        using var sources = new Sources();
        var name = "collision-" + Guid.NewGuid().ToString("N");
        var commands = factory.Services.GetRequiredService<ILibraryStructureCommands>();
        var jobs = new[] { sources.First, sources.Second }.Select((path, index) => Task.Run(() => commands.AddVirtualFolderAsync(normalizeNames && index == 1 ? " " + name + " " : name, null, Options(path), false, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        await Task.WhenAll(jobs);
        var folders = factory.Services.GetRequiredService<ILibraryStructureReader>().GetVirtualFolders().Where(folder => folder.Name.StartsWith(name, StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, folders.Length);
        Assert.Contains(folders, folder => folder.Name == name);
        Assert.Contains(folders, folder => folder.Name == name + "2");
        Assert.Equal(2, folders.SelectMany(folder => folder.Locations).Distinct().Count());
        foreach (var folder in folders)
        {
            await commands.RemoveVirtualFolderAsync(folder.Name, false, TestContext.Current.CancellationToken);
        }

        sources.AssertMediaUntouched();
    }

    [Fact]
    public async Task FailedOptionWrite_KeepsPreviouslyPublishedOptions_AndReleasesTheMutationSlot()
    {
        using var client = await ClientAsync();
        using var sources = new Sources();
        var commands = factory.Services.GetRequiredService<ILibraryStructureCommands>();
        var store = factory.Services.GetRequiredService<ILibraryOptionsStore>();
        var name = "failure-" + Guid.NewGuid().ToString("N");
        await commands.AddVirtualFolderAsync(name, null, Options(sources.First), false, TestContext.Current.CancellationToken);
        var folder = factory.Services.GetRequiredService<ILibraryStructureReader>().GetVirtualFolders().Single(folder => folder.Name == name);
        var root = factory.Services.GetRequiredService<IServerConfigurationManager>().ApplicationPaths.DefaultUserViewsPath;
        var path = Path.Combine(root, name);
        var file = Path.Combine(path, "options.xml");
        var previous = store.Get(path);
        var bytes = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        File.Delete(file);
        Directory.CreateDirectory(file);
        try
        {
            var changed = Options(sources.First);
            changed.SaveLocalMetadata = true;
            var failure = Record.Exception(() => store.Save(path, changed));
            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Same(previous, store.Get(path));
            await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                try
                {
                    await commands.UpdateLibraryOptionsAsync(Guid.Parse(folder.ItemId), changed, TestContext.Current.CancellationToken);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new IOException("expected failed options write", ex);
                }
            });
        }
        finally
        {
            Directory.Delete(file);
            await File.WriteAllBytesAsync(file, bytes, TestContext.Current.CancellationToken);
            store.Invalidate();
        }

        Assert.False(store.Get(path).SaveLocalMetadata);
        await commands.RemoveVirtualFolderAsync(name, false, TestContext.Current.CancellationToken);
        sources.AssertMediaUntouched();
    }

    private async Task<HttpClient> ClientAsync()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
        return client;
    }

    private static LibraryOptions Options(params string[] paths)
        => new() { Enabled = false, EnableRealtimeMonitor = false, PathInfos = paths.Select(path => new MediaPathInfo(path)).ToArray() };

    private sealed class Sources : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("structure-media-").FullName;

        public Sources()
        {
            First = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
            Second = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
            File.WriteAllText(Path.Combine(First, "keep.mkv"), "first media bytes");
            File.WriteAllText(Path.Combine(Second, "keep.mkv"), "second media bytes");
        }

        public string First { get; }

        public string Second { get; }

        public void AssertMediaUntouched()
        {
            Assert.Equal("first media bytes", File.ReadAllText(Path.Combine(First, "keep.mkv")));
            Assert.Equal("second media bytes", File.ReadAllText(Path.Combine(Second, "keep.mkv")));
        }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
