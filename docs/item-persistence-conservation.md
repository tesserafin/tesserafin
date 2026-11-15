# Item deletion and history conservation

Related tracking: [#360](https://github.com/tesserafin/tesserafin/issues/360)
and [#351](https://github.com/tesserafin/tesserafin/issues/351).
Reproduction base: `d2fd71e800ce440cfd3438bebc1c2cceb9ca945c`.

## Scope of a destructive operation

`ItemPersistenceService.DeleteItem` prepares the transitive closure of the
requested ids through the persisted `BaseItems.ParentId` and `BaseItems.OwnerId`
relationships. `ParentId` has a database cascade; `OwnerId` identifies owned
extras and local versions that must be removed with their owner. Preparation
must visit both relationships at every level, regardless of `IsFolder`.
It rejects a closure containing the UserData placeholder before making changes.

`AncestorIds` is a query projection and does not determine which items this
destructive operation owns. A missing entry must not leave a cascaded child's
history unprepared; a stale entry must not authorize deleting unrelated media.
`LinkedChildren` is an association: deleting its parent removes the association,
not the linked media. This scope rule does not redefine every hierarchy query or
rebuild existing projections.

The traversal retains `WhereOneOrMany` parameter binding for large id sets.
It queries each frontier through the existing ParentId/OwnerId indexes. This is
a correctness change; no throughput improvement is claimed.

## One preparation path, caller-owned transaction

The public deletion method opens a transaction, computes the scope, detaches
UserData, cleans item-dependent rows and deletes the items before committing.
The preparation routine does not create or commit a transaction of its own.
For normalized item values, it records the values referenced by this deletion
scope, removes the scope's mappings, then deletes only those candidate values
that have no remaining references. Shared values and unrelated unmapped values
remain intact; this operation does not perform global value maintenance.

Saving a video may remove local alternate versions whose `OwnerId` is the video's
id. The save path queues these obsolete version ids, flushes its tracked item and
association changes, then uses that same preparation routine before committing
the existing save transaction. This includes descendants and owned extras of the
obsolete versions. Removing a linked alternate version's association does not
delete that media item.

A new parent attached in the same save batch is valid even though a database
query cannot see it before the first `SaveChanges`. Ancestor validation includes
the batch's item ids in addition to persisted ids, while still rejecting ancestor
ids that are neither persisted nor in the batch.

## Conservation policy and limits

The existing placeholder model, user/key isolation and retention date assignment
remain in use. There is no new purge, migration or change to the existing
retention task.

### Arbitration at deletion

Only one row per `(UserId, CustomDataKey)` can sit under the placeholder. The
rows of the deleted items and an already detached placeholder row for the same
user and key are arbitrated together, with the existing priorities:

1. greatest `LastPlayedDate` (a missing date ranks last, so two undated rows
   fall through to the count);
2. then greatest `PlayCount`;
3. on a complete tie, an incoming row over the existing placeholder row
   (the previous outcome of every conflict);
4. then the lowest `ItemId`, so the result never depends on storage order.

The winning row is kept whole; no field is merged from a losing row. Losers are
deleted. The kept row is under the placeholder with this deletion's retention
date, whether it came in now or was already detached (previously the key also
always carried this deletion's date).

| Example (same user, same key) | Before | Now |
| --- | --- | --- |
| Placeholder played 2026-10-05 ×9, deleted item played 2026-10-02 ×2 | Placeholder discarded, older history kept | Placeholder kept, retention date refreshed |
| Placeholder played 2026-10-01 ×1, deleted item played 2026-10-03 ×3 | Incoming kept | Incoming kept |
| Placeholder and deleted item both 2026-10-04 ×4 | Incoming kept | Incoming kept |
| Two deleted items both 2026-10-04 ×4 | Lowest `ItemId` in the observed SQLite plan | Lowest `ItemId`, explicitly |

### Reattachment

Callers: `MetadataService.SaveItemAsync` on an item's first refresh, and
`Folder.ValidateChildrenInternal2` after removing children, for each remaining
child (in enumeration order) sharing a removed child's user-data key. Each call
runs its own transaction.

`UserDataManager.SaveUserData` writes the same row under every key the item has
at that moment, and reads take one row per user and item (`FirstOrDefault`, or
the first row returned by the database). An item must therefore never hold two
divergent rows for the same user.

`ReattachUserDataAsync(item)` moves the placeholder rows whose key is one of the
item's user-data keys to that item and clears their retention date, for every
user **who holds no row on the item yet, under any key**. Such a user keeps the
active row unchanged; their detached rows stay under the placeholder, still
subject to retention. Before, a same-key conflict failed the UNIQUE key and
rolled back every user's reattachment, and a conflict under another key (item
played before its IMDb id was known) gave the user a second history that reads
listed first.

Reattachment is idempotent. With several candidate items for the same key, the
first item reattached receives the detached history and later candidates receive
nothing; no sharing policy between ambiguous media is defined here.

### Product decisions left open

- **Active row versus more relevant detached row at reattachment.** Example: a
  user watched a film to the end (2026-10-06, ×6), the file was deleted, the film
  was re-added and started (2026-10-04, ×1) before metadata refresh reattached
  it. Today the active row wins and the old history stays detached until retention
  deletes it. Replacing or merging the active row is a product choice.
- **Several candidate media.** Example: two editions with the same IMDb id are
  added; whichever refreshes first takes the history, and after a removal the
  folder walk reattaches in child enumeration order. Making that order stable
  still decides which copy gets the history; assigning, copying or splitting is
  a product choice.
- **Fields other than `LastPlayedDate`/`PlayCount` on a complete tie.** Example:
  same date and count, but only the losing row is a favorite. The winner is kept
  whole; per-field merging is not done.

## Reproduction and verification of the #351 slice

Before the fix (base `8496c120d8`, #363 head), on migrated SQLite through the real
service:

| Scenario | Before | After |
| --- | --- | --- |
| Placeholder more relevant than incoming row | More relevant history discarded (`500` expected, `200` kept) | Kept |
| Complete `LastPlayedDate`/`PlayCount` ties, both storage orders | Same winner (plan order) | Same winner (explicit rule) |
| Placeholder and active row, same user/key, at reattachment | `UNIQUE constraint failed` for every user | Active kept, detached kept, other user reattached |
| Active row under the item's own key, detached row under its IMDb key | Second, divergent history for that user, listed first | Detached row kept aside, one history |
| Undated placeholder vs dated or undated incoming row | Incoming always kept | Date first, then count |
| Several keys of one user, no active row | All attached | All attached |
| Several candidates, repeated reattachment, two users | Holds | Holds |
| Failure after the first reattached row | Whole database restored | Whole database restored |
| Failure after placeholder arbitration (trigger) | Arbitration never reached | Whole database restored |

## Reproduction and verification of the deletion scope

`ItemPersistenceConservationTests` invokes the actual persistence service with
SQLite initialized by the existing migrations. It mocks domain item lookup for
save inputs; it is not an HTTP end-to-end test or a production database repair.

The initial eight-case regression control found seven failures and one success:

| Scenario | Initial implementation |
| --- | --- |
| Missing ancestor entries, multi-level ParentId cascade | Cascaded histories are lost |
| Stale ancestor entry pointing to unrelated linked media | Unrelated media is deleted |
| Nested owned extras and their children | SQLite foreign-key failure |
| New parent/child batch, both input orders | Child's ancestor projection is missing |
| Removed local version and its child | Histories are lost |
| Injected failure during local-version removal | Deletion is reached without history preparation |
| Injected failure during public deletion | Prior effective mutations roll back |

Additional guards cover the placeholder entering the deletion closure, and batch
versus successive deletions with shared content keys and an older placeholder.
Two further cases exercise targeted value cleanup through both entry points:
they reject the pre-existing global cleanup, retain shared/unrelated values and
remove values made unreferenced by the current operation.
Fixtures contain two users, unrelated media, content-pack memberships and media
segments. Successful deletion checks retained playback/favorite/stream settings,
cleanup of dependent rows, surviving linked media and `PRAGMA foreign_key_check`.

The rollback witness is a SQLite trigger reached only after UserData has moved
to the placeholder and the item's content-pack membership has been deleted.
Its diagnostic distinguishes that state from failure before preparation. Both
entry points must restore snapshots of every application table; the save witness
also exercises an earlier primary-item update and association removal.

Run the regression and existing content-pack deletion guard with:

```sh
dotnet test --project tests/Tesserafin.Server.Implementations.Tests/Tesserafin.Server.Implementations.Tests.csproj \
  --filter 'FullyQualifiedName~ItemPersistenceConservationTests|FullyQualifiedName~ContentPackItemDeletionTests'
```
