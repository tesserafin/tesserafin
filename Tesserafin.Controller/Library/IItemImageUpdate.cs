#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>
/// A single-use image update holding per-item admission until disposal. Preparation never changes
/// published item metadata. Commit saves image rows before publishing the item/cache; disposal
/// removes uncommitted managed files. It never saves the whole item or invokes metadata providers.
/// </summary>
public interface IItemImageUpdate : IAsyncDisposable
{
    /// <summary>Gets a copy of the prepared image snapshot.</summary>
    IReadOnlyList<ItemImageInfo> Images { get; }

    /// <summary>Gets a copy of the selected localized image, if any.</summary>
    ItemImageInfo? Image { get; }

    /// <summary>Saves image rows before publishing metadata and cache; never saves the whole item.</summary>
    /// <param name="cancellationToken">Cancellation before the replacement transaction begins.</param>
    /// <returns>Publication or the original processing/persistence error.</returns>
    Task CommitAsync(CancellationToken cancellationToken);
}
