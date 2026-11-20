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

    /// <summary>Checks for persisted local version links on the requested video.</summary>
    /// <param name="video">The video.</param>
    /// <returns>Whether the video has any local version links.</returns>
    bool HasLocalVersions(Video video);

    /// <summary>Counts version links for a video, including links to missing local files.</summary>
    /// <param name="video">The video.</param>
    /// <returns>The catalogue version count.</returns>
    int GetMediaSourceCount(Video video);
}
