# Live TV: handing the tuner stream to ffmpeg over stdin

Frozen internal contract for `#153-LTV-S0`.

## The defect

`SharedHttpStream.Open` opens the tuner's HTTP stream, starts copying it into a temp file, and
then publishes a *different* URL as the media source path:

```csharp
MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
MediaSource.Protocol = MediaProtocol.Http;
```

`LiveTvController.GetLiveStreamFile` — the endpoint that serves that path — carries `[Authorize]`.

When a client asks for `/videos/{id}/live.m3u8`, `EncodingHelper.GetInputArgument` used to turn
that path into ffmpeg's `-i` argument, and `TranscodeManager.StartFfMpeg` launched ffmpeg with it.
ffmpeg is a child process. It has no session, no api key, no user, and no way to acquire one. Its
fetch is anonymous, the endpoint answers **401**, ffmpeg exits with **code 8**, and the playlist
request answers **500**. Measured on the real rig against server `c0f39e07aa`:

```
http://192.168.0.234:8096/LiveTv/LiveStreamFiles/2db848437faf4a19b756d9d7425c4b50/stream.ts:
    Server returned 401 Unauthorized (authorization failed)
```

Probing fails the same way, so the tuner source reports `Codec: null` / `Index: -1` and direct
stream is unselectable too. `master.m3u8` still answers 200, which is why the defect looks like a
playback bug rather than an authorization one.

## Why this is transport, not authorization

The fix is **not** to open the endpoint. `/LiveTv/LiveStreamFiles/**` keeps its `[Authorize]` and
its external behaviour byte-for-byte; the routes are still refused anonymously and still refused to
a user without permission.

The server is already holding this live stream open, in this process, on behalf of an
**authenticated** request that opened it. Handing ffmpeg a file descriptor to read is the same
trust boundary that every non-live transcode already crosses when the server hands ffmpeg a path to
a library file. Nothing is minted, transmitted, or persisted:

- no ticket, token, api key, session or capability is created;
- nothing is added to ffmpeg's argv, environment, or headers;
- `UniqueId` and `LiveStreamId` do not become credentials — they are not sent anywhere new;
- there is no loopback, IP, `Host`, proxy or `X-Forwarded-For` exemption anywhere;
- there is no HTTP fallback: if the pipe fails, the job fails.

The authorization decision was taken once, by the request that opened the live stream. The pipe
carries only the bytes that decision already authorized.

## The contract

When `StreamState.DirectStreamProvider` is not null:

1. **The ffmpeg input is `pipe:0`**, never the `/LiveTv/LiveStreamFiles/**` URL.
   `EncodingHelper.GetInputArgument` selects it on `state is StreamState { DirectStreamProvider: not null }`.
2. **Each ffmpeg process gets its own `GetStream()` call.** `TranscodeManager.StartFfMpeg` calls it
   once per process, so two concurrent consumers get two independent readers over the tuner's temp
   file and never share a `Stream` instance.
3. **The stream is copied to `process.StandardInput.BaseStream`** by `DirectStreamPump`, wrapped in
   `ProgressiveFileStream` so a temporary end-of-file on the still-growing tuner file is a wait, not
   an end.
4. **Producer, stdin and the pumping task have one owner and a deterministic end.** The pump owns
   both streams and disposes them in its own `finally`. The `TranscodingJob` owns the pump.
5. **Stop, cancellation, ffmpeg exit and live-stream close all stop the pump with no orphan.**
   `TranscodingJob.Stop` awaits `DirectStreamPump.StopAsync()` *before* stopping the process — for a
   piped job, closing stdin *is* the graceful stop, because ffmpeg then sees EOF and finalizes its
   output. `TranscodingJob.Dispose` is the backstop for `OnFfMpegProcessExited`.
6. **An early stdin close is neither a hang nor an unobserved exception.** A write that fails with
   `IOException`/`ObjectDisposedException` ends the pump normally. `DirectStreamPump.Completion`
   never faults.
7. **A producer failure before first output fails the job explicitly.** The read-side exception is
   recorded on `DirectStreamPump.Fault`, and `StartFfMpeg`'s wait-for-output loop turns it into an
   `FfmpegException` naming the live stream instead of an anonymous ffmpeg exit code.
8. **Paths without a provider are behaviourally unchanged.** The `else` branch is the original
   `" -i " + _mediaEncoder.GetInputPathArgument(state)`, reached by every `EncodingJobInfo` that is
   not a `StreamState` and every `StreamState` without a provider.

### One non-obvious consequence

ffmpeg only interprets stdin as a keyboard when stdin is not one of its inputs. `TranscodeAttempt`
therefore must not write `"q"` to a piped job — the byte would be muxed into the media as garbage
and would stop nothing. `TranscodeAttempt.StandardInputIsMediaPipe` selects the close-then-kill stop
instead.

### What still constrains it

- Throttling is unaffected: `EnableThrottling` requires `InputProtocol == MediaProtocol.File`, and a
  live stream is `Http`, so `TranscodingThrottler` never writes its pause/resume keys into a pipe.
- `LiveStream.GetStream()` seeks backwards 20000 bytes when the stream has been open more than ten
  seconds. That offset is not a multiple of 188, so a late consumer's first TS packet is partial;
  ffmpeg resynchronizes. It is why a second start/stop cycle need not be byte-identical to the
  first.

## Inventory

`ci/livetv-handoff-inventory.sh` asserts that all seven cooperating places still exist:
`SharedHttpStream.Open` → `MediaSource.Path` → `StreamingHelpers.GetStreamingState` →
`DirectStreamProvider` → `EncodingHelper` → `TranscodeManager.StartFfMpeg` →
`LiveTvController.GetLiveStreamFile`. It fails if any category becomes empty.


## General media-info probing

`MediaEncoder.GetMediaInfo` captures `pipe:0` when a direct reader is present and delegates
interpretation/lifetime to `MediaProbeService`. `IFfmpegProcessRunner.RunMediaProbeAsync`
opens the progressive reader once after launching ffprobe and owns the reader, stdin pump,
concurrent stdout/stderr drains and native shutdown. There is no native probe wrapper left
in the encoder and no HTTP retry if input fails. Existing HTTP/RTSP flags are omitted on pipes.

A normal child exit stops an unfinished progressive read immediately. Producer failure stops
a child that ignores EOF and preserves the source exception. Timeout (two-minute native limit),
caller cancellation and encoder shutdown kill/wait/drain and join the pump before returning.
A result is interpreted only after successful native exit; valid JSON from a nonzero exit is
rejected. Probes and image work share operation registration, and encoder disposal begins
cancelling both before awaiting their cleanup. Reads must cooperate with cancellation.
