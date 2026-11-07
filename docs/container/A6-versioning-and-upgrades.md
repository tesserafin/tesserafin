# A6 — Image versioning, channels and the upgrade contract

The public pre-launch image `0.1.0` is unsupported and scheduled for
withdrawal. No supported public replacement is available until launch.

Issue [#92](https://github.com/tesserafin/tesserafin/issues/92). This is
both the operator-facing upgrade contract and the A6 implementation note: the
audit that preceded the change is recorded in the last section.

Prerequisites: [A1](./A1-implementation-note.md) (the image),
[A2](./A2-persistent-state.md) (volumes, migrations, backup/restore),
[A3](./A3-guided-install.md) (installation).

> **Current operator scope.** Set an accessible immutable image reference as
> described in [the installation guide](../install.md). The development/audit evidence in the
> later sections is retained at its original paths and identities; it is not a
> source of supported installation defaults. No mutable public channel exists.
>
> The historical harness proves state preservation between its recorded builds.
> It does not prove a pending forward migration between published `1.x` images;
> [#127](https://github.com/tesserafin/tesserafin/issues/127) remains the
> separate acceptance gate. No database downgrade or backport is promised here.

---

## 1. Where the version comes from

`SharedVersion.cs` supplies the numeric application version. Ask the existing
contract rather than inferring it from a release label:

```sh
docker/version-contract.sh version
```

The published `v1.1.0` native assets still contain `1.0.0` in their filenames and
application version. Those are different identifiers, not a reason to rename the
downloads. [The version policy](../versioning-policy.md) owns this distinction.
Image labels, `/health`, `/System/Info/Public` and build metadata must agree with
the application version of the artifact they describe.

## 2. The tag policy

[The version policy](../versioning-policy.md) is authoritative for publication.
The old public container default is unsupported and scheduled for withdrawal.
Compose requires `TESSERAFIN_IMAGE`. There is no published
`latest`, `stable`, `preview`, `1` or `1.0` channel. Do not invent one or use a
private development package as an installation fallback.

Development tag derivation in `docker/version-contract.sh` serves build
provenance; it does not publish a release. Historical `12.x` references below
belong to the frozen archive and are outside the supported `1.x` upgrade graph.

### Compose policy

Set `TESSERAFIN_IMAGE` in `.env` to an accessible immutable reference. Pulling that reference does not choose
a future version for you. Before deliberately replacing it, record the old
reference and take the backup in section 4. See section 3 for inspection commands.

---

## 3. Inspecting what you are actually running

Three independent surfaces, which must agree. If they ever disagree, the image
was not produced by the contract and should not be trusted. Export
`TESSERAFIN_IMAGE` in your shell for the commands below.

```console
# What the image claims
$ docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.version"}} {{index .Config.Labels "org.opencontainers.image.revision"}}' \
    "$TESSERAFIN_IMAGE"

# Which exact image is running, by digest
$ docker inspect --format '{{.Image}} {{.Config.Image}}' tesserafin
$ docker image inspect --format '{{index .RepoDigests 0}}' <image>

# What the running application reports
$ curl -fsS http://localhost:8096/health
{"status":"healthy","version":"1.0.0","database":"healthy"}
```

`/health` answers the same JSON schema from the moment the container starts. It
returns `503` with `"status":"starting"` while the server is coming up — that is
the readiness contract, not a fault. Readiness is `200` **and**
`"status":"healthy"`; a bare 200 from another endpoint is not readiness.

To check everything at once against a real container:

```console
$ docker/version-verify.sh "$TESSERAFIN_IMAGE" --require-digest
```

### Pinning to an exact version or digest

```dotenv
# .env — digest pin, the strongest form; survives a tag being re-pointed
TESSERAFIN_IMAGE=<registry>/<image>@sha256:<digest>
```

Resolve a tag to its digest before pinning:

```console
$ docker pull "$TESSERAFIN_IMAGE"
$ docker image inspect --format '{{index .RepoDigests 0}}' "$TESSERAFIN_IMAGE"
```

---

## 4. The upgrade contract

### What is supported

* **Use only an explicitly published and accepted target.** A release label is
  not a verified database migration boundary. When newer minor targets exist,
  proceed through intervening minors; skipping them has no acceptance evidence.
  The genuine published-image forward-migration proof remains #127.
* **Migrations run automatically on container replacement.** There is no manual
  migration command, no SQL to run, and no maintenance mode. The server runs its
  migration routines during startup and only then answers `/health` as healthy.
* **A backup is required before any upgrade.** Not advisory — this is the only
  supported recovery path.
* **Database downgrades are unsupported** unless a specific downgrade has been
  explicitly proven, and none has been.
* **Rolling the container back does not roll the database back.** Once the newer
  image has started and migrated, the older image may be unable to read the
  result. Recovery from a bad upgrade is *restore the pre-upgrade backup*, not
  *re-run the old tag*.
* Recovery uses the A2 backup/restore mechanism — `docker/backup.sh` and
  `docker/restore.sh`, documented in [A2](./A2-persistent-state.md).

### The procedure

```console
# 1. Back up first. This stops the server, snapshots /config and /data, restarts it.
$ docker/backup.sh --out ~/tesserafin-preupgrade.tgz \
    --config tesserafin_config --data tesserafin_data --container tesserafin

# 2. Choose the new immutable reference and record the old one.
$ docker image inspect --format '{{index .RepoDigests 0}}' <current image>   # write this down

# 3. Point .env at the new version and replace ONLY the container.
$ $EDITOR .env          # TESSERAFIN_IMAGE=...:<new immutable tag or digest>
$ docker compose pull
$ docker compose up -d  # recreates the container; the named volumes are kept

# 4. Wait for the real readiness contract, not for the container to exist.
$ until curl -fsS http://localhost:8096/health | grep -q '"status":"healthy"'; do sleep 2; done

# 5. Confirm the version actually moved, and that the migration path ran.
$ curl -fsS http://localhost:8096/health
$ docker compose logs tesserafin | grep -i "migration"
```

If step 4 never completes, do **not** start deleting volumes. Read
`docker compose logs tesserafin`, then restore the step-1 backup into fresh
volumes with `docker/restore.sh` and start the previous image against those.

`docker compose down` removes the container and keeps the named volumes;
`docker compose down -v` **deletes them**. Never use `-v` as part of an upgrade.

### What lives in each volume

| Mount | Contents | Survives an upgrade |
|---|---|---|
| `/config` | server configuration XML, user policy, plugin configuration, migration state | yes — and it is what the migration routines rewrite |
| `/data` | the SQLite databases (library, users, activity), metadata, images | yes — this is the volume a bad upgrade would damage |
| `/cache` | transcode scratch, image cache, other regenerable derivatives | yes, but losing it is harmless |
| `/media` | your library, mounted read-only | never written by the server |

Back up `/config` and `/data`. `/cache` is regenerable and is deliberately not
part of the backup archive.

### The automated rehearsal

```console
$ docker/upgrade-roundtrip.sh \
    --baseline  ghcr.io/tesserafin/tesserafin@sha256:<baseline digest> \
    --candidate ghcr.io/tesserafin/tesserafin@sha256:<candidate digest>
```

It boots the baseline by digest on fresh volumes, onboards it, creates two user
identities, a library, a visible media item, playback state and a configuration
change; replaces **only** the container; starts the candidate by digest on the
same three volumes; waits for `/health` healthy; and compares a semantic
before/after state that includes ids, not just names. It cleans up on success and
on failure unless `--keep` is passed.

It reports the number of pending migrations the runner found rather than
asserting one. `--require-migration` turns a zero count into a failure, which is
the flag to use once an honest migration boundary exists between the baseline and
the candidate.

---

## 5. Releasing

```console
# 1. Move the canonical version, verify every declaration agrees.
$ ./bump_version 12.1.0
$ ./bump_version --check

# 2. Commit, then tag. The contract refuses a tag that disagrees with the source.
$ git tag v12.1.0
$ docker/version-contract.sh verify-tag v12.1.0

# 3. Dry-run the tags the release is allowed to publish.
$ docker/version-contract.sh check --channel stable --release-tag v12.1.0

# 4. Build and push.
$ docker/build-clean.sh --target server --output push --channel stable --release-tag v12.1.0
```

The contract refuses, non-zero and before anything is built:

* a malformed canonical version;
* a git tag whose core differs from `SharedVersion.cs`;
* `latest` from a dev or pre-release build;
* missing or malformed commit provenance;
* a release from a dirty working tree, unless `--allow-dirty` is passed — which
  prints the dirty file list to stderr and records `TESSERAFIN_DIRTY_RELEASE=1`
  in the emitted environment.

`.github/workflows/release-version.yaml` runs the same assertions on a published
release and, separately, can propose the next development version as a pull
request. **Hosted GitHub Actions are parked for this repository** (#62;
restoration is tracked as #94). That workflow is correctly wired but has not been
executed on hosted infrastructure; the assertions are currently run locally.

---

## 5b. The validated A6 image (historical, pre-v1 archive)

Retained with its original names and digests as the A6 evidence record. It is a
pre-v1 development artifact in the frozen `tesserafin` archive package and is **not**
the installation default; see §5c for the current baseline.


```
ghcr.io/tesserafin/tesserafin:12.0.0-dev.a8a18bb7c07b
```

| | Digest |
|---|---|
| Multi-arch manifest | `sha256:29493c0ecf956a61f06c2b801e7c867d560fb95ab184c09d05fdb6db508a7722` |
| `linux/amd64` | `sha256:c0b6af488905270fb18a33577e753d2a9662e9bf591fc68dd729c10f697b200d` |
| `linux/arm64` | `sha256:02b836e8d799ec3a8c4d8d332d574809415a0095216f75f26c936a2214d1e5f5` |

Built from `a8a18bb7c07b3d629ecab019aea9668a145eb5bc`. The equally immutable
`sha-a8a18bb7c07b3d629ecab019aea9668a145eb5bc` tag names the same manifest, and
`docker-compose.yml` pins the manifest digest.

Gates were run against that digest on `linux/amd64`: `docker/smoke.sh`,
`docker/observability.sh`, `docker/state-roundtrip.sh`,
`docker/browser-onboarding.sh`, `docker/version-verify.sh --require-digest`, and
`docker/upgrade-roundtrip.sh` from the A5 image
`sha256:6e3dbaab6eeaef163e81f9cc5ffb03f5a05bb9d8165e3f6487b2bb3003bc7608`.
The upgrade run reported **0 pending migrations** — see §6 for why that is the
honest outcome today.

Pull it by digest rather than by tag if you want the guarantee in writing:

```console
$ docker pull ghcr.io/tesserafin/tesserafin@sha256:29493c0ecf956a61f06c2b801e7c867d560fb95ab184c09d05fdb6db508a7722
```

---

## 5c. The published Tesserafin Server 1.0.0 release

Historical provenance of the unsupported pre-launch `0.1.0` image, published
as `1.0.0`. It was the installation default before the withdrawal decision.

```
ghcr.io/tesserafin/server:1.0.0
```

| | Digest |
|---|---|
| Multi-arch manifest | `sha256:bc7587bc7d99b40d77770046d49a2931ff8162b6d1df3da8d3f108b7677d7dc3` |
| `linux/amd64` | `sha256:63961ace461302693646a77e3204b1706d5cdc66a38152c910849c4a7b0912c8` |
| `linux/arm64` | `sha256:27f555cc38c999dd9aa84ce66caa47f4dbb0f0e041a061c010e6a470ba004a28` |

Built from `6227dbef66c631081e3179a521561b32b94dac59`; reports version `1.0.0`.
The `1.0.0` tag is immutable and is never re-pointed. The same manifest also exists
in the private build package as
`ghcr.io/tesserafin/tesserafin-server:1.0.0-dev.6227dbef66c6` and
`…:sha-6227dbef66c631081e3179a521561b32b94dac59`; those are build provenance, not
installation references.

### It supersedes the B2 candidate, which is not deleted

| | Digest |
|---|---|
| Multi-arch manifest | `sha256:636c3038a7eb34c5749c938a725f81998f85610d53b8d8598e4ca251eddb15d9` |
| `linux/amd64` | `sha256:db1201535094b768716bb136508b0229ad28a8f447968e98f2afb84f618a9651` |
| `linux/arm64` | `sha256:1d0cf9351f9ff1b71fe4339de9bffa0f59da999e160c6958c1fa7d4effea20fb` |

Tag `1.0.0-dev.28ace1b74f42`, built from
`28ace1b74f42ae3c5e86c9dbb54f7977e34d541b`. Its B2 presentation evidence still
stands, but it carried three third-party metadata provider credentials inherited
from upstream — TheMovieDb, TheAudioDB and OMDb — compiled into
`Tesserafin.Providers.dll` as .NET `const string`s in the UTF-16LE metadata heap,
where a filesystem secret scan does not see them:

> **SUPERSEDED — contains inherited upstream provider credentials; do not use**

It remains published and immutable and is **not** deleted, so historical evidence
that names it stays valid. It is simply no longer an installation default. The
replacement above is the same tree with those credentials removed and nothing
else changed; the three providers are now operator-supplied only. See
[`../metadata-provider-keys.md`](../metadata-provider-keys.md),
[`../provider-auth-audit.md`](../provider-auth-audit.md) and
[tesserafin#174](https://github.com/tesserafin/tesserafin/issues/174). `docker-compose.yml` originally pinned the manifest digest; the dev tag is
provenance and an alternative spelling, never a moving channel. `1.0.0`, `1.0`,
`1`, `latest`, `stable` and `preview` are **not** published.

Bundled Tesserafin Web `1.0.0` at commit
`a9a362eec764a9fe3fa6ba9b4a7dd7473677e35a`
(`ghcr.io/tesserafin/tesserafin-web-assets@sha256:6150380052c8a3a154a8a25a9f40a741175a7563afdf89284f9c1f46d3042a6c`,
tag `1.0.0-dev.a9a362eec764`).

This is the accepted **B2** presentation/responsive/a11y release candidate
([tesserafin-web#55](https://github.com/tesserafin/tesserafin-web/issues/55),
closed as completed). Its B2 evidence — three consecutive rounds of the complete
127-test tesserafin-web browser suite, three separate pristine-instance onboarding
rounds, `npm run validate:full` (901 tests) and the bundle budget — is
[on that issue](https://github.com/tesserafin/tesserafin-web/issues/55#issuecomment-5119431103),
with [maintainer visual acceptance](https://github.com/tesserafin/tesserafin-web/issues/55#issuecomment-5122635858)
across desktop, mobile and TV.

Gates re-run against that digest when it was promoted to the installation
default: `docker/version-verify.sh --require-digest --expect-commit 28ace1b74f42…
--expect-version 1.0.0`, `docker/compose-smoke.sh` (which runs the real browser
onboarding suite through the shipped Compose file, so it exercises the new
default reference itself), and `ci/verify-release-pair.sh --lifecycle-runs 3`
with no skip flag — all seven A7 layers PASS, three consecutive lifecycle rounds
from a new container, new volumes and newly synthesised fixtures.
`ci/verify-release-pair.sh` ran on its **default** spec list — the
contract-critical playback set. It is deliberately not pointed at the whole
`tests/e2e` tree: the Section B specs (`b2-onboarding.spec.ts` in particular)
assert their own precondition of an *un-onboarded* container, and the A7 rig
seeds an administrator before the suite starts, so passing `--e2e-spec tests/e2e`
makes that spec abort on its guard rather than test the image. The full 127-test
Section B suite is B2's gate and its evidence is on tesserafin-web#55.
Promoting this digest rebuilt, retagged and republished nothing: the image was
already published and immutable, and only the references in this repository
moved.

### The candidate this one replaced

`sha256:89dd01add7cbe7fd1d1529979f6aa4e6537c9b4b31e2ebec1836a583548a1bf9`
(tag `1.0.0-dev.a8ac09f3ff5a`, server `a8ac09f3ff5a715b35b9dc31d1b23c5865a6d34e`,
bundled web `a63cb11e8e9cfa137b6c3f739e8881a6dfb39dfb`) was the baseline named here
before. It is **not** deleted, retagged or moved. It passed its own gates — its
record is [`A7-server-web-release-pair.md`](./A7-server-web-release-pair.md) §6c —
and was superseded only because the B2 presentation/responsive/a11y work landed a
newer web client, which forced a new web-assets publication and a new server
candidate. It is no longer an installation default.

### The candidate that one replaced

`sha256:fd1fa9e0f5a28a07e5872cc5ff13257a92d988717a33519f62c4b26c6ab36249`
(tag `1.0.0-dev.44f5ab62b522`, server `44f5ab62b522684b4fa58ed10de80b8c6a7bb392`,
bundled web `489a90be0dbe80aede3dbbc028b140756211d43c`) was the baseline named here
before. It is **not** deleted, retagged or moved, and it did **not** pass
tesserafin/tesserafin-web#67: it shipped the web client in which a
terminal playback failure was silent. Its own record, including that failure, is
[`A7-server-web-release-pair.md`](./A7-server-web-release-pair.md) §6b. It is no
longer an installation default.

No forward migration is claimed. The upgrade contract in §6 is unchanged: there is
still no real schema migration proving a `12.x` → `1.x` upgrade, which is why #127
remains open and A6 remains unchecked in #104.

### The superseded candidate

```
ghcr.io/tesserafin/tesserafin-server:1.0.0-dev.965fadf37e20
sha256:fffb46a41919dfedf0d5bacc68b9c37ebbca0400df74b487ba5858b395440e5c
```

Published immutable candidate; failed A3/A7 browser acceptance because it bundled
the upstream Jellyfin `10.10.0` minimum-version boundary; never pinned as an
installation default; superseded by the validated replacement above. It remains
published, immutable and private, and is not deleted, retagged, moved or
overwritten. Do not install it.

---

## 6. Audit — the version system before A6

Recorded for review; every item below is either fixed by A6 or explicitly left
alone with a reason.

**Canonical source.** `SharedVersion.cs`. Unambiguous, and unchanged by A6.

**Duplicate / divergent version sources.**

| Location | Problem | Disposition |
|---|---|---|
| `docker/build-clean.sh` | its own `grep -oP 'AssemblyVersion…'` | now calls the contract |
| `docker/state-roundtrip.sh` | a second copy of the same grep plus its own tag string | now calls the contract |
| `docker-bake.hcl` | derived `<version>-dev.<short(VCS_REF)>` itself | derives nothing; consumes `TAGS` |
| `src/Tesserafin.Database/…Implementations.csproj` | `<VersionPrefix>10.11.0</VersionPrefix>` against a 12.0.0 source | aligned to 12.0.0 |
| `src/Tesserafin.MediaEncoding.Keyframes.csproj` | `<VersionPrefix>10.11.0</VersionPrefix>` | aligned to 12.0.0 |
| `.github/ISSUE_TEMPLATE/issue report.yml` | a hard-coded 10.11.x dropdown | left as-is; it is a Jellyfin-era support-version picker, not a version source, and rewriting it belongs to #101 |

**References to files that no longer exist.**

* `release-bump-version.yaml` read `yq e '.version' build.yaml`. There is no
  `build.yaml` in this repository, so the workflow could never complete its first
  step. Replaced.
* `bump_version` hard-coded six csproj paths, four of which
  (`MediaBrowser.Common`, `MediaBrowser.Controller`, `MediaBrowser.Model`,
  `Emby.Naming`) disappeared in the Tesserafin rename. It therefore silently
  skipped the real projects — which is how the two 10.11.0 declarations above
  survived. Rewritten to discover every csproj declaring a `<VersionPrefix>`.

**Stale Jellyfin/Reefin identity in release tooling.**

* `release-bump-version.yaml` committed as `jellyfin-bot <team@jellyfin.org>` and
  waited on a `jitterbit/await-check-suites` deploy gate this fork does not have.
  Removed with the file.
* `.github/workflows/commands.yml` still reacts to `@jellyfin-bot` comments. Left
  untouched: A6 does not touch that file, and rewriting the bot-command surface
  belongs to the identity work in #101.

**Where a disagreement was possible before A6.**

1. A git release tag could name any version; nothing compared it to
   `SharedVersion.cs`.
2. `release-bump-version.yaml` pushed a patch bump onto the release branch the
   moment a release was published — mutating the source version while that
   release's images were still being built.
3. Bake's tag interpolation and the build script's version grep were independent;
   a change to either alone produced a mislabelled image with no failure.
4. Two csproj files published NuGet packages at 10.11.0 from a 12.0.0 tree.
5. Nothing compared the OCI labels, the `/health` version and the tag of a
   produced image. `docker/version-verify.sh` now does.

**Migration boundary honesty.** The migration system (`TesserafinMigrationService`
plus the EF Core SQLite migrations) is capable of providing a real upgrade
boundary — it logs `There are N migrations for stage <Stage>` and applies them at
startup. It cannot provide one *today*: the last commit to touch either migration
tree is `7dcf867334` (2026-07-21), and every image published to GHCR was built
from a later commit. Baseline and candidate therefore share an identical
migration set, and the runner correctly reports zero pending migrations. A6
deliberately does not invent a no-op migration to colour the gate green; see the
blocker referenced from #92.
