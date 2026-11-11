# L0 — Native Linux server packages

Issue [#225]. This document is the artifact contract for the downloadable native
Linux server distribution: what is produced, what is inside it, what the service
does on your machine, and — just as importantly — what is *not* claimed.

For the current release downloads and installation commands, start with the
[installation guide](../install.md#native-linux). The provenance and acceptance
records below describe their original artifacts; they are not a promise that
every build output or sidecar is attached to a public release. In particular,
there is no APT/DNF repository and no signed-package claim.

Docker/OCI remains an equally supported distribution path. Nothing here changes
it. See `docs/container/` for that half.

---

## 1. Supported formats and architectures

| Format | `linux-x64` | `linux-arm64` |
| --- | --- | --- |
| Debian package `.deb` | `amd64` | `arm64` |
| RPM package `.rpm` | `x86_64` | `aarch64` |
| Portable archive `.tar.gz` | `linux-x64` | `linux-arm64` |

Both architectures are built from one source commit and are **accepted on
architecture-native hosted runners** — `ubuntu-24.04` and `ubuntu-24.04-arm`. No
architecture is declared supported on the strength of emulation or metadata
inspection.

Environments the lifecycle suite actually exercises:

| Family | Environment | Architectures |
| --- | --- | --- |
| Debian | `ubuntu:24.04`, digest-pinned, booted with systemd as PID 1 | amd64, arm64 |
| RPM | `rockylinux:9`, digest-pinned, booted with systemd as PID 1 | amd64, arm64 |
| Portable archive | `debian:12`, digest-pinned, only the documented libraries | amd64, arm64 |

Each lifecycle boots its own container so the environment is genuinely clean —
the runner is not. Neither base image ships systemd, so the scripts layer it (and
the test tooling) on top of the pinned base first. That layered image is an
acceptance environment only: it is never a build input and never reaches an
artifact.

Anything not in that table is undeclared, not implied.

---

## 2. Package name and filesystem layout

One bundled package, `tesserafin-server`, carrying the server and the web client
together. There is no separately versioned web package to drift.

| Path | Contents |
| --- | --- |
| `/usr/bin/tesserafin` | relative symlink into the payload |
| `/usr/lib/tesserafin/` | application payload (self-contained .NET build) |
| `/usr/lib/tesserafin/ffmpeg/bin/` | the bundled `ffmpeg` and `ffprobe` |
| `/usr/lib/tesserafin/ffmpeg/lib/` | the runtime's own shared libraries |
| `/usr/share/tesserafin/web/` | bundled Tesserafin Web client |
| `/usr/share/tesserafin/web-revision.json` | web payload provenance |
| `/usr/share/tesserafin/ffmpeg/` | runtime SBOM, source manifest, capability record, notices |
| `/usr/share/licenses/tesserafin-server/LICENSE` | the server licence, GPL-2.0-or-later |
| `/usr/share/licenses/tesserafin-server/ffmpeg/` | the runtime's per-component licence texts |
| `/usr/share/doc/tesserafin-server/copyright` | DEP-5, one stanza per licensed component |
| `/usr/share/doc/tesserafin-server/FFMPEG-CORRESPONDING-SOURCE.txt` | where to obtain the runtime's source |
| `/etc/tesserafin/` | configuration, owned by the service account |
| `/etc/tesserafin/tesserafin.conf` | the service environment file |
| `/var/lib/tesserafin/` | persistent state: database, metadata, plugins |
| `/var/cache/tesserafin/` | cache and transcoding workspace |
| `/var/log/tesserafin/` | rolling application log files |
| `/usr/lib/systemd/system/tesserafin.service` | the unit |

`bin/` and `lib/` under `/usr/lib/tesserafin/ffmpeg/` are siblings **on purpose**.
The executables carry `RUNPATH=$ORIGIN/../lib`, so they resolve their bundled
libraries relative to themselves rather than through any system search path.
Flattening the two directories, or moving the libraries under `/usr/lib`, would
silently hand the encoder to whatever `libva` the host happens to carry. Nothing
is installed under `/opt/tesserafin-ffmpeg`; that prefix exists only inside the
F0 build container.

The portable archive carries the same application, web and runtime payload under
a relocatable prefix and **no** `/etc` content and **no** unit file. It installs
nothing; see its own `README.md` and `LICENSES.md`.

---

## 3. Service identity and lifecycle

* Runs as the unprivileged system user and group `tesserafin`. The acceptance
  suite reads the running process UID back and fails if it is 0.
* Started with `--webdir /usr/share/tesserafin/web`. Never `--nowebclient`.
* Started with `--ffmpeg` pointing at the bundled encoder, so the server never
  silently falls back to whatever `ffmpeg` is on `$PATH`.
* `WorkingDirectory=/usr/lib/tesserafin`, matching the container's `WORKDIR`, so
  the server resolves its own `wwwroot` the same way.
* Logs go to the journal. The application *also* keeps its rolling file sink
  under `/var/log/tesserafin`, because `logging.json` ships one; removing it
  would change runtime behavior rather than package it.
* On first install the unit is **enabled but not started**. An operator decides
  when a media server begins serving.
* On upgrade, a running service is restarted onto the new payload.

Directories are created by the maintainer scripts at mode `0750`, owned by
`tesserafin:tesserafin`. `tesserafin.conf` stays `root:tesserafin 0640`: systemd
reads it before dropping privileges.

---

## 4. Version derivation

The package version is the canonical product version and nothing else:

```
SharedVersion.cs  ->  docker/version-contract.sh version  ->  1.0.0
```

`.deb` versions are `<version>-<revision>` and `.rpm` versions are
`<version>-<release>`. The revision/release exists so the acceptance suite can
build a synthetic *higher* package from an **identical payload** and prove an
upgrade preserves configuration and state. It never encodes a new product
version, and this work introduces none.

`SOURCE_DATE_EPOCH` is the commit time from the same contract. Every timestamp in
every artifact derives from it.

---

## 5. Provenance

| Input | Value |
| --- | --- |
| Tesserafin Web commit | `a9a362eec764a9fe3fa6ba9b4a7dd7473677e35a` |
| Tesserafin Web assets image | `ghcr.io/tesserafin/tesserafin-web-assets@sha256:6150380052c8a3a154a8a25a9f40a741175a7563afdf89284f9c1f46d3042a6c` |
| Tesserafin Web payload SHA-256 | `4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f` |
| FFmpeg runtime revision | `7.1.4-tesserafin.1` |
| FFmpeg upstream commit | `d4590e12452f94d40e413caecb34b08de608353b` |

The server commit is whatever commit the artifact was built from and is recorded
per artifact rather than pinned here; freezing it in prose only guarantees the
prose goes stale.

Each pin has exactly **one** definition and is read from it:

* the web assets image and the paired web commit — the `ARG` block in the
  `Dockerfile`;
* the FFmpeg runtime revision and its upstream commit —
  `ci/ffmpeg/components.json`.

`ci/package/pins.env` restates both only in order to **assert** them, and the
build fails closed if either source disagrees with it. There is no package-side
FFmpeg version pin coupled to the container any more: the container installs an
upstream `.deb` and the packages build the accepted runtime from source, and
coupling those two is what previously made the packages inherit a binary nobody
in this project built.

The build fails closed on web provenance: the commit recorded inside the payload
must equal the pin, **and** the payload's own digest must equal the pinned
digest. Either mismatch stops the build; neither is a warning.

### Embedded build paths

No artifact may carry a path from the machine that built it. Two rules enforce
that, and neither depends on who is building:

* the checkout directory is matched literally and is never excusable;
* every other absolute build path found anywhere in the three artifacts must be
  **exactly** one of the upstream dependency paths enumerated in
  `ci/package/embedded-build-paths.allow`.

A handful of third-party NuGet assemblies are compiled by their own maintainers
with a PDB path baked in. Those bytes are build inputs and cannot be rewritten
here, so they are listed in full rather than waved through by prefix. The list is
closed: a dependency upgrade that moves a path, or a new dependency that embeds
one, fails the gate. It also cannot legitimise a first-party path — an entry
naming Tesserafin is rejected when the list loads.

`${HOME}` is deliberately not used as the discriminator. On a hosted runner it is
`/home/runner` for this build *and* for every upstream project that was itself
built on GitHub Actions, so it cannot tell a first-party leak from an upstream
one.

### Artifact manifest

Every artifact gets `<artifact>.provenance.json` next to it, validated against
the committed schema `ci/package/provenance.schema.json` by
`ci/package/verify-provenance.sh`. The schema is strict: required keys, no
unknown keys at any declared level, lowercase 64-hex digests, and a validator
that fails closed on any JSON Schema keyword it does not implement.

```json
{
  "schemaVersion": 2,
  "packageFormat": "deb",
  "packageName": "tesserafin-server",
  "packageVersion": "1.0.0",
  "architecture": "amd64",
  "runtimeIdentifier": "linux-x64",
  "serverCommit": "...",
  "serverRepository": "https://github.com/tesserafin/tesserafin.git",
  "webCommit": "...",
  "webVersion": "1.0.0",
  "webRepository": "https://github.com/tesserafin/tesserafin-web.git",
  "webAssetsImage": "ghcr.io/...@sha256:...",
  "webPayloadSha256": "...",
  "ffmpegRuntime": {
    "buildRevision": "7.1.4-tesserafin.1",
    "upstreamRepository": "https://github.com/jellyfin/jellyfin-ffmpeg.git",
    "upstreamCommit": "d4590e12452f94d40e413caecb34b08de608353b",
    "upstreamBaseline": "v7.1.4-3",
    "architecture": "linux-x64",
    "license": "GPL-3.0-or-later",
    "ffmpegSha256": "...",
    "ffprobeSha256": "...",
    "runtimeArchive": "tesserafin-ffmpeg-7.1.4-tesserafin.1-linux-x64.tar.xz",
    "runtimeArchiveSha256": "...",
    "capabilityManifestSha256": "...",
    "sbomSha256": "...",
    "sourceManifestSha256": "...",
    "noticesSha256": "...",
    "correspondingSource": "tesserafin-ffmpeg-7.1.4-tesserafin.1-corresponding-source.tar.zst",
    "correspondingSourceSha256": "..."
  },
  "licensing": {
    "server": "GPL-2.0-or-later",
    "ffmpegRuntime": "GPL-3.0-or-later",
    "spdxExpression": "GPL-2.0-or-later AND GPL-3.0-or-later",
    "note": "..."
  },
  "applicationPayloadSha256": "...",
  "stagedTreeSha256": "...",
  "sourceDateEpoch": "...",
  "buildTimestamp": "...",
  "toolchain": {
    "dotnetSdk": "...", "tar": "...", "dpkgDeb": "...",
    "rpmbuild": "4.20.1", "rpmBuilderImage": "fedora@sha256:..."
  },
  "artifactFilename": "...",
  "artifactSizeBytes": 0,
  "artifactSha256": "..."
}
```

There is deliberately **no** `ffmpegVersion` field. A single version string could
not distinguish the upstream baseline `7.1.4-3` from the Tesserafin build
revision `7.1.4-tesserafin.1`, and that ambiguity is precisely how a package
could claim to carry a runtime it did not build. `buildRevision` and
`upstreamBaseline` are separate fields for that reason. The obsolete
`ffmpegAsset` and `ffmpegSha256` fields, which described a release download, are
gone; `verify-provenance.sh` rejects a manifest that carries them.

Beyond the schema, the gate checks what a schema cannot: that the manifest names
the artifact it sits beside, with its actual size and digest; that the format's
architecture spelling, the RID and the runtime's architecture describe one
machine; that the corresponding-source archive it names is the one actually
delivered, with the recorded digest; that the version matches the version
contract; and that the three manifests agree on everything not artifact-specific.

---

## 5b. Corresponding source and redistribution closure

The bundled FFmpeg runtime is GPL-3.0-or-later, so its complete corresponding
source has to travel with it. That archive is about 232 MB, it is
architecture-independent, and it is identical for all three formats — putting it
inside every `.deb`, `.rpm` and `.tar.gz` would ship six copies of one tree and
give them six chances to disagree.

It is therefore delivered as an explicit **sidecar** beside the packages:

```
tesserafin-ffmpeg-7.1.4-tesserafin.1-corresponding-source.tar.zst
```

* its SHA-256 is in `SHA256SUMS-<rid>.txt`, so a recipient verifying the binaries
  verifies the source from the same manifest;
* its filename and SHA-256 are in every artifact provenance manifest;
* every package carries `/usr/share/doc/tesserafin-server/FFMPEG-CORRESPONDING-SOURCE.txt`,
  naming it and its digest, so an operator holding only the installed package
  knows exactly what to ask for;
* the runtime's own `SOURCE.json` names it too, and `ci/ffmpeg/verify-closure.sh`
  fails if that name and digest do not match the archive shipped beside it.

Both architecture jobs produce it. The `Corresponding-source identity` job
requires the two copies to be **byte-identical** and records one canonical digest;
two different archives under one filename is not a size optimisation to
deduplicate later, it is a defect. If they ever differ, the job fails rather than
picking one.

Each package also carries, inside itself, everything needed to identify what it
contains: F0's `SOURCE.json`, its CycloneDX SBOM, `THIRD_PARTY_NOTICES.md`, the
capability record and the full `LICENSES/` set.

**What the source archive is not.** It is the corresponding source for the FFmpeg
runtime only. It does not contain the Tesserafin server source or the Tesserafin
Web source, and nothing in this delivery implies it does. Those are separate
works, recorded separately in the provenance manifest as `serverRepository` +
`serverCommit` and `webRepository` + `webCommit`.

---

## 5c. Licensing

The package contains three separately licensed works. Nothing here relicenses
anything: a GPL-2.0-or-later server distributed alongside a GPL-3.0-or-later
runtime is still a GPL-2.0-or-later server.

| Component | Licence | Where the text is |
| --- | --- | --- |
| Tesserafin server | GPL-2.0-or-later | `/usr/share/licenses/tesserafin-server/LICENSE` |
| Tesserafin FFmpeg runtime | GPL-3.0-or-later | `/usr/share/licenses/tesserafin-server/ffmpeg/` |
| Tesserafin Web client | GPL-2.0-or-later plus its recorded dependency licences | `/usr/share/tesserafin/web-licenses/` |

Format-appropriate metadata, rather than one licence stamped over everything:

* **RPM** — `License: GPL-2.0-or-later AND GPL-3.0-or-later`. `AND` is the SPDX
  operator for "this artifact contains both". Measured against the pinned
  rpm 4.20.1 builder rather than assumed: the expression is accepted and
  round-trips through `%{LICENSE}` byte-identically. Both licence directories are
  marked `%license`, so `rpm -qL` lists the runtime's texts as well as the
  server's.
* **Debian** — a real machine-readable DEP-5 `copyright` with one stanza per
  differently licensed component. dpkg has no `License` control field and none is
  invented; a reader parsing `control` would simply never see it.
* **Portable archive** — `LICENSES.md`, a licence index naming each component,
  its paths, its licence and where its text is, next to the texts themselves.

`ci/package/verify-artifacts.sh` rejects: a missing server licence, a server
licence that is not byte-identical to the project `LICENSE`, fewer than twenty
installed FFmpeg component licence texts, missing F0 notices, a missing
corresponding-source notice, a package-wide GPL-2-only claim, a GPL-3 claim over
the server itself, and any nonfree or FDK AAC capability drift. The FDK AAC
boundary is not rhetorical: F0 skips
`0029-remove-fdk-aac-from-nonfree.patch` as unsafe, and `verify-runtime.sh`
refuses any trace of `libfdk_aac`.

## 6. Docker coexistence

The two paths are siblings, not alternatives, and they differ in exactly one
deliberate way:

| | Container image | Native packages |
| --- | --- | --- |
| .NET runtime | framework-dependent, supplied by the base image | **self-contained**, carried in the payload |
| Web payload | the pinned assets image | the same pinned assets image |
| Encoder | upstream jellyfin-ffmpeg noble `.deb` | the accepted Tesserafin FFmpeg runtime, built from source |
| Identity | fixed UID 10000 in the image | system user `tesserafin` |

The .NET difference is forced: an image can supply a runtime, a `.deb` cannot
unless it depends on a vendor APT/DNF feed, which is out of scope here.

The **encoder** difference is not a packaging accident either. The container's
FFmpeg is unchanged by this work and stays out of scope; the packages carry the
accepted Tesserafin runtime because that is the runtime this project builds,
gates and can supply corresponding source for. The two are different artifacts
under different terms — the container's upstream `.deb` is GPL-2.0-or-later, the
Tesserafin runtime is GPL-3.0-or-later — and nothing claims they are the same
FFmpeg or carry the same capability surface.

Because of it, **payload equivalence is an intra-L0 property**: the `.deb`, the
`.rpm` and the `.tar.gz` for one architecture are proven to carry identical
application and web bytes. They are deliberately *not* byte-equal to the
container's `/opt/tesserafin`, and no gate claims they are.

Existing container gates (`docker/smoke.sh`, `docker/repro-check.sh`,
`docker/hwa-smoke.sh`) are unchanged by this work. They have always been local
scripts — this repository has no container workflow and `local-ci.yml` has no
runner — so their evidence is local, as it has always been.

---

## 7. FFmpeg runtime

The packages carry the **accepted Tesserafin FFmpeg runtime** (F0 / #229),
reconstructed from the pinned sources on every build. The relevant facts:

1. The server's encoder precedence is `--ffmpeg` / environment, then
   `EncoderAppPath` in `encoding.xml`, then a bare `ffmpeg` on `$PATH`.
2. The server **refuses to start** without a usable encoder:
   `FfmpegException: Failed to find valid ffmpeg` is fatal. "Ship no encoder" is
   therefore not a shippable option — the service would never start.
3. The unit passes `--ffmpeg /usr/lib/tesserafin/ffmpeg/bin/ffmpeg`, so the
   server never silently falls back to a distribution `ffmpeg`.

### How it is produced

`ci/package/ffmpeg-runtime.sh` is an **adapter over `ci/ffmpeg/**`**, not a second
FFmpeg build. It runs the merged F0 scripts in the F0 workflow's own order —
`build-runtime.sh`, `verify-runtime.sh`, `package-runtime.sh`,
`verify-closure.sh`, `delivered-digests.sh` — and restates no configure flag, no
component pin, no patch decision and no version constant. Every name and identity
is derived from `ci/ffmpeg/components.json`.

The build depends on **none** of: an expiring workflow artifact, a historical run
ID, a manually downloaded accepted binary, a Jellyfin release asset, the system
FFmpeg, an unpinned container or package repository, or a registry publication.
The runtime is never cross-built or emulated, which is why both architectures now
build on architecture-native runners.

### What the package build refuses

* a runtime revision that is not the accepted `7.1.4-tesserafin.1`;
* a runtime architecture that does not match the package RID;
* a failed F0 closure gate;
* an absent or additional F0 delivered path;
* a wrong ELF machine for `ffmpeg` or `ffprobe`;
* a `RUNPATH` that is not exactly `$ORIGIN/../lib`;
* a bundled SONAME symlink that does not resolve inside the runtime;
* a missing corresponding-source archive;
* a delivered digest that differs from the accepted baseline while
  `ci/ffmpeg/**` is unchanged.

### The accepted digest baseline

`ci/package/f0-accepted-digests.txt` holds the sixteen delivered digests F0-A2
accepted. It is a **comparison oracle**: nothing consumes those bytes, and using
an accepted binary as a build input is exactly what this work removed. It is
enforced only while `git rev-parse HEAD:ci/ffmpeg` equals `F0_ACCEPTED_CI_TREE`
in `pins.env` — when `ci/ffmpeg/**` legitimately changes the baseline is stale by
construction, and a stale oracle must not be able to green a build or fail one.

One caveat is recorded rather than hidden. The corresponding-source archive is
zstd-compressed, and zstd does not guarantee identical compressed bytes across
library builds, so a workstation and a hosted runner can emit different
`.tar.zst` files from an identical tar stream. Four further digests
(`SOURCE.json`, `THIRD_PARTY_NOTICES.md`, `sbom.cdx.json` and the runtime
`.tar.xz` that contains them) record that archive's digest and move with it. The
baseline therefore also records the **decompressed** stream digest, and the gate
distinguishes that case explicitly — but accepts it only when
`PKG_ALLOW_COMPRESSOR_DRIFT=1` is set deliberately. CI never sets it: a hosted
build runs the same runner image that produced the baseline, so a difference
there is a real one.

### The inherited runtime, and why it is gated

Before this work the packages downloaded
`jellyfin-ffmpeg_<version>_portable_{linux64,linuxarm64}-gpl.tar.xz` from an
upstream release page, pinned by two SHA-256 values, and described it as "the
same FFmpeg release and GPL terms" as the container. That binary is gone, along
with the version pin, both checksums, the asset-name construction, the download,
the extraction and the `ffmpegAsset` provenance field.

`ci/package/verify-no-inherited-ffmpeg.sh` fails if any of them returns. It
deliberately does **not** forbid the strings `jellyfin-ffmpeg` or `7.1.4-3` on
their own: the runtime is genuinely built from that fork at a pinned commit, F0's
`SOURCE.json`, SBOM and notices say so, and those files ship inside every
package. Erasing honest upstream provenance to quiet a grep would be the
dishonest fix. The gate targets the download, not the ancestry.

---

## 8. Hardware boundary

Three distinct claims, kept distinct because collapsing them is how a package
ends up promising hardware it has never run.

### Compiled and advertised

Taken from the runtime's own `capability.json`, which each package installs at
`/usr/share/tesserafin/ffmpeg/capability.json`. It states **compiled capability
only**: listing an encoder proves it was compiled in and nothing more.

| Path | linux-x64 | linux-arm64 |
| --- | --- | --- |
| VAAPI | yes | yes |
| DRM | yes | yes |
| OpenCL | yes | yes |
| CUDA / NVENC / NVDEC / CUVID | yes | as declared by that architecture's manifest |
| QSV | yes | **no** — x64 only |
| AMF | yes | **no** — x64 only |

`-hwaccels` on the accepted linux-x64 runtime reports exactly
`cuda vaapi qsv drm opencl`. **Vulkan is not present** — not as an hwaccel, not
as an encoder, not as a filter — so no package claims it, and libplacebo is out
of scope for this work. V4L2 M2M encoders (`h264_v4l2m2m`, `hevc_v4l2m2m`,
`vp8_v4l2m2m`, `mpeg4_v4l2m2m`, `h263_v4l2m2m`) *are* compiled in and the
capability manifest proves it; that is a compiled-capability statement and
nothing more.

### Actually executed

* Software encode and decode flows, on both architectures, in every declared
  acceptance environment.
* **VAAPI**, on the available AMD render node, from a real package artifact —
  see §8b.

### Not runtime-tested

QSV, NVENC/NVDEC, AMF and Rockchip. The runtime's own manifest says so in
`hardwareRuntimeEvidence`, and this document does not upgrade it.

### The unit's privilege

The unit ships the least privilege that is *proven* device-compatible:
`NoNewPrivileges=true`, and nothing else. Every directive that hides or relabels
device nodes — `PrivateDevices`, `DevicePolicy`, `ProtectClock` and relatives —
is deliberately absent, because each of them breaks VAAPI (`/dev/dri/renderD*`)
or NVIDIA (`/dev/nvidia*`) access. This package does not ship hardening it has
not demonstrated to be compatible.

Device *access* still needs group membership, which is a host decision:

```sh
usermod -aG render,video tesserafin
systemctl restart tesserafin
```

The hosted lifecycle jobs have no GPU. They prove the unit does not block device
access and that the software path works; they do **not** prove hardware encoding
on any specific GPU.

---

## 8b. AMD VAAPI package integration

`ci/package/accept-vaapi.sh` performs this, against a **real package artifact**
rather than the staging tree: the `.deb` is unpacked and the `ffmpeg` executable
inside it is the one invoked. The transcode itself is delegated to
`ci/ffmpeg/accept-hardware.sh`, the F0 script that already classifies a crash
against a controlled failure and refuses to record a claim it did not observe.

It runs where a render node exists and **defers** where none does — absence of a
GPU is a deferral, never a pass. The hosted runners have no GPU, so this is local
evidence by construction. A complete `h264_vaapi` transcode is run on the AMD render node, and the
output is verified to be genuine H.264 with the expected frame count and duration
and to decode back cleanly.

What this proves: the packaged runtime's bundled `libva` layout works from its
installed location, `$ORIGIN/../lib` resolves to the package's own libraries and
not the host's, and no abort or silent software substitution occurs.

What this does **not** prove: automatic hardware selection by the server. That is
issues #29 and #76 and neither is complete. This evidence is about package
integration with the accepted runtime.

---

## 9. Uninstall and state retention

Maintainer scripts never delete data. Not on `remove`, not on `purge`,
not on `rpm -e`:

| Path | Ordinary uninstall | Debian `purge` |
| --- | --- | --- |
| `/usr/lib/tesserafin`, `/usr/share/tesserafin`, `/usr/bin/tesserafin`, the unit | removed | removed |
| `/var/lib/tesserafin` | **kept** | **kept** |
| `/etc/tesserafin` and its contents | **kept** | **kept** |
| `/var/log/tesserafin` | **kept** | **kept** |
| the `tesserafin` user and group | **kept** | **kept** |

Package management removes what the package installed. Everything the server
produced — the database, metadata, user images, and anything an operator put
beside it — belongs to the operator, and only the operator deletes it. The user
is kept too, so nothing left behind becomes orphaned.

Upgrades preserve `/etc/tesserafin/tesserafin.conf` through the ordinary
mechanisms: a dpkg conffile and an rpm `%config(noreplace)`. The acceptance suite
writes sentinels into configuration and state, upgrades, and reads them back.

---

## 10. Reproducibility

Two independent clean builds of every artifact must produce identical SHA-256
digests. The gate compares digests; it does not compare "functional
equivalence".

What makes it hold:

* `SOURCE_DATE_EPOCH` from the commit clamps every mtime, the `ar` member
  timestamps, the RPM build time and the RPM payload mtimes.
* `dotnet publish` runs with `Deterministic` and `ContinuousIntegrationBuild`.
* `tesserafin.staticwebassets.endpoints.json` has its `Last-Modified` values
  normalised to the commit time — the one genuinely non-deterministic file in the
  publish output, normalised the same way the container build normalises it.
* `dpkg-deb --root-owner-group` removes the build user's identity; `xz` and
  `gzip -n` carry no timestamp.
* `rpmbuild` runs in a digest-pinned image with `_buildhost`,
  `use_source_date_epoch_as_buildtime` and `clamp_mtime_to_source_date_epoch`
  set, and with debuginfo, stripping and build-id links disabled.
* `tar --sort=name --owner=0 --group=0 --numeric-owner`.
* the FFmpeg runtime is itself reproducible: the F0 build is pinned to one
  builder image, one package snapshot, a fixed `FF_JOBS` rather than `$(nproc)`,
  and an epoch derived from the pinned FFmpeg baseline.

### What crosses between the two sides

Only the checksum manifest. `ci/package/repro-check.sh` exports `PKG_REPRO=1`,
which makes `ci/package/ffmpeg-runtime.sh` **refuse** `--reuse` — so the second
machine rebuilds the FFmpeg runtime from source too. No compiled object and no
staged payload is shared. Within a single build `--reuse` is used, and only
there: one freshly built runtime feeds the `.deb`, the `.rpm` and the `.tar.gz`
of that architecture, which is what makes their equivalence a fact rather than a
coincidence.

Both sides run on architecture-native hosted runners, and the rebuild runs on a
different runner from the build.

### Path set before digests

The delivered **path set** is compared before any digest. Two builds that agree
on every digest they both produced have still not reproduced if one produced
fewer files — a shortened reference list is the failure mode a digest-only
comparison cannot see. The corresponding-source archive must be in that set.

### Controls

`ci/package/repro-controls.sh` damages a real artifact set eight ways and
requires each damaged copy to be rejected, plus one undamaged control that must
be accepted — without it, "rejected" could just mean the comparison rejects
everything:

1. shortened reference list
2. additional delivered path
3. renamed delivered path
4. obsolete v1 provenance manifest (the removed upstream-asset fields)
5. corrupted package
6. corrupted provenance manifest
7. mismatched corresponding-source archive
8. architecture mismatch in a manifest

---

## 11. What this work does not do

No signing keys, no signed APT or DNF repository, no repository metadata, no
public release, no publication of any artifact, no automatic updates. The
workflow has no `release:` trigger and no push step; artifacts exist only as
workflow artifacts.

That paragraph describes this work, and is still true of it. Separately, on
2026-09-17 the `.deb`, `.rpm` and `.tar.gz` files for
both architectures were attached to the
1.1.0 GitHub Release (pre-launch `0.2.0`, scheduled for deletion),
with `SHA256SUMS-linux-x64.txt`. They are unsigned, there is still no APT or DNF
repository, and no checksum file is attached for `linux-arm64`. The acceptance
described in this document was made on the workflow's own outputs, and the
repository pins no digest for the server packages, so nothing here shows that
the attached packages are those bytes. The two attached FFmpeg archives are the
exception: their SHA-256 values are the ones in
`ci/package/f0-accepted-digests.txt`.

The packaging checks are **not** branch-protection required checks. Branch
protection lists a fixed set of contexts, and this work does not modify it.

This work also does not implement automatic hardware selection, does not add
Vulkan or libplacebo, does not change the server's playback behaviour, does not
change Docker's FFmpeg or container distribution, and does not change the F0
component set, flags, patches, pins or runtime version.

### Deferred to L1

* signed APT and DNF repositories, and the metadata they need
* signing-key management and rotation
* stable-channel publication
* a proven clean upgrade from a downloaded L0 package to a repository-installed
  one
