# Backup and recovery baseline

This baseline separates the recovery paths that already exist and the evidence
needed before extending them. It is not an import assistant or a promise of
cross-platform recovery. It implements a bounded part of
[tracker #144](https://github.com/tesserafin/tesserafin/issues/144).

## Choose the right archive

| Path | What it captures | How it is restored | Evidence and limits |
| --- | --- | --- | --- |
| Docker cold snapshot, `docker/backup.sh` | Whole `config` and `data` volumes, including databases, configuration and state stored there. Cache and source media are excluded. | `docker/restore.sh` into empty replacement volumes, then boot the selected server image. | Existing [A2 round trip](container/A2-persistent-state.md#verifying-the-contract) compares users, libraries and watched-state through the API. It does not certify nonzero resume position, favourites or a genuine forward migration. |
| Server-managed ZIP, `BackupService` | Database tables when `Database` is enabled (default); selected configuration files and managed folders; metadata, trickplay and subtitles according to options. It is not a whole-volume snapshot. | Administrator backup API schedules a restart and restore; `--restore-archive` is the process-level recovery entry point. | Existing [backup tests](../tests/Tesserafin.Server.Implementations.Tests/FullSystemBackup) cover content-pack round trips and archive boundaries. They do not establish whole-server disaster recovery on every platform. |
| Native Linux `.deb` cold snapshot | `/etc/tesserafin` and `/var/lib/tesserafin` with owners, modes, ACLs and xattrs. Cache, logs, the package payload and source media are excluded. | Reinstall the same package, replace both directories from the archive, then start the service. See [native `.deb` procedure](#native-linux-deb-recovery-procedure). | [Rehearsed](#native-deb-rehearsal-evidence) on the published `v1.1.0` amd64 `.deb`, same host after purge and state deletion. Not a fresh-host, forward-migration or RPM/portable result. |
| Native Windows and other Linux forms | Requires the actual configured persistent directories and service configuration. | Stop the actual server/service and use a platform-specific, independently rehearsed procedure. | [Linux paths](../packaging/linux/tesserafin.conf) and [Windows state/service rules](distribution/install-windows.md) are documented. The Docker scripts are not native backup tools; the x86_64 RPM has a [same-host rehearsal](#native-rpm-rehearsal); none exists for the portable archive or Windows. |

A `.tgz` from the Docker script and a server-managed `.zip` are different formats.
Do not feed one to the other's restore operation. A successful manifest listing is
not proof that the database and all required state can be recovered.

## Docker recovery procedure

Use this for the existing container baseline. Commands run from the repository
root, with Bash, Docker access, Python 3 and the host tools used by
[`backup.sh`](../docker/backup.sh). The pinned helper image must be available.
Examples use placeholder volume/container names: inspect the actual container's
mounts and deployment configuration first, rather than assuming Compose chose
these names.

1. Record the running image's immutable reference, the container name, actual
   configuration/data mounts, media mount paths and deployment files. Retain the
   Compose file and `.env` separately: a volume snapshot does not capture host-side
   deployment configuration. Record any external certificates/proxy configuration
   separately too. Keep source media on its own backup plan.
2. Choose a new archive filename in an existing private backup directory. Avoid
   overwriting an earlier recovery point: the script refuses an existing archive
   or either sidecar, including dangling symlinks, before stopping the source.
   Concurrent backups to that filename are excluded by an adjacent `.lock`
   directory. If a process was forcibly killed, verify no backup is active before
   removing its stale lock, or choose a fresh filename. When supplying `--container`, the script
   must successfully inspect that container before any snapshot; a missing or
   uninspectable container is an error, not evidence that the server is stopped.

   ```sh
   docker/backup.sh --out /backups/tesserafin-before-change.tgz \
     --config tf_config --data tf_data --container tesserafin
   ```

   The script stops a running source around the snapshot and restarts it. Check
   its exit status and then `/health`; a failed restart is an error even when an
   archive was created. Without `--container`, the operator must stop every
   writer before invoking the script. The scripts do not discover other
   containers or processes sharing the volumes, and do not lock out a concurrent
   administrator restarting one.
3. Retain the archive, `.sha256` and `.manifest.json` together on separate storage.
   The files contain users, tokens and private configuration; the script creates
   them owner-only. The manifest describes sources and exclusions, not a restore
   success. Files are published without replacing an existing destination; a
   failed attempt removes only sidecars it created. The checksum detects changed
   bytes when the sidecar is trusted; it
   does not authenticate the archive's origin.
4. Restore into new, empty volumes. Create each volume separately. Keep the
   original state intact and ensure no server writes to the replacement volumes.

   ```sh
   docker volume create tf_config_recovery
   docker volume create tf_data_recovery
   docker/restore.sh --archive /backups/tesserafin-before-change.tgz \
     --config tf_config_recovery --data tf_data_recovery
   ```

   Restore checks a checksum **if present**; retain and check the sidecar rather
   than relying on the missing-checksum warning. It checks archive layout and
   resets ownership to the container identity. `--force` empties a populated
   target, including hidden files, and may stop a named running container; it
   cannot bypass an unknown container state. Fresh volumes avoid that destructive
   path. Do not treat the archive-layout checks as permission to restore an
   untrusted archive.
5. Boot the recorded image against the replacement volumes, a fresh cache and
   the same media paths, using a separate local port/name while checking it.
   Reproduce the actual deployment's mount and runtime options. Confirm
   `/health`, sign-in, user/library identity, library visibility and playback
   state. Only switch the normal deployment to recovered volumes after checking
   the data. Stop the temporary instance before reusing those volumes.

For a failed upgrade, restore the **pre-upgrade** snapshot with its recorded old
image. Starting an old image on a database already migrated by a newer one is
not a rollback. A forward upgrade that crosses a real pending migration remains
[separate acceptance #127](https://github.com/tesserafin/tesserafin/issues/127).
There is no new backport or release in this procedure.

## Native Linux `.deb` recovery procedure

For the `tesserafin-server` package from the [installation guide](install.md#native-linux),
run as root. Paths are the package defaults in
[`tesserafin.conf`](../packaging/linux/tesserafin.conf); if you changed them, archive the
directories you actually configured.

1. Record the installed package file and its SHA-256 (`dpkg -s tesserafin-server`,
   the downloaded `.deb`), media paths, and any proxy/firewall configuration. Keep
   the `.deb` itself: recovery reinstalls the **same** package.
2. Stop the service and confirm nothing runs as the service account:

   ```sh
   systemctl stop tesserafin
   systemctl is-active tesserafin   # must not print "active"
   pgrep -u tesserafin              # must print nothing
   ```

3. Archive both state directories, keeping owners and modes, then write a checksum.
   Restart afterwards and check `/health`.

   ```sh
   tar -C / --acls --xattrs -cpzf /backups/tesserafin-native.tgz etc/tesserafin var/lib/tesserafin
   (cd /backups && sha256sum tesserafin-native.tgz > tesserafin-native.tgz.sha256)
   chmod 600 /backups/tesserafin-native.tgz /backups/tesserafin-native.tgz.sha256
   systemctl start tesserafin
   ```

   The archive contains users, tokens and private configuration. Store it with its
   sidecar on separate, access-controlled storage.
4. To recover, check the checksum first, then install the recorded package. A first
   install creates the `tesserafin` account and leaves the service stopped:

   ```sh
   (cd /backups && sha256sum -c tesserafin-native.tgz.sha256)
   apt install ./tesserafin-server_1.0.0-1_amd64.deb
   ```

5. With the service still stopped, replace both directories with the archive.
   As root, GNU tar restores owners by account **name**, so a recreated account with a
   different numeric UID still owns the files. The rehearsal below does not force a
   different UID.

   ```sh
   rm -rf /etc/tesserafin /var/lib/tesserafin
   tar -C / --acls --xattrs --same-owner -xpzf /backups/tesserafin-native.tgz
   systemctl start tesserafin
   ```

   Extract only an archive you created. Confirm `/health`, sign-in, users,
   libraries and playback state before relying on the instance. Media must be at
   the same paths and readable by the `tesserafin` account.

Do not restore a snapshot taken by a newer package onto an older one; that is not a
rollback. The Docker scripts (`docker/backup.sh`, `docker/restore.sh`) are not used here.

### Native `.deb` rehearsal evidence

[`ci/native-recovery-deb.sh`](../ci/native-recovery-deb.sh) performed this
procedure on a disposable Ubuntu 24.04 amd64 runner. The former native recovery
workflow downloaded the pre-launch packages and is retired ahead of their
deletion. The script remains for a future rehearsal using the launch packages.

| Step | What it asserts |
| --- | --- |
| Artifact | The `v1.1.0` release asset `tesserafin-server_1.0.0-1_amd64.deb`, SHA-256 `a01121d984b9cfbbe4f3e494ac1c72f16084f2c43c8cc682f4d365c9fff44495` (also listed in `SHA256SUMS-linux-x64.txt`), is checked before each install. Its control file names server commit `bb2234aebbf0ff2c1cbe291df48f2ffa62beea46` and Web commit `a9a362eec764a9fe3fa6ba9b4a7dd7473677e35a`. Nothing is built or published. |
| Install | Clean host (no package, account, state or media); first install leaves the unit enabled and stopped; fixture generated with the bundled FFmpeg is readable by the service account. |
| Populate | Same API fixture and [`recovery-state.py`](../docker/recovery-state.py) oracle as the container rehearsal: two users, policies, preferences, watched state, favourite, saved position, library, content pack, server name, pre-backup tokens. |
| Backup | Service stopped with no process left under the account; archive and sidecar root-owned `0600`; service restarts and becomes ready. |
| State loss | `apt purge`, then `/etc/tesserafin`, `/var/lib/tesserafin`, `/var/cache/tesserafin`, `/var/log/tesserafin`, the account and group deleted; nothing answers on the port. |
| Restore | Checksum and archive layout verified; same package reinstalled; data directory empty before extraction; every restored entry identical in content, owner, group and mode to the pre-backup listing; directories `tesserafin:tesserafin 0750`, `tesserafin.conf` `root:tesserafin 0640`. |
| Restored state | Server runs as `tesserafin`; byte-identical oracle snapshot using the original tokens; member password and fresh administrator login; users, libraries and watched state equal. |

Green run: [39973087073](https://github.com/tesserafin/tesserafin/actions/runs/39973087073)
at `1c4ab9f236150c856c6828cf5df23678a2365eed`, 27 assertions passed, 0 failed.
Retained artifact `native-recovery-deb-1c4ab9f2…` (`sha256:644eab9e788e7317ca5796b873a8ff47a4d1f5d0425867b70717993f07889564`,
expires 2026-11-06) holds only allowlisted JSON: source provenance, package
identity, stage list, counts and HTTP/systemd status codes.

What this does **not** prove:

- **Fresh host or disk loss.** Same runner, after purge and deletion; no off-host
  transfer of the archive. That is the [disk-loss pair](#native-deb-disk-loss-rehearsal) below.
- **Player resume.** The saved position is seeded through the user-data API; no
  client resumed playback.
- **Encryption or authenticity.** The archive is not encrypted; the checksum sidecar
  detects changed bytes, not origin.
- **Upgrade or migration.** Same package both sides; no schema change.
- **Other native forms.** RPM, portable `.tar.gz`, arm64 and Windows are not rehearsed.
  A custom `tesserafin.conf` path layout is not exercised.
- **Live or hot backup.** Only a stopped-service snapshot.

### Native `.rpm` rehearsal

[`ci/native-recovery-rpm.sh`](../ci/native-recovery-rpm.sh) runs the same procedure
and oracle (`native-recovery-deb.sh`, which follows the package extension) on the
`v1.1.0` asset `tesserafin-server-1.0.0-1.x86_64.rpm`, SHA-256
`35fa7435de643385e58b240e19a094a80fad661b8dc9e8bfd79ee1b6e2cfe369`. The host is the
digest-pinned Rocky 9 lifecycle image from `ci/package/accept-rpm.sh`, with systemd as
PID 1 on native x86_64, in a privileged container that is discarded afterwards;
`dnf remove` replaces `apt purge`. Evidence phase `native-rpm`. Every `.deb` limit
applies, plus: a container rather than a VM, and no RPM disk-loss or aarch64 run.

### Native `.deb` disk-loss rehearsal

The same workflow also splits the procedure across two hosted runners
(`TF_NATIVE_RECOVERY_PHASE=source|recover`):

| Job | What it asserts |
| --- | --- |
| Source host | Steps install → populate → cold backup, as above. The archive, its checksum, the pre-backup listing and the private oracle fixture leave the runner as a one-day artifact. The package, media, logs and evidence are not in it. |
| Fresh host | A different boot ID; no package, account, state directory or media before it starts. It downloads the same pinned `.deb` itself and checks the digest, verifies the handed-off checksum, installs, regenerates the media fixture at the same path, restores, and runs the same file-listing and `recovery-state.py` oracle. The handoff artifact is deleted afterwards. |

Accepted only when both jobs pass on the same commit (evidence phases
`native-deb-source` and `native-deb-recover`; boot-ID hashes in `hosts.json`). Every
limit above except the first still applies: amd64 `.deb` only, same package, no
encryption or authenticity, user-data-API position rather than player resume.

## What existing evidence proves

[`docker/state-roundtrip.sh`](../docker/state-roundtrip.sh) already creates an
administrator and movie library, records watched-state, takes a cold snapshot,
removes the source container, restores into independent volumes and compares
state after boot. It also checks permissions, restart failure, archive layout,
forced cleanup, pinned helpers, JSON manifests and bind mounts with spaces.
Run it against an exact available image reference; merely finding the test in
the repository does not mean it ran against a new artifact.

The new container-preflight check runs the real backup/restore shell entry
points with a controlled Docker process boundary. It proves refusal before volume
access or output cleanup for failed, empty and malformed state observations,
including restore with `--force`. Positive controls reach the helper for a known
stopped container and stop a running backup source before snapshotting. It does
**not** simulate a database restore or certify a Docker daemon.

The output-preservation check also drives the actual backup CLI. It verifies
refusal of an existing archive, either sidecar, dangling symlinks and an existing
lock before volume access; failed-snapshot cleanup; private successful output;
and collisions introduced during snapshotting. This protects earlier recovery
points without claiming a new artifact-based server restore run.

```sh
python3 ci/tests/backup-container-preflight.test.py
python3 ci/tests/backup-output-preservation.test.py
python3 ci/tests/recovery-state.test.py
# Requires Docker and the selected image; this is a separate, heavier test:
docker/state-roundtrip.sh <immutable-image-reference> <unused-local-port>
```

## Disk-loss recovery rehearsal

The same script can be split across two hosts with `TF_ROUNDTRIP_PHASE`:

```sh
# Source host: populate, seed, cold snapshot; writes into an existing empty directory.
TF_ROUNDTRIP_PHASE=source TF_ROUNDTRIP_HANDOFF=<dir> docker/state-roundtrip.sh <image> <port>
# Copy <dir> off the host. On a host that never held the source volumes:
TF_ROUNDTRIP_PHASE=recover TF_ROUNDTRIP_HANDOFF=<dir> docker/state-roundtrip.sh <image> <port>
```

The recovery phase first requires that the host holds no `tf_a2_` volumes. It
then requires the archive, both sidecars and the private fixture, and checks the
archive against its retained `.sha256` before restoring. `restore.sh` alone only
warns when that sidecar is missing. The media fixture is regenerated at the same
path, because media is on its own backup plan. Steps 4–7 then run unchanged,
including the byte-identical before/after oracle comparison using the original
pre-backup tokens.

The `Backup recovery rehearsal` workflow runs this on two GitHub-hosted runners.
The source job builds the exact candidate without publishing it. It hands the
snapshot, sidecars, fixture and `docker save` output of that image to the
recovery job through an artifact kept for one day at most, which the recovery
job deletes when it finishes. The recovery job loads the image, re-checks its
image ID, labels and user, and confirms that its kernel boot ID differs from the
source runner's. Only allowlisted JSON (provenance, image identity, host
boot-ID hashes, phase results) is retained.

The handoff artifact contains the disposable fixture server's database, tokens
and fixture passwords. That server never exists outside the job. Use a separate,
access-controlled store for real backups; this rehearsal does not test
encryption, transfer authenticity or a particular off-host storage product.

## Extended recovery rehearsal (prepared, not executed here)

The A2 script now uses [`docker/recovery-state.py`](../docker/recovery-state.py)
to seed and capture a second user before backup, and to validate and compare an
independent restored instance. Its oracle requires nonempty expected values both
before and after, so equally empty snapshots do not pass:

| State | Assertion prepared in the rehearsal |
| --- | --- |
| User identity and credentials | Both user IDs are stable; both pre-backup tokens still identify their respective users; the member password authenticates again, alongside the existing fresh administrator login. |
| Policy and user preferences | Full API policy/configuration snapshots are equal; the member is non-administrator, has audio transcoding disabled, prefers French audio and disables next-episode autoplay. |
| Per-user item state | The administrator retains watched-state; the member retains a distinct favourite, play count, last-played date and a nonzero, non-completed saved position for the same item. |
| Libraries | IDs, names, collection type, mount paths and library options are equal. |
| Server configuration | A deliberately changed server name is preserved. This is one setting, not every possible external service/plugin setting. |
| Content packs | Created pack ID/metadata and exact member item IDs are equal. |

The saved position is seeded through the public user-data API, independently of
the existing playback-report fixture. It checks persisted user state, not a
browser's ability to resume a playable media version or A/V synchronization.
Tokens and fixture snapshots remain in the private temporary work directory and
are removed by cleanup; mismatch output does not print their contents.

The oracle's local tests reject missing favourites/positions, mixed user state,
policy/preference loss, missing libraries, changed pack identity/membership and
server-setting loss. These tests do **not** execute Docker or certify a server
artifact. A full run on the selected immutable current-contract image is still
required before treating any new assertion as recovery evidence. Native service,
off-host/disk-loss and genuine cross-schema migration rehearsals remain separate.
The native `.deb` rehearsal reuses this oracle (see [above](#native-deb-rehearsal-evidence)).

Readiness is judged from `/health` (200 only once core startup has completed),
not from `/Startup/Configuration`: after the wizard is complete, authorization
answers that endpoint 401 before the startup middleware runs, so it reported a
restored instance ready while authenticated calls still received 503. The
hosted evidence keeps allowlisted `DIAG` observations only: container restart
count, state and exit code, and HTTP status codes for `/health`,
`/Startup/Configuration` and `/Users/Me` (including a 30-second observation
window after a failed capture). It never retains bodies, tokens or log text,
and the observations never retry or replace an assertion.

## Remaining acceptance work

These are gaps in evidence, not claims that the corresponding data is known to
be lost. Create implementation issues only after a reproducible defect.

| Scenario | Required evidence before claiming it |
| --- | --- |
| Favourites and resume position | Execute the extended rehearsal on the selected immutable image. The oracle now requires a favourite and nonzero saved position by user/item identity, but this change does not supply a new Docker execution or real player-resume proof. |
| Native Linux and Windows disaster recovery | The amd64 `.deb` [same-host rehearsal](#native-deb-rehearsal-evidence) is green, and the [disk-loss pair](#native-deb-disk-loss-rehearsal) covers a fresh host once its hosted run is green. The x86_64 `.rpm` has a [same-host rehearsal](#native-rpm-rehearsal) once its hosted run is green. Still open: RPM disk-loss and aarch64, portable archive, arm64 and Windows (ZIP/service), each with an artifact-based stopped-service backup/restore preserving persistent directories, credentials, service identity/permissions and media paths. |
| Newer database schema | Genuine published-image migration with preservation assertions under #127; an unchanged-schema round trip is insufficient. |
| Disk-loss recovery | Off-host copy available, matching artifact and deployment configuration retained, and an isolated recovery with source storage unavailable. The [two-host rehearsal](#disk-loss-recovery-rehearsal) covers the container path once a hosted run is green; the native amd64 `.deb` has its own [disk-loss pair](#native-deb-disk-loss-rehearsal). |
| Import from another product | Separately selected source/version and identity mapping; not restoring a Tesserafin backup and not assuming Jellyfin compatibility. |

No scheduled-backup service, retention UI, recovery key, automatic rollback or
managed-publication recovery guarantee is introduced here.
