using Tesserafin.Controller.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Owns the application aggregate root independently of library operations.</summary>
public interface ILibraryRootFolderProvider
{
    /// <summary>Gets the shared root, creating it once after application composition.</summary>
    /// <returns>The application aggregate root.</returns>
    AggregateFolder GetRootFolder();
}
