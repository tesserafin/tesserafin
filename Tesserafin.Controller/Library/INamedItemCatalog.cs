using System;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Read-only lookup and identity for artists, studios, genres, music genres and years.</summary>
public interface INamedItemCatalog
{
    /// <summary>Finds a persisted item; an artist backed by a media folder takes precedence.</summary>
    /// <param name="name">The catalogue name.</param>
    /// <typeparam name="T">A supported named item type.</typeparam>
    /// <returns>The existing item, or null. This method never creates a directory or catalogue row.</returns>
    T? Find<T>(string name)
        where T : BaseItem, new();

    /// <summary>Gets the matching identity without materialization, even when the item is absent.</summary>
    /// <param name="name">The catalogue name.</param>
    /// <typeparam name="T">A supported named item type.</typeparam>
    /// <returns>An existing artist's preferred identity, or the deterministic named identity.</returns>
    Guid GetId<T>(string name)
        where T : BaseItem, new();
}
