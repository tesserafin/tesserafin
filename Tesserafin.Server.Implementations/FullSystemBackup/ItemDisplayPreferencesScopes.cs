using System;
using System.Collections.Generic;
using System.Linq;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;

namespace Tesserafin.Server.Implementations.FullSystemBackup;

/// <summary>
/// Applies the rule of the <c>DeduplicateItemDisplayPreferences</c> migration to the item display
/// preferences of a backup being restored: a backup taken before that migration can hold several rows
/// for one scope (user, item, client), which the unique index of the current schema refuses. The row
/// with the lowest original id stays active; every other row of the scope is archived in full.
/// </summary>
internal sealed class ItemDisplayPreferencesScopes
{
    private readonly Dictionary<(Guid UserId, Guid ItemId, string Client), List<(ItemDisplayPreferences Row, int OriginalId)>> _scopes = new();

    /// <summary>
    /// Adds a row read from the backup.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="originalId">The id the row had in the backed up database.</param>
    public void Add(ItemDisplayPreferences row, int originalId)
    {
        var key = (row.UserId, row.ItemId, row.Client);
        if (!_scopes.TryGetValue(key, out var rows))
        {
            rows = [];
            _scopes.Add(key, rows);
        }

        rows.Add((row, originalId));
    }

    /// <summary>
    /// Splits the rows added into the rows to restore and the rows to archive.
    /// </summary>
    /// <param name="archivedAt">When the rows are archived, in UTC.</param>
    /// <returns>One active row per scope, and the archive of every other row.</returns>
    public (IReadOnlyList<ItemDisplayPreferences> Active, IReadOnlyList<ItemDisplayPreferencesArchive> Archived) Resolve(DateTime archivedAt)
    {
        var active = new List<ItemDisplayPreferences>();
        var archived = new List<ItemDisplayPreferencesArchive>();
        foreach (var rows in _scopes.Values)
        {
            var ordered = rows.OrderBy(entry => entry.OriginalId).ToList();
            var kept = ordered[0];
            active.Add(kept.Row);
            if (ordered.Count == 1)
            {
                continue;
            }

            var duplicateClass = kept.Row.ItemId.Equals(Guid.Empty)
                ? ItemDisplayPreferencesDuplicateClass.Global
                : ordered.Select(entry => Settings(entry.Row)).Distinct().Count() == 1
                    ? ItemDisplayPreferencesDuplicateClass.Identical
                    : ItemDisplayPreferencesDuplicateClass.Contradictory;
            archived.AddRange(ordered.Skip(1).Select(entry => new ItemDisplayPreferencesArchive
            {
                Id = entry.OriginalId,
                UserId = entry.Row.UserId,
                ItemId = entry.Row.ItemId,
                Client = entry.Row.Client,
                ViewType = entry.Row.ViewType,
                RememberIndexing = entry.Row.RememberIndexing,
                IndexBy = entry.Row.IndexBy,
                RememberSorting = entry.Row.RememberSorting,
                SortBy = entry.Row.SortBy,
                SortOrder = entry.Row.SortOrder,
                KeptId = kept.OriginalId,
                DuplicateClass = duplicateClass,
                ArchivedAt = archivedAt,
            }));
        }

        return (active, archived);
    }

    private static (string SortBy, SortOrder SortOrder, ViewType ViewType, IndexingKind? IndexBy, bool RememberSorting, bool RememberIndexing) Settings(ItemDisplayPreferences row)
        => (row.SortBy, row.SortOrder, row.ViewType, row.IndexBy, row.RememberSorting, row.RememberIndexing);
}
