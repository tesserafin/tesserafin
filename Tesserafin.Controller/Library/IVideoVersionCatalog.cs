using System.Collections.Generic;
using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Reads the catalogue versions of a video.</summary>
public interface IVideoVersionCatalog
{
    /// <summary>Reads the distinct versions, starting with the requested video.</summary>
    /// <param name="video">The video whose version group is requested.</param>
    /// <returns>The video and its persisted alternate versions.</returns>
    IReadOnlyList<Video> GetVersions(Video video);
}
