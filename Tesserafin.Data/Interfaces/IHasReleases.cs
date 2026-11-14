using System.Collections.Generic;
using Tesserafin.Data.Entities.Libraries;

namespace Tesserafin.Data.Interfaces;

/// <summary>
/// An abstraction representing an entity that has releases.
/// </summary>
public interface IHasReleases
{
    /// <summary>
    /// Gets a collection containing this entity's releases.
    /// </summary>
    ICollection<Release> Releases { get; }
}
