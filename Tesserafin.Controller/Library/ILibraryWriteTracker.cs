using System.Collections.Generic;
using System.Threading.Tasks;

namespace Tesserafin.Controller.Library;

/// <summary>Tracks server writes until their delayed filesystem events have settled.</summary>
public interface ILibraryWriteTracker
{
    /// <summary>Gets a snapshot of paths whose filesystem events are suppressed.</summary>
    IEnumerable<string> Paths { get; }

    /// <summary>Begins a server write.</summary>
    /// <param name="path">The written path.</param>
    void Begin(string path);

    /// <summary>Releases one write after the filesystem quiet period.</summary>
    /// <param name="path">The written path.</param>
    /// <returns>The delayed release.</returns>
    Task CompleteAsync(string path);
}
