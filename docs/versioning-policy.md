# Tesserafin versioning policy

Issues [#92](https://github.com/tesserafin/tesserafin/issues/92) ([A6]),
[#101](https://github.com/tesserafin/tesserafin/issues/101) ([E1]),
[#127](https://github.com/tesserafin/tesserafin/issues/127).

This document is authoritative for **which numbers Tesserafin publishes, where it
publishes them, and how a consumer is allowed to resolve a release**. It sits
above the mechanism: `docker/version-contract.sh` implements the tag derivation,
[A6](./container/A6-versioning-and-upgrades.md) records how that mechanism was
proven, and this document states the policy both of them serve.

---

## 1. The public version epoch

**`1.0.0` is reserved for the first public launch.** Everything published before it
is a pre-launch `0.x` build (owner ruling, 2026-11-05; tracker #386):

| Pre-launch version | Published as | Commit | Status |
| --- | --- | --- | --- |
| `0.1.0` | `1.0.0` — Foundation, tag `1.0.0`, 2026-08-05 | `6227dbef66` | withdrawal approved 2026-11-05. Deletion pending. |
| `0.2.0` | `1.1.0`, tag `v1.1.0`, 2026-09-13 | `bb2234aebb` | withdrawal approved 2026-11-05. Deletion pending. |

Neither is supported; there is no upgrade commitment from them. The canonical
version in the tree is `0.3.0` until the launch (#389). The reasoning below about
the inherited `12.x` / `13.x` lines is unchanged: it is why the public line starts
at `1`, not `12`.

Tesserafin is a fork. It inherited a `12.x` server version line and a `13.x` web
version line from its upstream history. Those numbers describe an upstream
lineage, not a Tesserafin release history: the two unsupported pre-launch builds above do
not establish a supported history of releases numbered 1 through 11. Shipping a first public
release as `12.0.0` would claim eleven major versions of public compatibility
history that does not exist.

The first public Tesserafin release is therefore numbered `1.0.0`, and the
server and the web client share that number.

### 1.1 What the inherited artifacts are

Every `ghcr.io/tesserafin/tesserafin:12.0.0-dev.*` server image and every
`ghcr.io/tesserafin/tesserafin-web-assets:13.0.0-dev.*` web-assets image
that already exists:

* **is retained.** None is deleted, retagged, moved or rewritten. They are the
  reproducibility record and the evidence behind the A1–A7 gates.
* **is an internal, unsupported development artifact.** It predates the public
  version epoch.
* **is not a public release.** It was never announced, never carried a channel
  tag, and never appeared in a GitHub Release.
* **is not a valid input to an automatic supported-upgrade decision.** No
  updater, client or operator tool may treat one of these images as a version a
  supported installation can be on, or upgrade from, or downgrade to.

The transition from an inherited `12.x` development image to the `1.x` line is
**neither a supported upgrade nor a supported downgrade**. It is a change of
version epoch. An operator who ran a `12.0.0-dev.*` image during development and
wants to move to the `1.x` line is performing a manual migration outside the
supported graph, and the project makes no compatibility promise about it.

### 1.2 What the inherited artifacts still prove

The `12.x` evidence remains valid **as evidence**. The A6 upgrade-round-trip runs
recorded in [A6](./container/A6-versioning-and-upgrades.md) prove that the
upgrade harness preserves users, libraries, configuration and playback state
across two immutable builds. That property is about the harness and the
persistence layer, and it does not stop being true because the version numbers
changed. What those runs do **not** establish is a forward-migration boundary
inside the `1.x` epoch — see §5.

### 1.3 Consequence for plugins

`ApplicationVersion` is read from the assembly version
(`Tesserafin.Server.Core/ApplicationHost.cs`), and the plugin manager admits a
plugin only when `ApplicationVersion >= targetAbi`
(`Tesserafin.Server.Core/Plugins/PluginManager.cs`,
`Tesserafin.Server.Core/Updates/InstallationManager.cs`). At `1.0.0`, any plugin
declaring a `targetAbi` in the upstream `10.x` or the inherited `12.x` range is
reported as not supported.

This is intended. A Tesserafin plugin targets a Tesserafin ABI, and the
Tesserafin ABI starts at `1.0.0`. The policy does not create a compatibility
claim over upstream plugin binaries, and it does not silently accept them either.

---

## 2. Package namespaces

| Package | Role | Visibility |
|---|---|---|
| `ghcr.io/tesserafin/server` | **public release package** — the only thing a user installs from; contains released manifests and nothing else. Withdrawal of its pre-launch `1.0.0` tag (now `0.1.0`) was approved on 2026-11-05. Registry removal is a separate owner action. | pre-launch image pending withdrawal |
| `ghcr.io/tesserafin/tesserafin` | **pre-v1 server archive** — every inherited `12.0.0-dev.*` image | private |
| `ghcr.io/tesserafin/tesserafin-server` | **canonical v1+ build package** — every immutable CI build, including the ones that are never released | private |
| `ghcr.io/tesserafin/tesserafin-web-assets` | web-assets package, spanning both epochs | private |

A release is a **copy, by digest, from the build package into the release
package**. The manifest is never rebuilt, so a released image and its build-package
original are the same bytes under two names. GHCR visibility is per package, not
per version, which is exactly why the two are separate: publishing the build
package would publish every unreleased development image in it.

Rules:

* Only `server` may ever receive a public release tag such as `1.0.0`, and only
  `tesserafin-server` may ever receive a future stable or channel tag inside the
  build epoch.
* The archive `tesserafin` **must never** receive `latest`, `stable`, `1`, `1.0`
  or any other public-release alias. It is immutable: nothing is added to it, and
  nothing is removed from it.
* `tesserafin-web-assets` keeps its inherited `13.0.0-dev.*` versions and gains
  `1.0.0-dev.*` versions. The web-assets package is a build input, not a product
  a user installs, so it does not carry product channel aliases either.

The archive reference is a strict **prefix** of the build-package reference. Any
check that identifies a package must compare the full repository reference or
stop at the `:`/`@` boundary; a substring test for `tesserafin` matches the
archive, the build package and the web-assets package and proves nothing. The
release package `server` shares no prefix with any of them, which is deliberate:
an installation reference cannot be mistaken for a build artefact.

---

## 3. Version selection

A client, updater or installer **must never**:

* enumerate arbitrary GHCR tags;
* sort those tags globally by SemVer;
* assume the numerically largest tag is the supported release.

Under this policy the numerically largest tag in the organisation is
`12.0.0-dev.*` — an unsupported development artifact from the previous epoch —
and it will stay the largest for as long as the `1.x` line runs. "Greatest
SemVer tag wins" therefore does not merely risk the wrong answer here; it is
guaranteed to produce it.

Supported resolution uses an **explicit source of authority**, and only one of:

1. an **immutable exact version or digest** the operator names
   (`server:1.0.0`, or `server@sha256:…`);
2. a **documented channel tag** from the table in §4;
3. a **curated release manifest** published by the project (not yet defined).

Historical tags are not channels. The presence of a tag in a registry is not a
statement that the project supports it.

Tesserafin ships no auto-updater today, and this policy is what any future one
must be built against.

---

## 4. Planned channel contract

The tag classes below are the complete set `docker/version-contract.sh` is
allowed to derive. **This section documents the contract; it does not authorise
publication.** Everything except the immutable development class is unpublished
at the time of writing.

| Class | Tag shape | Mutability | Published today |
|---|---|---|---|
| immutable development | `1.0.0-dev.<12-char source commit>` | immutable | **yes**, private |
| immutable source | `sha-<40-char source commit>` | immutable | **yes**, private |
| release candidate | `1.0.0-rc.N` | immutable | no |
| pre-release channel | `preview` | mutable | no |
| stable exact | `1.0.0` | immutable | no |
| minor alias | `1.0` | mutable | no |
| major alias | `1` | mutable | no |
| stable channel | `stable` | mutable | no |
| default channel | `latest` | mutable | no — only if [E3] explicitly ratifies it for stable releases |

`docker/version-contract.sh` enforces that `latest` is reachable only from the
`stable` channel, and `docker/version-contract.test.sh` asserts it.

No mutable tag has been published in either package. The first public release is
gated on [#103](https://github.com/tesserafin/tesserafin/issues/103) [E3].

---

## 5. The forward-migration gate (#127)

Two promises are deliberately distinct.

**Before v1 — prove one real forward migration inside the `1.x` epoch.**
[#127](https://github.com/tesserafin/tesserafin/issues/127) is satisfied
only by a run of

```
docker/upgrade-roundtrip.sh \
  --baseline  <the v1 baseline digest> \
  --candidate <a digest built after a real schema migration landed> \
  --require-migration
```

that applies **more than zero** pending migrations and preserves every existing
assertion. A dummy migration, a version-only migration or a configuration no-op
does not satisfy this gate and must not be added to close it.

**After v1 — maintain documented compatibility across one supported minor
upgrade.** Once `1.0.0` is public, the project commits to a documented upgrade
path across at least one supported minor step within the `1.x` line.

The `12.x` A6 evidence satisfies neither promise, because both are scoped to the
`1.x` epoch.

---

## 6. Where each number lives

| Surface | Authority |
|---|---|
| `SharedVersion.cs` `[assembly: AssemblyVersion]` | **the** canonical server product version |
| `*/…​.csproj` `<VersionPrefix>` | derived; must equal the canonical version |
| `ApplicationVersion`, `/System/Info`, `/health` | read from the assembly at runtime |
| `openapi/openapi.json` `info.version`, `x-tesserafin-version` | generated from the assembly version |
| `openapi/contract.lock.json` | pins the generated contract by version and sha256 |
| server image tags, `org.opencontainers.image.version` | derived by `docker/version-contract.sh` from the canonical version |
| `org.opencontainers.image.revision` | the exact source commit; never a version |
| `package.json` `version` (tesserafin-web) | **the** canonical web product version |
| web-assets image tags, `org.opencontainers.image.version` | derived from `package.json` by `docker/build-assets.sh` |
| `org.tesserafin.web.revision` / `.version` / `.assets.image` | the exact web commit, web product version and immutable assets digest bundled into a server image |

Nothing else derives a version. A new consumer asks one of these; it does not
re-implement the derivation.

---

## 7. Publication rules for 1.1

Tracker [#276](https://github.com/tesserafin/tesserafin/issues/276).
Contract: [W5-A0](./distribution/W5-A0-acceptance-contract.md).

1.1 adds native artifacts beside the container: Linux `.deb`, `.rpm` and
`.tar.gz`, a `win-x64` portable ZIP and a `win-x64` MSI. §1–§6 still govern the
container. For 1.1, additionally:

* **The Git tag and the GitHub Release are W5-A4, and owner-only.** No agent
  slice, workflow or pull request creates either. W5-A4 created both: Git
  tag `v1.1.0` at `bb2234aebbf0ff2c1cbe291df48f2ffa62beea46`, and the GitHub
  Release published 13 September 2026. The owner approved withdrawal of both on
  2026-11-05. Deletion follows the documentation and website updates (#386), and that build is now the pre-launch `0.2.0` (§1).
* **The unsigned `win-x64` ZIP digest is the W5-A1 pin**,
  [`ci/windows/w5/accepted-unsigned-zip.json`](../ci/windows/w5/accepted-unsigned-zip.json):
  `zipSha256` `c1f6261cb770bd3dcbf255f4dd15b287a7289adad5a619d31725c158cd20e2d8`,
  `assembledAtHead` `41d3411c9838feec6650dd10ec89a1f198aafdeb`, evidence run
  `34766464798`. That pin names the ZIP built from that commit. It is not a
  claim that a rebuild from any other commit produces the same bytes: the
  archive records its own commit, so it does not.
* **The MSI has no bit-identical claim.** No slice built it twice and compared
  bytes, and no MSI digest is pinned.
* **Signing is a later transformation**
  ([W0 §8, §11](./distribution/W0-windows-server.md)). A signature is applied to
  the accepted unsigned bytes, never before acceptance, and a signed artifact
  must name the unsigned digest it was made from. The signing vendor is
  **UNDECIDED** ([W5-A0 §D](./distribution/W5-A0-acceptance-contract.md)).
  Unsigned artifacts remain publishable and usable.
