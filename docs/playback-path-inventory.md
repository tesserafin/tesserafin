# Playback path inventory

Status: consolidation programme, step 1 baseline (#129, owner #305). Measured on server `master`
`ac6570e75f` and Web `main` `e7a33187de`, 2026-11-12. Update this file in the same change that
removes or adds a path; the architecture check `ci/architecture-ratchet.sh` holds the
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

## 4. Loop 1 slice: direct-play film

Target chain:

```
Web (video item) -> POST /Playback/Sessions (generated SDK)
  server: engine decides FIRST; DirectPlay with an executable plan -> session planned
          from the v2 decision only, no StreamBuilder call, Plan.StreamInfo = null
  -> GET /Playback/Sessions/{id}/Stream -> descriptor minted from the execution plan
     (no StreamInfo), URL = Sessions-owned file delivery of the selected source
  -> Web builds its player stream from the descriptor alone (no PlaybackInfo, no
     createStreamInfo, no overlay)
  -> stop = DELETE /Playback/Sessions/{id}; resume = startTimeTicks on GET Stream
```

Every other decision (remux, transcode, audio, Live TV) keeps today's chain and the global flag.

Code deleted when the slice closes (named before implementation):

- Server: the DirectPlay branch of `PlaybackExecutionPlanAdapter.ToStreamInfo` and its tests, and
  the legacy fallback for an engine-decided direct-play session in `PlaybackLiveStreamResolver`
  (kill switch, stop threshold and source-mismatch fallbacks no longer apply to it).
- Web: the DirectPlay handling of the V2 URL overlay (`applyResolvedV2ResultToStreamInfo` DirectPlay
  case and the pre-#46 `directPlayMimeType` fallback) and the PlaybackInfo / `createStreamInfo`
  route for a film the server direct-plays.

Proof required on a real server: start, stop (session and capability gone), resume at a stored
position, and per-user isolation (another user can neither read the descriptor nor stop the session).
