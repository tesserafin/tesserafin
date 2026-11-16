using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Microsoft.Extensions.Caching.Memory;
using Tesserafin.Common.Net;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Net;

namespace Tesserafin.Providers.Manager;

/// <summary>Owns HTTP image acquisition without metadata, persistence or item-service dependencies.</summary>
/// <param name="clients">The configured HTTP client factory.</param>
/// <param name="cache">The shared short-lived response cache.</param>
public sealed class RemoteImageSource(IHttpClientFactory clients, IMemoryCache cache) : IRemoteImageSource, IDisposable
{
    private readonly AsyncKeyedLocker<string> _requests = new();

    /// <inheritdoc />
    public async Task<RemoteImageContent> GetAsync(string url, CancellationToken cancellationToken)
    {
        using (await _requests.LockAsync(url, cancellationToken).ConfigureAwait(false))
        {
            var key = (typeof(RemoteImageSource), url);
            if (cache.TryGetValue(key, out RemoteImageContent? cached) && cached is not null)
            {
                return cached;
            }

            using var response = await clients.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrEmpty(contentType) && url.Contains("/imagecache/", StringComparison.OrdinalIgnoreCase))
            {
                contentType = MediaTypeNames.Image.Png;
            }

            if (contentType is null || contentType.Equals(MediaTypeNames.Application.Octet, StringComparison.OrdinalIgnoreCase))
            {
                contentType = MimeTypes.GetMimeType(new Uri(url).GetLeftPart(UriPartial.Path));
            }

            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException($"Request returned '{contentType}' instead of an image type", null, HttpStatusCode.NotFound);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var content = new RemoteImageContent(contentType, bytes);
            cache.Set(key, content, TimeSpan.FromSeconds(10));
            return content;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _requests.Dispose();
}
