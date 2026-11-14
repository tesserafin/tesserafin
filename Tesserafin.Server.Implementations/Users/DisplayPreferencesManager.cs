using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Tesserafin.Controller;
using Tesserafin.Data.Entities;
using Tesserafin.Database.Implementations;

namespace Tesserafin.Server.Implementations.Users;

/// <summary>
/// Manages the storage and retrieval of display preferences through Entity Framework.
/// </summary>
public sealed class DisplayPreferencesManager : IDisplayPreferencesManager
{
    private readonly IDbContextFactory<TesserafinDbContext> _dbContextFactory;

    // Serializes find-or-create. ItemDisplayPreferences and DisplayPreferences both have a unique index
    // on their scope, which would turn the race into a failed request; the lock makes the second reader
    // find the row the first one created instead. Creation only happens once per scope, so the lock is
    // taken on a cache miss only.
    private readonly Lock _createLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DisplayPreferencesManager"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The database context factory.</param>
    public DisplayPreferencesManager(IDbContextFactory<TesserafinDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    /// <inheritdoc />
    public DisplayPreferences GetDisplayPreferences(Guid userId, Guid itemId, string client)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        DisplayPreferences? Find() => dbContext.DisplayPreferences
            .Include(pref => pref.HomeSections)
            .FirstOrDefault(pref =>
                pref.UserId.Equals(userId) && pref.Client == client && pref.ItemId.Equals(itemId));

        var prefs = Find();
        if (prefs is not null)
        {
            return prefs;
        }

        lock (_createLock)
        {
            prefs = Find();
            if (prefs is null)
            {
                prefs = new DisplayPreferences(userId, itemId, client);
                dbContext.DisplayPreferences.Add(prefs);
                dbContext.SaveChanges();
            }

            return prefs;
        }
    }

    /// <inheritdoc />
    public ItemDisplayPreferences GetItemDisplayPreferences(Guid userId, Guid itemId, string client)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        // The scope is unique since the DeduplicateItemDisplayPreferences migration, which kept the
        // oldest row of each scope; ordering by id keeps reading that row.
        ItemDisplayPreferences? Find() => dbContext.ItemDisplayPreferences
            .Where(pref => pref.UserId.Equals(userId) && pref.ItemId.Equals(itemId) && pref.Client == client)
            .OrderBy(pref => pref.Id)
            .FirstOrDefault();

        var prefs = Find();
        if (prefs is not null)
        {
            return prefs;
        }

        lock (_createLock)
        {
            prefs = Find();
            if (prefs is null)
            {
                prefs = new ItemDisplayPreferences(userId, itemId, client);
                dbContext.ItemDisplayPreferences.Add(prefs);
                dbContext.SaveChanges();
            }

            return prefs;
        }
    }

    /// <inheritdoc />
    public IList<ItemDisplayPreferences> ListItemDisplayPreferences(Guid userId, string client)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        return dbContext.ItemDisplayPreferences
            .Where(prefs => prefs.UserId.Equals(userId) && !prefs.ItemId.Equals(default) && prefs.Client == client)
            .ToList();
    }

    /// <inheritdoc />
    public Dictionary<string, string?> ListCustomItemDisplayPreferences(Guid userId, Guid itemId, string client)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        return dbContext.CustomItemDisplayPreferences
            .Where(prefs => prefs.UserId.Equals(userId)
                            && prefs.ItemId.Equals(itemId)
                            && prefs.Client == client)
            .ToDictionary(prefs => prefs.Key, prefs => prefs.Value);
    }

    /// <inheritdoc />
    public void SetCustomItemDisplayPreferences(Guid userId, Guid itemId, string client, Dictionary<string, string?> customPreferences)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        // Replace the whole set atomically: without the transaction the delete commits on its own,
        // so concurrent saves interleave into a mix of both sets (or fail on the unique index), and a
        // failed insert loses every stored preference.
        InTransaction(dbContext, () =>
        {
            dbContext.CustomItemDisplayPreferences.Where(prefs => prefs.UserId.Equals(userId)
                                && prefs.ItemId.Equals(itemId)
                                && prefs.Client == client)
                                .ExecuteDelete();

            foreach (var (key, value) in customPreferences)
            {
                dbContext.CustomItemDisplayPreferences
                    .Add(new CustomItemDisplayPreferences(userId, itemId, client, key, value));
            }

            dbContext.SaveChanges();
        });
    }

    /// <inheritdoc/>
    public void UpdateDisplayPreferences(DisplayPreferences displayPreferences)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();

        // The entity was loaded by another context. Attaching it only marks new home sections as added:
        // sections removed from the collection would stay in the database, and edits to loaded ones would
        // be ignored. Delete the removed ones explicitly and save the loaded ones as modified, in one
        // transaction so a concurrent save cannot interleave with it.
        InTransaction(dbContext, () =>
        {
            var keptSectionIds = displayPreferences.HomeSections
                .Where(section => section.Id != 0)
                .Select(section => section.Id)
                .ToList();
            dbContext.Set<HomeSection>()
                .Where(section => section.DisplayPreferencesId == displayPreferences.Id && !keptSectionIds.Contains(section.Id))
                .ExecuteDelete();

            dbContext.DisplayPreferences.Attach(displayPreferences).State = EntityState.Modified;
            foreach (var section in displayPreferences.HomeSections.Where(section => section.Id != 0))
            {
                dbContext.Entry(section).State = EntityState.Modified;
            }

            dbContext.SaveChanges();
        });
    }

    /// <inheritdoc/>
    public void UpdateItemDisplayPreferences(ItemDisplayPreferences itemDisplayPreferences)
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        dbContext.ItemDisplayPreferences.Attach(itemDisplayPreferences).State = EntityState.Modified;
        dbContext.SaveChanges();
    }

    private static void InTransaction(TesserafinDbContext dbContext, Action write)
    {
        using var transaction = dbContext.Database.BeginTransaction();
        try
        {
            write();
            transaction.Commit();
        }
        catch
        {
            // Roll back explicitly: disposing an uncommitted transaction does not reach the transaction
            // interceptors, so the pessimistic locking mode would never release its database write lock.
            transaction.Rollback();
            throw;
        }
    }
}
