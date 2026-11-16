using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Drawing;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Database.Implementations;
using Tesserafin.Model.Entities;
using Tesserafin.Providers.Manager;
using Xunit;

namespace Tesserafin.Server.Integration.Tests;

public sealed class ItemImageProcessingEndToEndTests(ItemImageProcessingEndToEndTests.ImageFactory factory) : IClassFixture<ItemImageProcessingEndToEndTests.ImageFactory>
{
    private static string? _token;

    [Fact]
    public async Task ImageInfos_LocalizesAndPersistsDimensionsAndBlurHash_WithoutSavingTheWholeItem()
    {
        using var client = await ClientAsync();
        using var source = new Source();
        var item = await SeedAsync(source.Path);
        var changes = factory.Services.GetRequiredService<IItemChangeEvents>();
        var notifications = 0;
        changes.ItemUpdated += Updated;
        try
        {
            using var response = await client.GetAsync($"Items/{item.Id}/Images", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("BlurHash", body, StringComparison.Ordinal);
            var image = Assert.Single(item.ImageInfos);
            Assert.True(image.IsLocalFile);
            Assert.Equal(32, image.Width);
            Assert.Equal(24, image.Height);
            Assert.False(string.IsNullOrEmpty(image.BlurHash));
            Assert.Equal(ImageHttp.Png, await File.ReadAllBytesAsync(image.Path, TestContext.Current.CancellationToken));
            var row = await ImageRowAsync(item.Id);
            Assert.Equal(image.Path, row.Path);
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(image.BlurHash!), row.Blurhash);
            Assert.Equal(DateTime.UnixEpoch, item.DateLastSaved);
            Assert.Equal(1, notifications);
            Assert.True(File.Exists(source.Path));
            using var repeated = await client.GetAsync($"Items/{item.Id}/Images", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.Equal(1, notifications);
        }
        finally
        {
            changes.ItemUpdated -= Updated;
        }

        void Updated(object? sender, ItemChangeEventArgs args)
        {
            if (args.Item.Id.Equals(item.Id))
            {
                notifications++;
            }
        }
    }

    [Fact]
    public async Task ImageGet_UsesThePreparedImage_AndConcurrentRequestsPublishOneManagedFile()
    {
        using var client = await ClientAsync();
        using var source = new Source();
        var item = await SeedAsync(source.Path);
        var requests = Enumerable.Range(0, 2).Select(_ => client.GetAsync($"Items/{item.Id}/Images/Primary?format=png", TestContext.Current.CancellationToken)).ToArray();
        var responses = await Task.WhenAll(requests);
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
                Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }
        }

