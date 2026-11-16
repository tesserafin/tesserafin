#pragma warning disable CS1591

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tesserafin.Controller.Providers;

/// <summary>Image-only HTTP acquisition shared by provider saves and managed image localization.</summary>
public interface IRemoteImageSource
{
    /// <summary>Acquires a validated image response, or a cached response for the same URL.</summary>
    /// <param name="url">The remote image URL.</param>
    /// <param name="cancellationToken">Request and queued-admission cancellation.</param>
    /// <returns>The MIME type and immutable-view image payload.</returns>
    Task<RemoteImageContent> GetAsync(string url, CancellationToken cancellationToken);
}
