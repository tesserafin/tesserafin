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
