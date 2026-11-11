# Changelog

All notable changes to the Tesserafin server are recorded here. The browser client has its
own changelog in [`tesserafin/tesserafin-web`](https://github.com/tesserafin/tesserafin-web).

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versioning
follows [`docs/versioning-policy.md`](./docs/versioning-policy.md), which is authoritative:
**`1.0.0` is reserved for the first public launch; everything before it is a pre-launch `0.x`.**

**Pre-launch renumbering (2026-11-05).** The builds originally published as
`1.0.0` and `1.1.0` are recorded as **0.1.0** and **0.2.0**. Both are unsupported.
Deletion of the original GitHub releases and tags follows the documentation
and website updates (#386). The changelog links to the original commits.

## [Unreleased]

### Fixed

- **A hardware transcode that fails no longer ends the playback or the server's ability to
  transcode** ([#119](https://github.com/tesserafin/tesserafin/issues/119)). When
  ffmpeg's own output positively identifies a hardware failure, the playback continues in
  software and the backend is withheld from new transcodes until the server restarts; the stored
  configuration is not changed. Failures that are not hardware ones (unreadable input, an
  unrecognised crash, a software transcode) are not retried and are reported to the client as
  such. Observed and proven on VAAPI with an AMD GPU only. See
  [`docs/hardware-transcode-recovery.md`](./docs/hardware-transcode-recovery.md).

### Changed

- **Historical publication: the pre-launch 0.2.0 build received native Linux packages.** They were attached on 2026-09-17, four days after the release and one day after
  this changelog last said they were not. Attached at the time: `.deb` (`amd64`, `arm64`), `.rpm`
  (`x86_64`, `aarch64`), `.tar.gz` (`linux-x64`, `linux-arm64`), the `linux-x64` Tesserafin
  FFmpeg runtime archive with its corresponding-source archive, and `SHA256SUMS-linux-x64.txt`.
  What that does and does not establish:
  - a file being downloadable is not a new acceptance. The acceptance is still the one in
    [`docs/distribution/L0-linux-packages.md`](./docs/distribution/L0-linux-packages.md), made
    on architecture-native runners. The repository pins no digest for the server packages, so
    nothing in it shows the attached packages are the accepted bytes. The two FFmpeg archives
    are the exception: their SHA-256 values match `ci/package/f0-accepted-digests.txt`;
  - `SHA256SUMS-linux-x64.txt` covers `linux-x64` only. No checksum file is attached for the
    `arm64` / `aarch64` files, and no `linux-arm64` FFmpeg runtime archive is attached;
  - that checksum file also lists `tesserafin-server_1.0.0-2_amd64.deb` and
    `tesserafin-server-1.0.0-2.x86_64.rpm`, which are not attached. Use
    `sha256sum -c --ignore-missing`;
  - the MSI is still not attached, and nothing is signed.

## [0.2.0] - 2026-09-13

Published as `1.1.0` (Git tag `v1.1.0`, commit `bb2234aebb`); release and tag scheduled for deletion.
0.2.0 adds native distribution surfaces to the contents of [0.1.0](#010---2026-08-05), below.
The acceptance contract is
[`docs/distribution/W5-A0-acceptance-contract.md`](./docs/distribution/W5-A0-acceptance-contract.md);
tracker [#276](https://github.com/tesserafin/tesserafin/issues/276).

**Unsigned.** Nothing in 1.1.0 is Authenticode-signed. **The MSI is not attached** to the GitHub
Release. On the release date the one attached asset was the unsigned `win-x64` ZIP pinned below;
the native Linux packages were attached on 2026-09-17 (see [Unreleased](#unreleased)). The application version was
`1.0.0`, which is why that ZIP is named `tesserafin-server_1.0.0_win-x64.zip`.

### Added

- **Linux container**, unchanged: the original 0.1.0 image, pinned by immutable digest at publication.
- **Native Linux packages**: `.deb`, `.rpm` and a portable `.tar.gz`, for `linux-x64` and
  `linux-arm64`, accepted on architecture-native runners
  ([#225](https://github.com/tesserafin/tesserafin/issues/225)).
  See [`docs/distribution/L0-linux-packages.md`](./docs/distribution/L0-linux-packages.md).
- **Native `win-x64` portable ZIP** (W2, W5-A1), unsigned, attached to the GitHub Release as
  `tesserafin-server_1.0.0_win-x64.zip`: a self-contained, relocatable server that ships
  no state. The unsigned ZIP accepted by W5-A1 is pinned in
  [`ci/windows/w5/accepted-unsigned-zip.json`](./ci/windows/w5/accepted-unsigned-zip.json):
  SHA-256 `c1f6261cb770bd3dcbf255f4dd15b287a7289adad5a619d31725c158cd20e2d8`, assembled at
  `41d3411c9838feec6650dd10ec89a1f198aafdeb`, identical on two independent allocations in run
  [34766464798](https://github.com/tesserafin/tesserafin/actions/runs/34766464798).
  That digest identifies the ZIP built from that commit; a ZIP built from any other commit has a
  different one. A rebuild from the `v1.1.0` tag commit is not this ZIP.
- **Native `win-x64` MSI and Windows Service** (W3, W4), documented and measured but **not
  attached to the 1.1.0 GitHub Release**: installs the server, registers the
  service `Tesserafin` under the virtual account `NT SERVICE\Tesserafin` with explicit
  `%ProgramData%` state directories and least-privilege ACLs, and leaves it stopped. Install,
  `MajorUpgrade` (state kept, `INSTALLFOLDER` remembered) and uninstall (state kept) are
  measured. The `UpgradeCode` is frozen at `0f0c9f4e-1c5a-4b8e-9a3d-6d1f2b7c8e05`.
- **Bundled Tesserafin Web**, pinned by canonical tree digest
  `4148c4bc6e0c7c2d6b35ed9992e874a06dcc11d2b6d9e0aad06719e36567be4f`.
- **Tesserafin FFmpeg runtime**, pinned by archive digest
  `f28cc9186aad757491a6f44e7950d39bc39354dfe9505e278af91d7619811c9e`.
- **Windows install documentation** for the ZIP and the MSI:
  [`docs/distribution/install-windows.md`](./docs/distribution/install-windows.md).

The interface on every platform remains Tesserafin Web in a browser.

### Not in 1.1

Stated rather than omitted:

- **No Authenticode signature yet.** The ZIP, the MSI and the executables are unsigned. Signing
  is a later transformation applied to the accepted unsigned bytes; the vendor is undecided.
  Unsigned artifacts remain usable.
- **No bit-identical MSI.** Only the unsigned ZIP carries a reproducibility pin.
- **No `win-arm64`.**
- **No native client.**
- **No advertised MSI repair.** Repair is not exercised by any slice.
- **No Secure Remote Access or managed HTTPS.**
  [#241](https://github.com/tesserafin/tesserafin/issues/241) is post-1.1.
- **No auto-updater.**
- **No Jellyfin client or plugin compatibility.**
- **No D3D11VA, DXVA2, QSV, NVENC or AMF runtime claim on Windows.** The runners that measured the
  Windows forms have no GPU.

## [0.1.0] - 2026-08-05

Published as **1.0.0 — Foundation** (Git tag `1.0.0`, commit `6227dbef66`); release and tag
scheduled for deletion.

Tesserafin is a fork of [Jellyfin](https://github.com/jellyfin/jellyfin). It inherited a
`12.x` server line and a `13.x` web line from upstream history; those numbers describe a
lineage, not a Tesserafin release history. Every existing
`ghcr.io/tesserafin/tesserafin:12.0.0-dev.*` and
`ghcr.io/tesserafin/tesserafin-web-assets:13.0.0-dev.*` image is an internal,
unsupported development artifact, is retained as the reproducibility record, and is **not**
a release. Moving from one of those to the `1.x` line is a change of version epoch, not a
supported upgrade.

Because this is a first release, the entries below are stated against upstream Jellyfin
rather than against a previous Tesserafin version.

### Added

- **A distributable container image.** A production `Dockerfile`, a `docker-compose.yml`
  pinning an immutable digest by default, and an Unraid template. A clean host can
  `docker run` the image and reach the API with no source tree present.
  See [`docs/container/A1-implementation-note.md`](./docs/container/A1-implementation-note.md).
- **Persistent state with a scripted backup and restore round trip** across `/config`,
  `/data` and `/cache`, with first-boot migration.
  See [`docs/container/A2-persistent-state.md`](./docs/container/A2-persistent-state.md).
- **A guided container install** that brings up a browser-reachable instance in a documented,
  bounded number of steps.
  See [`docs/container/A3-guided-install.md`](./docs/container/A3-guided-install.md).
- **Hardware-acceleration autodetection with a guaranteed software fallback.** Selection is
  re-probed by a real trial encode on every start, so a `/config` volume moved from a GPU
  host to a GPU-less one falls back to software and keeps transcoding. VAAPI and software are
  hardware-validated.
  See [`docs/container/A4-hardware-acceleration.md`](./docs/container/A4-hardware-acceleration.md).
- **Minimal observability.** A database-aware `/health` endpoint that answers its JSON
  contract from boot (`503 status=starting` before readiness) and reports version and
  database status, plus JSON container logs with an environment-configurable level.
  See [`docs/container/A5-observability.md`](./docs/container/A5-observability.md).
- **A version contract.** Image tag, `SharedVersion.cs`, the application version, `/health`
  and the OCI image labels are proven equal from a single derivation point, and an upgrade
  round trip replaces only the container across the same volumes while preserving users,
  libraries, media visibility, playback state and configuration.
  See [`docs/container/A6-versioning-and-upgrades.md`](./docs/container/A6-versioning-and-upgrades.md).
- **A fail-closed server-to-web release-pair gate** (`ci/verify-release-pair.sh`) covering
  image provenance, bundled-web provenance across both architectures, OpenAPI equality,
  web-SDK regeneration drift, browser onboarding and repeated lifecycle rounds.
  See [`docs/container/A7-server-web-release-pair.md`](./docs/container/A7-server-web-release-pair.md).
- **A vulnerability-reporting policy.** [`SECURITY.md`](./SECURITY.md) with a coordinated
  disclosure process, response targets, and GitHub private vulnerability reporting as the
  confidential intake channel.
- **Enforced CI as required status checks on `master`**: build and full test suite, format,
  ABI compatibility, OpenAPI compatibility, dependency audit, secret scanning, cross-repository
  SDK provenance, and CodeQL with the `security-extended` query suite over C# and GitHub
  Actions.
- **Contributor and architecture documentation.** [`BUILDING.md`](./BUILDING.md) documents the
  reproducible build, the local gate and the required checks;
  [`ARCHITECTURE.md`](./ARCHITECTURE.md) documents the media pipeline, module map and the
  divergences from upstream.

### Changed

- **Identity.** Full `Jellyfin*` → `Tesserafin*` namespace and assembly cutover. Environment
  variables are `TESSERAFIN_*`.
- **Version epoch.** Public SemVer restarts at `1.0.0` for both the server and the web client
  rather than continuing the inherited `12.x`/`13.x` numbering.
- **Playback is rewritten.** Jellyfin's monolithic, DLNA-coupled `StreamBuilder` becomes a
  layered decision / engine / execution / shadow pipeline with a DLNA adapter at the edge. The
  `[Flags] TranscodeReason` bitfield is replaced by a causal reason tree that records *why* a
  method was chosen, not only which constraints were hit.
- **Hardware acceleration is rewritten.** A startup hardware-selection planner with a priority
  catalogue and trial-encode probing replaces upstream's global capability lists and inline
  selection, removing global environment-variable mutation.
- **Persistence is rewritten.** The hand-written SQLite repository is replaced by an EF Core
  context over a provider abstraction.
- **God objects are being decomposed.** `LibraryManager` and `BaseItem` are progressively split
  into narrow injected services rather than static or global access.

### Removed

- **Jellyfin client, plugin and protocol compatibility**, deliberately. A plugin declaring a
  `targetAbi` in the upstream `10.x` or the inherited `12.x` range is reported as not
  supported at `1.0.0`.
- **`DeviceProfile` from the network contract.**
- **The inherited built-in metadata provider key.** There is no built-in default; provider
  access is operator-configured. See [`docs/metadata-provider-keys.md`](./docs/metadata-provider-keys.md).

### Security

- Backup selection, manifest reading, enumeration and restore extraction now walk **every**
  path component below a managed root and refuse to traverse a symbolic link. Checking only
  the final component is insufficient, because a linked parent leaves the final component
  reporting no link target at all.
- A shared leaf-name contract is enforced at the filesystem trust boundary, and the
  operator-configured XMLTV listings path has a written authorization boundary.
  See [`docs/xmltv-listings-path.md`](./docs/xmltv-listings-path.md).
- The Schedules Direct token request body is serialized rather than concatenated, so an
  administrator-supplied listings username can no longer alter the request structure.
- Every GitHub Actions workflow declares explicit, least-privilege permissions.

### Known limitations at the first release

Stated rather than omitted:

- **No forward-migration boundary has been crossed yet.** The upgrade round trip is proven,
  but no published image pair has a pending migration between it, so *"runs forward
  migrations"* is unproven. Tracked as
  [#127](https://github.com/tesserafin/tesserafin/issues/127).
- **Hardware acceleration is validated for VAAPI and software only.** QSV, NVENC, AMF,
  VideoToolbox, RKMPP and V4L2M2M are probe-gated but not hardware-validated. MJPEG VAAPI is
  tracked as [#76](https://github.com/tesserafin/tesserafin/issues/76).
- **A live mid-session retry after a hardware transcode failure is out of scope.** Selection is
  re-probed at start, not mid-playback. Tracked as
  [#119](https://github.com/tesserafin/tesserafin/issues/119).
- **The CodeQL inventory is being worked through in the open**, not dismissed. Open findings
  are classified in [#185](https://github.com/tesserafin/tesserafin/issues/185) and
  [#188](https://github.com/tesserafin/tesserafin/issues/188); one finding is handled
  under coordinated disclosure and is deliberately not described publicly.
- **The Linux container is the first release's deployment surface.**
  [0.2.0](#020---2026-09-13) (published as 1.1.0), above, adds native Linux `.deb`, `.rpm` and `.tar.gz` forms,
  *accepted* on architecture-native runners. Until 2026-09-16 they were **not attached** to the
  1.1.0 GitHub Release and the container was the only published Linux path; they were attached
  on 2026-09-17 (see [Unreleased](#unreleased) for what is and is not there). The MSI is still
  not attached.
  The full server test suite is not green on native Windows; see
  [`docs/distribution/W0-windows-server.md`](./docs/distribution/W0-windows-server.md) §2.7.

[Unreleased]: https://github.com/tesserafin/tesserafin/compare/bb2234aebbf0ff2c1cbe291df48f2ffa62beea46...master
[0.2.0]: https://github.com/tesserafin/tesserafin/tree/bb2234aebbf0ff2c1cbe291df48f2ffa62beea46
[0.1.0]: https://github.com/tesserafin/tesserafin/tree/6227dbef66c631081e3179a521561b32b94dac59
