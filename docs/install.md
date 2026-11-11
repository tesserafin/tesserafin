# Install Tesserafin

**No supported public download is available until the `1.0.0` launch.**
Withdrawal of the pre-launch releases was approved on 2026-11-05. The procedures
below document the historical packages and installations with an existing artifact. Each path includes Tesserafin Web, which you
open in a browser; there is no native desktop client to install. Mobile/TV
foundations are tracked separately and are not a released app.

| Your environment | Installation status | Instructions and limits |
| --- | --- | --- |
| Linux host or NAS with Docker | No supported public image. Compose requires `TESSERAFIN_IMAGE`. | [Docker/NAS guide](container/A3-guided-install.md). Unraid/DSM guidance does not certify every NAS model; [independent reports](https://github.com/tesserafin/tesserafin/issues/117) remain incomplete. |
| Native Linux | `.deb`, `.rpm`, `.tar.gz` for x64 and arm64 — **no supported download** (pre-launch withdrawal approved) | [Native Linux below](#native-linux), then the [L0 platform contract](distribution/L0-linux-packages.md). Unsigned downloads, no APT/DNF repository. |
| Windows x64 | Portable ZIP — **no supported download** (pre-launch withdrawal approved) | [Windows install guide](distribution/install-windows.md). Unsigned, no published MSI or Windows arm64 artifact; Windows hardware acceleration is not validated. |

The unsupported pre-launch build `0.2.0` was labelled `v1.1.0` while its packages carried
`1.0.0`. See [versioning policy](versioning-policy.md) and [release contents](../CHANGELOG.md).

## Docker and NAS

Follow [the five-step Docker/NAS guide](container/A3-guided-install.md). Save the
repository's [Compose file](../docker-compose.yml) and copy
[`.env.example`](../.env.example) to configure media paths.

Set `TESSERAFIN_IMAGE` in `.env` to an accessible immutable image reference.
Compose has no default until launch. The former public default (pre-launch
`0.1.0`) is unsupported and scheduled for withdrawal. Private build packages
serve as development evidence.

Named volumes retain configuration and data across container replacement;
source media is mounted read-only. The guide explains host bind mounts and
permissions. Keep the initial installation on a trusted local network; a wizard
setting does not configure HTTPS, a firewall or a router for public access.

## Native Linux

**No supported package download is available.** The pre-launch `0.2.0` release
(published as `v1.1.0`) is scheduled for deletion. The steps below describe
its packages for operators who already hold a copy.

The lifecycle environments recorded in L0 are Ubuntu 24.04 (DEB), Rocky Linux 9
(RPM), and Debian 12 (portable), on architecture-native x64/arm64 runners. Other
distributions are not implied by the architecture name alone.

| Format | x64 filename | arm64 filename |
| --- | --- | --- |
| DEB | `tesserafin-server_1.0.0-1_amd64.deb` | `tesserafin-server_1.0.0-1_arm64.deb` |
| RPM | `tesserafin-server-1.0.0-1.x86_64.rpm` | `tesserafin-server-1.0.0-1.aarch64.rpm` |
| Portable | `tesserafin-server-1.0.0-linux-x64.tar.gz` | `tesserafin-server-1.0.0-linux-arm64.tar.gz` |

For an existing x64 package, place its original `SHA256SUMS-linux-x64.txt` in the same directory:

```sh
sha256sum -c --ignore-missing SHA256SUMS-linux-x64.txt
```

The output must explicitly name your downloaded file followed by `OK`.
`--ignore-missing` skips absent files, including higher-revision package names in
the checksum list that are not attached to the release. A skipped filename has
not been checked. There is no corresponding arm64 checksum-list asset. Neither
packages nor checksum lists are signed; a checksum served beside a file detects
changed bytes but does not establish independent authenticity.

On the matching distribution, install the downloaded package. For example:

```sh
# Ubuntu 24.04, x64, from the directory containing the verified DEB:
sudo apt install ./tesserafin-server_1.0.0-1_amd64.deb
# Or Rocky Linux 9, x64, from the directory containing the verified RPM:
sudo dnf install ./tesserafin-server-1.0.0-1.x86_64.rpm
```

Choose one package manager, not both. On arm64 use the corresponding filename
from the table and obtain/verify the artifact according to your trust policy;
this guide does not invent a signed repository or a missing checksum list.
For the portable archive, follow the README inside that artifact; it installs no
systemd unit.

A first package installation enables the service but leaves it stopped. Review
`/etc/tesserafin/tesserafin.conf`, your media paths and network exposure before
starting it:

```sh
sudo systemctl start tesserafin
sudo systemctl status tesserafin
sudo journalctl -u tesserafin -n 50 --no-pager
```

The package uses the unprivileged `tesserafin` account. Give that account read
access to the media directories you select in the wizard. The persistent paths,
FFmpeg layout, declared environments, GPU evidence and uninstall/state retention
are owned by [L0](distribution/L0-linux-packages.md). Do not use Docker backup
commands against native directories as if they were Docker volumes. For a
stopped-service snapshot of a `.deb` installation, see
[native backup and recovery](backup-recovery.md#native-linux-deb-recovery-procedure).

## Windows x64

Use the [Windows guide](distribution/install-windows.md) to check an existing
`tesserafin-server_1.0.0_win-x64.zip`, extract it and start it with explicit state
directories outside the package tree. Its accepted SHA-256 is recorded in
[`accepted-unsigned-zip.json`](../ci/windows/w5/accepted-unsigned-zip.json).
A rebuild from a different commit is a different ZIP.

The ZIP includes an optional service-registration script, whose limits are
specified in that guide. The MSI sections describe engineering acceptance of
another packaging form; no MSI asset is attached to `v1.1.0`. Do not choose a
download that does not exist. Nothing here changes signing or SmartScreen policy.

## First connection and troubleshooting

Open `http://localhost:8096/` on the server machine, or use its local address from
another device. Use your configured port/base path if you changed them. A new
server takes you to the Web onboarding wizard: language, administrator account
and first library. [The user guide](user-guide.md) describes the next steps.
Metadata providers require your own optional credentials; see
[provider configuration](metadata-provider-keys.md).

[Share your installation results](installation-reports.md), whether the setup
worked, failed or remains incomplete. Independent native ARM64 and NAS reports
help verify the guides; the form records onboarding and state persistence
separately.

| Symptom | Check |
| --- | --- |
| Connection refused | Process/service/container status, configured address/port and host network policy. |
| `503` while starting | Allow migrations to finish; read logs if startup fails or does not progress. |
| API docs instead of Web onboarding | Confirm the selected artifact and bundled Web path; an API-only development image is not the installation path. |
| Permission denied for state or media | Use the platform's actual runtime identity and configured paths; see A2 or L0. |
| Private-package authentication prompt | Confirm you have access to the selected registry and image. |

After setup, use [the administrator guide](admin-guide.md) for health, logs,
backup and upgrade procedures. Back up before replacing an artifact; restoring
pre-upgrade state differs from running old binaries against a migrated database.
Keep state when uninstalling/replacing: do not use `docker compose down -v` as an
upgrade step. For exact removal behavior use A3/L0/the Windows guide, and for
problems use [support](support.md).
