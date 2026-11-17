using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Entities;
using Tesserafin.Data.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Collects provider candidates and resolves persisted, user-visible playback intros.</summary>
public interface IPlaybackIntroService
{
    /// <summary>Registers the providers at application startup, in playback order.</summary>
    /// <param name="providers">The intro providers.</param>
    void AddParts(IEnumerable<IIntroProvider> providers);

    /// <summary>Resolves an ordered, fully materialized list; this operation never saves candidates.</summary>
    /// <param name="item">The main media item.</param>
    /// <param name="user">The requesting user.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>Persisted videos visible to the user, in provider and candidate order.</returns>
    Task<IReadOnlyList<Video>> GetIntrosAsync(BaseItem item, User user, CancellationToken cancellationToken);
}
