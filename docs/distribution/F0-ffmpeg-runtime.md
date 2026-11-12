# F0 — the Tesserafin FFmpeg runtime

Issue [#229]. This document is the distribution contract for the FFmpeg runtime
that Tesserafin's native Linux packages will carry: what it is built from, what
is inside it, what it is licensed under, what it can be claimed to do, and — as
importantly — what is deliberately absent.

It exists because Tesserafin cannot redistribute someone else's binary. PR #228
bundled the upstream `jellyfin-ffmpeg` portable build; independent acceptance of
that PR found it reports GPLv3-or-later while the package claimed
GPL-2.0-or-later, that it is configured with `--enable-libfdk-aac`, that no
licence text or corresponding source ships with it, and that its VAAPI path
aborts on two of the three declared lifecycle environments. None of that is
fixable by wrapping the binary in a notice.

Nothing here publishes anything. There is no release, no channel, no signing key
and no repository.

---

## 1. Source baseline

| Input | Value |
| --- | --- |
| Project | `jellyfin/jellyfin-ffmpeg` |
| Release baseline | `v7.1.4-3` |
| Resolved commit | `d4590e12452f94d40e413caecb34b08de608353b` |
| Commit signature | GPG **verified** |
| Tesserafin build revision | `7.1.4-tesserafin.1` |

Stable only. Jellyfin-FFmpeg 8.1 prereleases, FFmpeg 9.0, nightlies and moving
branches are out of scope; FFmpeg 9.0 is a separate migration.

**`v7.1.4-3` is a lightweight tag.** The tag-object endpoint returns 404 and the
ref points straight at a commit, so there is no signed *tag*. The immutable
anchor is the commit SHA, and that commit is itself GPG-verified. Recorded as a
deviation from "resolve the signed tag", not treated as a blocker: a commit SHA
is content-addressed and cannot be moved under us.

### Why the fork rather than upstream FFmpeg 7.1.4

`Tesserafin.MediaEncoding/Encoder/EncoderValidator.cs` lists `tonemapx` among its
required filters, and `tonemapx` exists only in the Jellyfin tree. The fork is
therefore the baseline. The fork-versus-upstream patch delta is shipped as an
inventory in the corresponding-source artifact rather than cherry-picked, so the
baseline stays the named one instead of becoming something this project invented.

### Why upstream's builder is replaced rather than reused

`builder/` cannot be pinned:

* `builder/images/base/Dockerfile` is `FROM ubuntu:noble` — a moving tag —
  followed by `apt-get dist-upgrade`;
* `builder/images/base-linux64/Dockerfile` clones **crosstool-NG from `master`
  with no ref** and builds an entire toolchain;
* it clones **`Implib.so` from `master` at `--depth=1`**;
* only the ~40 `builder/scripts.d/*` component scripts carry a `SCRIPT_COMMIT`.

A digest-pinned builder environment and bit-for-bit reproducibility are both
required here, and neither is reachable from that base.

`Implib.so` matters for a second reason. `builder/scripts.d/50-vaapi/50-libva.sh`
builds libva from a recent commit and then runs `gen-implib` over it, replacing
the library with a trampoline stub that `dlopen`s the *host's* `libva.so.2` and
resolves symbols with `dlsym`. When the host libva is older than the one the
stub was generated from, the missing symbol becomes `assert(0)`:

```
implib-gen: libva.so.2: failed to resolve symbol 'vaMapBuffer2' via dlsym
ffmpeg: libva.so.2.init.c:290: _libva_so_2_tramp_resolve: Assertion `0' failed.
```

Tesserafin links libva normally, from a pinned old release. That is the fix.

---

## 2. Licence shape

The runtime is **GPL-3.0-or-later**, and it says so.

`--enable-gpl` is required by x264 and x265, both GPL-2.0-or-later.
`--enable-version3` is required because the closure contains OpenSSL 3, which is
Apache-2.0 — GPLv3-compatible, GPLv2-incompatible. OpenSSL provides the `https`
protocol, which Tesserafin needs for remote media sources, so the choice is
version3 or no TLS. It is version3, stated rather than inherited.

Tesserafin's own server code is GPL-2.0. That is not a conflict: the server
*invokes* FFmpeg as a subprocess, so the two are aggregated, not combined. What
was wrong in #228 was not the GPLv3 — it was a package claiming
`GPL-2.0-or-later` while shipping GPLv3 material with no notices and no source.

Permitted licences for a component, enforced by
`ci/ffmpeg/verify-components.sh`: Apache-2.0, BSD-2-Clause, BSD-3-Clause,
BSD-3-Clause AND AOM-Patent-License-1.0, FTL OR GPL-2.0-or-later,
GPL-2.0-or-later, ISC, LGPL-2.0-or-later, LGPL-2.1-or-later, MIT, WTFPL, Zlib.
Anything else stops the build until it is classified. There is no prose
allowlist.

### Hard exclusions

* `libfdk_aac` — FFmpeg classifies it nonfree. FFmpeg's native AAC encoder
  covers the requirement. `--disable-libfdk-aac` is stated explicitly so that
  removing it shows up in a diff.
* `--enable-nonfree` — stated as `--disable-nonfree` for the same reason.
* Any component whose licence is not in the permitted set.

Excluding libfdk-aac does not change server behaviour.
`EncoderValidator.GetCodecs()` computes `found = ffmpeg ∩ _requiredEncoders`: the
list is an intersection filter, not a mandatory set, so a component that is
absent is simply never advertised. `_requiredEncoders` does name `libfdk_aac`,
which is exactly why this needed checking rather than assuming.

---

## 3. Capability requirement

Derived from `EncoderValidator._requiredEncoders`, `_requiredDecoders` and
`_requiredFilters` — what the server actually looks for — not from what
Jellyfin's builder happens to include.

| Requirement | Provided by |
| --- | --- |
| H.264 software encode | x264 |
| HEVC software encode | x265, static multilib Main (8-bit) + Main10 (10-bit) |
| AV1 software encode | SVT-AV1 |
| AV1 decode | dav1d |
| VP8/VP9 decode | libvpx |
| H.264/HEVC/MPEG-2/MPEG-4 decode | native |
| AAC encode and decode | **native** — deliberately not libfdk-aac |
| AC-3, AC-4, ALAC, DCA, FLAC, TrueHD | native |
| MP3 encode | LAME |
| Opus, Vorbis | opus, libvorbis (+ libogg) |
| `zscale` | zimg |
| `tonemapx`, `alphasrc` | native (fork patch) |
| Subtitle burn-in | libass + freetype + fribidi + harfbuzz + fontconfig + libunibreak |
| SubRip (`srt`) encode | native — *not* libsrt, which is a transport |
| HLS segmenting, image extraction, `ffprobe` JSON | native |
| VAAPI encode/decode/filters | libva 2.24.1 (bundled) + libdrm |
| QSV | libvpl |
| NVENC/NVDEC/CUVID | nv-codec-headers |
| `scale_cuda`, `tonemap_cuda`, `overlay_cuda`, `transpose_cuda` | nv-codec-headers **+ `--enable-cuda-llvm`** — the kernels are `.cu` files gcc cannot compile, and `--enable-cuda` alone silently omits every one of these filters |
| AMF | AMF headers |
| `*_opencl` filters | OpenCL headers |

### Software HEVC bit depth (linux, #342)

`build-x265.sh` builds the pinned x265 3.6 twice: a namespaced Main10 library,
then an 8-bit library with `LINKED_10BIT`. GNU ar combines the two static
archives, retaining duplicate member names. FFmpeg links that single library;
no new shared library, source component or Windows recipe is introduced.
The 12-bit path is not added by this change.

`verify-hevc-depth.sh` is run by the runtime gate before packaging and by
`accept-runtime.sh` on every declared distribution, on each native architecture.
It generates its own lossless FFV1 10-bit source, separately encodes Main and
Main10, checks codec/profile/pixel format with the runtime's ffprobe, and fully
decodes both outputs. The Main10 HLS/fMP4 case decodes a later independent
fragment with its init segment and compares its first two frames with linear
playback at that offset. A successful encode that silently drops to 8 bits
must fail; `ffmpeg-controls.sh` exercises that mutation.

These are runtime/packaging checks, not evidence that a browser or Tesserafin's
server selected and played Main10. Real-server evidence belongs to the media
bench. This Linux runtime change requires new measured accepted digests and a
matching `F0_ACCEPTED_CI_TREE` before an image can adopt it; the revision shared
with the unchanged Windows runtime remains `7.1.4-tesserafin.1`.

### Vulkan and libplacebo (linux, #342)

`--enable-vulkan`, `--enable-libshaderc` and `--enable-libplacebo` are in the
linux-x64 and linux-arm64 flag files, so the runtime lists `libplacebo` (named
by `_requiredFilters`) and the `*_vulkan` filters and encoders the container's
`.deb` has. The toolchain is pinned in `components.json`: Vulkan-Headers
v1.4.337 (headers and registry only), shaderc v2024.2 built with glslang 14.3.0,
SPIRV-Tools v2024.3 and SPIRV-Headers into one static `shaderc_combined`, and
libplacebo v7.360.1 built by a pinned meson 1.4.1 (the bullseye builder's meson
is 0.56; libplacebo needs 0.63). jellyfin-ffmpeg's newer shaderc pins need cmake
3.22; the builder has 3.18.

Neither FFmpeg nor libplacebo (`-Dvk-proc-addr=disabled`) links the Vulkan
loader: both load the host's `libvulkan.so.1` at run time, so `DT_NEEDED` is
unchanged and a host without Vulkan gets a failed device creation, not a
missing-library abort. Rehearsed on an AMD host: an HDR10 → SDR
`libplacebo=tonemapping=bt.2390` run on a Vulkan device completed cleanly.

The build revision stays `7.1.4-tesserafin.1`. It is shared with win-x64,
whose MSYS2 lock and signed build-input bundle derive from it, and the Windows
runtime does not change here; the linux bytes are identified by
`ci/package/f0-accepted-digests.txt` and `F0_ACCEPTED_CI_TREE`. win-x64 has no
Vulkan or libplacebo yet.

Rockchip (`rkmpp`, `rkrga`) is outside the declared `linux-x64`/`linux-arm64`
matrix. macOS paths (`aac_at`, `*_videotoolbox`, `scale_vt`) do not apply.

---

## 3a. The fork's patch series

The jellyfin-ffmpeg checkout is **not pre-patched**. Its 95 changes live in
`debian/patches` as a quilt series and are applied during upstream's own Debian
packaging, which this build does not use. Configuring the checkout directly
produces plain FFmpeg 7.1.4 — no `tonemapx`, no `alphasrc`, none of the fork's
VAAPI/QSV/NVENC work — while every manifest still names the fork. The build
therefore applies the series itself, in series order, at zero fuzz, and then
asserts that `tonemapx` and `alphasrc` actually reached `libavfilter/allfilters.c`
before configuring anything.

**The whole series is applied except the unsafe class.** `v7.1.4-3` is the named
compatibility baseline; the 95 patches are what make it that baseline rather than
plain 7.1.4, and they are interdependent — patch 0057 (`tonemapx`) sits on
refactors introduced earlier. A cherry-picked subset would be neither the fork
nor upstream, which is exactly what §1 says the baseline must not become. What
the audit changes is not *which* patches apply but whether each one was looked
at: every patch is classified in `ci/ffmpeg/fork-patches.json`, and
`ci/ffmpeg/verify-components.sh` refuses a series entry with no classification.

| Class | Count | Meaning |
| --- | --- | --- |
| required | 50 | Tesserafin names the capability, or the patch is correctness for one it names |
| useful | 18 | A real improvement Tesserafin does not depend on |
| irrelevant | 26 | A platform outside the declared `linux-x64`/`linux-arm64` matrix |
| unsafe | 1 | Weakens a Tesserafin guarantee — **not applied** |

`required` is grounded in what the server source actually references, not in what
sounds important: `tonemapx`, `alphasrc`, `sub2video`, `hwupload_vaapi`,
`overlay_vaapi`/`_opencl`/`_cuda`, `tonemap_vaapi`/`_opencl`/`_cuda`,
`yadif_opencl`, `bwdif_opencl`, `vpp_qsv`, `remove_dovi` and `ac4` all appear in
`Tesserafin.MediaEncoding`.

`irrelevant` is almost entirely platform: `0009`, `0012`, `0013`, `0031` … are
Windows D3D11/DXVA, macOS VideoToolbox, the Windows executable icon, and the
Rockchip RK3588 pipeline. They are applied anyway, because they are part of the
named baseline and skipping them buys nothing.

**The one exclusion** is `0029-remove-fdk-aac-from-nonfree.patch`. It deletes
`libfdk_aac` from FFmpeg's own `EXTERNAL_LIBRARY_NONFREE_LIST`, so
`--enable-gpl --enable-libfdk-aac` stops being a configure-time error. Tesserafin
excludes FDK AAC and uses the native AAC encoder; keeping this patch out leaves
FFmpeg's upstream licence enforcement in place as a fourth independent layer
under the flag policy, the `-buildconf` scan and the encoder-listing scan —
rather than disarming it inside the very source tree those layers inspect. The
build asserts after patching that `libfdk_aac` is still in the nonfree list.

The exclusion set lives in `ci/ffmpeg/excluded-patches.txt` and must equal the
`unsafe` class exactly; the gate fails if the two disagree.

---

## 4. Architectures and the portability floor

`linux-x64` and `linux-arm64`. Both built on architecture-native hosted runners;
runtime acceptance is always architecture-native, never emulated.

The two architectures do not get the same configure flags. QSV (oneVPL) and AMF
are Intel and AMD x86-64 vendor stacks with no arm64 target — upstream's own
builder gates both on `[[ $TARGET == *arm64 ]] && return -1` — so they live in
`ci/ffmpeg/ffmpeg-configure.linux-x64.txt` and `components.json` marks
`libvpl` and `amf-headers` as `linux-x64` only.
`ci/ffmpeg/ffmpeg-configure.linux-arm64.txt` exists and is empty on purpose, so
that "arm64 adds nothing" is a decision with a place to change rather than an
absence. Everything else — VAAPI, NVENC/NVDEC/CUVID, OpenCL, and the whole
software codec set — is common to both.

**GLIBC floor: 2.34.** Measured on the declared environments — Rocky 9 = 2.34,
Debian 12 = 2.36, Ubuntu 24.04 = 2.39, Fedora 42 = 2.41 — so Rocky 9 sets the
ceiling. The builder is `debian:11` (glibc 2.31), pinned by digest, and the
highest `GLIBC_2.x` symbol referenced by the produced binaries is gated at
≤ 2.34.

**libva is bundled at 2.24.1 — the newest stable, deliberately not the oldest.**

None of the four declared images ships `libva.so.2` at all, so a hard
`DT_NEEDED` on the host's copy would make `ffmpeg` fail to load on every row of
the matrix. libva is therefore built from a pinned release, shipped in `lib/`,
and resolved through `RUNPATH=$ORIGIN/../lib`.

Given that it is bundled, it must be the *newest* stable rather than the oldest.
libva loads a VA driver by looking up `vaDriverInit_<major>_<minor>` and walking
the minor version downwards: it is backward compatible with older drivers and
not forward compatible with newer ones. A bundled 2.17 could not load a host
driver built against 2.20 at all. "Build against the oldest libva in the matrix"
would be the right instruction for a binary that links the *host's* libva; for a
bundled one it inverts.

This is a stated deviation from a flat "no RPATH/RUNPATH" rule. The gate does
not relax to accommodate it: `ci/ffmpeg/verify-runtime.sh` accepts exactly the
string `$ORIGIN/../lib` and refuses every other value, including the mangled
`25ORIGIN/../lib` that FFmpeg's `configure` produces from `--extra-ldflags`.
Negative control 8 proves the refusal with a real absolute vendor RUNPATH.

A bundled libva also needs its **driver search path** set explicitly, and this
is the option that is easy to miss. meson derives `driverdir` from `--prefix`,
so a libva built into `/opt/tesserafin-ffmpeg` looks for VA drivers in
`/opt/tesserafin-ffmpeg/lib/dri` — a directory this project never installs
anything into. The symptom is not an obvious one: `ffmpeg -hwaccels` still lists
`vaapi` and looks entirely healthy, and only a real transcode reveals
`va_openDriver() returns -1` on a machine with a perfectly good driver. libva is
therefore built with an explicit `driverdir` covering every location the four
declared distributions put a VA driver, on both architectures.
`LIBVA_DRIVERS_PATH` still takes precedence for anything unusual.

On every declared environment, `ffmpeg -hwaccels` and the server-style hardware
probe must return without aborting, and an unavailable device must produce a
controlled "device creation failed", not `SIGABRT`. A runtime that survives only
because the caller wraps the subprocess is not accepted.

Note that **exit code alone cannot make that distinction** for ffmpeg. It
reports AVERROR values by truncating a negative errno into the exit status, so
`EINVAL` leaves 234 and `EIO` leaves 251 — squarely inside the range a signal
death occupies. What separates a controlled failure from a crash is the abort
signature on stderr, and that is what the acceptance scripts test. A non-zero
exit with nothing on stderr is treated as a failure too: a death with no
diagnostic is not something a caller can fall back on.

---

## 5. Hardware claims

Compiled capability and runtime capability are recorded separately, always.

Listing an encoder proves it is compiled in. It does not prove it operates.

**The runtime archive never carries an affirmative hardware claim — not even a
true one.** Every path in `capability.json` reads `"not runtime-tested"`, always,
and `ci/ffmpeg/verify-closure.sh` refuses any other value. Two reasons pointing
the same way:

* *reproducibility* — a machine with a GPU and a hosted runner without one would
  otherwise produce different bytes for the same build revision, and bit-for-bit
  is not something to trade away for a hardware accident;
* *honesty* — "works" is a property of a machine, not of an artifact. The same
  binary on the same distribution succeeds or fails depending on a driver the
  archive does not contain.

Runtime hardware evidence therefore lives **beside** the archive, produced by
`ci/ffmpeg/accept-hardware.sh` on a machine that actually had the hardware. That
script performs a complete transcode and verifies the output is genuinely H.264
and genuinely non-empty rather than trusting an exit code — a device that
initialises and then produces nothing is not a working hardware path. Where no
render node exists it records `deferred` and exits 0, because absence of a GPU
is a deferral rather than a failure, and it will never write an affirmative
claim it did not observe. Hosted CI has no GPU and makes no hardware claim.

---

## 6. Redistribution closure

Every runtime archive ships `LICENSES/`, `THIRD_PARTY_NOTICES.md`, `SOURCE.json`,
the exact build configuration, and the name and SHA-256 of the corresponding
source artifact.

A single deterministic corresponding-source artifact carries the FFmpeg source,
every applied patch, complete source for every statically linked dependency, the
exact build scripts and configuration, the dependency manifest, and instructions
sufficient to rebuild both runtime archives. It holds the preferred form for
modification — real source trees, not links or binaries.

This is the engineering closure that has to exist before legal review. It is not
a legal ruling, and this document does not make one.

---

## 7. Reproducibility

Two independent clean hosted builds per architecture must produce identical
SHA-256 values for `ffmpeg`, `ffprobe`, the runtime archive, the SBOM, the
provenance manifest, the capability manifest and the corresponding-source
archive. Functional equivalence is not accepted.

What makes it hold: a digest-pinned builder image so the compiler is an input
rather than something this build produces; `SOURCE_DATE_EPOCH`;
`-ffile-prefix-map`; `-Wl,--build-id=none`; deterministic archives; fixed
`LC_ALL` and `TZ`; sorted, ownerless tar members with clamped mtimes.

**No LTO.** Upstream's `defaults-gpl.sh` sets `--enable-lto=auto`; it is dropped
here. LTO partitioning depends on job count and link order and is the largest
single threat to bit-for-bit reproducibility. It buys throughput, not capability.

---

## 8. Update policy

The baseline moves only on a deliberate revision of this document. A component
version or digest change is a reviewed change to `ci/ffmpeg/components.json`,
never maintenance. A new component must arrive with a licence classification, an
immutable pin and a named Tesserafin capability that requires it, or the gate
refuses it.

---

## 9. What this work does not do

No publication, no release, no channel, no registry, no APT/DNF repository, no
signing key, no new credential. Docker's FFmpeg runtime is untouched. Server
playback and transcoding behaviour is untouched. Web, Android and OpenAPI are
untouched. PR #228 is untouched.
