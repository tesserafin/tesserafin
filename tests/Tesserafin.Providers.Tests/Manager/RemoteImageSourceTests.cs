using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Tesserafin.Providers.Manager;
using Xunit;

namespace Tesserafin.Providers.Tests.Manager;

public sealed class RemoteImageSourceTests
{
    [Theory]
    [InlineData("https://images.invalid/poster.png?key=1", null)]
    [InlineData("https://images.invalid/poster.png", "application/octet-stream")]
    [InlineData("https://images.invalid/imagecache/123", null)]
    public async Task HeaderFallbackAndCache_PreserveBytes_WithoutAnotherRequest(string url, string? contentType)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var calls = 0;
        using var handler = new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(contentType));
        });
        using var client = new HttpClient(handler);
        using var source = new RemoteImageSource(new Factory(client), cache);
        var first = await source.GetAsync(url, TestContext.Current.CancellationToken);
        var second = await source.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal("image/png", first.ContentType);
        Assert.Equal(new byte[] { 1, 3, 5 }, first.Bytes.ToArray());
        Assert.Equal(first.Bytes.ToArray(), second.Bytes.ToArray());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NonImageResponse_IsRejectedAndNeverCached()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var calls = 0;
        using var handler = new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response("text/html"));
        });
        using var client = new HttpClient(handler);
        using var source = new RemoteImageSource(new Factory(client), cache);
        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => source.GetAsync("https://images.invalid/poster", TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        }

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelTheAdmittedDownload_OrPoisonItsCache()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new Handler(async (_, token) =>
        {
            calls++;
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return Response("image/png");
        });
        using var client = new HttpClient(handler);
        using var source = new RemoteImageSource(new Factory(client), cache);
        const string Url = "https://images.invalid/poster.png";
        var first = source.GetAsync(Url, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var queued = source.GetAsync(Url, cancellation.Token);
        try
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        }
        finally
        {
            release.SetResult();
            await first;
        }

        await source.GetAsync(Url, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
    }

    private static HttpResponseMessage Response(string? contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 3, 5]) };
        if (contentType is not null)
        {
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        return response;
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
