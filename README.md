# Tesserafin

[![License: GPL-2.0-or-later](https://img.shields.io/badge/license-GPL--2.0--or--later-blue.svg)](LICENSE)

Tesserafin is a self-hosted media system. You run the server on hardware you
control, it organises your own media library, and it streams that library to
your own devices over your own network.

Tesserafin is a fork of [Jellyfin](https://github.com/jellyfin/jellyfin), which
is itself descended from Emby's 3.5.2 release. Tesserafin is an independent
project: it does **not** claim product or protocol compatibility with Jellyfin,
it is not compatible with Jellyfin clients or plugins, and it is neither endorsed
by nor affiliated with the Jellyfin project. See [NOTICE](NOTICE) for the full
fork attribution.

## Project status

**No supported public release yet.** `1.0.0` is reserved for the first public launch.

Two pre-launch builds were published earlier as `1.0.0` (2026-08-05) and `1.1.0`
(2026-09-13). They are renumbered **0.1.0** and **0.2.0**, their GitHub Releases
and tags are scheduled for deletion. Withdrawal was approved on 2026-11-05. What they
contained is recorded in the [changelog](CHANGELOG.md). Nothing is
Authenticode-signed yet; signing starts with the launch
([proposed signing plan](https://github.com/tesserafin/tesserafin/pull/385)).
- `1.0.0` is the first public version epoch. The server and the web client share
  that number.
- The build package `ghcr.io/tesserafin/tesserafin-server` and the
  web-assets package are **private build provenance**, not a supported product.
- The inherited `12.x` server images and `13.x` web-assets images describe
  upstream lineage, not a Tesserafin release history. They are retained as
  development evidence and are **unsupported**.
- Only **Stable** is planned as a public channel. No beta, preview or nightly
  public channel is promised, and no mutable tag (`latest`, `stable`, `preview`,
  `1`, `1.0`) has been published.

The authoritative rules for which numbers exist, where they are published and how
a release may be resolved are in
[`docs/versioning-policy.md`](docs/versioning-policy.md). What each release
contains, and what it does not, is in [CHANGELOG.md](CHANGELOG.md).

## What Tesserafin is

- A **media server** that indexes your library, fetches metadata, and transcodes
  on demand — in software on any host, with hardware acceleration when a
  supported GPU is present.
- A **browser client**, Tesserafin Web, served by the same server on the same
  origin and port as the API.
- Designed for **local and private infrastructure**: a NAS, a home server, or any
  machine you administer.

Server plus browser client is the whole product today. Native mobile and TV
clients are roadmap items, not shipped software.

## Install and run

The [installation guide](docs/install.md) records the status of Docker/NAS,
native Linux and Windows packages. See [development](#development) for source
builds.

| Task | Start here |
| --- | --- |
| First installation | [Installation guide](docs/install.md) |
| Onboarding, browsing and playback | [User guide](docs/user-guide.md) |
| Health, logs, backup, restore and upgrades | [Administrator guide](docs/admin-guide.md) |
| Find a guide or an engineering reference | [Documentation index](docs/README.md) |
| Report a problem | [Support](docs/support.md) |

No supported public image is available until the `1.0.0` launch. Withdrawal of
`ghcr.io/tesserafin/server:1.0.0` (pre-launch `0.1.0`) was approved on 2026-11-05.
Registry removal is a separate owner action. Private build packages and inherited development images are not
installation fallbacks.
No mutable public image channel is published.

**Tried an installation on your own machine?**
[Share an installation report](docs/installation-reports.md), including successful
or incomplete runs. Native ARM64, Unraid and Synology DSM reports are especially
useful for checking the published guides on independent hardware.

## Product principles

- **The essential self-hosted server core is Free Software**, licensed
  GPL-2.0-or-later.
- **Essential server functions are not designed to sit behind a cloud
  subscription.** Indexing, browsing, transcoding and streaming your own library
  are server functions, and they stay in the server.
- **No mandatory Tesserafin-hosted service is required** to organise or stream
  your own local library. A Tesserafin install works on a network with no route
  to us.
- **Separately distributed official clients may follow their own commercial
  model.** Official mobile and TV applications, if and when they ship, are
  distributed separately from this server and are not covered by the statements
  above.

## Repository architecture

| Repository | Contents |
| --- | --- |
| [tesserafin/tesserafin](https://github.com/tesserafin/tesserafin) | This repository: the server, its container packaging, and the server-owned contracts (OpenAPI document, version contract, release-pair gate). |
| [tesserafin/tesserafin-web](https://github.com/tesserafin/tesserafin-web) | Tesserafin Web — the browser and desktop reference client, bundled into the server image. |

Native Android foundations live in the separately developed mobile repository.
They are not a shipped client: [the native tracker](https://github.com/tesserafin/tesserafin/issues/221)
distinguishes the incomplete phone vertical from later Android TV delivery.

## Development

This section is for building the server from source. It is not the installation
path.

**Prerequisites**

- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet).
  [`global.json`](global.json) pins SDK `10.0.0` with `rollForward: latestMinor`.
- `ffmpeg` on `PATH` for the media-encoding and playback tests. The container
  image and the native Linux packages instead carry the same Tesserafin FFmpeg
  runtime, built from pinned source by [`ci/ffmpeg`](ci/ffmpeg) (FFmpeg with the
  jellyfin-ffmpeg patch series, GPL-3.0-or-later, no non-free components), and
  point the server at it with `--ffmpeg`.
- Optionally an IDE for debugging: Visual Studio 2022 or later, or Visual Studio
  Code with the workspace-recommended extensions.

Supported on all major operating systems except FreeBSD.

**Clone, build, test**

```bash
git clone https://github.com/tesserafin/tesserafin.git
cd tesserafin
dotnet build Tesserafin.sln
dotnet test Tesserafin.sln
```

`./ci/run.sh` is the authoritative local merge gate — it runs the full suite with
the analyzers armed. Purge `bin/` and `obj/` before trusting a pass; stale build
output can skip analyzers.

**Running the server**

The server hosts the web client's static files as well as the API. Get those
files by building
[tesserafin-web](https://github.com/tesserafin/tesserafin-web) (`npm
install`, then `npm run build:development`), then point the server at its `dist`
directory:

```bash
dotnet run --project Tesserafin.Server --webdir /absolute/path/to/tesserafin-web/dist
```

With no `--webdir` and no `TESSERAFIN_WEB_DIR`, the server falls back to a
`jellyfin-web` directory next to the built assembly. That inherited default path
is still live in the code and is not a branding leftover in the build.

To build once and run the executable directly:

```bash
dotnet build
cd Tesserafin.Server/bin/Debug/net10.0
./tesserafin --help          # tesserafin.exe on Windows
```

With the web client hosted, it is served at `http://localhost:8096`, and the API
documentation at `http://localhost:8096/api-docs/swagger/index.html`.

**Hosting the web client separately**

For frontend work it is usually better to run the web client from its own dev
server (`npm start` in tesserafin-web) and tell the server not to host any web
content, with the `--nowebclient` switch. A `Tesserafin.Server (nowebclient)`
launch profile is defined for that. Note that the setup wizard cannot run when
the web client is hosted separately.

**Continuous integration**

Build, test, and lint run automatically on `master` and on every pull request.
The [Tests](.github/workflows/ci-tests.yml) (build + full test suite) and
[Format](.github/workflows/ci-format.yml) (`dotnet format`) workflows, together
with the ABI, OpenAPI, dependency-audit, secret-scan, SDK-provenance, and CodeQL
workflows, are **required status checks** on `master`: a pull request cannot
merge until they are green. `./ci/run.sh` reproduces the build and test stages
locally in Docker. See [BUILDING.md](BUILDING.md) for the full contributor build
setup and the list of required checks.

## Contributing and reporting problems

For building, testing, and linting the server from source, see
[BUILDING.md](BUILDING.md) — it documents the reproducible build, the local
merge gate (`./ci/run.sh`), and the checks that must pass before a pull request
can merge. A broader contribution guide (coding conventions, review process) is
not written yet.

**Ordinary bugs and feature requests** —
[open an issue](https://github.com/tesserafin/tesserafin/issues) in this
repository. Browser and UI problems belong in
[tesserafin/tesserafin-web](https://github.com/tesserafin/tesserafin-web/issues).
[`docs/support.md`](docs/support.md) covers where a problem goes, what to include,
and what this project does and does not promise in return.

**Suspected security vulnerabilities — do not open a public issue or
discussion, and do not post details in a pull request.** Use the repository's
**Security** tab and select *Report a vulnerability*: that form opens a private
advisory readable only by you and the maintainers. The full policy — supported
versions, response targets, coordinated disclosure and the diagnostics posture —
is in [SECURITY.md](SECURITY.md). Vulnerability reports must not be sent to
upstream Jellyfin channels — they are not Tesserafin's.

## Licence and lineage

Tesserafin is licensed **GPL-2.0-or-later**. The bundled [LICENSE](LICENSE) file
contains the GNU General Public License version 2 text, and the same
`GPL-2.0-or-later` SPDX expression is declared by every Tesserafin-owned packable
project.

Tesserafin is a fork of Jellyfin and inherits that licensing. All prior copyright
of the Jellyfin project and its contributors is retained; this fork does not
revoke or replace any upstream attribution. Third-party components retain their
own copyright and licence notices in the source tree. Full attribution is in
[NOTICE](NOTICE).
