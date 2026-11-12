# Playback path inventory

Status: consolidation programme, step 1 baseline (#129, owner #305). Measured on server `master`
`ac6570e75f` and Web `main` `e7a33187de`, 2026-11-12. Update this file in the same change that
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
             DirectPlayFilmPlanner: every candidate source is a local file -> the engine decides
             from the DECLARED capabilities/constraints (no DeviceProfile round trip);
             DirectPlay + executable plan -> V2PlanRecord published, PlaybackPlan(DirectPlay),
             StreamInfo = null; StreamBuilder is never constructed
  -> GET /Playback/Sessions/{id}/Stream    DirectPlayFileDescriptorMapper, from the retained plan
  -> GET|HEAD /Playback/Sessions/{id}/File?itemId&mediaSourceId&playSessionId
     PlaybackSessionFilesController: MediaDelivery + Media capability; per request: session
     exists, caller owns it, item/source/play session match the plan, owner still sees the
     item; serves the retained source with range support. The client names no file.
  -> subtitles: /Videos/{item}/{source}/Subtitles/{index}/0/Stream.{vtt|ass} + Subtitles capability
  -> stop: DELETE /Playback/Sessions/{id} (player `stopped` event); resume: client seek to the
     stored position (the file is served verbatim)
```

Any other decision for a film (remux, transcode, 422) releases its session and continues on the
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

Remaining for the film journey itself (later journeys, not this slice): audio/subtitle track change
during playback re-plans through the legacy `changeStream`; image subtitles are not offered on a
direct play; `/Sessions/Playing*` reports still go through `jellyfin-apiclient` (step 3).
