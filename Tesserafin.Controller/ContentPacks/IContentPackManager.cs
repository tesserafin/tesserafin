using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;

namespace Tesserafin.Controller.ContentPacks;

/// <summary>
/// Owns content pack identity, metadata, ordering and membership.
/// </summary>
/// <remarks>
/// Nothing here evaluates item visibility. Membership is a relation, never a capability: callers
/// decide what a user may see through the ordinary item query path.
/// </remarks>
public interface IContentPackManager
{
    /// <summary>
    /// Gets every content pack, in the server's global ordering.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ordered packs.</returns>
    Task<IReadOnlyList<ContentPack>> GetPacksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one content pack.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pack, or <c>null</c> when it does not exist.</returns>
    Task<ContentPack?> GetPackAsync(Guid packId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the ids of the packs that hold at least one membership row.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ids of the packs that are not empty.</returns>
    /// <remarks>
    /// One bounded query, used only to tell a genuinely empty pack from one whose whole content is
    /// invisible to the caller. The raw membership count itself is never returned to a client.
    /// </remarks>
    Task<IReadOnlyCollection<Guid>> GetNonEmptyPackIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a content pack and gives it the last ordering position, in one transaction.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="description">The optional description.</param>
    /// <param name="origin">How the pack came into existence.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created pack.</returns>
    /// <exception cref="ArgumentException">The name is empty or too long.</exception>
    /// <exception cref="ContentPackNameConflictException">The normalized name is taken.</exception>
    Task<ContentPack> CreatePackAsync(string name, string? description, ContentPackOrigin origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a content pack's metadata. The identifier never changes.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="name">The new name.</param>
    /// <param name="description">The new optional description.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated pack.</returns>
    /// <exception cref="ArgumentException">The name is empty or too long.</exception>
    /// <exception cref="ContentPackNotFoundException">The pack does not exist.</exception>
    /// <exception cref="ContentPackNameConflictException">The normalized name belongs to another pack.</exception>
    Task<ContentPack> UpdatePackAsync(Guid packId, string name, string? description, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the global pack ordering, in one transaction.
    /// </summary>
    /// <param name="orderedPackIds">Every pack id, exactly once, in the wanted order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the reorder.</returns>
    /// <exception cref="ArgumentException">The submitted set is not a complete ordering.</exception>
    /// <exception cref="ContentPackNotFoundException">One of the ids does not exist.</exception>
    Task ReorderPacksAsync(IReadOnlyList<Guid> orderedPackIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a content pack and its membership rows, and nothing else, in one transaction.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> when a pack was deleted; <c>false</c> when none existed.</returns>
    Task<bool> DeletePackAsync(Guid packId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an item to a pack, or upgrades the provenance of an existing membership.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="provenance">Why the item is being added.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    /// <exception cref="ContentPackNotFoundException">The pack does not exist.</exception>
    /// <remarks>
    /// Idempotent. The caller must already have established that the user may see the item.
    /// </remarks>
    Task AddItemAsync(Guid packId, Guid itemId, ContentPackMembershipProvenance provenance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an item from a pack. Removing an absent membership succeeds.
    /// </summary>
    /// <param name="packId">The pack id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    /// <exception cref="ContentPackNotFoundException">The pack does not exist.</exception>
    Task RemoveItemAsync(Guid packId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the packs that contain an item, in the server's global ordering.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ordered packs containing the item.</returns>
    Task<IReadOnlyList<ContentPack>> GetPacksForItemAsync(Guid itemId, CancellationToken cancellationToken = default);
}
