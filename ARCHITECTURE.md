# Tesserafin Architecture

This document describes how the Tesserafin **server** is put together: the media
pipeline (scan → metadata → transcode → stream), the module boundaries between
projects, and where the fork deliberately diverges from upstream Jellyfin. It is
written for a new contributor who wants a map before diving into the source.

For the *product* rationale behind these choices (the "why"), see
[`docs/major-rewrite-plan-v13.md`](docs/major-rewrite-plan-v13.md). This file is
the "what and where".

> **Naming note.** The codebase was renamed twice: `Jellyfin.*`/`MediaBrowser.*`/`Emby.*`
> → `Reefin.*` → **`Tesserafin.*`**. Namespaces, assemblies and env vars are now
> all `Tesserafin*`/`TESSERAFIN_*`. A few historical residues are intentional and
> **not** branding bugs: the older code name "Reefin" still appears throughout
> `docs/`, the analyzer diagnostic id is `JF0001`, and `jellyfin-ffmpeg` /
> `jellyfin-web` fallback names survive where they name real upstream artefacts.

---

## 1. What Tesserafin is

Tesserafin is a self-hosted media system — you run the server on hardware you
control, it indexes your library, fetches metadata, and transcodes on demand to
stream to your own devices. It is a fork of
[Jellyfin](https://github.com/jellyfin/jellyfin) (itself descended from Emby
3.5.2), licensed GPL-2.0-or-later.

Tesserafin is an **independent** project. It does **not** claim product or
protocol compatibility with Jellyfin, is **not** compatible with Jellyfin clients
or plugins, and is neither endorsed by nor affiliated with the Jellyfin project.
See [`NOTICE`](NOTICE) for the full fork attribution. This non-compatibility is a
deliberate design freedom, not an accident — it is what lets the internals below
be rewritten without preserving upstream wire formats.

