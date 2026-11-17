using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Entities.Movies;
using Tesserafin.Controller.Entities.TV;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// What <see cref="LibraryItemSeeder.SeedVideo"/> registers.
/// </summary>
public enum SeededVideoKind
{
    /// <summary>A <see cref="Movie"/>.</summary>
    Film,

    /// <summary>An <see cref="Episode"/> of its own new series.</summary>
    Episode,

    /// <summary>A plain <see cref="Video"/>: what a home videos library resolves.</summary>
    HomeVideo,

    /// <summary>A <see cref="MusicVideo"/>.</summary>
    MusicVideo,
}
