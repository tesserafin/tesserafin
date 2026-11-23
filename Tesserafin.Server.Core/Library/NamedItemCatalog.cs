using System;
using System.Linq;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Audio;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.Persistence;

namespace Tesserafin.Server.Core.Library;

/// <summary>Reads named metadata without creating directories, items or notifications.</summary>
internal sealed class NamedItemCatalog(NamedItemIdentity identity, IItemLookupService lookup, ILinkedChildrenService links) : INamedItemCatalog
{
    public T? Find<T>(string name)
        where T : BaseItem, new()
    {
        var id = identity.GetId<T>(name);
        if (typeof(T) == typeof(MusicArtist)
            && links.FindArtists([name]).TryGetValue(name, out var artists)
            && artists.Length > 0)
        {
            return artists.OrderBy(artist => artist.IsAccessedByName ? 1 : 0).ThenBy(artist => artist.Id).First() as T;
        }

        return lookup.GetItemById<T>(id);
    }

    public Guid GetId<T>(string name)
        where T : BaseItem, new()
        => (typeof(T) == typeof(MusicArtist) ? Find<T>(name)?.Id : null) ?? identity.GetId<T>(name);
}
