using System;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.Library;

/// <summary>Owns configuration edits, reconciliation and scan requests for library structure.</summary>
public interface ILibraryStructureCommands
{
    /// <summary>Creates a library with a unique name under exclusive scan admission.</summary>
    /// <param name="name">The library name.</param>
    /// <param name="collectionType">The library collection type.</param>
    /// <param name="options">The library options.</param>
    /// <param name="refreshLibrary">Whether to queue a full scan after reconciliation.</param>
    /// <param name="cancellationToken">Cancellation for admission; committed edits are reconciled before cancellation is reported.</param>
    /// <returns>The operation, including reconciliation and monitor restoration.</returns>
    Task AddVirtualFolderAsync(string name, CollectionTypeOptions? collectionType, LibraryOptions options, bool refreshLibrary, CancellationToken cancellationToken);

    /// <summary>Removes a library definition, leaving its source media untouched.</summary>
    /// <param name="name">The library name.</param>
    /// <param name="refreshLibrary">Whether to queue a full scan after reconciliation.</param>
    /// <param name="cancellationToken">Cancellation for admission; committed edits are reconciled before cancellation is reported.</param>
    /// <returns>The operation, including reconciliation and monitor restoration.</returns>
    Task RemoveVirtualFolderAsync(string name, bool refreshLibrary, CancellationToken cancellationToken);

    /// <summary>Renames a library, checking both names after exclusive admission.</summary>
    /// <param name="name">The library name.</param>
    /// <param name="newName">The destination name.</param>
    /// <param name="refreshLibrary">Whether to queue a full scan after reconciliation.</param>
    /// <param name="cancellationToken">Cancellation for admission; committed edits are reconciled before cancellation is reported.</param>
    /// <returns>The operation, including reconciliation and monitor restoration.</returns>
    Task<LibraryRenameResult> RenameVirtualFolderAsync(string name, string newName, bool refreshLibrary, CancellationToken cancellationToken);

    /// <summary>Adds a source directory and persists its path options.</summary>
    /// <param name="name">The library name.</param>
    /// <param name="path">The source directory and its options.</param>
    /// <param name="refreshLibrary">Whether to queue a full scan after reconciliation.</param>
    /// <param name="cancellationToken">Cancellation for admission; committed edits are reconciled before cancellation is reported.</param>
    /// <returns>The operation, including reconciliation and monitor restoration.</returns>
    Task AddMediaPathAsync(string name, MediaPathInfo path, bool refreshLibrary, CancellationToken cancellationToken);

    /// <summary>Synchronizes library options with the stored source directory references.</summary>
    /// <param name="name">The library name.</param>
    /// <param name="path">The source directory and its options.</param>
    /// <param name="cancellationToken">Cancellation for admission; committed edits are reconciled before cancellation is reported.</param>
    /// <returns>The operation, including reconciliation and monitor restoration.</returns>
    Task UpdateMediaPathAsync(string name, MediaPathInfo path, CancellationToken cancellationToken);

    /// <summary>Removes a source directory reference without deleting source files.</summary>
    /// <param name="name">The library name.</param>
    /// <param name="path">The source directory and its options.</param>
    /// <param name="refreshLibrary">Whether to queue a full scan after reconciliation.</param>
    /// <param name="cancellationToken">Cancellation for admission; committed edits are reconciled before cancellation is reported.</param>
    /// <returns>The operation, including reconciliation and monitor restoration.</returns>
    Task RemoveMediaPathAsync(string name, string path, bool refreshLibrary, CancellationToken cancellationToken);

    /// <summary>Updates a library's options; returns false if the library no longer exists.</summary>
    /// <param name="id">The collection folder identifier.</param>
    /// <param name="options">The library options.</param>
    /// <param name="cancellationToken">Cancellation for admission; committed edits are reconciled before cancellation is reported.</param>
    /// <returns>The operation, including reconciliation and monitor restoration.</returns>
    Task<bool> UpdateLibraryOptionsAsync(Guid id, LibraryOptions options, CancellationToken cancellationToken);
}