        var path = Assert.Single(item.ImageInfos).Path;
        Assert.Equal(path, (await ImageRowAsync(item.Id)).Path);
        Assert.Single(Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!, "*.png"));
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.True(File.Exists(source.Path));
    }

    [Fact]
    public async Task DatabaseFailure_RollsBackRows_LeavesCacheUnchanged_CleansStagedFiles_AndAllowsRetry()
    {
        using var client = await ClientAsync();
        using var source = new Source();
        var item = await SeedAsync(source.Path);
        var remote = item.ImageInfos[0].Path;
        var original = item.ImageInfos;
        var databases = factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>();
        await using (var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await database.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_prepared_images BEFORE INSERT ON BaseItemImageInfos WHEN NEW.Path LIKE '%/images/%' BEGIN SELECT RAISE(ABORT, 'image commit failed'); END;", TestContext.Current.CancellationToken);
        }

        try
        {
            using var response = await client.GetAsync($"Items/{item.Id}/Images", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Same(original, item.ImageInfos);
            Assert.Equal(remote, (await ImageRowAsync(item.Id)).Path);
            AssertManagedFilesEmpty(item);
            Assert.True(File.Exists(source.Path));
        }
        finally
        {
            await using var database = await databases.CreateDbContextAsync(TestContext.Current.CancellationToken);
            await database.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_prepared_images;", TestContext.Current.CancellationToken);
        }

        using var retry = await client.GetAsync($"Items/{item.Id}/Images", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True(item.ImageInfos[0].IsLocalFile);
        Assert.Equal(item.ImageInfos[0].Path, (await ImageRowAsync(item.Id)).Path);
    }

    [Fact]
    public async Task CancelledHttpDownload_PreservesRemoteRowsAndCache_AndReleasesAdmissionForRetry()
    {
        using var client = await ClientAsync();
        using var source = new Source();
        var item = await SeedAsync(source.Path, slow: true);
        var url = item.ImageInfos[0].Path;
        var gate = factory.Images.Slow[url];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var request = client.GetAsync($"Items/{item.Id}/Images/Primary?format=png", cancellation.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        factory.Images.Slow.TryRemove(url, out _);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using (var discarded = await factory.Services.GetRequiredService<IItemImageService>().PrepareRefreshAsync(item, false, false, deadline.Token))
        {
            // This proves that request cancellation also drained its image admission.
        }

        Assert.False(item.ImageInfos[0].IsLocalFile);
        Assert.Equal(url, (await ImageRowAsync(item.Id)).Path);
        AssertManagedFilesEmpty(item);
        using var retry = await client.GetAsync($"Items/{item.Id}/Images/Primary?format=png", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task DeletionBetweenPreparationAndCommit_DoesNotResurrectTheItemOrCache()
    {
        using var client = await ClientAsync();
        using var source = new Source();
        var item = await SeedAsync(source.Path);
        var images = factory.Services.GetRequiredService<IItemImageService>();
        await using var update = await images.PrepareLocalImageAsync(item, item.ImageInfos[0], 0, true, TestContext.Current.CancellationToken);
        var staged = update.Image!.Path;
        await factory.Services.GetRequiredService<IItemDeletionCommands>().DeleteAsync(item, new DeleteOptions { DeleteFileLocation = false }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => update.CommitAsync(TestContext.Current.CancellationToken));
        Assert.Null(factory.Services.GetRequiredService<IItemLookupService>().GetItemById(item.Id));
        Assert.False(File.Exists(staged));
        Assert.True(File.Exists(source.Path));
        await using var database = await factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await database.BaseItemImageInfos.Where(row => row.ItemId.Equals(item.Id)).ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PermanentlyRejectedImage_IsRemovedOnce_WithoutRetryingOrChangingTheMedia(HttpStatusCode status)
    {
        using var client = await ClientAsync();
        using var source = new Source();
        var item = await SeedAsync(source.Path);
        var url = item.ImageInfos[0].Path;
        factory.Images.Failures[url] = status;
        using var response = await client.GetAsync($"Items/{item.Id}/Images", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(item.ImageInfos);
        await using var database = await factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await database.BaseItemImageInfos.Where(row => row.ItemId.Equals(item.Id)).ToArrayAsync(TestContext.Current.CancellationToken));
        using var repeated = await client.GetAsync($"Items/{item.Id}/Images", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(1, factory.Images.Calls[url]);
        Assert.True(File.Exists(source.Path));
    }

    private async Task<HttpClient> ClientAsync()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_token ??= await AuthHelper.CompleteStartupAsync(client));
        Assert.NotEqual("NullImageEncoder", factory.Services.GetRequiredService<IImageEncoder>().GetType().Name);
        return client;
    }

    private async Task<Movie> SeedAsync(string path, bool slow = false)
    {
        var id = Guid.NewGuid();
        var url = $"https://images.test/{id:N}.png";
        if (slow)
        {
            factory.Images.Slow[url] = new ImageGate();
        }

        var item = new Movie { Id = id, Name = "image-film", Path = path, IsInMixedFolder = true, DateLastSaved = DateTime.UnixEpoch, ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = url }] };
        factory.Services.GetRequiredService<ILibraryManager>().CreateItems([item], null, TestContext.Current.CancellationToken);
        Assert.True(await factory.Services.GetRequiredService<IItemPersistenceService>().SaveImagesAsync(item.Id, item.ImageInfos, TestContext.Current.CancellationToken));
        return item;
    }

    private async Task<Tesserafin.Data.Entities.BaseItemImageInfo> ImageRowAsync(Guid id)
    {
        await using var database = await factory.Services.GetRequiredService<IDbContextFactory<TesserafinDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await database.BaseItemImageInfos.SingleAsync(row => row.ItemId.Equals(id), TestContext.Current.CancellationToken);
    }

    private void AssertManagedFilesEmpty(BaseItem item)
    {
        var configuration = factory.Services.GetRequiredService<IServerConfigurationManager>();
        var directory = System.IO.Path.Combine(item.GetInternalMetadataPath(configuration.ApplicationPaths.InternalMetadataPath), "images");
        Assert.True(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0);
    }

#pragma warning disable CA1034 // xUnit requires public fixture types.
    public sealed class ImageFactory : TesserafinApplicationFactory
    {
        public ImageHttp Images { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_ => Images);
                services.AddSingleton<IRemoteImageSource>(provider => new RemoteImageSource(provider.GetRequiredService<ImageHttp>(), provider.GetRequiredService<IMemoryCache>()));
            });
        }
    }

    public sealed class ImageGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ImageHttp : IHttpClientFactory, IDisposable
    {
        public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAACAAAAAYCAIAAAAUMWhjAAAAJElEQVR4nGM4IedGU8QwasGoBaMWjFowasGoBaMWjFowNCwAAK76hC5iNzkIAAAAAElFTkSuQmCC");

        private readonly ConcurrentBag<HttpClient> _clients = [];

        public ConcurrentDictionary<string, ImageGate> Slow { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, HttpStatusCode> Failures { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new Handler(this));
            _clients.Add(client);
            return client;
        }

        public void Dispose()
        {
            foreach (var client in _clients)
            {
                client.Dispose();
            }
        }

        private sealed class Handler(ImageHttp owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.ToString();
                owner.Calls.AddOrUpdate(url, 1, (_, count) => count + 1);
                if (owner.Failures.TryGetValue(url, out var failure))
                {
                    return new HttpResponseMessage(failure);
                }

                if (owner.Slow.TryGetValue(url, out var gate))
                {
                    gate.Entered.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        gate.Cancelled.TrySetResult();
                        throw;
                    }
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return response;
            }
        }
    }

    private sealed class Source : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("image-host-source-").FullName;

        public Source()
        {
            Path = System.IO.Path.Combine(_root, "film.mp4");
            File.WriteAllBytes(Path, [1, 3, 5, 7]);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_root, true);
    }

#pragma warning restore CA1034
}