The whole product today is **server + bundled browser client** ([Tesserafin
Web](https://github.com/tesserafin/tesserafin-web), a separate repo,
served on the same origin/port). Native mobile and TV clients are roadmap items.

### Tech stack at a glance

| | |
|---|---|
| Language / runtime | C# on **.NET 10** (`global.json` pins SDK `10.0.0`) |
| Web framework | ASP.NET Core (Kestrel), MVC controllers |
| Persistence | **EF Core**, multi-provider (SQLite today; PostgreSQL is a future provider) |
| Media | `ffmpeg` (the `jellyfin-ffmpeg` build), SkiaSharp for images |
| Shape | **Modular monolith** — one process, many bounded projects. No microservices. |
| Version epoch | `1.0.0` (`SharedVersion.cs`); server and web share the number |

---

## 2. Design principles

1. **Modular monolith, not microservices.** Everything runs in one process, but
   the code is split into many projects with enforced dependency direction.
   Boundaries are compiler-checked (see §9), not merely conventional.
2. **Decisions before I/O.** The playback subsystem is built around a pure,
   side-effect-free *decision domain* that never touches ffmpeg, DLNA, or the
   database. Adapters live at the edges. This is the single biggest structural
   change from upstream (see §5).
3. **Break the god-objects.** Upstream's two largest classes — `LibraryManager`
   and `BaseItem` — are being decomposed into narrow, cycle-free service
   interfaces resolved by DI, rather than static/global access.
4. **Rewrite behind gates.** New subsystems ship behind shadow comparison and
   canary flags, running in parallel with the legacy path before it is retired,
   instead of a hard cutover.
5. **Explain, don't just decide.** Playback decisions and hardware selection
   carry a *causal reason trace*, so "why did this transcode?" is answerable.

---

## 3. Module map

Projects fall into two roots. **`/` (repo root)** holds the larger,
partly-inherited core projects; **`src/`** holds the newer, rewritten leaf
libraries. Every project has a matching `*.Tests` project in `tests/`.

### Root projects — inherited core (progressively rewritten)

| Project | Responsibility |
|---|---|
| `Tesserafin.Server` | Host bootstrap / entry point (`Program.cs`), Kestrel + DI composition, static web-client serving. |
| `Tesserafin.Server.Core` | The application host base (`ApplicationHost`), `LibraryManager`, file resolvers, scheduled tasks, plugin manager, session manager. |
| `Tesserafin.Server.Implementations` | Persistence-facing repositories/services over the item store (`BaseItemRepository`, `ItemPersistenceService`, people/chapters/streams). |
| `Tesserafin.Controller` | Core **contracts**: `BaseItem` entity hierarchy, `ILibraryManager`, provider interfaces, `EncodingHelper`, streaming/`StreamState`, hardware-selection types. |
| `Tesserafin.Model` | DTOs and enums shared across layers. |
| `Tesserafin.Api` | ASP.NET Core controllers, auth handlers/policies, middleware, OpenAPI. |
| `Tesserafin.Providers` | Metadata/image **orchestration** (`ProviderManager`, `MetadataService`) + media-info probing. |
| `Tesserafin.LocalMetadata` / `Tesserafin.XbmcMetadata` | Local metadata: BoxSet/Playlist XML, and Kodi/XBMC `.nfo` read/write. |
| `Tesserafin.Naming` | Pure filename/path parser (episode/season/movie/stack detection). No DB. |
| `Tesserafin.MediaEncoding` | The transcoding engine: `TranscodeManager`, `MediaEncoder`, ffmpeg process control. |
| `Tesserafin.Data` | Shared POCO entities, enums, interfaces, query objects and event args. No EF Core dependency. |
| `Tesserafin.Common` / `Tesserafin.Photos` | Small shared utilities; photo-library support. |

### `src/` projects — rewritten libraries

| Project | Responsibility |
|---|---|
| `Tesserafin.Playback.Decision` | Pure decision **domain** (immutable model, no I/O). |
| `Tesserafin.Playback.Engine` | The decision **engine** (`IPlaybackEngine.Decide`). |
| `Tesserafin.Playback.Execution` | Projects a decision into a frozen **execution plan**. |
| `Tesserafin.Playback.Dlna` | Legacy adapters bridging DLNA/`StreamInfo` in and out of the domain. |
| `Tesserafin.Playback.Shadow` | Comparison planner, metrics and diagnostic retention; implements the encoding observation port. |
| `Tesserafin.Playback.Contract.Scan` / `.Contract.Diagnostics` | Structurally-closed contract-drift observability. |
| `Tesserafin.Database` (+ `.Implementations`, `.Providers.Sqlite`) | **EF Core** data layer: `TesserafinDbContext` and pluggable DB providers. |
| `Tesserafin.MediaEncoding.Hls` / `.Keyframes` | HLS playlist generation and keyframe extraction. |
| `Tesserafin.Drawing` / `.Drawing.Skia` | Image processing orchestration and the SkiaSharp encoder. |
| `Tesserafin.Networking` / `Tesserafin.Extensions` / `Tesserafin.LiveTv` | Networking, shared extensions, Live TV. |
| `Tesserafin.CodeAnalysis` | Custom Roslyn analyzer(s) enforcing repo rules (§9). |

---

## 4. The media pipeline, end to end

```
   ┌───────────┐   ┌────────────┐   ┌───────────────┐   ┌────────────┐
   │  1. SCAN  │──▶│ 2. METADATA│──▶│ (persist to   │   │  playback  │
   │  library  │   │ + probe    │   │  EF Core DB)  │   │  request   │
   └───────────┘   └────────────┘   └───────┬───────┘   └─────┬──────┘
                                            │                 │
                                            ▼                 ▼
                        ┌───────────────────────────────────────────┐
                        │        3. PLAYBACK DECISION (v2)           │
                        │   Engine: DirectPlay / Remux / Transcode   │
                        │        (pure domain, + reason trace)       │
                        └───────────────────┬───────────────────────┘
                                            ▼
                        ┌───────────────────────────────────────────┐
                        │   4. EXECUTION PLAN  →  StreamInfo bridge  │
                        └───────────────────┬───────────────────────┘
                                            ▼
                        ┌───────────────────────────────────────────┐
                        │  5. TRANSCODE (ffmpeg + hw accel)          │
                        │  6. STREAM   (HLS segments / progressive)  │
                        └───────────────────────────────────────────┘
```

### Stage 1 — Library scan

- The public contract is `ILibraryManager` (`Tesserafin.Controller/Library/`),
  implemented by `LibraryManager` (`Tesserafin.Server.Core/Library/LibraryManager.cs`).
- A scan is queued as a scheduled task: `QueueLibraryScan()` / `ValidateMediaLibrary()`
  enqueue `RefreshMediaLibraryTask` (`Tesserafin.Server.Core/ScheduledTasks/Tasks/`).
  The work walks the tree — `ValidateTopLibraryFolders` then
  `RootFolder.ValidateChildren(...)` recursively over `Folder`/`AggregateFolder`,
  guarded by `ILibraryMonitor` (filesystem watching) and an `IsScanRunning` gate.
- **Resolution.** `LibraryManager.ResolvePath(s)` dispatches to ordered
  `IItemResolver` implementations (`MovieResolver`, `EpisodeResolver`,
  `SeriesResolver`, `AudioResolver`, `PhotoResolver`, …) that turn paths into the
  `BaseItem` hierarchy (`Tesserafin.Controller.Entities`). `IResolverIgnoreRule`s
  skip junk.
- **Naming.** Filename/path interpretation is isolated in `Tesserafin.Naming` (a
  pure parser: `VideoResolver`, `StackResolver`, `EpisodePathParser`,
  `CleanDateTimeParser`, …), wrapped server-side by `IItemNamingService`.
- Post-scan aggregation runs via `ILibraryPostScanTask` validators
  (`GenresPostScanTask`, `StudiosPostScanTask`, …).

### Stage 2 — Metadata & media probing

- Contracts live in `Tesserafin.Controller.Providers`: `ILocalMetadataProvider`,
  `IRemoteMetadataProvider`, `ILocalImageProvider`/`IRemoteImageProvider`,
  `IMetadataService`, `IMetadataSaver`.
- The orchestrator is `IProviderManager` / `ProviderManager`
  (`Tesserafin.Providers/Manager/`). It registers providers via `AddParts`,
  orders them **local-then-remote**, and drives `RefreshSingleItem`/`RefreshFullItem`.
  Refreshes are queued (`QueueRefresh` + a background processing loop keyed by
  `RefreshPriority`). `MetadataService` is the per-item-type base.
- Local providers: `Tesserafin.XbmcMetadata` (Kodi `.nfo` parse/save) and
  `Tesserafin.LocalMetadata` (BoxSet/Playlist XML). Media-derived facts come from
  `Tesserafin.Providers/MediaInfo/` (`FFProbeVideoInfo`, `ProbeProvider`,
  embedded-image extraction).

### Persistence

The data layer is **EF Core**, and it is intentionally split:

- `src/Tesserafin.Database` holds `TesserafinDbContext` (with `DbSet`s for
  `BaseItemEntity`, `People`, `Chapter`, `ItemValue`, `MediaStreamInfo`, …) and a
  **provider abstraction** — `Tesserafin.Database.Providers.Sqlite` implements
  SQLite today; the multi-provider design keys off
  `TesserafinDatabaseProviderKeyAttribute` so PostgreSQL can be added later.
- Repositories that use it live in `Tesserafin.Server.Implementations/Item/`
  (`BaseItemRepository`, `ItemPersistenceService`, `PeopleRepository`,
  `ChapterRepository`, `MediaStreamRepository`), all resolving an
  `IDbContextFactory<TesserafinDbContext>`.
- `Tesserafin.Data` (root) is **not** the EF layer — it holds query objects and
  DTOs, plus the shared POCOs under `Entities/`, their enums and interfaces. It references no
  database implementation or EF Core package. `Tesserafin.Model` consumes these types through
  `Data`; `Tesserafin.Database.Implementations` also consumes `Data` and owns all EF configuration,
  `TesserafinDbContext` and persistence operations. The dependency points from persistence to
  shared values, never the reverse.
- `IItemQueryHelpers`, whose signatures take `TesserafinDbContext`, lives with its implementor in
  `Tesserafin.Server.Implementations/Item/`, rather than leaking the context through Controller's
  service contracts. `Data`, `Model` and `Controller` are checked for direct and transitive EF
  package references by `ConsolidationRatchetTests`; Model's allowance to use Data is conditional
  on Data remaining EF-free. Shared-type assembly ownership is checked in `SharedDataBoundaryTests`.
- The namespace/assembly move is a breaking source change. It introduces no compatibility
  facade or duplicate types. The current SQLite model snapshot names the new POCO namespaces;
  historical migration snapshots and their schema operations retain their original identity.
- This pre-launch programme permits source/binary breaks (owner ruling in `AGENTS.md`). CI still
  runs ApiCompat and retains every finding, with `ABI_ALLOW_BREAKING=1` explicitly accepting
  classified changes instead of requiring compatibility facades. Missing assemblies, invalid
  manifests, tool failures and zero comparisons remain failures. The report distinguishes an
  accepted break from a compatible result; the strict standalone script remains the default.

### Stages 3–4 — Playback decision & execution

This is the headline rewrite and has its own section below (§5).

### Stage 5 — Transcode

- `ITranscodeManager` / `TranscodeManager` (`Tesserafin.MediaEncoding/Transcoding/`,
  a DI singleton) owns the live-job registry and per-output locks.
- `StartFfMpeg(StreamState, outputPath, args, …)` builds an ffmpeg command line
  via `EncodingHelper` (`Tesserafin.Controller/MediaEncoding/`), spawns a
  `Process` (`MediaEncoder.EncoderPath`), streams stderr to a per-job log, and
  registers a `TranscodingJob` (holds the process, `PlaySessionId`, output path,
  job type). `KillTranscodingJob(s)` tears jobs down and optionally deletes temp
  output; idle jobs are reaped off playback-progress pings.
- Probe/validation uses a separate, safe process path
  (`IFfmpegProcessRunner`/`FfmpegProcessRunner`) that drains stdout+stderr
  concurrently and kills on timeout.

**Hardware acceleration** was rewritten into a decision-first design
(`docs/hwa-refactor-plan.md`), with the types in `Tesserafin.Controller/MediaEncoding/`:

- `HardwareBackendCatalog` lists candidates in priority order (NVENC, QSV, AMF,
  VAAPI, VideoToolbox, RKMPP, V4L2M2M).
- At startup `HardwareSelectionPlanner.Decide(...)` picks a backend **once**,
  returning a `HardwareSelectionDecision` (chosen backend + reason + per-candidate
  failure categories). A backend is only selected after `HardwareBackendProbe`
  runs a **real trial encode** — the invariant is "never selected-and-broken".
- Capabilities are a typed `HardwareCapabilitySnapshot` instead of upstream's
  loose global lists, and the chosen backend is persisted to config.

### Stage 6 — Stream delivery

- **Keyframes** are extracted by `src/Tesserafin.MediaEncoding.Keyframes`
  (ffprobe / Matroska EBML parsers → `KeyframeData`); `src/Tesserafin.MediaEncoding.Hls`
  wraps them behind a cache and generates VOD playlists
  (`IDynamicHlsPlaylistGenerator`).
- **Serving** is in `Tesserafin.Api`: `DynamicHlsController` exposes
  `Videos|Audio/{itemId}/…/master.m3u8`, `main.m3u8`, `live.m3u8` and the
  `.../hls1/{playlistId}/{segmentId}.{container}` segments — it calls
  `TranscodeManager.StartFfMpeg` and waits for segment files. Progressive/direct
  playback goes through `VideosController` / `AudioController` /
  `UniversalAudioController` via `StreamingHelpers.GetStreamingState`.

### Image pipeline (parallel branch)

Independent of the ffmpeg video path: `IImageProcessor` (`src/Tesserafin.Drawing`)
orchestrates resize/format decisions and delegates to an `IImageEncoder` —
`SkiaEncoder` (`src/Tesserafin.Drawing.Skia`, SkiaSharp) when available, else
`NullImageEncoder`. It also builds collages, splash screens and
watched-indicators for posters/thumbnails.

---

## 5. Playback v2 — the core divergence

Upstream Jellyfin decides Direct Play vs Direct Stream vs Transcode inside a
monolithic `StreamBuilder`, tightly coupled to DLNA types (`DeviceProfile`,
`MediaOptions`, `StreamInfo`) and a `[Flags] TranscodeReason` bitfield.

Tesserafin splits that into a layered, DLNA-free pipeline of small projects
(design in `docs/pr91-rfc-playback-decision-v2.md`,
`docs/design-playback-v2-lifecycle.md`):

```
 DLNA / legacy request                 legacy StreamInfo / ffmpeg
        │  (adapter in)                        ▲  (adapter out)
        ▼                                       │
 ┌─────────────┐   ┌────────────┐   ┌──────────────────┐
 │  Decision   │──▶│   Engine   │──▶│    Execution     │
 │  (domain)   │   │  .Decide() │   │  (plan builder)  │
 └─────────────┘   └────────────┘   └──────────────────┘
        ▲                                       │
        └──────────  Shadow observes  ──────────┘
```

- **`Tesserafin.Playback.Decision`** — the pure domain. Immutable records and
  vocabulary with **zero** DLNA/IO/DB dependencies: `PlaybackDecision` (the
  output), `PlaybackMethod` (`DirectPlay` / `Remux` / `Transcode` — Jellyfin's
  "DirectStream" maps to `Remux`), `PlaybackRequestContext`, `ClientCapabilities`
  (separating decoder support from an *ordered* list of output profiles),
  `MediaSourceSnapshot`, `OutputSpec`, `SelectedStreams`, and a causal
  `ReasonNode`/`ReasonCode` trace. `PlaybackDecision` can only be built through
  validating factories (`DirectPlay`/`Remux`/`Transcode`/`NotViable`), so illegal
  states (e.g. a DirectPlay with transforms) are unrepresentable.
- **`Tesserafin.Playback.Engine`** — `IPlaybackEngine.Decide(context,
  capabilities, sources, constraints) → PlaybackDecision`. This is where source
  selection, stream/codec/level/bit-depth checks, bitrate/resolution caps, HDR
  tonemapping and subtitle handling happen, emitting the reason tree.
- **`Tesserafin.Playback.Execution`** — `PlaybackExecutionPlanBuilder.TryBuild`
  projects a *viable* decision into a frozen `PlaybackExecutionPlan`. It **never
  re-decides**: it copies fields verbatim and **refuses** (`NotViable`,
  `NoStreamsSelected`, `MissingOutputContainer`) rather than guess. Request-scoped
  facts the engine never sees (`PlaySessionId`, `DeviceId`, start position) ride
  in a separate `PlaybackExecutionContext`.
- **`Tesserafin.Playback.Dlna`** — the edge adapters. `ReverseDlnaAdapter` maps a
  legacy request *into* the domain; `PlaybackExecutionPlanAdapter` fills a legacy
  `StreamInfo` *out of* an execution plan so the existing HLS/ffmpeg machinery
  runs unchanged. **This out-adapter is an explicitly temporary bridge** (PR114a)
  until a native plan-consuming execution path lands.
- **`Tesserafin.Playback.Shadow`** — a comparison harness. It runs the v2 engine
  in parallel with the legacy planner (legacy stays authoritative), reduces both
  sides to a comparable `DecisionVector`, and classifies differences
  (`Equivalent`, `ExpectedImprovement`, `KnownV2Limitation`, `PotentialRegression`,
  `Unexplained`). It is **off by default**, log-only, sampled and time-budgeted —
  its job is to route divergences to a human, not to certify correctness.

**Comparison boundary.** `MediaEncoding` has no reference to `Playback.Shadow`. Its session
manager depends on `IPlaybackSessionDiagnostics` and an opaque planning capture, which can
attach observations to a successful session or discard them on failure. The Shadow assembly
references MediaEncoding and owns the decorator, records and stores. Core injects the same store
through the execution port and the admin diagnostics interface. `IV2PlanStore` remains the separate
execution authority. `ConsolidationRatchetTests` forbids the old edge transitively, with no
remaining forbidden-dependency exceptions. See `docs/playback-path-inventory.md` §10.

**How it reaches ffmpeg.** Execution does not call `MediaEncoding` directly. A
plan is retained per session and read back by `PlaybackExecutionPlanResolver`
(`Tesserafin.MediaEncoding/Playback/`); the `PlaybackExecutionPlanAdapter` then
produces the `StreamInfo` that `StreamingHelpers.GetStreamingState` →
`DynamicHlsController`/`VideosController` → `EncodingHelper` →
`TranscodeManager.StartFfMpeg` consume. The v2 path ships behind flags
(`enableV2PlaybackPath`, `CanaryPercentage`) and a shadow kill switch, rolled out
progressively rather than as a hard cutover.

**Network contract.** The DLNA `DeviceProfile` is removed from client requests.
Clients send symmetric `ClientCapabilities` / `PlaybackConstraints` to a stable
DTO surface split across a client controller (`/Playback/Sessions`) and an admin
diagnostics controller (`/System/PlaybackDiagnostics`). Note the bundled web
client still speaks the legacy playback protocol today; migrating it is tracked in
`docs/pr116-client-migration-design.md`.

---

## 6. Server host & API

- **Entry point:** `Tesserafin.Server/Program.cs`. It parses CLI options, sets up
  Serilog and application paths, runs startup DB migrations
  (`TesserafinMigrationService`), and — while the real host builds — serves a
  pre-boot `SetupServer` for the setup UI.
- **Composition:** `CoreAppHost` extends `ApplicationHost`
  (`Tesserafin.Server.Core`) and uses the generic .NET host
  (`Host.CreateDefaultBuilder(...).ConfigureWebHostDefaults(...).UseSerilog()`).
  `ApplicationHost.Init` dispatches to `CoreAppHost.RegisterServices`: the executable
  composes the image encoder, user/device managers, authentication providers and HTTP services,
  then calls the base registration for core services. Plugin registrations follow.
  `Server.Core` has no project/assembly dependency on `Api`; only the executable references both.
  The API owns `WebSockets.WebSocketManager`, `WebSockets.WebSocketConnection`,
  `WebSockets.WebSocketSessionTransport` and `WebSocketListeners.SessionWebSocketListener`. HTTP delivery helpers, the session delivery guard
  and HLS ownership authorizer are also registered by the executable, with their existing scoped
  or singleton lifetimes. `HttpCompositionBoundaryTests` verifies this on a booted host.
- **Shared classification:** `Tesserafin.Data.CollectionTypeMapping` supplies the item kinds for
  library collection types. Both the Items API and collection-folder image selection use it;
  core image generation no longer imports an API extension to classify a library.
- **Plugins** are loaded **in-process** by `PluginManager` from the plugins path;
  some providers (TMDb, ListenBrainz) are compiled-in. (An out-of-process plugin
  SDK v2 is planned but not started.)
- **API:** ASP.NET Core MVC controllers in `Tesserafin.Api`, registered via
  `AddTesserafinApi(...)`. Authentication is a **custom scheme**
  (`CustomAuthenticationHandler`, API-key in the Authorization header), **not**
  JWT, with many named authorization policies (`FirstTimeSetupPolicy`,
  `UserPermissionPolicy`, `AnonymousLanAccessPolicy`, …). The pipeline is
  `UseAuthentication → UseAuthorization → UseIPBasedAccessValidation`.
- **OpenAPI** is generated with Swashbuckle (doc `api-docs`, title "Tesserafin
  API"). The contract is **committed and pinned**: `openapi/openapi.json` plus a
  `openapi/contract.lock.json` fingerprint, regenerated by a single authoritative
  generator (`ci/openapi-generate.sh`), with byte-for-byte drift and
  compatibility (via **oasdiff**) enforced in CI. See `docs/openapi-contract.md`.
- **Web client:** the server serves the bundled Tesserafin Web assets as static
  files at `/web` (same origin/port, `8096`), redirecting `/` → `/web/`. The web
  directory falls back to a `jellyfin-web` folder next to the assembly if unset.

---

## 7. Observability

Three nested correlation scopes make a playback problem traceable end to end
(`docs/observabilite-identifiants-correlation.md`,
`docs/container/A5-observability.md`):

- **`RequestId` / `TraceId`** — server-minted, one per HTTP round-trip. Emitted as
  `X-Request-Id` by `RequestCorrelationMiddleware` (before the exception
  middleware) and exposed via `IRequestCorrelationAccessor`.
- **`PlaybackAttemptId`** — client-minted, spans retries of one attempt
  (diagnostics only).
- **`PlaySessionId` / `PlaybackSessionId`** — the client-minted stream id vs the
  server-minted session resource. Session teardown keys on `PlaybackSessionId`, so
  a stale `DELETE` cannot kill a newer session.

The production surface is deliberately minimal: an anonymous `GET /health`
(with a `SELECT 1` database probe) and structured JSON Serilog logs. Prometheus
metrics are opt-in behind `EnableMetrics`.

---

## 8. Module boundaries & engineering standards

Boundaries are enforced by the build, not left to convention:

- `Directory.Build.props` sets `Nullable=enable`, `TreatWarningsAsErrors=true`,
  and `AllEnabledByDefault` analysis in Debug. `src/Directory.Build.props` layers
  on BannedApiAnalyzers, IDisposableAnalyzers, StyleCop, and Serilog/Multithreading
  analyzers.
- **`BannedSymbols.txt`** forbids error-prone APIs repo-wide: `Task<T>.Result`
  (forces proper async) and `Guid` `==`/`!=`/`Equals(object)` (forces the typed
  `Guid.Equals(Guid)`). The playback `Contract.*` projects carry their own
  `BannedSymbols.txt` to keep DTOs *structurally closed* (no stray
  strings/Guids/`JsonElement` reachable), verified by contract-closure tests.
- **`src/Tesserafin.CodeAnalysis`** is a custom Roslyn analyzer (rule `JF0001`,
  `AsyncDisposalPatternAnalyzer`) referenced as an analyzer into every project in
  Debug — e.g. it flags a synchronous `using` on an async-created
  `IAsyncDisposable`.
- **DI closure.** An active campaign (`docs/pr99…`, `docs/pr111-di-closure-audit.md`)
  breaks constructor cycles — e.g. the `LibraryManager ↔ UserViewManager` cycle,
  once patched with `Lazy<IUserViewManager>`, is now cut by narrow leaf services
  (`IUserViewCatalog`, `IUserViewFactory`, `IItemStore`, …). An architectural test
  locks the closure in place.

The `src/` vs root split is meaningful: **`src/` is where rewritten, cycle-free
leaf libraries live**; the root holds the larger inherited projects still being
decomposed.

---

## 9. Divergences from upstream Jellyfin — summary

For a contributor coming from Jellyfin, the things that are *different*:

1. **Identity & compatibility.** Full `Jellyfin*` → `Tesserafin*` namespace/assembly
   cutover; env vars are `TESSERAFIN_*`. Tesserafin explicitly gives up Jellyfin
   client/plugin/protocol compatibility. Security reports go to Tesserafin, not
   upstream.
2. **Playback rewritten (§5).** Jellyfin's monolithic, DLNA-coupled `StreamBuilder`
   becomes a layered `Decision` / `Engine` / `Execution` / `Shadow` / `Dlna-adapter`
   pipeline. The `[Flags] TranscodeReason` bitfield is replaced by a causal
   `ReasonNode` tree ("*why* this method", not just "which walls were hit").
   `DeviceProfile` is removed from the network contract.
3. **Hardware acceleration rewritten.** A startup `HardwareSelectionPlanner` with a
   priority catalog and **trial-encode probing** replaces upstream's global
   capability lists and inline selection in `EncodingHelper` — fixing the
   unconsumed-stream deadlock and removing global environment-variable mutation.
4. **EF Core, multi-provider.** The hand-written SQLite repository is replaced by
   `TesserafinDbContext` over an EF Core provider abstraction (SQLite now,
   PostgreSQL later).
5. **God-object decomposition.** `LibraryManager` and `BaseItem` are being split
   into narrow DI services (`IItemLookupService`, `IItemQueryScopeService`,
   `IItemStore`, `IUserViewCatalog`, `IItemNamingService`, …) instead of
   static/global access.
6. **Pinned OpenAPI contract** with byte-for-byte + compatibility gates in CI.
7. **Enforced boundaries.** Custom analyzer + banned symbols + DI-closure
   architectural tests, none of which exist upstream.
8. **Rewrite-behind-gates methodology.** Shadow comparison + canary flags, rather
   than hard cutovers.

What is intentionally **kept from upstream**: the modular-monolith shape, C#/.NET,
the `jellyfin-ffmpeg` build, and the broad execution shape of the HLS/progressive
streaming controllers (retained behind the v2 execution adapter for now).

---

## 10. Where to look next

| I want to understand… | Start here |
|---|---|
| A library scan | `Tesserafin.Server.Core/Library/LibraryManager.cs`, `.../ScheduledTasks/Tasks/RefreshMediaLibraryTask.cs` |
| Metadata fetching | `Tesserafin.Providers/Manager/ProviderManager.cs` |
| Why something transcodes | `src/Tesserafin.Playback.Engine`, `src/Tesserafin.Playback.Decision/PlaybackDecision.cs` |
| How a stream is produced | `Tesserafin.MediaEncoding/Transcoding/TranscodeManager.cs`, `Tesserafin.Api/Controllers/DynamicHlsController.cs` |
| Hardware acceleration | `Tesserafin.Controller/MediaEncoding/HardwareSelectionPlanner.cs`, `docs/hwa-refactor-plan.md` |
| Host / DI startup | `Tesserafin.Server/Program.cs`, `Tesserafin.Server.Core/ApplicationHost.cs` |
| The rewrite plan & rationale | `docs/major-rewrite-plan-v13.md` |
| Playback v2 design | `docs/pr91-rfc-playback-decision-v2.md`, `docs/design-playback-v2-lifecycle.md` |
| Content packs (planned core concept) | `docs/content-pack-contract.md` |

> **Status caveat.** Several rewrites described here are in progress: the v2
> playback path runs behind shadow/canary gates and still reaches ffmpeg through a
> temporary `StreamInfo` adapter; the bundled web client still uses the legacy
> playback protocol; PostgreSQL and an out-of-process plugin SDK are planned but
> not yet implemented. Treat `docs/major-rewrite-plan-v13.md` as the live status
> board.
