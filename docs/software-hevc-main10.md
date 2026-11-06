# Software HEVC Main10

The server enables software HEVC Main10 only after the configured FFmpeg has
successfully encoded a strict ten-bit test frame during startup. Listing
`libx265` or reporting a particular FFmpeg version is insufficient. The probe
uses the existing 20-second process runner, drains both pipes, and treats a
timeout, nonzero exit or process exception as unsupported. `+yuv420p10le`
disallows FFmpeg's automatic fallback to an eight-bit encoder format.

`ISoftwareVideoEncoderCapabilities` is an optional interface alongside
`IMediaEncoder`; existing external implementations without this capability
remain at Main. The capability resets when the configured runtime is validated
again. It does not add a client capability or enable HEVC encoding globally.

## Selection policy

The first requested HEVC profile determines the preference, as before.
Whitespace and case in `Main 10` are normalized. Codec-qualified options
(`hevc-profile`, `hevc-videobitdepth`) use the existing request parser;
`MaxVideoBitDepth` takes precedence over the codec-qualified depth.

| Request and selected encoder | Output |
| --- | --- |
| `main10`, depth ceiling at least 10 or absent, probed `libx265` | Main10, `yuv420p10le` |
| Explicit ceiling below 10 | Main, eight bits |
| Missing profile, `main`, or `main` first in a profile list | Main, eight bits |
| Runtime without a successful Main10 probe | Main, eight bits |
| Hardware HEVC encoder | Existing Main behavior |
| Dolby Vision reshaping path (`VideoRangeType.DOVI`) | Main, existing eight-bit intermediate |
| Stream copy | Source profile, unchanged |

The command and the HLS master playlist share `GetHevcOutputProfile`. The
playlist resolves the effective encoder options too, including an active
hardware-to-software recovery. It advertises Main10 only when that selected
software attempt can produce it. Existing hardware encoders are not qualified
for Main10 by this change.

The software filter chain selects ten-bit planar output for Main10 scaling and
`tonemapx`, and explicitly sets the graphical subtitle overlay to
`format=yuv420p10`. HDR10 sources still follow the existing SDR tone-mapping
policy. A ten-bit output is not a claim of HDR passthrough, Dolby Vision
preservation, or improved precision when the source was eight bits.

## Reproduce the server proof

Build the Release server and use an actual Main10-capable F0 runtime with its
sibling `ffprobe`. The output directory must not already exist. The script
requires Linux `/proc` and a local environment able to launch the native server.

```sh
dotnet build Tesserafin.Server/Tesserafin.Server.csproj -c Release
python3 tools/playback-proof/hevc_main10.py \
  --server Tesserafin.Server/bin/Release/net10.0 \
  --ffmpeg /path/to/f0/bin/ffmpeg \
  --out /tmp/tesserafin-main10-proof
```

The script generates its own SDR, HDR10 and PGS media, onboards a disposable
server, disables hardware encoding, and makes real HLS requests. It checks the
master's codec string against the served fMP4 bytes using the supplied runtime,
fully decodes those bytes, checks the PGS rectangle in decoded pixels, requests
a distant segment and verifies FFmpeg restarted with `-ss`, then stops every
live job and checks its process is gone. HLS jobs are paced with `-readrate 1`
so stop assertions exercise a live process. Startup probes are not paced.

`results.json` records the server/runtime hashes, observed codec/profile/pixel
format and color transfer, running FFmpeg arguments, subtitle pixel measurement,
and process counts before and after stop. Tokens are not printed. The native
server log and fetched fragments stay in the disposable output directory. This
is an HTTP/decoder proof, not browser, television or GPU-device acceptance.

## Recorded validation, 2026-11-06

[Measured result](software-hevc-main10-proof.json) records this change on a native
Linux x64 server with the x64 F0 artifact from #397, run `42274991467`, commit
`6be32dec64ac434bad62c5b1aaa8255761ce368f`. It includes the tested source and
assembly hashes and fetched-fragment hashes.

| Scenario | Served output | Additional assertion |
| --- | --- | --- |
| SDR, request Main10, ceiling 10 | Main 10 / `yuv420p10le` | HLS advertises Main10 |
| SDR, request Main, ceiling 10 | Main / `yuv420p` | HLS advertises Main |
| SDR, request Main10, ceiling 8 | Main / `yuv420p` | Ceiling overrides profile |
| SDR, request distant segment 25 | Main 10 / `yuv420p10le` | Restart with `-ss 00:00:50.000`, start number 25 |
| HDR10, request Main10 | Main 10 / `yuv420p10le` | Output transfer is BT.709 |
| PGS burn-in, request Main10 | Main 10 / `yuv420p10le` | 100% white pixels in the sampled subtitle rectangle |

All six cases fully decode, return HTTP 204 on stop, and go from one live FFmpeg
process to zero. The 21 new unit cases pass. The API suite passes 638 tests;
Controller passes 398 with two skips. MediaEncoding passes 295 with two skips
and one failure: the unchanged process-runner shell timeout test takes 30 seconds
instead of less than ten, also reproduced in isolation here. It is not waived
or changed. Release server build has zero warnings/errors. Changed-file
whitespace verification passes; full MSBuild formatting cannot run locally
because its IPC socket is denied, so the hosted Format job remains relevant.

The runtime component is tracked separately in #397 and the distribution work
in #342; this server change does not replace accepted runtime digests or publish
an image.
