# Playback path inventory

Status: consolidation programme, step 1 (#129, owner #305). Baseline (§1–§3) measured on server
`master` `ac6570e75f` and Web `main` `e7a33187de`, 2026-11-12; §4, §5 and §6 describe what has been
delivered since. Update this file in the same change that
removes or adds a path; the architecture check `tests/Tesserafin.Server.Tests/Architecture/ConsolidationRatchetTests.cs` (baseline `consolidation-ratchet-baseline.json` beside it) holds the
named exceptions and may only shrink.

## 1. Server chain as it runs today

```
POST /Playback/Sessions                       PlaybackSessionsController.CreatePlaybackSession
  -> PlaybackSessionManager.Create
     -> IPlaybackSessionPlanner = ShadowPlaybackSessionPlanner          (ApplicationHost.cs)
        -> PrepareShadow: Mode Legacy -> no engine run
                          Mode V2/Canary -> map inputs for the v2 engine
        -> PlaybackSessionPlanner.PlanVideo / PlanAudio   ALWAYS RUNS
           -> new StreamBuilder(...).GetOptimalVideoStream   (legacy decision)
        -> CompleteShadow: engine.Decide, publish V2PlanRecord when authoritative,
                           project + compare against legacy (shadow)
     -> PlaybackPlan { PlayMethod, TranscodeReasons, StreamInfo (legacy) }
GET /Playback/Sessions/{id}/Stream
  -> requires session.Plan.StreamInfo (422 without one)
  -> PlaybackLiveStreamResolver.Resolve
       Mode Legacy / stop threshold / no record / source mismatch / DV  -> legacy StreamInfo
       otherwise PlaybackExecutionPlanAdapter.ToStreamInfo(v2 plan)       -> StreamInfo
  -> StreamInfo.ToUrl -> descriptor URL, e.g. /Videos/{id}/stream?Static=true&...
GET /Videos/{id}/stream?static=true          VideosController.GetVideoStream
  -> StreamingHelpers.GetStreamingState -> EncodingHelper.AttachMediaSourceInfo (legacy state)
  -> FileStreamResponseHelpers.GetStaticFileResult
DELETE /Playback/Sessions/{id}
  -> credential revocation + StopTranscodingJobs for the owner (#301)
```

Conclusion: in every mode a call to `/Playback/Sessions` still runs the legacy decision
(`StreamBuilder`) and still executes through the legacy `StreamInfo` and the legacy streaming
controller. Mode V2 only replaces the URL that `StreamInfo` produces.

## 2. Web chain as it runs today

```
playbackmanager.playInternal
  -> getPlaybackInfo (POST /Items/{id}/PlaybackInfo, jellyfin-apiclient)   legacy decision
  -> createStreamInfo (client streamInfo built from the legacy MediaSource)
  -> applyV2PlaybackUrlIfEnabled  (flag appSettings.enableV2PlaybackPath, default off)
       POST /Playback/Sessions + GET .../Stream through the generated PlaybackApi,
       configured from the @jellyfin/sdk Api object; overwrites streamInfo.url / playMethod
  -> player.play(streamInfo)
  -> reportPlaybackStart / Progress / Stopped (jellyfin-apiclient, /Sessions/Playing*)
```

No Web journey is v2-authoritative: with the flag off nothing calls Sessions; with it on, the legacy
`PlaybackInfo` decision still runs first and v2 only patches its result.

## 3. Legacy paths, consumers and removal conditions

| Legacy path | Consumers today | Removal condition |
| --- | --- | --- |
| `StreamBuilder` (`Tesserafin.Model/Dlna`) | `PlaybackSessionPlanner` (Sessions + PlaybackInfo), `StreamingHelpers` / `MediaInfoHelper` (container normalisation), `PlaybackExecutionPlanAdapter` (profile helpers), DLNA adapters, shadow projector | Every supported journey decided by the engine; PlaybackInfo retired or engine-backed; helpers moved to their owners |
| `PlaybackSessionPlanner` (legacy planner) | `ShadowPlaybackSessionPlanner._inner`, `MediaInfoHelper` | Same as above |
| `ShadowPlaybackSessionPlanner` + `Tesserafin.Playback.Shadow` | DI planner, admin diagnostics UI (`DiagnosticComparison`) | Legacy planner gone (nothing left to compare); diagnostics show v2 reasoning only |
| `PlaybackLiveStreamResolver` legacy fallback | Sessions `GET Stream`, `MediaInfoHelper` | Every Sessions decision executable without `StreamInfo` |
| `PlaybackExecutionPlanAdapter.ToStreamInfo` (v2 -> `StreamInfo` bridge) | `PlaybackLiveStreamResolver` | Descriptors minted from the execution plan for every play method |
| `/Videos/{id}/stream` legacy streaming state | All Web/native playback URLs | Sessions-owned delivery for every play method |
| Global mode `PlaybackShadow.Mode` / Web flag `enableV2PlaybackPath` | Remux, transcode, audio, Live TV journeys | Last of those journeys migrated (step 2) |
| Web `getPlaybackInfo` + `createStreamInfo` + V2 URL overlay | Every Web playback | Each journey migrated to a Sessions-native start |
| Web `/Sessions/Playing*` reports through `jellyfin-apiclient` | Progress, resume, stop for every journey | Step 3: same reports through the generated SDK |

Kept on purpose: hardware -> software fallback is an execution concern of the transcoding job; it
does not require the legacy decision engine.

## 4. Loop 1 slice: direct-play film (delivered)

Chain as built:

```
Web, film (Type Movie, file system) on the local HTML player
  -> POST /Playback/Sessions          generated SDK, configured from TesserafinApi (not @jellyfin/sdk)
     server: PlaybackSessionsController opens the film scope (item is Movie)
             EngineFilmPlanner: every candidate source is a local file -> the engine decides
             from the DECLARED capabilities/constraints (no DeviceProfile round trip);
             DirectPlay + executable plan -> V2PlanRecord published, PlaybackPlan(DirectPlay),
             StreamInfo = null; StreamBuilder is never constructed
  -> GET /Playback/Sessions/{id}/Stream    EngineFilmDescriptorMapper, from the retained plan
  -> GET|HEAD /Playback/Sessions/{id}/File?itemId&mediaSourceId&playSessionId
     PlaybackSessionFilesController: MediaDelivery + Media capability; per request: session
     exists, caller owns it, item/source/play session match the plan, owner still sees the
     item; serves the retained source with range support. The client names no file.
  -> subtitles: /Videos/{item}/{source}/Subtitles/{index}/0/Stream.{vtt|ass} + Subtitles capability
  -> stop: DELETE /Playback/Sessions/{id} (player `stopped` event); resume: client seek to the
     stored position (the file is served verbatim)
```

A remux the server executes from the session is §5, an SDR transcode §6. Any other decision for a
film (a remux or a transcode outside §5 and §6, 422) releases its session and continues on the
existing chain with direct play disabled, so a film never reaches the legacy direct-play path.
A 401/403/404, a failed descriptor, broker or item read, or a cancellation ends the attempt
(the session is released; the original error is kept if the release fails).

What was deleted and what stays, by consumer:

- Server `StreamBuilder`, `PlaybackSessionPlanner`, `ShadowPlaybackSessionPlanner`: still reached by
  every non-film video, audio and `PlaybackInfo`; never by the film journey (proved by
  `DirectPlayFilmEndToEndTests` on a host where both throw).
- `PlaybackExecutionPlanAdapter.ToStreamInfo` DirectPlay branch and the `PlaybackLiveStreamResolver`
  fallback: still used by engine-authoritative non-film sessions (Mode V2/Canary) and by
  `PlaybackInfo`; unreachable for a film. They go with their last consumer (step 2).
- Kill switch: no longer applies to a film's direct play (there is no legacy fallback left for it);
  `KillSwitch_ForcesLegacyOnNextRequest_UrlStillServable` now runs on a non-film video.
- Web V2 URL overlay DirectPlay case and the pre-#46 `directPlayMimeType` fallback: still used by
  non-film videos when the global flag is on. The flag stays until its last consumer migrates.
- Web `PlaybackInfo` + `createStreamInfo` for a film: no longer reached on a direct play
  (`directPlayFilmPlayback.test.ts` and the browser spec `directPlayFilm.spec.ts` fail if they are).

Remaining for the film journey itself (later journeys, not this slice): image subtitles are not
offered on a direct play or a remux; `/Sessions/Playing*` reports still go through
`jellyfin-apiclient` (step 3). A track or quality change during playback no longer re-plans through
the legacy `changeStream`: see §5.

## 5. Loop 2 slice: local film in remux (delivered)

Scenario: a `Movie` in a local file with H.264 + AAC in a container the client does not declare,
played by a browser that decodes both codecs and declares HLS output profiles (fMP4 first, MPEG-TS
second). Codecs are compatible, the container is not: both streams are copied into the declared
container. The server tests use a Matroska source against explicit mp4-only capabilities; the
browser rig uses an MPEG-TS source ("Container Film (2023)"), because current Chrome declares and
plays H.264/AAC in Matroska, which is therefore a direct play there.

Chain as built:

```
Web, film (Type Movie, file system) on the local HTML player
  -> POST /Playback/Sessions          same request as §4
     server: EngineFilmPlanner, per candidate source (each with its own user track defaults):
             a direct play wins; otherwise the first EXECUTABLE remux
             engine v8: a remux is delivered through the first declared OutputProfile that can
             carry BOTH copied codecs over a protocol the client supports (container + protocol
             come from the client; before v8 a remux was always "a decode container over HTTP")
             executable = Remux, HLS, fMP4 or TS, only RemuxContainer/CopyVideo/CopyAudio (+ text
             subtitle extraction/conversion), subtitle none or External
             -> V2PlanRecord published, PlaybackPlan(DirectStream), StreamInfo = null
  -> GET /Playback/Sessions/{id}/Stream    EngineFilmDescriptorMapper, from the retained plan:
             /Playback/Sessions/{id}/Hls/main.m3u8?itemId&mediaSourceId&playSessionId  (Protocol Hls)
             no codec, container, stream index, offset or credential in the url
  -> GET /Playback/Sessions/{id}/Hls/main.m3u8
     GET /Playback/Sessions/{id}/Hls/hls1/main/{n}.{mp4|ts}   (-1 = fMP4 init segment)
     DynamicHlsController (partial DynamicHlsController.PlaybackSessions.cs):
       PlaybackSessionDeliveryGuard per request: session exists, caller OWNS it (no administrator
       exception: the job a segment starts is its caller's), plan is an executable remux,
       item/source/play session match, owner still sees the item AND the selected version
       the encoding request (VideoRequestDto) is built from the retained plan alone
       StreamingHelpers.GetStreamingState -> EncodingHelper -> TranscodeManager.StartFfMpeg
       refused (422) unless the resulting state is the plan - both streams copied, and the
       planned stream indices: the executor never encodes for this route and never re-decides;
       no stream option is read from the query of a plan-built request
       a granted request keeps the session alive past the manager's idle limit
  -> subtitles: as §4 (text sidecar/embedded text through the subtitle route, client-side)
  -> seek and resume: the playlist always describes the whole film; the player seeks in it
  -> stop: DELETE /Playback/Sessions/{id} ends the owner's job under the play session, removes
     its output and revokes its capabilities (#301)
  -> re-plan (PUT, or POST under the same play session): the owner's job is stopped first; the
     output is named after the play session, not after the plan
```

Web: `directPlayFilm.ts` accepts `Method: Remux` only when the descriptor names
`/Playback/Sessions/{id}/Hls/main.m3u8` of that same session; anything else is released and
treated as "not covered". The player is handed an HLS source (`TranscodingSubProtocol: hls`, one
audio rendition). A change the player cannot make by itself (audio track, quality, retry after an
error) releases the session in place and plans again through `Playback/Sessions`
(`replanSessionOwnedStream`); `PlaybackInfo`/`createStreamInfo` are reached only if the server then
answers something this journey does not cover, after the release, with direct play disabled.

Shared code this journey still needs, and why it stays:

| Code | Used for | Leaves when |
| --- | --- | --- |
| `StreamingHelpers.GetStreamingState`, `EncodingHelper`, `StreamState` | Building the FFmpeg command of the stream copy from the plan-built request | Encoding is split plan -> renderer -> executor (step 5) |
| `DynamicHlsController` playlist/segment internals, `IDynamicHlsPlaylistGenerator`, `IHlsJobOwnershipAuthorizer`, `TranscodeManager` | Segment naming, job start/stop/ownership, output cleanup | Never as such: they are execution, shared with every HLS output |
| `VideoRequestDto` | The executor's input shape; here built server-side, never bound from the query | Step 5 |
| `PlaybackPlan(DirectStream, …)` with `StreamInfo = null` | The session's legacy-shaped plan slot; the authority is the `V2PlanRecord` | `PlaybackPlan` is replaced by the execution plan (step 2) |

Not reached by this journey (proved by `RemuxFilmEndToEndTests` on the host where they throw):
`PlaybackSessionPlanner`/`StreamBuilder`, `ShadowPlaybackSessionPlanner`,
`PlaybackLiveStreamResolver`, `PlaybackExecutionPlanAdapter.ToStreamInfo`, `StreamInfo.ToUrl`,
`/Videos/{id}/master.m3u8`, `/Videos/{id}/main.m3u8`, `/Videos/{id}/hls1/...`.

What lost its consumer: the `#374` legacy remux path for a film over HLS
(`PlaybackSessionPlanner` re-planning onto a declared output profile, then the `DirectStream ->
Transcode` url normalisation in `GET Stream`). Both remain for every non-film video and for a film
remux the client declared no HLS output for; their test
(`Remux_LegacyPath_UndeclaredSourceContainer_…`) now runs on a non-film video. Nothing could be
deleted outright in this slice: each legacy path above still has a named consumer in §3.

Outside this slice: a remux over plain HTTP (a client with no HLS output profile), other media
families, the global flag and the shadow. A remux that needs an audio transcode, and transcoding,
are §6.

## 6. Loop 3 slice: local film in SDR transcode (delivered)

Scenario: a `Movie` in a local file that cannot be delivered by copying both streams - an audio or
a video codec the client does not decode, a bitrate ceiling under the source's, a resolution
ceiling the client declares for the codec, or a stream copy the request forbids - played by a
client that declares HLS output profiles. The server tests use Matroska sources with AC-3 audio or
MPEG-4 Part 2 video; the browser rig uses "Dubbed Film (2024)" (H.264 + two AC-3 tracks) and the
existing "Transcode Probe (2021)" (MPEG-4 Part 2 + AC-3), both `Movie`s.

Chain as built - the same planner, descriptor mapper, guard and HLS route as §5, extended, not a
third pipeline:

```
POST /Playback/Sessions          same request as §4
  server: EngineFilmPlanner, per candidate source: a direct play wins, then the first executable
          remux (§5), then the first EXECUTABLE TRANSCODE
          executable = Transcode, HLS, fMP4 or TS; each stream copied or encoded, video to H.264,
          audio to AAC (down-mix allowed); output in the source's video range; subtitle none or
          External (text extraction/conversion). Tone mapping and a burnt-in subtitle are decided
          by the engine but not executed from here: they stay on the existing chain.
          the engine encodes to a profile's FIRST codec; a browser lists av1, hevc, h264. The
          planner hands the engine each declared profile with H.264 / AAC moved to the front
          (nothing added, nothing removed: a profile also says which copied streams it carries)
          engine v9: a stream copy the request forbids (AllowVideoStreamCopy /
          AllowAudioStreamCopy) is re-encoded inside a transcode too, and a remux demoted by such
          a prohibition re-encodes the forbidden stream only
          -> V2PlanRecord published, PlaybackPlan(Transcode, reasons), StreamInfo = null
GET /Playback/Sessions/{id}/Stream    EngineFilmDescriptorMapper: the same url as a remux,
          /Playback/Sessions/{id}/Hls/main.m3u8?itemId&mediaSourceId&playSessionId
          no codec, bitrate, resolution, stream index, offset or credential in the url
GET /Playback/Sessions/{id}/Hls/main.m3u8, .../Hls/hls1/main/{n}.{mp4|ts}
          PlaybackSessionDeliveryGuard as §5 (owner only, plan/ids match, item and version still
          visible), now for any plan EngineFilmPlanner.IsExecutableHlsOutput accepts
          EnginePlanEncodingRequest.Build renders the executor's input from the retained plan:
            source and stream indices; per stream "copy" or the plan's target codec
            VideoBitRate = the plan's video ceiling narrowed to (total ceiling - audio share)
            MaxWidth/MaxHeight = the plan's resolution; audio bitrate and channel ceilings
          nothing is bound from the query (BuiltFromRetainedPlan: stream options are not parsed)
          StreamingHelpers.GetStreamingState -> EncodingHelper -> TranscodeManager.StartFfMpeg:
          hardware selection and the hardware -> software fallback are the existing execution
          refused (422 "Plan not executable") unless the resulting state IS the plan: the planned
          stream indices, each stream copied exactly when the plan copies it, each encoded stream
          encoded to the plan's codec. The executor never re-decides.
stop, re-plan (PUT, or POST under the same play session): as §5 - the owner's job under the play
          session is stopped and its output removed before the new plan is served
```

A plan the executor refuses (422) is planned again by the client with the copy that failed
forbidden; the engine then answers a transcode of that stream on the same session
(`TranscodeFilm_RemuxTheExecutorCannotCopy_…`: Annex-B H.264 in AVI). No legacy planner is involved.

Web: `directPlayFilm.ts` accepts `Method: Transcode` when the descriptor names the session's HLS
route, exactly like a remux. A quality change (`MaxBitrate`), a track change and a retry after an
error re-plan through `Playback/Sessions` (`replanSessionOwnedStream`) and stay there.
`PlaybackInfo`/`createStreamInfo` are reached for a film only when the server answers something it
does not serve from the session.

Not reached by this journey (proved by `TranscodeFilmEndToEndTests` on the host where they throw):
the same list as §5. Encoding options a client adds to the address change nothing
(`TranscodeFilm_EncodingOptionsInTheQuery_ChangeNothing`).

What lost its consumer: for a local film with an HLS output profile, the legacy transcode decision
(`PlaybackSessionPlanner`/`StreamBuilder`), the `ToStreamInfo` Transcode branch and the
`/Videos/{id}/master.m3u8` url are no longer reached for an SDR transcode. Remaining consumers of
those paths, by name - nothing is deleted globally while they exist:

| Legacy path | Still reached by |
| --- | --- |
| `PlaybackSessionPlanner` / `StreamBuilder` | non-film video (episodes, home videos), audio, Live TV, `PlaybackInfo` (every native client start that is not Sessions), DLNA; a film needing tone mapping, a burnt-in subtitle, or with no HLS output profile; a film source that is not a local file |
| `PlaybackLiveStreamResolver`, `PlaybackExecutionPlanAdapter.ToStreamInfo`, `PlaybackSessionStreamDescriptorMapper` | every session of the rows above |
| `/Videos/{id}/master.m3u8`, `main.m3u8`, `hls1/...` with client-supplied encoding options | the same: Web and Android for everything that is not a film served from its session |
| Web `getPlaybackInfo` + `createStreamInfo` + V2 URL overlay, flag `enableV2PlaybackPath` | non-film video, audio, Live TV; a film the server does not serve from the session |

Outside this slice: HDR tone mapping and subtitle burn-in (explicitly), a transcode over plain HTTP,
target codecs other than H.264/AAC, other media families, the global flag and the shadow.


## 8. Shared model boundary (consolidation step 4, candidate)

`Model -> Data -> Database.Implementations` has been removed. Shared entities, enums and
interfaces now belong to the EF-free `Tesserafin.Data` assembly and namespace. The EF project
references Data; Model and Controller no longer acquire EF through their shared values.
`IItemQueryHelpers` moved from Controller to Server.Implementations because its query operations
explicitly take a database context. The architecture baseline loses the three Model/Data
exceptions; direct or transitive EF packages are forbidden for Data, Model and Controller.

The `Server.Core -> Api` inversion is addressed in §9; `MediaEncoding -> Playback.Shadow`
is addressed in §10. No playback path or delivery mode is added by this boundary change.


## 9. Core / HTTP boundary (consolidation step 4, candidate)

`Server.Core` no longer references `Api`, directly or transitively. The executable server
references both projects and owns their composition. The API owns WebSocket upgrade handling,
the concrete connection, the session transport and the session WebSocket listener. Their authentication, ticket
consumption, credential binding and keep-alive logic are moved unchanged; no forwarding types
or replacement protocol are added. The former WebSocketController is named
WebSocketSessionTransport in the API assembly so MVC cannot discover it as an HTTP controller;
a negative controller-discovery assertion covers that boundary. Existing WebSocket boundary/identity and SyncPlay-loss tests
exercise the relocated implementations.

The HTTP delivery helpers, session delivery guard and HLS ownership authorizer are registered in
`CoreAppHost.RegisterServices`, with the same scoped/singleton lifetimes. Core domain services
stay in `ApplicationHost`. `CollectionTypeMapping` moved from the API's DTO extensions to Data,
so collection-folder image generation and the Items API share one classification independent of
HTTP. All former mappings, their ordering and their default are preserved.

The architecture baseline loses its Core-to-API exception. A booted-host test verifies transport
assembly ownership and DI lifetimes, and a compiled-reference test verifies the core assembly has
no API reference. Credential inventory follows the new WebSocket manager path. The HTTP contract
is unchanged; no SDK generation or pair-lock movement belongs to this slice. This candidate is
stacked on #412 and retains that parent's pending SDK-pair merge requirement.


## 10. Encoding / comparison boundary (consolidation step 4, candidate)

`MediaEncoding` no longer reaches `Playback.Shadow`, directly or transitively. The comparison
assembly now owns its planner decorator, diagnostic records, request metadata and retention
stores. It references MediaEncoding to implement the execution-owned `IPlaybackSessionDiagnostics`
port. Core composes that implementation and supplies the same singleton to the manager, API
capture scopes and administrative diagnostics. No forwarding types or duplicate stores remain.

The session manager opens an opaque `IPlaybackPlanningDiagnostics` capture around each synchronous
planning attempt. Only a successful create or replan attaches it to the resulting session; an
empty capture evicts stale observations. Refusal or an exception disposes it without modifying
retained diagnostics. The implementation inherits request metadata, restores enclosing scopes,
and evicts lifecycle events with the session. Authoritative `IV2PlanStore` records and execution
remain separate; the manager never reads a comparison record or divergence.

The final forbidden-dependency exception is deleted from the ratchet baseline; the rule remains.
Tests of combined comparison/session behavior live in Playback.Shadow.Tests, while execution-only
tests remain in MediaEncoding.Tests without a comparison dependency. Port-level tests cover
attachment/disposal ordering and failure; a booted-host check verifies singleton identity and
compiled assembly references. Existing playback and OpenAPI integration tests verify runtime
composition. This candidate is stacked on #414 and inherits #412's pending SDK-pair merge
requirement. It changes neither the HTTP contract nor the set of supported playback journeys;
audio and other unmigrated consumers still require the comparison decorator.


## 11. Played-state commands leave the entities (consolidation step 5, candidate)

"Mark played / unplayed" is a service command. `IItemPlayedStateService` (Controller) and
`ItemPlayedStateService` (Server.Core) own target selection, the transitions
(`PlayedStateTransitions`, pure) and persistence through `IUserDataManager`. Its dependencies are
injected: `IUserDataManager`, `IItemQueryService`, `IItemAccessService`, `IItemLookupService`,
`ILinkedChildrenService`, `IChannelManager`, `IServerConfigurationManager`, `TimeProvider`.
`PlaystateController` and `SessionManager` (a completed version marks the other versions) call it.
The HTTP contract is unchanged.

Removed: `BaseItem.MarkPlayed`, `BaseItem.MarkUnplayed`, `BaseItem.ResetPlayedState`, the `Folder`
and `BoxSet` overrides, `Video.MarkPlayed`, `Video.MarkUnplayed` and `Video.PropagatePlayedState`.
The command no longer reaches `BaseItem.UserDataManager`, `BaseItem.LibraryManager`,
`BaseItem.ChannelManager`, `BaseItem.ConfigurationManager`, `BaseItem.FileSystem`,
`Folder.CollectionManager`, `Folder.UserViewManager` or `UserView.TVSeriesManager`; its recursive
selection no longer calls the obsolete five-argument `Folder.GetItemList`.
`PlayedStateCommandBoundaryTests` fails if one of these methods returns on a media entity.

Selection rules carried over: a leaf marks itself and its other versions; a folder marks its
non-folder descendants (the user root and aggregate root stand for the whole library), excluding
virtual items when marking played for a user who hides missing episodes, and never an unaired
virtual episode; a series marks its real episodes by series key; a season marks the episodes of
its number (specials per `DisplaySpecialsWithinSeasons`); a collection with members marks the
members the user may see, each by its own rule; a channel folder asks the channel.

Deliberate narrowing: a `UserView` marks nothing (it was routed through `UserViewBuilder`);
collection members are resolved by `ItemId` only, not by legacy path links.

Still on `BaseItem.UserDataManager` (read and filter paths, not this command):
`BaseItem.IsPlayed`, `IsUnplayed`, `IsFavoriteOrLiked`; `Folder.GetRawQueryItems` and the
children filter in `Folder` via `UserViewBuilder.Filter`; `Season.GetItemsInternal`;
`UserView.GetItemsInternal`. The static and its `ApplicationHost` assignment therefore stay; the
ratchet baseline is unchanged. Stacked on #415.


## 12. Software filter renderer (consolidation step 5, candidate)

The session film SDR transcode still consumes its retained execution plan through
`EnginePlanEncodingRequest`; no request parameter is promoted into a new decision. The shared
encoding helper now captures a `SoftwarePipelinePlan`, and `SoftwarePipelineRenderer` produces
its immutable main/subtitle/overlay filter sequences. Software scaling and deinterlacing have
one rendering implementation, including hardware copy-back consumers.

The former public `GetSwVidFilterChain`, `GetSwScaleFilter` and `GetSwDeinterlaceFilter` methods
are removed. Frame-rate insertion, map/filter-graph assembly, codec/bitrate/quality selection and
process execution remain outside this extraction. The diagnostic `TranscodePlan` is not made an
execution authority by this change. The NVIDIA filter renderer is extracted in §13. The VAAPI renderer is extracted in §14. Other hardware
renderers, global playback switches and all remaining StreamInfo/StreamBuilder consumers above
remain to be migrated.

The Controller test suite checks snapshot isolation and forbids runtime/configuration services
in the renderer. Real FFmpeg/ffprobe tests validate software output; existing film direct-play,
remux and transcode integration cases validate their HTTP/session/job lifecycle. No new browser,
physical-device, hardware, HDR or 3D acceptance is claimed.

## 13. NVIDIA filter renderer (consolidation step 5, candidate)

The NVIDIA production routing now calls `NvidiaPipelineRenderer` with a captured immutable
`NvidiaPipelinePlan`; this does not select a new playback decision. The renderer owns the
main/subtitle/overlay sequences and CUDA upload/download ordering. Shared geometry, capability
resolution and subtitle-path preparation stay at the capture boundary. Software/copy-back
routing still consumes the single software renderer. Common FPS insertion and command assembly
stay outside the renderer.

`GetNvidiaVidFilterChain` and `GetNvidiaVidFiltersPrefered` are removed, with no public forwarding
methods. CUDA deinterlacing is rendered in the NVIDIA renderer; the common helper retains only
the other backends. The architecture guard covers both renderer namespaces, immutable captured
values and the removed public entry points. No forbidden-dependency baseline is expanded.

67 static pre-extraction command graphs cover production routing with mocked capabilities.
These are supplemented by snapshot-isolation tests and software FFmpeg/session regression tests.
There is no claim of real NVIDIA hardware acceptance, HDR/3D acceptance or completion of step 5.
All remaining StreamInfo/StreamBuilder consumers in §6 are unchanged by this extraction.

## 14. VAAPI filter renderer (consolidation step 5, candidate)

The VAAPI production route captures one immutable `VaapiPipelinePlan` strategy before calling
`VaapiPipelineRenderer`: software/copy-back, Intel iHD, AMD Vulkan/DRM or limited VAAPI. The
renderer owns main/subtitle/overlay composition and surface transfers, without consulting job
state, configuration, device probes, operating-system APIs or subtitle-path services.

Software/copy-back filtering reuses the single software renderer. The VAAPI output upload is
appended after main or overlay composition as required. Shared geometry, tone-map/libplacebo
formatting and subtitle resolution stay at the capture boundary. Common FPS insertion, mapping,
codec/device selection and final command assembly remain outside this renderer.

The four public VAAPI filter-chain entries are removed, without forwarders or a coexistence
flag. The shared VAAPI deinterlace formatter still serves QSV paths. No project/package/API/SDK
change or ratchet-baseline expansion is introduced.

328 pre-extraction production graph fixtures cover Intel/AMD/i965/generic profiles and optional
capabilities. Snapshot-isolation and strategy-selection cases supplement them. Existing real
VAAPI tests are retained; their hardware requirement is not waived by command characterization.
Hardware acceptance remains outstanding. Remaining playback consumers in §6 are unchanged, and
step 5 remains open.

## 15. QSV filter renderer (consolidation step 5, candidate)

QSV production routing captures one immutable `QsvPipelinePlan`: software/copy-back, Linux
VAAPI or Windows D3D11. `QsvPipelineRenderer` composes main/subtitle/overlay sequences and
surface transfers without reading a job, options, platform APIs or hardware services. OpenCL,
VAAPI VPP and Windows VPP tone mapping are exclusive; Windows procamp/two-pass requirements
are frozen before rendering. Shared software scaling has one implementation.

The public entries `GetIntelVidFilterChain`, `GetIntelQsvDx11VidFiltersPrefered` and
`GetIntelQsvVaapiVidFiltersPrefered` are removed without compatibility forwarders or flags.
The renderer retains QSV overlay pools, the 1080 preparation ceiling, native surface relays
and Linux-specific reverse mapping pools. Shared geometry/filter helpers and subtitle paths
remain resolved at capture. Common FPS insertion, final command assembly and codec/device
selection stay outside this slice. The retained Sessions execution plan remains authoritative.

The static baseline contains 234 pre-extraction backend graphs and 117 complete production
Linux responses. D3D11 graph characterization executes explicit parameters on Linux; native
Windows startup and Intel GPU execution remain unproven. Architecture checks cover all four
renderer namespaces and removed entry points without expanding the consolidation baseline.
No OpenAPI, SDK, project or package changes. AMF, VideoToolbox and RKMPP are extracted in §16;
step 5 and all remaining playback consumers in §6 stay open.

## 16. Remaining backend renderers (consolidation step 5, candidate)

AMF, VideoToolbox and RKMPP now capture immutable software/hardware plans before invoking
their pure renderers. This completes backend-specific filter-sequence extraction across the
seven renderer namespaces. AMF owns D3D11/OpenCL composition; VideoToolbox owns native/Metal
filtering and surface upload policy; RKMPP owns AFBC, two-pass RGA scaling and DRM/OpenCL
transfers. Software/copy-back filtering continues to use one software renderer.

Six public entries lose their implementation and are deleted: `GetAmdVidFilterChain`,
`GetAmdDx11VidFiltersPrefered`, `GetAppleVidFilterChain`, `GetAppleVidFiltersPreferred`,
`GetRkmppVidFilterChain` and `GetRkmppVidFiltersPrefered`. Native platform and hardware support
are resolved at capture; rendering reads no mutable job, options or capability services.
No forwarder, coexistence flag, OpenAPI/SDK/project/package change or ratchet expansion.

281 explicit pre-extraction graphs cover the three backends, plus 116 complete RKMPP/Linux
production responses. The static reference originates from #421 (19079c53da), before all three
extractions. Snapshot-isolation, immutable-output and architecture cases supplement it.
Native Windows/macOS startup, real AMD/Apple/Rockchip execution and HDR/Dolby Vision/3D
acceptance remain outstanding; explicit graph tests on Linux do not provide those proofs.

Device/codec decisions, shared geometry and filter-expression helpers, quality/bitrate arguments,
common FPS placement, mapping, final command serialization and process execution remain outside
these renderers. Step 5 remains open, including library service decomposition. Remaining playback
consumers in §6 are unchanged. The retained Sessions execution plan remains authoritative.

## 17. Managed FFmpeg process boundary (consolidation step 5, candidate)

HLS and progressive playback now carry `FfmpegCommand` into `ITranscodeManager`.
`IFfmpegProcessRunner` creates the unstarted process and launches it only after the manager
has registered its owner, under the same registry lock that removes stopped jobs. Probes,
encoder validation and hardware selection use the same injected runner. The manager retains
its registry, input pump, logging, throttling, failure classification and output cleanup.
There is no second launch path or compatibility overload in the manager.

`EncodingHelper` no longer calls `Environment.SetEnvironmentVariable`. `FfmpegArguments`
captures i965 (`LIBVA_DRIVER_NAME`, `LIBVA_DRIVER_NAME_REEFIN`) and AMD (`AMD_DEBUG`) overrides
in the same branch that selects device arguments. HLS/progressive commands and interval
trickplay extraction deliver these values to their own child only. The parent's configured
environment remains inherited normally. The former singleton `ImmutableArray<string>` that
held an entire quoted command line is replaced with an explicit `ArgumentLine` string;
tokenized argv construction is not implemented or claimed by this slice.

Managed transcodes redirect stdin/stderr only. Probes drain stdout and stderr before feeding
optional input, preventing pipe deadlock when a child writes before reading a large input.
Preparation/launch cancellation and errors remove the failed registered attempt and dispose
its log, including cancellation during pre-launch log writes.

Tests cover concurrent driver captures, separate real child environments, a concurrent probe,
unstarted creation, pre-launch cancellation, launch failure, both-pipe flooding with large
stdin, and the existing ownership/restart/fallback cases. Architecture checks forbid native
creation/launch in `TranscodeManager` and environment mutation in command construction.
Image/attachment/subtitle process scheduling, tokenized argv and physical GPU/Windows/macOS
acceptance remain separate work. No OpenAPI, SDK, project/package or ratchet-baseline change;
step 5 and the remaining playback consumers in §6 stay open.

Local validation: Controller 1,673 passed / 2 hardware skips; MediaEncoding 324 passed /
2 platform/hardware skips; API 650 passed; Server 528 passed, including the consolidation
ratchet with its baseline unchanged; integration 94 passed (82 film/OpenAPI cases and 12
HLS/progressive output-isolation cases with software FFmpeg). Fifteen new cases exercise
this boundary. Both architecture negative controls fail when their violation is injected,
then pass after source restoration. The startup-exit fixture explicitly disables network-change
subscriptions after clearing inherited settings, so a restricted netlink socket cannot replace
the failure it is meant to observe. No startup/exit assertion is removed or relaxed.

## 18. Finite attachment/subtitle extraction (consolidation step 5, candidate)

The six native process implementations in `AttachmentExtractor` and `SubtitleEncoder` are
removed. Both inject `IFfmpegProcessRunner` and use its finite-file profile through one
`FfmpegFileExecutor`. The runner closes stdin, drains stdout/stderr concurrently, retains at
most 65,536 characters per diagnostic stream, and kills/waits/drains before returning a timeout
or throwing cancellation. Probe capture remains complete; managed transcodes retain their
owner-before-launch lifecycle from §17.

The file executor accepts only the caller's permitted exit statuses and requires every expected
file to exist and contain data. On failure it cleans the attempt's named outputs while its
consumer still owns the cache locks. Cleanup I/O errors are logged while preserving the primary
failure. Output publication still uses the existing cache paths; transactional publication is
not implemented by this slice.

Attachment bulk/single extraction now share one folder lock and one command builder. Explicit
per-stream destinations replace the filename-driven bulk dump. Cache names consist of stream
index plus an ASCII-safe basename (original extension retained), so separators, quotes and
duplicate basenames do not select another output. This deliberately changes internal cache
names without a compatibility lookup. Previously completed attachments are not rewritten or
deleted by a later failed attempt. The no-audio/video Matroska/MKS exit-code-1 case is accepted
only when all requested attachments were produced. Attachments retain their caller-controlled
lifetime; subtitle extraction retains its configured timeout and now also observes cancellation.

Subtitle extraction/conversion refuses nonzero exits even if a partial nonempty file exists.
Batch errors and cancellation propagate to the requesting reader; failure no longer turns into
a successful descriptor for a missing file. Source metadata is published only after successful
extraction and postprocessing. The three duplicated subtitle launch/validation/cleanup blocks
are replaced with the common executor.

Twenty new cases cover bounded diagnostics, closed stdin, accepted/rejected exits, missing/empty
outputs, kill-and-wait before cleanup, primary-error preservation, cache-lock handoff, failed
subtitle retry, real Matroska/MKS attachments and real embedded/external subtitle extraction.
Architecture guards reject native process creation/launch in both consumers. The new real
FFmpeg cases are regular tests, included by the existing hosted non-Smoke test job that installs
FFmpeg. No OpenAPI, SDK, project/package or consolidation-baseline change.

Image/trickplay scheduling, general media probing outside the runner, transactional cache
publication and tokenized argv remain separate work. Step 5 and remaining playback consumers
in §6 stay open; no physical GPU or native Windows/macOS acceptance is claimed here.

Local validation: Controller 1,673 passed / 2 hardware skips; MediaEncoding 344 passed /
2 platform/hardware skips; API 650 passed; Server 528 passed, including the unchanged
consolidation ratchet; real software FFmpeg film/OpenAPI/HLS/progressive integration 94 passed.
Injecting native construction into either extractor fails its architecture check. Removing
file-content validation fails both the missing-output and empty-output controls; restoring
the implementation passes the full MediaEncoding suite.

## 19. Image/trickplay scheduling (consolidation step 5, candidate)

`MediaEncoder` no longer constructs or controls native processes in its image/trickplay paths.
`FfmpegImageScheduler` owns the shared concurrency permits and an attempt registry, including
queued attempts. Registration happens before the runner can launch. Closing the scheduler
rejects new work, cancels active/queued work, waits for every attempt's runner and cleanup,
then disposes the permit pool. Concurrent disposal waits for the same shutdown completion.
`MediaEncoder.Dispose` closes this scheduler before stopping its remaining general probes.

`IFfmpegProcessRunner.RunImageAsync` shares finite-process creation, closed stdin, bounded
diagnostic drains, kill-tree and wait-for-exit with the file/probe profiles. Child priority is
optional at the OS level: failure is logged without changing another attempt's settings.
The runner receives its logger through DI; no compatibility constructor or native image launch
fallback remains. Hardware initialization environment remains part of the captured command.

Single images retain the configured total timeout (SDR/HDR defaults unchanged). Trickplay
retains the configured inactivity window and renews it only when another nonempty JPEG appears;
zero-byte files, unchanged images and unrelated files do not count as progress. Observer I/O
errors propagate after native shutdown. Cancellation overrides a healthy progress observer.
Native exit 0 still requires real output; failed attempts clean their own file/directory before
releasing a permit. Queued cancellation creates no output. Cancellation and disposal do not
trigger the I-frame retry; content/decoder failures retain the existing standard-decode retry.

Twenty-two new cases cover permit limits, queued cancellation, progress observation, immutable
command/priority handoff, missing/failed/timed-out output, concurrent disposal, real child
shutdown, inactivity renewal, observer failure and cancellation without retry. Real software
FFmpeg produces a JPEG and two interval sequences (keyframe-only on/off); JPEG signatures are
checked. Two architecture guards reject native process creation/launch in the scheduler and
the image region of `MediaEncoder` without pretending its remaining probe wrapper is removed.

General media probing and its live-stream pump, transactional cache publication and tokenized
argv remain separate work. No OpenAPI, SDK, project/package or ratchet-baseline change. Step 5
and remaining playback consumers in §6 stay open; native GPU/Windows/macOS acceptance is not
provided by these Linux software tests.

Local validation: Controller 1,673 passed / 2 hardware skips; MediaEncoding 366 passed /
2 platform/hardware skips; API 650 passed; Server 528 passed, including the unchanged
consolidation ratchet; real software FFmpeg film/OpenAPI/HLS/progressive integration 94 passed.
Native-launch injections fail both image architecture guards. Removing the configured permit
limit fails both concurrency cases. Reintroducing the cancellation/disposal retry fails both
public image-reader checks. Restoring source passes the full MediaEncoding suite.

## 20. Atomic extraction-cache publication (consolidation step 5, candidate)

`FfmpegFileExecutor` now accepts a command factory receiving staged output paths. Attachment
and subtitle builders assemble against those paths directly; no serialized-path replacement
or direct-to-cache overload remains. `AtomicCachePublication` owns a private sibling directory
outside the served source-cache folder, preserving file extensions for FFmpeg's muxer selection.
An abandoned or undeletable attempt is never selected by a cache lookup or the attachment font
directory. Cleanup failure is logged while preserving the primary error and previous cache.

All expected outputs must exist and be nonempty before postprocessing. ASS font rewriting and
subtitle metadata preparation happen in staging. Publication revalidates every artifact and
sidecar and observes a final cancellation checkpoint before native renaming starts. Cancellation
after that checkpoint does not interrupt an artifact's data/metadata pair. Failure, cancellation,
missing output or a postprocessing error before the first rename leaves the previous cache untouched.

Data is renamed before its freshness marker: metadata must never bless old data with a new
source revision. Source size/mtime is captured before extraction and checked again after
preparation; change or removal aborts publication. A failed metadata rename can leave complete
new data with an old/missing marker, which remains subject to the existing freshness checks.

Native POSIX `rename` and Windows `MoveFileExW(REPLACE_EXISTING)` are used without a cross-device
copy fallback. A mount boundary fails closed instead of copying a partially visible file.
Atomicity is per file: all staging is validated before the first rename, but an I/O failure
during a multi-file commit may leave a prefix of complete, prepared artifacts published. The
remaining artifacts keep their old values. This is not a batch transaction or an fsync/crash-
durability guarantee. Open readers can finish reading the old file while new readers see the
replacement where the filesystem/share permissions permit the rename.

Seventeen new cases cover failure/cancellation with an existing cache, whole-attempt validation,
postprocessing failure/cancellation/empty output, old-reader isolation, data/metadata rename
failures, complete-prefix behavior, real cross-device refusal, undeletable orphan staging and
retry, source-change refusal and real ASS extraction. The copy/move fallback is forbidden by
an architecture guard. Existing native extraction and kill-before-cleanup tests now exercise
the staging contract. No OpenAPI, SDK, project/package or consolidation-baseline change.

Windows/macOS native execution remains unproven by the Linux tests. General media probing and
its live input pump, other cache families and tokenized argv remain separate work. Step 5 and
remaining playback consumers in §6 stay open.

Local validation for §20: Controller 1,673 passed / 2 hardware skips; MediaEncoding 383 passed /
2 platform/hardware skips; API 650 passed; Server 528 passed, including the unchanged
consolidation ratchet; real software FFmpeg film/OpenAPI/HLS/progressive integration 94 passed.
Publishing before preparation fails all three postprocessing controls. Publishing metadata
before data fails the disappeared-data control. A copy/delete fallback fails the cross-device
case and architecture guard. Restoring source passes the full MediaEncoding suite.

## 21. General media probing and Live TV input ownership (consolidation step 5, candidate)

`MediaEncoder.GetMediaInfo` now only captures the selected executable and probe arguments,
source metadata and a lazy reader factory. `MediaProbeService` owns operation registration,
JSON interpretation and the existing `ProbeResultNormalizer`. It rejects timeouts and nonzero
native exits before interpreting output, including syntactically valid partial results. Invalid
`0:1` ratios retain their previous normalization. Chapters, first-frame options, audio/video
selection and HTTP/RTSP option suppression for pipe inputs remain at command construction.

`IFfmpegProcessRunner.RunMediaProbeAsync` owns native launch, concurrent complete stdout capture,
a bounded 65,536-character stderr tail, the binary input pump and all teardown. A two-minute
native execution deadline now bounds probes even without caller cancellation. Each provider
reader is opened exactly once, only after launch, through `ProgressiveFileStream`; probe and
transcode consumers never share readers. Native exit cancels an unfinished producer. Producer
failure interrupts even a consumer ignoring EOF, kills/waits/drains the child, and preserves the
producer exception. Caller cancellation remains cancellation rather than a timeout or metadata
success. Unexpected stream-disposal errors cannot abandon disposal of the other owned stream.

The old `GetMediaInfoInternal`, `ProcessWrapper`, native-process registry and start/stop helpers
are deleted. `MediaEncoder` has no native launch or pump ownership left. `FfmpegOperationScope`
is shared by probes and the image scheduler: register before launch, close admission before
cancelling, join every operation's cleanup, then release owned resources. Encoder disposal
starts both shutdowns before awaiting either, so an image cleanup cannot delay cancelling a
live probe. Concurrent stops await the same completion. This supersedes the remaining-probe
wrapper and sequential disposal described in §§17–20; other cache families and tokenized argv
remain separate work. No API/OpenAPI, SDK, project/package or ratchet-baseline change.

The new cases exercise binary input byte integrity across concurrent output floods, diagnostic
bounds, closed unused stdin, child exit with a blocked source, producer failure, timeout and
caller cancellation with PID reaping, failed/native-pre-cancelled launch without opening input,
invalid/failed probe results, ratio normalization, isolated cancellation, shutdown joining and
public encoder command capture. Real software FFmpeg generates an MPEG-TS fixture: the public
file probe and two independent progressive Live TV probes return the same useful audio/video
streams, without fetching the protected LiveStreamFiles URL, and close all their readers.
Architecture guards now cover the whole encoder and probe interpreter, not only image paths.
These are regular non-Smoke tests under the existing FFmpeg-equipped hosted job.

The Live TV fixture models the internal tuner-file handoff; it is not a physical tuner rig.
Stream reads must observe cancellation. Native Windows/macOS and hardware execution remain
unproven by Linux software tests. Step 5 and remaining playback consumers in §6 stay open.

Local validation for §21: Controller 1,674 passed / 2 hardware skips; MediaEncoding 405 passed /
2 platform/hardware skips; API 650 passed; Server 528 passed, including the unchanged
consolidation ratchet; film/OpenAPI/HLS/progressive integration 94 passed with software FFmpeg.
Injecting native construction into the encoder fails the expanded architecture guard. Accepting
metadata despite failed/timed-out native execution fails both process-result cases. After source
restoration, the full MediaEncoding suite and analyzer-enabled builds pass. No hosted CI result
or physical tuner/GPU acceptance is claimed.

## 22. Library scan ownership (consolidation step 5, candidate)

`ILibraryScanCoordinator` now owns visible scan requests, full execution, top-folder refreshes,
post-scan registration and running status. `RefreshMediaLibraryTask` calls it directly. The old
scan methods, state, post-scan array, wizard subscription, queue helper and concrete-manager cast
are removed from `LibraryManager`/`ILibraryManager`. The manager loses its task-manager and lazy
monitor dependencies; it consumes the scan/root ports for its remaining library operations.
Its source shrinks from 3,522 to 3,181 lines. Constructor arity remains 34: these two responsibilities
are replaced by two narrow ports, while its remaining item-management dependencies stay open work.

`LibraryScanCoordinator` owns one traversal slot for full scans, top-folder reconciliation and
monitored structure edits. Queued cancellation touches neither files nor monitoring. Active
cancellation propagates through traversal/post-scan tasks; cancellation prevents inherited-value
commit. Non-cancellation post-scan failures retain the existing log-and-continue policy. Inline
progress callbacks map traversal to 0–96 and post-scan work to 96–100. Monitor restoration and
ignore-cache cleanup are both attempted on error, preserving the first failure and resetting the
running state before releasing admission. Status covers active full scans through cleanup; it is
not a transaction lock for maintenance or every metadata operation.

One DI singleton is exposed through both the scan port and `IHostedService`. Host stop closes
admission, cancels queued/active traversal, joins cleanup and leaves monitoring stopped. Repeated
stop/disposal awaits the same completion. Exported post-scan tasks are registered after application
composition, avoiding an eager task-enumeration construction cycle. Cancellation remains cooperative
with the existing metadata/Folder implementations. Wizard completion, API requests,
recording setup, backup and maintenance status checks use the dedicated scan port.

`LibraryScanTraversal` owns root/recursive metadata validation and deletion/cache invalidation for
missing collection folders. `LibraryRootFolderProvider` owns the shared aggregate root and playlist
child, with explicit container types/IDs and a single persistence batch for their relationship.
Application-owned containers bypass media resolvers, metadata exporters and item-update events.
New root identities use `AggregateFolder` directly. The remaining manager root getter reads the
same provider; it has no independent cache or construction algorithm. Entity-level refresh and
validation still use the existing `Folder`/`BaseItem` implementation and its remaining statics.

Create/remove library and rename/add/remove path operations use the coordinator's traversal slot
and monitor scope. The controller's detached `Task.Run` cleanup paths are removed: the response
awaits reconciliation and restoration; a requested full scan is then queued visibly. Structure
edit errors retain their original failure if reconciliation also fails. The existing one-second
notification-settling delay remains for edits without a full refresh.

The contract change is internal to server composition: scan members move off the public .NET
`ILibraryManager` interface. HTTP routes, operation shapes, OpenAPI and SDK remain unchanged.
No project/package dependency or consolidation-ratchet baseline is expanded. Step 5 remains open,
including the remaining manager responsibilities and entity static services.

Local validation for §22: Server.Implementations 1,296 passed / 4 Windows-path skips; API 650
passed; Server 528 passed, including the unchanged consolidation ratchet; Providers 358 passed;
Live TV 61 passed. Integration 111 passed: the existing film/OpenAPI/HLS/progressive checks,
library-structure cases and a new booted-host create → full scan → rename → remove journey,
including the shared root and shared hosted-service instance. The 27 new unit cases cover
ordering/progress, post-task error/cancellation, each cleanup failure, queued cancellation,
serialization, stop/drain, host stop, structure edits, wizard scheduling, root caching/persistence
and architectural boundaries. Both the restored scan facade and a second traversal slot fail
their negative controls; restored source passes. Analyzer-enabled builds and diff checks pass.
The backup roundtrips use a temporary directory on the workspace filesystem because this
container's /tmp volume is smaller than the existing 5-GiB backup requirement; the requirement
and assertions remain unchanged. The scan fixture uses an empty disabled library, not a large
media-library performance benchmark or a native Windows acceptance run. Hosted CI is unclaimed.


## 23. Library structure mutation ownership

The follow-up to scan extraction moves add/remove library, rename, add/update/remove source path,
and options updates to `ILibraryStructureCommands`. `LibraryStructureController`, collections and
recordings call that port. The six old structure methods, including public `CreateShortcut`, are
removed from `ILibraryManager` and `LibraryManager`; no forwarding methods remain. The manager
also loses its coordinator dependency. The shared read-only shortcut reader is used by the read
catalogue and command-side options synchronization.

The command service owns filesystem containment checks, collision allocation and configuration
writes inside the scan coordinator's exclusive slot. HTTP cancellation reaches admission. Once
an edit begins, reconciliation uses an independent 30-second cooperative cleanup deadline, with monitor
restoration and primary-error preservation. Options caching is published after successful XML
serialization. This does not promise rollback of a batch of filesystem changes, and it does not
remove the remaining CollectionFolder options-store statics or LibraryManager responsibilities.


## 24. Item deletion ownership

`IItemDeletionCommands` replaces the manager deletion overloads and `DeleteItemsUnsafeFast`.
The singleton command owner serializes destructive operations. Controllers, maintenance,
providers, Live TV and migrations await it; entity refresh/validation methods pass it explicitly
through recursive calls. No synchronous adapter or new entity static service is introduced.
`LibraryManager` loses deletion helpers, IPathManager and lazy external-data dependencies
(33 → 31 constructor parameters). Its removal-event publisher was subsequently removed in §26. Alternate queries and
metadata/linked-reference writes remain dependencies of the command owner.

The required source path is removed before metadata, alternate promotion or catalogue/cache
changes; a required-file failure leaves those untouched. Optional metadata cleanup retains the
existing best-effort policy. Surviving alternates are promoted/reparented before deleting the
primary, playlist references are rerouted, and missing alternate paths are excluded from the
promoted version's graph. UserData detachment/retention remains owned by the persistence
transaction. Catalogue-only removal leaves external media bytes intact; managed internal
entity paths still follow their existing cleanup policy, with path-component containment.
Bulk unreferenced cleanup retains its leaf-only/no-promotion/no-event semantics.

Cancellation can refuse admission or a queued operation. Once deletion starts, the command
finishes its side effects independently of caller cancellation. This is not atomic rollback
across filesystem, external providers and database: a later failure may occur after source bytes
are gone. The command serializes deletions, not all scan or item-write operations, and does not
claim a separate host stop/drain protocol. Broader entity-global removal remains pending.

Alternate resolution no longer secretly deletes an older generic Video. Async version refresh
explicitly requests obsolete-entry cleanup before resolving the typed version; synchronous item
creation/update only resolves and persists the typed graph. No legacy compatibility promise is
made for old .NET manager or refresh signatures. HTTP operations and OpenAPI shapes are unchanged.


## 25. Image preparation and explicit publication

`IItemImageService` owns refresh/localization preparation and returns `IItemImageUpdate`.
`UpdateImagesAsync` and `ConvertImageToLocal` are removed from both manager and interface, with
no forwarders. The remaining metadata-save helper is private. The manager loses IImageProcessor,
unused IMediaEncoder and unused IUserDataManager, and consumes the image port (31 → 29 parameters).

Preparation copies image metadata without changing the published entity. Remote bytes are
written to unique temporary files and atomically renamed beneath the item's internal metadata
root. The root-taking path overload is explicit and channel identity is stable; it does not
consult the global recording manager. A shared IRemoteImageSource acquires and caches HTTP
payloads for both localization and existing provider artwork saves. The latter still use
ImageSaver's configured paths; localization itself is a managed cache, not a write beside media.

The caller explicitly commits. A transaction replaces only the image rows; cancellation can
refuse admission but, once replacement begins, the database completes before cache publication.
A failed SQL write leaves the original image rows, published objects and cache unchanged.
A missing item is rejected and evicted rather than reinserted. No whole-item save, metadata
provider invocation, recursive image processing or DateLastSaved update is performed by this owner.
The committed change is published after cache publication through the shared port (§26);
surrounding full-item metadata saves suppress the extra image notification. Live TV programs keep their notification filter.

Per-item admission spans preparation through commit/disposal. Disposing the handle drains a running commit
before releasing admission and removes uncommitted temporary/final files. A changed image
snapshot is rejected before writing. This serializes this pipeline, not every provider/item
writer, and does not promise a distributed filesystem/database transaction or crash recovery for
abandoned files. Old media-side artwork is retained; managed files live under the metadata root
already cleaned by item deletion. Existing item/provider globals outside this path remain work.

Folder validation carries the image port explicitly. BaseItem no longer refreshes images through
the manager; ProviderManager's existing item-refresh entry point prepares and commits them.
ImageController, Live TV precaching and recording-image copying likewise commit explicit results.
HTTP routes, response shapes, OpenAPI and SDK remain unchanged. The six image integration method
names are required by CI, including both rows of the permanent-rejection theory.


## 26. Item change notification ownership

`IItemChangeEvents` and `IItemChangePublisher` share one `ItemChangeEvents` singleton.
The feed is subscription-only; writers receive only the publication port. The owner depends on
logging alone, never on item stores, resolvers, metadata providers or `LibraryManager`.
`ItemStore`, general manager creation/update, committed image changes and reconciled deletion
publish directly to it. The manager constructor no longer installs notification relays or retains
an `IItemStore`. Its three events and `ReportItemRemoved` are deleted, as are `ItemSaved` and
`ImagesChanged` on their former owners/interfaces. No compatibility forwarding surface remains.

Monitor and client-notifier lifecycle subscriptions use the common feed. Producer filtering,
parent/reason information and the persistence → cache → notification order are retained.
Failure to save/register does not publish a creation; deletion does not publish before cache
invalidation; bulk unreferenced deletion retains its no-event policy. Full-item image preparation
still suppresses an extra notification. One observer throwing is logged independently, so it
cannot prevent later observers from receiving a completed write or make that write appear failed.
Delivery is synchronous with the invocation-list snapshot captured for that publication.
A failed late parent lookup is still logged and suppresses that notification without failing
an already persisted creation or image update.

This is a process-local best-effort feed, not a durable outbox or background delivery queue.
It references live entities, does not serialize concurrent writers or replay past events, and
still requires observers to subscribe before the changes they need. `LibraryManager` remains
29 constructor parameters but shrinks by removing notification orchestration; general write and
alternate-version planning extraction is the next dependency boundary. HTTP/OpenAPI and SDK
shapes are unchanged; the old .NET event contracts are intentionally removed.


## 27. Item graph write ownership

General creation, update, version propagation and user-data reattachment now belong to
`IItemWriteCommands`. `CreateItem(s)`, `UpdateItem(s)Async`, `ReattachUserDataAsync` and
`RunMetadataSavers` are removed from the manager/interface without compatibility forwarders.
A read-only resolution service and options policy replace the writer's dependency on the manager.
Resolver arguments/helpers use explicit lookup/filesystem/configuration inputs. Deterministic
identity generation is shared by the item store, resolution service and remaining manager queries.

One planner prepares typed local versions and their multipart paths for both create and update.
Individual video updates stage propagated fields and dates on shallow copies, include existing
versions whose link has not yet been inserted, and persist the whole graph before cache/events.
Bulk refresh retains its nonrecursive version behavior. A save failure leaves staged dates and
alternate metadata unpublished; a post-save cache failure evicts graph entries and emits no event.
Caller mutations made before admission, shared mutable child objects, concurrent writers and
external sidecar files are outside that database transaction. No deep snapshot or crash-recovery
claim is made.

Saver registration/execution moves to `IItemMetadataExporter`; the provider manager no longer
exposes SaveMetadataAsync. File exports pair watcher suppression/restoration; cancellation
propagates before catalogue persistence. Image localization is no longer a hidden full-item-write
side effect: refresh and image delivery use the image lifecycle explicitly. No new entity service
static is introduced. HTTP/OpenAPI and SDK shapes remain unchanged; .NET removals/signature
changes are intentional. The new real-host graph rollback/retry/update/promotion test is required
by name in CI. Validation results are recorded on the implementation PR, not assumed here.

Filesystem suppression is shared by the monitor and metadata exporter through the autonomous
`ILibraryWriteTracker`, without a new lazy edge to the library monitor. Overlapping writes keep
suppression until each one's 45-second quiet period expires. The existing DI exception guard
remains unchanged.

## 28. Credit and person write ownership

`IItemPeopleCommands` replaces credits and materializes person records through `IItemWriteCommands`.
The synchronous manager bridge, asynchronous manager entry point and private person-save loop are
removed. All active callers await the dedicated port, including audio/video probing and album
refresh. New people produce one catalogue write and one added event, rather than two; existing
people publish updates through the shared writer. Provider-id dictionaries and image arrays are
copied before mutation so a failed item write does not change the published person. Person path
construction has explicit filesystem/root inputs, sharing the policy with active named-item reads.
Cancellation is checked before mapping replacement and between people. Mappings and people remain
separate commits; there is no cross-record or filesystem/SQL rollback guarantee. HTTP and OpenAPI
shapes remain unchanged. The real-host creation/update/clear journey is required by name in CI.

## 29. Linked item mutation ownership

`UpsertLinkedChild` and `RerouteLinkedChildReferencesAsync` are removed from `ILibraryManager`
and its implementation without forwarding members. The existing `IItemWriteCommands` owns
both operations and the manager no longer injects `IItemMetadataExporter`. Callers in scan
reconciliation, local alternate preparation, version grouping and deletion promotion use that
write port. Read-side alternate queries remain active on the manager.

Rerouting updates manual references and removes collisions in one database transaction. Its
cache reconciliation reloads the persisted ordered links, repairing stale arrays and preserving
nonmanual links before sidecar export; all parents are repaired before the first cancellable export. Cancellation before
admission writes nothing; after SQL commit it cannot undo the link changes. Metadata files are
outside that transaction. Upserts invalidate the parent lookup entry after a successful save.
A real-host trigger rejects the second SQL mutation to prove rollback, then a retry checks the
database, warmed parent caches, duplicate elimination, shortcut isolation and source bytes.
The journey is required by name in CI. HTTP/OpenAPI shapes are unchanged.

## 30. Playback intro selection ownership

`IPlaybackIntroService` replaces `LibraryManager.GetIntros`, its private provider/error loop and
path resolver. `ILibraryManager.AddParts` no longer accepts intro providers; the composition root
registers them with the dedicated service. The HTTP controller passes `RequestAborted`. The
provider contract is now `GetIntrosAsync(item, user, cancellationToken)` without a compatibility
bridge. The service depends on explicit access, resolution, filesystem and logging ports.

Providers run concurrently; registration order and candidate order determine playback order.
Each provider's enumerable is materialized inside its exception boundary, so deferred failures
cannot break another provider or leak a partial provider result. Filesystem resolution runs before
the service returns, not during DTO enumeration. Candidates are never saved. ID and path
references both resolve to persisted videos through the requesting user's access policy; hidden,
missing and nonvideo entries are excluded. Visibility is checked again on every request.
Request cancellation is cooperative and propagates instead of becoming an empty successful list.

The required real-host HTTP journey exercises ID/path resolution, saved metadata, provider failure,
unpersisted and nonvideo candidates, and visibility changes between requests. Unit tests cover
ordering with reversed provider completion, request registration snapshots, deferred failures,
eager resolution and cancellation. No playback byte-serving, episode intro detection, HTTP or
OpenAPI contract change is included in this slice.

## 31. Named metadata reads and explicit materialization

`LibraryManager.CreateItemByName`, GetArtist/GetStudio/GetGenre/GetMusicGenre/GetYear and their
named ID helpers are removed, including the manager's last general writer dependency.
`INamedItemCatalog.Find<T>` returns existing metadata; `GetId<T>` computes an identity without
creating a directory or row. Artist reads prefer actual folder records over named metadata and
use an ID tie-break. The existing batch artist read remains available to album entities; their
getters and lookup-info construction no longer create an artist. Genre views use read queries.

`IItemWriteCommands.GetOrCreateNamedItem<T>` is the explicit materialization boundary used by
validators and album refresh. A singleton admission lock covers lookup, managed-directory
preparation and the normal persistence/cache/notification sequence for that command only. A
failed database write publishes no named item; retrying uses the same identity. Already existing
names produce no second write or event. The five supported kinds share path helpers with explicit
filesystem/root inputs, deterministic identity policy and year validation/canonicalization.

HTTP reads and image lookup no longer create missing metadata. Missing names return 404; the
contract declares that response for artist/studio/genre/music-genre detail operations. Valid
unknown artist/studio filters use the missing deterministic identity, keeping the filter
restrictive instead of clearing it. DTO references compute IDs without materialization. Named
metadata becomes available through explicit writes/post-scan validation; it is not created by a
read while a scan or validation is still incomplete. Years previously had only read-time creation;
a dedicated post-scan task now materializes distinct positive production years projected in SQL,
without loading every complete media item. No unused PostgreSQL/plugin abstraction is added.

Three required real-host journeys cover HTTP no-write behavior and explicit visibility, SQL
failure/retry with concurrent creation, album read isolation/folder precedence and year task
discovery/materialization. Unit tests cover all supported kinds, ID and path policy, cancellation,
unsupported kinds, publication and absence of manager/write dependencies on the read side.
Directory creation is outside the SQL transaction: an empty managed directory may remain after a
failed save. No global general-write/deletion serialization or cross-process guarantee is made.

## 32. Configured library read ownership

`ILibraryStructureReader` replaces both `LibraryManager.GetVirtualFolders` overloads, with no
forwarders. It reads definition directories, `.mblink` source locations, collection-type markers
and existing library metadata through explicit filesystem, path-mapping, root, lookup and options
ports. Invalid shortcut files remain isolated; locations retain their sorted order. Definitions
not yet present in the catalogue remain visible with no item metadata. Cached user-root IDs use
`GetLibraries(lookup)` rather than the static manager lookup.

Refresh state belongs to the HTTP composition: the controller gets one provider queue snapshot
and projects progress/Active/Queued/Idle only for known item IDs. Non-HTTP consumers receive no
refresh state and cannot cause provider resolution. The reader never calls the entity's progress
method. `LibraryManager` drops its final `Lazy<IProviderManager>` and its application-host dependency
along with 80 lines. Other existing deferred provider edges, including `UserViewFactory`, remain.

API, storage diagnostics, recording maintenance and the three library-reading migrations use the
reader directly. The existing structure/scan journeys migrate to this port, and a named required
HTTP read journey checks options, locations, repeated reads and unchanged definition/source bytes.
Focused tests cover malformed shortcuts, unscanned definitions, refresh-state projection and
removed manager/provider dependencies. No HTTP/OpenAPI or SDK shape change is intended. There is
no new snapshot-isolation guarantee across concurrent definition edits, scans or provider updates.

## 33. Person read and validation ownership

`IItemPeopleService.GetPerson` and `GetPeopleItems` replace the manager's person lookup and page
expansion, without forwarding methods. Person paths and IDs use explicit filesystem/configuration
ports and the existing identity policy shared with credit writes. Missing records stay missing.
Expansion preserves credit-page order, start and total metadata, skips absent or malformed records,
and passes an explicit visibility scope to the same entity rule. HTTP/OpenAPI shapes do not change.
DTO, person-image, Persons controller and NFO actor lookup use the dedicated read service.

`PeopleValidator` now owns preparation of the managed people directory and queries unreferenced,
unlocked person items directly through the repository. `PeopleValidationTask` injects the validator
and an item lookup, rather than the manager. The manager's validation bridge, private named-person
ID helper, people repository dependency and logger field are deleted: 64 lines removed, 1,206 remain.
Entity credit-list forwarding members remain for the separate entity-static migration.

Cancellation before admission creates no managed directory; later cancellation propagates between
entries/batches. Each entry, including a missing or rejected name, advances the first half of the
progress range. Successful orphan-deletion batches advance the second half by the actual number
of items, avoiding the old over-100 and asynchronous progress-report ordering problems. Completed
refreshes/deletions and directory creation are not rolled back by later cancellation or failure.

Focused tests cover lookup without materialization, page isolation/order/visibility, cancellation,
601 orphan records split 500/101, progress bounds and removed manager dependencies. A named required
real-host HTTP/maintenance journey covers missing person reads, credit creation, paging, repeated
reads, unlocked orphan deletion, locked-person retention and unchanged source-media bytes.

## 34. Shared parent and user query scopes

The manager's temporary copies of parent/user scope logic are removed. It now injects the existing
`IItemQueryScopeService`, replacing its direct `IUserViewCatalog` dependency, and delegates parent,
recursive, latest, next-up, count and named-metadata aggregate scoping to it. The former named-read
ancestor projection joins the same service, whose constructor dependencies remain unchanged.

Unknown ancestors still throw; inaccessible library/aggregate roots and scopes resolving to no
physical libraries still use a nonempty sentinel. Ordinary ancestor filters remain intact. Linked
playlist/collection children retain per-child access filtering. User views, external-content flags,
grouped folders and top-parent traversal use the existing shared implementation; top-parent lookup
there uses the explicit lookup port rather than entity manager statics.

The manager keeps its existing compound playlist/collection refusal exclusion verbatim and invokes
it after shared user scoping. There is no new manager/repository cycle, lazy registration, HTTP or
OpenAPI shape change. The manager loses 213 lines and is now 993 lines. Existing characterization
fixtures compose the real scope service over their existing ports instead of an auto-mock.

New tests cover visible view translation, hidden library/aggregate sentinels, mixed ordinary
ancestors, unknown ancestors, empty libraries and removed manager copies. A named required real-host
journey compares visible/withheld library lists, totals, counts and filter values, then checks the
HTTP listing. The broader inherited authorization journeys remain active. No new snapshot isolation
or per-query compound-filter performance claim is made.

## 35. Owned bonus discovery and refresh ownership

The manager's `FindExtras` entry point and private resolver overload are deleted. Its bonus resolver,
naming, directory-service and ignore-rule constructor dependencies move into the existing filesystem
resolution service. The latter exposes `FindExtras` for trailers, special features and theme media;
existing naming cases migrate to that port. Persisted bonus records are copied before classification,
so merely discovering an extra cannot change its published type or mixed-folder flag.

`IItemWriteCommands.RefreshExtrasAsync` owns catalogue reconciliation and calls its new shared
`RefreshOwnedMetadataAsync` for inherited title metadata and refresh. `BaseItem` loses its private
extra lifecycle and owned-metadata helper; its and `Video`'s remaining owned-video paths call the
explicit writer directly. The manager loses 99 lines (894 remain), and BaseItem loses 128 net lines.
No new entity static, metadata-provider constructor edge or manager compatibility bridge is added.

Reconciliation compares only actual extras; non-extra owned versions do not trigger a spurious save
and are never candidates for removal. Repeated discovery IDs are refreshed once. Staging completes
before asynchronous refresh admission; all started tasks are awaited even when a later refresh
observes cancellation. Catalogue-only removal preserves original sources. Inherited metadata arrays
are copied, caller options stay unchanged, and owned-refresh cancellation propagates instead of being
logged as a successful owner refresh. Previously completed deletes/refreshes and sidecar writes are
not rolled back after later failure. There is no new global refresh lock or snapshot-isolation claim.

Focused tests cover detached discovery, unchanged extra sets with alternate versions, inheritance,
failed refresh publication, cancellation/admission/draining and ownership removals. A named required
real-host journey rejects a new trailer row with SQLite, proves retry, verifies LocalTrailers and
SpecialFeatures over HTTP, replaces catalogue extras, preserves alternate metadata and checks every
source byte. Metadata fetches are disabled in that deterministic lifecycle journey. HTTP/OpenAPI and
SDK shapes are unchanged; the .NET interface additions/removals are intentional.

## 36. Owned multipart and local-version refresh

`Video` no longer prepares, creates, cleans up or refreshes its additional parts and local alternate
versions. Its owned-item hook calls `IItemWriteCommands.RefreshOwnedVideosAsync` after bonus refresh.
The obsolete protected `BaseItem.RefreshMetadataForOwnedVideo` helper is also removed. Multipart
items keep generic `Video` identity; alternate items keep their primary's concrete type and existing
stack-discovery policy. Effective collection-type policy moves into `ILibraryItemOptions` and the
manager's existing read methods use that same policy. No new manager or provider dependency is added.

Admission deduplicates identities before asynchronous work starts. Persisted candidates and caller
options are copied before ownership, primary-version or inherited-metadata edits. Existing foreign
owner/primary relationships and self-ownership are rejected. New records are saved through the
existing graph writer; refresh uses a fresh copy of that published creation. The local-version link
is written only after its refresh succeeds. Nonexistent local paths are pruned only after all
admitted work completes; missing owned multipart rows use metadata-only deletion. All started tasks
are awaited on failure or cancellation. These are distinct operations, not a cross-item transaction:
initial creation, completed provider saves and obsolete-generic cleanup can survive a later failure.

Focused tests exercise type/identity, deduplication, title inheritance, option isolation, failed
ownership refresh/retry, foreign-owner refusal, missing parts and cancellation. A named required
real-host journey rejects a typed alternate insert, retries through the entity refresh entry point,
checks generic-obsolete removal, multipart metadata and HTTP sources, and compares every source byte.
Metadata downloads are disabled for the lifecycle proof. HTTP/OpenAPI/SDK shapes are unchanged.

## 37. Scan-time primary-version transfer

Folder validation retains discovery, accessibility and primary-change detection, but no longer
rewrites alternate ownership, clears version arrays or reroutes references itself. Explicit
`DemotePrimaryVersionAsync` and `ReplacePrimaryVersionAsync` commands use the existing graph planner
and bulk writer. They read the saved primaries and typed links through lookup/persistence ports,
without manager or provider dependencies. The scan no longer detaches a deferred old primary early.

Local alternates move to the new owner; linked alternates retain empty ownership. Duplicate IDs are
processed once and unrelated ownership/primary relationships are refused before export or save.
The new primary keeps both sets of version relationships; the old primary's outgoing lists clear in
the same graph save that moves ownership, preventing orphan cleanup from deleting transferred rows.
Caller snapshots and published cache entries stay unchanged on failed graph persistence. Bulk graph
updates do not propagate one version's descriptive metadata over the others.

Manual playlist/collection references are rerouted after that save. A removed old primary keeps a
successor ID while pending deletion, allowing a full subsequent scan to finish reference rerouting
through the deletion command if the separate reroute fails. Catalogue-only deletion preserves source
media. Initial scan creation, sidecar export, graph persistence, reroute and deletion are distinct
operations; no whole-scan transaction, global serialization or snapshot isolation is introduced.

Focused tests cover one-batch publication, failure/retry, local versus linked ownership, captured
parent, reroute failure, invalid identities and cancelled admission. A required real-host journey
controls only discovery inputs: it runs the actual folder validation and application services,
rejects a graph update with SQLite, retries demotion, rejects reference rerouting, retries the full
replacement scan, checks HTTP media sources/playlist references and compares all original bytes.
HTTP/OpenAPI/SDK shapes are unchanged.
