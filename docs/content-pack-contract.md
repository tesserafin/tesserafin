# The ContentPack contract

Issues [#129](https://github.com/tesserafin/tesserafin/issues/129)
(post-release product sequencing),
[#142](https://github.com/tesserafin/tesserafin/issues/142) (idea
registry), [#146](https://github.com/tesserafin/tesserafin/issues/146)
(core / official-module / plugin boundary).

This document is **normative** for what a content pack is, what owns it, what it
may and may not do to media, and where the server / client line falls. It does
not implement anything. No production source, schema, generated SDK or
dependency changes accompany it.

Vocabulary, fixed here and used everywhere:

| Context | Term |
| --- | --- |
| UI / product | **Content pack** |
| Brand concept | each content pack is a *tessera*; together they form the Tesserafin mosaic |
| Code / API / database | `ContentPack` |

The brand concept belongs in marketing copy and in this document. It does **not**
appear in setup screens, settings labels, API names, error strings or database
identifiers. A first-run wizard says "content pack", never "tessera".

---

## 1. Why this exists

Tesserafin inherited an information model built around *physical* media
libraries: a folder on disk, given a `CollectionType`, becomes a user view, and
the whole navigation surface is generated from that. It answers "where is this
file" well. It answers "what kind of thing is this to me" badly.

The product idea is the opposite direction. A household's media is scattered
across films, television, sport, concerts, music, podcasts, audiobooks, photos
and home video, and the user wants those to read as one coherent mosaic that
*they* arranged — not as a list of mount points.

A **content pack** is the unit of that arrangement. It is a high-level
organisational and navigation lens over items the server already knows about.
It is deliberately not a folder, not a collection, and not a library.

RFC-0007 (Theme Platform v2, in `tesserafin-web`) gave the product a
presentation foundation: themes can now shape surfaces, cards, navigation, Home,
Library and Item Details. Presentation is settled enough that the next
differentiation problem is *semantic*, not visual — and native clients should
consume a stable semantic contract rather than each re-deriving the inherited
information architecture. This document is that contract.

---

## 2. Audit of the inherited model

Every path below was read at `dce55f23` on `master` unless the row says
`tesserafin-web`, in which case it was read at `641c5c82` on `main`.

The eight questions each row answers:

1. **Multi** — can one item already belong to many instances?
2. **Inert** — is membership free of any move / copy / mutation of media?
3. **Scope** — is membership global, per user, or per household?
4. **Mixed** — can mixed media families coexist in one instance?
5. **Pre-filtered** — is authorization applied before results are exposed?
6. **Safe delete** — can it be renamed or deleted without touching media?
7. **Stable** — is it stable enough for every official client to depend on?
8. **Preserves** — would reusing it leave existing semantics intact?

### 2.1 Evidence table

| Concept | Where it lives | 1 Multi | 2 Inert | 3 Scope | 4 Mixed | 5 Pre-filtered | 6 Safe delete | 7 Stable | 8 Preserves | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Library / storage root** (`VirtualFolder`, `MediaPathInfo`) | `Tesserafin.Api/Controllers/LibraryStructureController.cs`; `Tesserafin.Model/Entities/CollectionTypeOptions.cs` | No — a path belongs to one root | No — it *is* the scan/filesystem boundary | Global, filtered per user by `PermissionKind.EnableAllFolders` / `PreferenceKind.EnabledFolders` (`Tesserafin.Controller/Entities/Folder.cs:267`) | Constrained by `CollectionType` (`Tesserafin.Data/Enums/CollectionType.cs`) | Yes | No — removing a root removes its items from the library | Yes | No — reuse would overload discovery with organisation | **Rejected.** Discovery and filesystem access only. |
| **Collection / `BoxSet`** | `Tesserafin.Controller/Entities/Movies/BoxSet.cs:27`; `Tesserafin.Api/Controllers/CollectionController.cs`; `LinkedChildEntity` in `Tesserafin.Data/Entities/LinkedChildEntity.cs` | Yes, via `LinkedChildren` | **No** — a `BoxSet` *is a* `Folder` *is a* `BaseItem` with a `Path` under the data path (`BoxSet.cs` `IsLegacyBoxSet`); creating one writes to disk | Global, with `FilterLinkedChildrenPerUser => true` | In practice yes | Yes | **No** — deleting a `BoxSet` is an item deletion and runs the item-deletion path | Yes | **No** — reuse would redefine what a collection means to every existing user and client | **Rejected.** A collection is a curated grouping *of works*; it is itself an item. |
| **Playlist** | same `LinkedChildEntity` relation, `LinkedChildType` | Yes | No — also a `BaseItem` | Owner-scoped | Yes | Yes | No — item deletion | Yes | No | **Rejected.** Same objection as `BoxSet`, plus playlist ordering semantics. |
| **Tags / genres / studios** (`ItemValue` + `ItemValueMap`) | `src/Tesserafin.Database/.../Entities/ItemValue.cs`, `ItemValueMap.cs`, `ItemValueType.cs` | Yes — genuine many-to-many | Yes | Global | Yes | Filtered at query time | Values are metadata-derived; a re-scan can restore or drop them | Yes | **No** — these are *metadata facts about a work*, refreshed from providers. A user's arrangement is not a metadata fact and must not be overwritten by a metadata refresh | **Rejected for identity.** Correct shape, wrong ownership and wrong lifetime. |
| **`UserView` / library types** | `Tesserafin.Controller/Entities/UserView.cs`, `UserViewBuilder.cs`; `Tesserafin.Api/Controllers/UserViewsController.cs` | Views are generated, not authored | Yes | Per user | By `CollectionType` only | Yes | Views are derived, so nothing to delete | Generated shape, not a stored contract | No — reuse would make an authored object out of a derived one | **Rejected as storage.** Retained as the *rendering* target: a content pack may surface as a view. |
| **`AncestorId`** | `src/Tesserafin.Database/.../Entities/AncestorId.cs` | Hierarchy only | Yes | Global | n/a | n/a | n/a | Internal | No | **Rejected.** Hierarchy closure, not user arrangement. |
| **Dormant `Libraries/*` schema** (`Collection`, `CollectionItem`, `Library`, `LibraryItem`, `Genre`, …) | `src/Tesserafin.Database/.../Entities/Libraries/`; every corresponding `DbSet` is commented out in `TesserafinDbContext.cs:176` onward | — | — | — | — | — | — | **Not live** — no table, no migration | — | **Rejected.** Not a shortcut. Reviving a dormant parallel schema is a larger and riskier change than adding two new tables. |
| **Permissions / parental control** | `PermissionKind` (`…/Enums/PermissionKind.cs`, `EnableCollectionManagement = 21`), `PreferenceKind.EnabledFolders`, `AccessSchedule`, `BaseItem.cs:1593` | n/a | n/a | Per user | n/a | Yes — this is the gate | n/a | Yes | — | **Reused as-is.** Content packs are subject to it and never modify it. |
| **Item query** (`InternalItemsQuery`) | `Tesserafin.Controller/Entities/InternalItemsQuery.cs`; `BaseItem.cs:1593` folder-permission branch | n/a | n/a | Per user | Yes | Yes | n/a | Yes | — | **Reused as-is.** Pack queries are expressed as a filter on it, so authorization stays on one path. |
| **First-run wizard** | server `Tesserafin.Api/Controllers/StartupController.cs` (`Configuration`, `RemoteAccess`, `User`, `Complete`); web `src/apps/wizard/` (`start`, `user`, `library`, `remote`, `settings`, `finish`) in `tesserafin-web` | n/a | n/a | n/a | n/a | n/a | n/a | Web owns the steps; the server owns only the four calls above | — | **Boundary recorded.** Seeding is a web step over a server API; it needs no new startup endpoint. |
| **Navigation, Home and Library composition** | `tesserafin-web` `src/themes/platform/resolvePresentation.ts`, `src/apps/modern/features/library/`, `…/features/details/` | n/a | n/a | Per user | Yes | Follows the query | n/a | Bound by RFC-0007 capabilities | — | **Consumer.** Presentation only; owns no membership truth. |
| **OpenAPI / SDK boundary** | committed contract `openapi/openapi.json`, generated by `ci/openapi-generate.sh`, verified by `OpenApiContractTests.CommittedContract_MatchesRunningServer`; `docs/openapi-contract.md`; `sdk-provenance.yml` | n/a | n/a | n/a | n/a | n/a | n/a | Yes — breaking changes gated by oasdiff in CI | — | **Constraint.** Any new endpoint is additive and must land in the committed contract in the same change. |
| **Backup / restore** | `Tesserafin.Server.Implementations/FullSystemBackup/BackupService.cs:336` — reflects over the public `DbSet` properties of `TesserafinDbContext` | n/a | n/a | n/a | n/a | n/a | n/a | Yes | — | **Reused as-is.** A new `DbSet` is included automatically; M1 owes a test proving it, not new backup code. |

### 2.2 Ruling

No inherited concept is adopted as the storage of a content pack.

Two of them come close and are still rejected for a reason that is not
convenience:

* `BoxSet` has the right *cardinality* and the wrong *ontology*. It is an item.
  It has a path, metadata, artwork, people and a deletion path that runs through
  item deletion. Making a content pack a `BoxSet` would make "delete this
  organisational lens" indistinguishable from "delete this thing in my library",
  and would silently change what a collection means for every existing user.
* `ItemValue` has the right *shape* — a real many-to-many map — and the wrong
  *ownership*. Tags and genres are assertions about a work, sourced from
  metadata providers and refreshed on scan. A content pack is an assertion by
  the household about its own arrangement. A metadata refresh must never be able
  to add or remove a user's pack membership.

`ContentPack` is therefore a new first-class core concept, with new tables.

---

## 3. Normative contract

### 3.1 Identity and ownership

* A content pack has a **stable opaque identifier**. Clients treat it as opaque
  and never parse it.
* It has a user-facing **name**, an optional **description**, and an **ordering
  position** within the server's list of packs.
* Initial ownership scope is **server / household-wide**. There is one set of
  content packs per server.
* **Renaming never changes identity.** A rename is a metadata update on an
  unchanged identifier; every existing membership, link and client bookmark
  survives it.
* Per-user visibility and per-user packs are a plausible later feature. They are
  **not** in the first storage model, and the first storage model must not make
  them impossible: no assumption may be baked in that the pack table is
  global-only. Concretely — the first migration does **not** add an owner
  column, and no query is written in a way that would have to be rewritten if
  one were added later.
* Deleting a content pack deletes **membership links only**. It never deletes,
  moves, renames or rewrites media, media files, metadata, artwork, collections
  or libraries.

### 3.2 Membership

* One media item may belong to **zero, one or many** content packs.
* One content pack may contain **mixed media families** — a Sport pack may hold
  a recorded match, a documentary series and a photo album at once.
* Membership is a **relation**, not a file operation. Adding or removing
  membership performs no filesystem work of any kind.
* Deleting an item removes its membership rows safely and leaves no dangling
  reference. No pack becomes invalid because an item vanished; it simply gets
  smaller.
* Deleting a pack is a **transactional** link deletion. It requires an explicit
  confirmation that states what is and is not being deleted. Where the existing
  recovery model can restore it — the full-system backup covers the pack tables
  the same way it covers every other `DbSet` — deletion is recoverable through
  that path. There is no separate pack-level undo in M1.
* Membership **survives backup and restore** and every supported server
  migration.

### 3.3 Relationship to existing concepts

These are five different things and are never aliases of each other:

| Concept | What it decides |
| --- | --- |
| **Library / storage root** | Discovery, scanning and filesystem access. Where files are. |
| **Collection** | A curated grouping or relationship *between works* — a saga, a trilogy, a linked set. |
| **Content pack** | A high-level organisational and navigation lens over items, chosen by the household. |
| **Theme** | Presentation only. How any of the above looks. |
| **Plugin / provider** | May *suggest* classification. Never owns core membership truth. |

An item may simultaneously exist in one or more physical libraries, in
collections, under genres and tags, and in multiple content packs. Adding it to
a pack changes none of the others.

### 3.4 Security and permissions

* **A pack never grants access to an item.** Membership is not a capability.
* **Item authorization is evaluated before pack query results are returned.**
  Pack queries are expressed through the existing item-query path
  (`InternalItemsQuery`, `BaseItem.cs:1593`) so that there is exactly one
  authorization implementation, not a second one that can drift.
* **Counts, artwork and empty-state metadata must not leak inaccessible items.**
  A pack's item count, as shown to a user, is the count *that user* may see. A
  pack's representative artwork is drawn only from items that user may see. A
  pack whose entire content is inaccessible to a user renders as empty or is
  omitted — it never renders as "12 items" with no visible items.
* **Administrative pack management requires an explicit permission**, following
  the `EnableCollectionManagement` precedent: a new `PermissionKind` member and
  a matching policy registered in
  `Tesserafin.Server/Extensions/ApiServiceCollectionExtensions.cs`. Create,
  rename, reorder, delete and membership edits are all behind it.
* **Ordinary users see only packs and items allowed by existing
  authorization.** Read access to the pack list requires no new permission
  beyond being an authenticated user.
* **A provider or plugin cannot bypass any of the above.** A suggestion is an
  input to a decision, never a write that skips the permission check.
* **Local-only operation requires no Tesserafin account.** Content packs are
  entirely server-local. Nothing in this contract contacts an external service.

### 3.5 Classification provenance

The model must be able to record *why* an item is in a pack. The full
provenance vocabulary is:

| Value | Meaning |
| --- | --- |
| `Manual` | A person put it there. |
| `SystemSeed` | Created by first-run seeding or a built-in default. |
| `Rule` | A deterministic, inspectable rule matched. |
| `ProviderSuggestion` | A metadata provider proposed it. |
| `PluginSuggestion` | A plugin proposed it. |

**M1 implements only `Manual` and `SystemSeed`.** The stored representation must
be able to carry the other three later without a breaking migration — which
means provenance is a stored, extensible enumeration from the first migration,
not a boolean and not a column added later.

Explicitly out of scope for this vertical: AI classification, confidence
scoring, and any provider or plugin integration.

**A suggestion never silently overrides explicit manual membership.** Once a
membership row is `Manual`, no automated source may remove it or rewrite its
provenance. This rule is stated now because it constrains the storage model
even though nothing produces suggestions yet.

### 3.6 Information architecture

One model must serve both browsing preferences:

* **By media family** — the familiar shape: Movies, Shows, Music, Photos.
* **By content pack** — the household's own categories, mixing media types where
  that is what the household means.

This is **one product with two navigation preferences**, not two library
systems. Nothing about the second mode duplicates storage, duplicates scanning,
or requires the user to maintain the same information twice.

The preference may be offered as a step in the first-run wizard. It **must
remain changeable afterwards** through ordinary settings, with no re-scan, no
data migration and no loss of packs either way.

### 3.7 Initial pack seeding

The contract *suggests* seeds. It does not define a closed set, and no code may
treat this list as an enumeration:

Movies and series · Music · Photos and home video · Sport · Concerts · Theatre
and performances · Podcasts · Audiobooks · Anime

A user may choose none, choose some, rename any of them, add custom packs, and
change the selection later. Choosing none is a fully supported outcome and must
not degrade the product.

**English is the only required product language for the initial
implementation.** Seed names ship in English. This vertical does not design a
language-plugin system and does not block on translation infrastructure.

### 3.8 Cross-client boundary

The **server** owns: identity · membership · authorization · ordering ·
provenance · query semantics · migration.

**Web and future native clients** own: native presentation · navigation
placement · editing interactions · platform accessibility.

The API describes content packs in product terms only. It does not encode
React, Compose, SwiftUI, theme names, RFC-0007 capability names, layout hints,
card aspects or any other client or presentation detail. If a future client
needs a different arrangement of the same packs, that is a client change, not an
API change.

---

## 4. Minimum M1 surface

This section specifies the M1 boundary. It does **not** fix endpoint paths or
database column types; those are decided in M1 against the repository's own
conventions, and any endpoint added there must land in `openapi/openapi.json` in
the same change (`docs/openapi-contract.md`).

### 4.1 Operations

| # | Operation | Authorization | Idempotency | Validation | Concurrency | Empty / missing | Error shape | Provenance effect | OpenAPI/SDK |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | List accessible content packs | Authenticated user | Safe | — | Read-consistent snapshot | Empty list, `200` — never `404` | — | none | Additive |
| 2 | Get one content pack | Authenticated user | Safe | Well-formed id | Read-consistent snapshot | `404` if absent or wholly inaccessible | Standard problem response | none | Additive |
| 3 | Create | Pack-management permission | Not idempotent | Non-empty name, length bound, name unique among packs | Unique constraint decides the race; loser gets a conflict | — | `400` invalid, `409` duplicate name | Records the creating provenance | Additive |
| 4 | Rename / update metadata | Pack-management permission | Idempotent — same body, same result | As create | Last-write-wins on metadata; identity never changes | `404` if absent | `400`, `404`, `409` | none | Additive |
| 5 | Reorder | Pack-management permission | Idempotent | Positions form a valid ordering | Whole reorder is one transaction | `404` if any id absent | `400`, `404` | none | Additive |
| 6 | Delete (never deletes media) | Pack-management permission | Idempotent — deleting an absent pack is not an error worth surfacing twice | Confirmation is a client concern; the API is explicit about scope | One transaction: pack row + its membership rows | `404` if absent | `404` | Membership rows go with the pack | Additive |
| 7 | Add item membership | Pack-management permission **and** the caller may see the item | **Idempotent** — adding twice is one membership | Pack exists; item exists | Unique `(pack, item)` constraint absorbs the race | `404` for unknown pack or item | `400`, `403`, `404` | Sets provenance | Additive |
| 8 | Remove item membership | Pack-management permission | **Idempotent** — removing an absent membership succeeds | Pack exists | Single delete | `404` for unknown pack | `404` | Row removed | Additive |
| 9 | Query authorized items in a pack | Authenticated user | Safe | Standard paging/sort validation | Read-consistent snapshot | Empty page, `200` | — | none | Additive |
| 10 | Query packs containing an item *(optional in M1)* | Authenticated user; item must be visible | Safe | — | Read-consistent snapshot | Empty list, `200` | — | none | Additive |

Cross-cutting rules for all ten:

* Operation 9 is implemented as a filter on the existing item query. It does not
  introduce a second authorization path.
* Operations 1, 2, 9 and 10 return only what the caller may see. Counts and
  artwork derived for 1 and 2 obey §3.4.
* Errors use the API's existing problem-response convention. No new error
  envelope is invented.
* Every operation is additive to the OpenAPI contract. None changes an existing
  schema, so the oasdiff gate stays green.

### 4.2 Persistence

Two new entities, both reached through `TesserafinDbContext` as public `DbSet`
properties so that `BackupService` picks them up by reflection
(`BackupService.cs:336`):

* **Pack** — opaque identifier (primary key), name, optional description,
  ordering position. Unique constraint on the normalised name. No owner column
  in M1 (§3.1).
* **Membership** — pack reference, item reference, provenance, creation
  instant. Composite uniqueness on `(pack, item)` so operation 7 is idempotent
  at the storage layer rather than by read-then-write.

Behaviour:

* Deleting a pack cascades to its membership rows and to nothing else.
* Deleting a `BaseItem` removes its membership rows. No membership row may
  outlive its item.
* Neither entity is reachable from `BaseItemEntity` in a way that would make a
  pack look like an item to any existing query.
* Create, delete and reorder each run in one transaction.

### 4.3 Migration

* One EF Core migration under
  `src/Tesserafin.Database/Tesserafin.Database.Providers.Sqlite/Migrations/`,
  following the existing timestamped naming, with its `.Designer.cs` and an
  updated `TesserafinDbModelSnapshot.cs`. `EfMigrationTests` must stay green.
* Migrating a server that has no content-pack tables creates them **empty**. No
  existing library, collection, tag, genre, view or item is read, rewritten or
  transformed. A server that upgrades and never opens the feature is
  byte-for-byte unaffected in every other table.
* **Downgrade:** the version epoch policy (`docs/versioning-policy.md`) governs
  what is a supported downgrade. Within that policy, an older server that lacks
  these tables simply does not see them; the tables are inert and no other
  subsystem reads them. M1 does not promise an automated down-migration beyond
  what EF Core's generated `Down` provides.
* **Backup/restore:** covered by the existing reflective enumeration. M1's
  obligation is a test proving a pack and its memberships survive a
  backup/restore round trip — not new backup code.

---

## 4.4 Decisions settled in M1

§4 left four questions to be answered against the repository's own conventions.
M1 answered them as follows. These are now part of the contract.

### 4.4.1 Browsing preference — per user, server-side

§3.6 requires the media-family-first vs content-pack-first preference to remain
changeable after onboarding, and §3.8 puts every product-level decision on the
server. The preference is therefore:

* **per user**, not household-global — one person switching does not move anyone
  else's navigation;
* **server-side**, never in browser storage, so Web, Android, Android TV, iOS and
  TV clients all observe the same choice;
* carried by the existing cross-client **user configuration**
  (`UserConfiguration.ContentPackBrowsingPreference`, persisted on the `Users`
  row, read and written through `GET /Users/Me` and
  `POST /Users/{userId}/Configuration`). It is deliberately **not** a
  `DisplayPreferences` record: those are scoped to one client and one display,
  which is the opposite of what a cross-client product preference needs.
* **not** a column on either content pack table, and **not** a global server
  setting.

Accepted values are `MediaFamilyFirst` and `ContentPackFirst`. An absent or
legacy value resolves to `MediaFamilyFirst`, so every existing user keeps exactly
the navigation they have today. M1 stores and exposes the field and changes no
navigation; M3 consumes it.

### 4.4.2 Pack origin is stored, and is a different vocabulary from membership provenance

Operation 3 "records the creating provenance". Nothing in the repository lets
that be derived after the fact — the pack row is the only durable record of how a
pack came to exist — so it is stored, as `ContentPack.Origin`.

It is a **separate enumeration** from membership provenance, not a reuse of it:

* `ContentPackMembershipProvenance` answers *why is this item in this pack*, and
  must be able to carry `Rule`, `ProviderSuggestion` and `PluginSuggestion` later
  (§3.5).
* `ContentPackOrigin` answers *who created this pack*, and only `Manual` or
  `SystemSeed` can ever be true of it. A provider cannot create a pack.

Sharing one enumeration would make `ProviderSuggestion` a legal pack origin,
which §3.4 forbids.

### 4.4.3 A pack the caller cannot see at all is omitted, then absent

§3.4 says such a pack "renders as empty or is omitted". M1 chooses **omitted**,
consistently across the read operations:

| Situation | Operation 1 (list) | Operation 2 (get one) | Operation 9 (items in pack) |
| --- | --- | --- | --- |
| Pack has no memberships at all | listed, count `0` | `200`, count `0` | `200`, empty page |
| Pack has members, none visible to the caller | omitted | `404` | `200`, empty page |
| Pack does not exist | absent | `404` | `404` |

An actually empty pack therefore stays distinguishable from a nonexistent one for
whoever is managing packs, while a pack whose whole content is inaccessible is
indistinguishable from one that does not exist. Operation 9 answering `200` with
an empty page for a pack that exists is the behaviour §4.1 already specifies
("Empty page, `200`"); it reveals only that the id names a pack, which the caller
had to hold already, and never that inaccessible content is filed there.

Counts and representative artwork in operations 1 and 2 are computed by the
ordinary item query for the calling user — one bounded query per pack, returning
the visible total and a single visible representative together. The raw
membership count is never returned to a client.

### 4.4.4 Operation 10 is implemented

`GET /Items/{itemId}/ContentPacks` ships in M1. The caller must be able to see the
item; an unknown item and an invisible one answer identically, so the response
never says which of the two happened.

---

## 5. Sequencing

| Milestone | Issue | Scope |
| --- | --- | --- |
| **M1** | [`tesserafin#216`](https://github.com/tesserafin/tesserafin/issues/216) | Core model, persistence, migration and API. No Web UI. |
| **M2** | [`tesserafin-web#138`](https://github.com/tesserafin/tesserafin-web/issues/138) | Content-pack management and mosaic browsing. Depends on M1. |
| **M3** | [`tesserafin-web#139`](https://github.com/tesserafin/tesserafin-web/issues/139) | First-run seeding and the browsing-preference choice. Depends on M1 and M2. |

M3 lands in `tesserafin-web` because the wizard steps live there
(`src/apps/wizard/`); the server's `StartupController` exposes only
configuration, remote access, first user and completion, and needs no new
startup endpoint for seeding.

---

## 6. Non-goals

Not in this vertical, and not to be started opportunistically inside it:

* AI or machine-learning classification, confidence scoring, automatic tagging.
* Metadata-provider or plugin integration for pack suggestions — the provenance
  values exist; nothing produces them.
* Per-user or per-profile content packs.
* Rule-driven or smart packs.
* Marketplace, sharing, import/export or remix of packs.
* Native clients. Android / Android TV remains the first native vertical and
  follows *after* this semantic contract and the client contract, not beside it.
* Any change to what a collection, a library, a tag or a genre means.
* Translation or language-plugin infrastructure.
* Any mandatory Tesserafin account.
