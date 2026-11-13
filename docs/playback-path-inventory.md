# Playback path inventory

Status: consolidation programme, step 1 (#129, owner #305). Baseline (§1–§3) measured on server
`master` `ac6570e75f` and Web `main` `e7a33187de`, 2026-11-12; §4–§6 describe what has been
delivered since. Update this file in the same change that
removes or adds a path; the architecture check `tests/Tesserafin.Server.Tests/Architecture/ConsolidationRatchetTests.cs` (baseline `consolidation-ratchet-baseline.json` beside it) holds the
named exceptions and may only shrink.

## 1. Server chain at the 2026-11-12 baseline

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

Baseline conclusion: in every mode a call to `/Playback/Sessions` still runs the legacy decision
(`StreamBuilder`) and still executes through the legacy `StreamInfo` and the legacy streaming
controller. Mode V2 only replaces the URL that `StreamInfo` produces.

## 2. Web chain at the 2026-11-12 baseline

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

At the baseline no Web journey was v2-authoritative: with the flag off nothing calls Sessions; with it on, the legacy
`PlaybackInfo` decision still runs first and v2 only patches its result.

## 3. Legacy paths, consumers and removal conditions

| Legacy path | Consumers at the baseline | Removal condition |
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

A remux the server executes from the session is §5. Any other decision for a film (transcode, a
remux outside §5, 422) releases its session and continues on the existing chain with direct play
disabled, so a film never reaches the legacy direct-play path.
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

Outside this slice: a remux over plain HTTP (a client with no HLS output profile), a remux that
needs an audio transcode, transcoding, other media families, the global flag and the shadow.

## 6. Local SDR film transcoding (candidate)

The film planner also owns HLS Transcode plans whose encoded axes are H.264 video and/or AAC
audio, without HDR conversion or subtitle burn-in. DirectPlay still wins over Remux, which wins
over Transcode when selecting among alternate versions. Text subtitle handling is unchanged.

The descriptor and byte routes are the same session-owned routes as §5. `SessionHlsRequest`
projects the retained plan into the shared FFmpeg executor. `StreamingHelpers` skips legacy
copy/encode and resolution heuristics for this request type. Selected stream indices, current
encoding permissions, source range and copy feasibility are checked before a job can start;
an unexecutable plan answers 422. Hardware encoder selection and software fallback remain in
the existing execution machinery. A combined bitrate ceiling reserves the selected audio's
budget before setting the video's ceiling; unknown copied bitrates cannot satisfy a known ceiling.

The Web film module accepts a Transcode descriptor only for its own session HLS route. Track,
quality and error recovery therefore stay on Sessions for this scope. The previous unconditional
Transcode-decision release and PlaybackInfo fallback is gone; a descriptor outside this scope
still identifies an unmigrated journey and is released before using the existing chain.

Validation lives with the implementation: `Film_Transcode_ExecutesOnlyThePlannedAxesAndCleansUp`
uses the legacy-free real host, FFmpeg and ffprobe for audio-only encoding, video-only encoding,
and both axes. It verifies actual output codecs/resolution/channels, copy flags, current
permissions, session teardown and eventual output cleanup. The hosted integration job requires
this theory by name. The manager test proves a decode-error recovery into a Transcode never
calls PlaybackInfo or createStreamInfo.

Remaining consumers: HDR/tone mapping, burn-in, unsupported output codecs/protocols, non-film
video, audio, Live TV and PlaybackInfo. Shared legacy decisions are not removed globally while
those consumers remain. Steps 1 and 2 stay open. This candidate depends on the credential-free
server descriptor contract in #409 and the Android authentication change in mobile#21.
