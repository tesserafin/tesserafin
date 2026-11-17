using System.Collections.Generic;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Reads configured library definitions without scheduling refreshes or changing them.</summary>
public interface ILibraryStructureReader
{
    /// <summary>Gets configured libraries, resolved locations and existing catalogue metadata.</summary>
    /// <returns>A fresh list without metadata refresh state.</returns>
    IReadOnlyList<VirtualFolderInfo> GetVirtualFolders();
}
