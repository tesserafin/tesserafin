using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Tesserafin.Controller.ContentPacks;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Database.Implementations;

namespace Tesserafin.Server.Implementations.ContentPacks;

/// <summary>
/// Stores content pack identity, metadata, ordering and membership.
/// </summary>
public class ContentPackManager : IContentPackManager
{
    private readonly IDbContextFactory<TesserafinDbContext> _dbProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContentPackManager"/> class.
    /// </summary>
    /// <param name="dbProvider">The database provider.</param>
    public ContentPackManager(IDbContextFactory<TesserafinDbContext> dbProvider)
    {
        _dbProvider = dbProvider;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentPack>> GetPacksAsync(CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            return await dbContext.ContentPacks
                .AsNoTracking()
                .OrderBy(e => e.SortOrder)
                .ThenBy(e => e.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<ContentPack?> GetPackAsync(Guid packId, CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            return await dbContext.ContentPacks
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id.Equals(packId), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<Guid>> GetNonEmptyPackIdsAsync(CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            return await dbContext.ContentPackMemberships
                .AsNoTracking()
                .Select(e => e.PackId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<ContentPack> CreatePackAsync(string name, string? description, ContentPackOrigin origin, CancellationToken cancellationToken = default)
    {
        var validatedName = ValidateName(name);
        var validatedDescription = ValidateDescription(description);
        var normalizedName = ContentPack.Normalize(validatedName);

        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                if (await dbContext.ContentPacks.AnyAsync(e => e.NormalizedName == normalizedName, cancellationToken).ConfigureAwait(false))
                {
                    throw new ContentPackNameConflictException(FormattableString.Invariant($"A content pack named '{validatedName}' already exists."));
                }

                var lastPosition = await dbContext.ContentPacks
                    .Select(e => (int?)e.SortOrder)
                    .MaxAsync(cancellationToken)
                    .ConfigureAwait(false);

                var pack = new ContentPack
                {
                    Id = Guid.NewGuid(),
                    Name = validatedName,
                    NormalizedName = normalizedName,
                    Description = validatedDescription,
                    SortOrder = (lastPosition ?? -1) + 1,
                    Origin = origin,
                    DateCreated = DateTime.UtcNow
                };

                dbContext.ContentPacks.Add(pack);

                try
                {
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException ex)
                {
                    // The unique index, not the check above, is what settles a genuine race. The
                    // name is re-read on a separate connection, so a failure with any other cause
                    // propagates untouched instead of being reported as a duplicate.
                    if (!await IsNameTakenAsync(normalizedName, pack.Id, cancellationToken).ConfigureAwait(false))
                    {
                        throw;
                    }

                    throw new ContentPackNameConflictException(FormattableString.Invariant($"A content pack named '{validatedName}' already exists."), ex);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return pack;
            }
        }
    }

    /// <inheritdoc />
    public async Task<ContentPack> UpdatePackAsync(Guid packId, string name, string? description, CancellationToken cancellationToken = default)
    {
        var validatedName = ValidateName(name);
        var validatedDescription = ValidateDescription(description);
        var normalizedName = ContentPack.Normalize(validatedName);

        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var pack = await dbContext.ContentPacks
                    .FirstOrDefaultAsync(e => e.Id.Equals(packId), cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new ContentPackNotFoundException(FormattableString.Invariant($"No content pack exists with id {packId}."));

                if (await dbContext.ContentPacks.AnyAsync(e => e.NormalizedName == normalizedName && !e.Id.Equals(packId), cancellationToken).ConfigureAwait(false))
                {
                    throw new ContentPackNameConflictException(FormattableString.Invariant($"A content pack named '{validatedName}' already exists."));
                }

                // Identity is untouched on purpose: a rename is a metadata update, so every
                // membership, link and client bookmark survives it.
                pack.Name = validatedName;
                pack.NormalizedName = normalizedName;
                pack.Description = validatedDescription;

                try
                {
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException ex)
                {
                    if (!await IsNameTakenAsync(normalizedName, packId, cancellationToken).ConfigureAwait(false))
                    {
                        throw;
                    }

                    throw new ContentPackNameConflictException(FormattableString.Invariant($"A content pack named '{validatedName}' already exists."), ex);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return pack;
            }
        }
    }

    /// <inheritdoc />
    public async Task ReorderPacksAsync(IReadOnlyList<Guid> orderedPackIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedPackIds);

        if (orderedPackIds.Distinct().Count() != orderedPackIds.Count)
        {
            throw new ArgumentException("The submitted ordering repeats a content pack id.", nameof(orderedPackIds));
        }

        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var packs = await dbContext.ContentPacks
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var known = packs.Select(e => e.Id).ToHashSet();

                foreach (var id in orderedPackIds)
                {
                    if (!known.Contains(id))
                    {
                        throw new ContentPackNotFoundException(FormattableString.Invariant($"No content pack exists with id {id}."));
                    }
                }

                if (orderedPackIds.Count != packs.Count)
                {
                    throw new ArgumentException("The submitted ordering must list every content pack exactly once.", nameof(orderedPackIds));
                }

                // Rewriting the whole list inside one transaction is what keeps the result a
                // contiguous ordering even when two callers reorder at the same time.
                for (var position = 0; position < orderedPackIds.Count; position++)
                {
                    var pack = packs.First(e => e.Id.Equals(orderedPackIds[position]));
                    pack.SortOrder = position;
                }

                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeletePackAsync(Guid packId, CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var pack = await dbContext.ContentPacks
                    .FirstOrDefaultAsync(e => e.Id.Equals(packId), cancellationToken)
                    .ConfigureAwait(false);

                if (pack is null)
                {
                    return false;
                }

                // Membership links only. No item, file, metadata, artwork, collection or library
                // is reachable from here, and the deletion never leaves this DbSet pair.
                await dbContext.ContentPackMemberships
                    .Where(e => e.PackId.Equals(packId))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);

                dbContext.ContentPacks.Remove(pack);

                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
    }

    /// <inheritdoc />
    public async Task AddItemAsync(Guid packId, Guid itemId, ContentPackMembershipProvenance provenance, CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            if (!await dbContext.ContentPacks.AnyAsync(e => e.Id.Equals(packId), cancellationToken).ConfigureAwait(false))
            {
                throw new ContentPackNotFoundException(FormattableString.Invariant($"No content pack exists with id {packId}."));
            }

            var existing = await dbContext.ContentPackMemberships
                .FirstOrDefaultAsync(e => e.PackId.Equals(packId) && e.ItemId.Equals(itemId), cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                await ApplyProvenanceTransitionAsync(dbContext, existing, provenance, cancellationToken).ConfigureAwait(false);
                return;
            }

            dbContext.ContentPackMemberships.Add(new ContentPackMembership
            {
                PackId = packId,
                ItemId = itemId,
                Provenance = provenance,
                DateCreated = DateTime.UtcNow
            });

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // The composite key, not the read above, is what absorbs a duplicate race. Re-read
                // the row: if it is there the add was simply concurrent and stays idempotent, and
                // if it is not the failure had another cause and must propagate.
                dbContext.ChangeTracker.Clear();

                var raced = await dbContext.ContentPackMemberships
                    .FirstOrDefaultAsync(e => e.PackId.Equals(packId) && e.ItemId.Equals(itemId), cancellationToken)
                    .ConfigureAwait(false);

                if (raced is null)
                {
                    throw;
                }

                await ApplyProvenanceTransitionAsync(dbContext, raced, provenance, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task RemoveItemAsync(Guid packId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            if (!await dbContext.ContentPacks.AnyAsync(e => e.Id.Equals(packId), cancellationToken).ConfigureAwait(false))
            {
                throw new ContentPackNotFoundException(FormattableString.Invariant($"No content pack exists with id {packId}."));
            }

            // Only the relation. Removing an absent membership deletes nothing and still succeeds.
            await dbContext.ContentPackMemberships
                .Where(e => e.PackId.Equals(packId) && e.ItemId.Equals(itemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentPack>> GetPacksForItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            return await dbContext.ContentPacks
                .AsNoTracking()
                .Where(e => dbContext.ContentPackMemberships
                    .Where(m => m.ItemId.Equals(itemId))
                    .Select(m => m.PackId)
                    .Contains(e.Id))
                .OrderBy(e => e.SortOrder)
                .ThenBy(e => e.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A content pack name must not be empty.", nameof(name));
        }

        if (trimmed.Length > ContentPack.NameMaxLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A content pack name must be at most {ContentPack.NameMaxLength} characters."),
                nameof(name));
        }

        return trimmed;
    }

    private static string? ValidateDescription(string? description)
    {
        if (description is null)
        {
            return null;
        }

        var trimmed = description.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length > ContentPack.DescriptionMaxLength)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A content pack description must be at most {ContentPack.DescriptionMaxLength} characters."),
                nameof(description));
        }

        return trimmed;
    }

    private static async Task ApplyProvenanceTransitionAsync(
        TesserafinDbContext dbContext,
        ContentPackMembership existing,
        ContentPackMembershipProvenance incoming,
        CancellationToken cancellationToken)
    {
        // Manual is a person's explicit decision and automation never overwrites it. The only
        // move an existing row makes is SystemSeed -> Manual.
        if (existing.Provenance == ContentPackMembershipProvenance.Manual
            || incoming != ContentPackMembershipProvenance.Manual)
        {
            return;
        }

        existing.Provenance = ContentPackMembershipProvenance.Manual;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsNameTakenAsync(string normalizedName, Guid excludedPackId, CancellationToken cancellationToken)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            return await dbContext.ContentPacks
                .AsNoTracking()
                .AnyAsync(e => e.NormalizedName == normalizedName && !e.Id.Equals(excludedPackId), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
