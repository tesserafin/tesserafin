#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Prepares detached image changes; callers explicitly commit or discard each update.</summary>
public interface IItemImageService
{
    /// <summary>Raised after a changed image snapshot is saved and published.</summary>
    event EventHandler<ItemChangeEventArgs>? ImagesChanged;

    /// <summary>Prepares image localization, validation, dimensions and BlurHash.</summary>
    /// <param name="item">The item whose published image snapshot is captured after admission.</param>
    /// <param name="forceUpdate">Whether to reread current local image properties.</param>
    /// <param name="notify">Whether the commit publishes an image-change notification.</param>
    /// <param name="cancellationToken">Preparation and admission cancellation.</param>
    /// <returns>A single-use update to commit or discard.</returns>
    Task<IItemImageUpdate> PrepareRefreshAsync(BaseItem item, bool forceUpdate, bool notify, CancellationToken cancellationToken);

    /// <summary>Prepares the current image at a type/index for local delivery.</summary>
    /// <param name="item">The item.</param>
    /// <param name="image">The requested image type.</param>
    /// <param name="imageIndex">The index within that type.</param>
    /// <param name="removeOnFailure">Whether to prepare removal after all URLs are permanently rejected.</param>
    /// <param name="cancellationToken">Preparation and admission cancellation.</param>
    /// <returns>A single-use update; committing a rejected image propagates its failure after any removal.</returns>
    Task<IItemImageUpdate> PrepareLocalImageAsync(BaseItem item, ItemImageInfo image, int imageIndex, bool removeOnFailure, CancellationToken cancellationToken);
}
