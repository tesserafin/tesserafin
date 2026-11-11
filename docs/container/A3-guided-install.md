# [A3] Guided NAS / Docker install — operator guide

Issue: tesserafin/tesserafin #89. Depends on #87 [A1] (the prebuilt image)
and #88 [A2] (the persistent-state contract). This guide gets a first-time
self-hoster from nothing to a running, onboarded Tesserafin server **without
building anything from source**.

Related docs:
- Image (build + contents): [`A1-implementation-note.md`](./A1-implementation-note.md)
- Volumes, permissions, backup/restore: [`A2-persistent-state.md`](./A2-persistent-state.md)
- Hardware acceleration and the software fallback:
  [`A4-hardware-acceleration.md`](./A4-hardware-acceleration.md) — optional; this
  guide needs none of it, since the server transcodes in software with no
  configuration when it finds no GPU
- Installation choices and published artifacts: [installation guide](../install.md)

## The image

**No supported public image is available until the `1.0.0` launch.** The
pre-launch image `ghcr.io/tesserafin/server:1.0.0` (now `0.1.0`) is scheduled
for withdrawal. These instructions require an immutable reference you already
have access to. See the [versioning policy](../versioning-policy.md).

Historical provenance of the old public image:

- Multi-arch manifest digest: `sha256:bc7587bc7d99b40d77770046d49a2931ff8162b6d1df3da8d3f108b7677d7dc3`
  (`linux/amd64` `sha256:63961ace461302693646a77e3204b1706d5cdc66a38152c910849c4a7b0912c8`,
  `linux/arm64` `sha256:27f555cc38c999dd9aa84ce66caa47f4dbb0f0e041a061c010e6a470ba004a28`).
  Built from `6227dbef66c631081e3179a521561b32b94dac59`. It reports version
  `1.0.0`. It was the checked-in Compose default at publication.
- It supersedes `1.0.0-dev.28ace1b74f42`
  (`sha256:636c3038a7eb…`), the accepted B2 presentation/responsive/a11y release
  candidate ([tesserafin-web#55](https://github.com/tesserafin/tesserafin-web/issues/55)).
  That image's presentation evidence still stands, but it carried three
  third-party metadata provider credentials inherited from upstream and compiled
  into `Tesserafin.Providers.dll`:
  **SUPERSEDED — contains inherited upstream provider credentials; do not use.**
  It stays published and immutable — it is not deleted — but it is no longer an
  installation default. The image above is the same tree with those credentials
  removed; nothing else about the build changed. See
  [tesserafin#174](https://github.com/tesserafin/tesserafin/issues/174)
  and [`metadata-provider-keys.md`](../metadata-provider-keys.md).
- Bundled Tesserafin Web `1.0.0` at commit `a9a362eec764a9fe3fa6ba9b4a7dd7473677e35a`
  (`ghcr.io/tesserafin/tesserafin-web-assets@sha256:6150380052c8a3a154a8a25a9f40a741175a7563afdf89284f9c1f46d3042a6c`),
  recorded in the image's `org.tesserafin.web.revision` label and in
  `/opt/tesserafin-web.revision.json` inside the image.

For upgrades and backups, see [A6](./A6-versioning-and-upgrades.md).

> **Do not use `12.0.0-dev.e2999e4e2feb` for this guide.** That earlier image is
> API-only: it runs with `--nowebclient` and bundles no web client, so
> `http://<host>:8096/` shows the Swagger API documentation and **there is no
> onboarding wizard**. It is still published and is not deleted, but it is
> **superseded for [A3]** because it cannot satisfy a browser install. See #115
> and the "Bundled web client" section of
> [`A1-implementation-note.md`](./A1-implementation-note.md).

> **Do not use `1.0.0-dev.965fadf37e20`
> (`sha256:fffb46a41919dfedf0d5bacc68b9c37ebbca0400df74b487ba5858b395440e5c`)
> either.** Published immutable candidate; failed A3/A7 browser acceptance because
> it bundled the upstream Jellyfin `10.10.0` minimum-version boundary, so the
> browser rendered `Update Required` instead of the onboarding wizard; never
> pinned as an installation default; superseded by the validated replacement named
> above. It remains published, immutable and private, and is not deleted, retagged,
> moved or overwritten. See tesserafin/tesserafin-web#65.

## Prerequisites

- A 64-bit Linux/NAS host with Docker Engine and the Docker Compose plugin
  (`docker compose version` works), or a NAS with a container manager (Unraid /
  Synology sections below).
- Somewhere to keep your media files, reachable from the host.
- An accessible immutable image reference that bundles Tesserafin Web.

You do **not** need: the Tesserafin source, a compiler, the .NET SDK, or Jellyfin's
installation docs.

## Install with Docker Compose (canonical path — 5 steps to onboarded)

Steps are counted honestly: each is one action or one decision.

**1. Create an install folder and save the compose file.** Put the repository's
root [`docker-compose.yml`](../../docker-compose.yml) into an empty folder, e.g.
`~/tesserafin/docker-compose.yml`. Nothing else is required in that folder.

**2. Select the image and media path.** In the same folder create a `.env` file (copy
[`.env.example`](../../.env.example)) and replace the image placeholder with
your accessible immutable reference:

```sh
TESSERAFIN_IMAGE=<registry>/<image>@sha256:<digest>
TESSERAFIN_MEDIA=/absolute/path/to/your/media
```

Leave `TESSERAFIN_MEDIA` unset to start with an empty `./media`.
`TESSERAFIN_IMAGE` is required.

**3. Start the server.**

```sh
docker compose up -d
```

This pulls the image and boots. Fresh named volumes for `/config`, `/data` and
`/cache` are created automatically with the correct `10000:10000` ownership — no
manual `chown`.

**4. Open the web UI.** Browse to `http://<host-ip>:8096/` (or
`http://localhost:8096/` on the same machine). The server redirects `/` to
`/web/` and serves the Tesserafin Web client from the same origin and the same
port as the API. If you land on API documentation instead, you are running the
superseded API-only image. Verify that the selected image bundles Tesserafin Web.

**5. Complete first-run onboarding.** In the browser wizard: pick your language →
create your admin username + password → add a media library (type *Movies*, folder
`/media`) → finish. You now have a running, onboarded server.

That is the whole path: **5 numbered steps** after the one-time prerequisites.

**Online metadata is opt-in.** Tesserafin ships no third-party provider credential, so
**TheMovieDb, TheAudioDB and OMDb** each fetch nothing until you supply your own API
key for that provider — see
[`metadata-provider-keys.md`](../metadata-provider-keys.md) for where to get each one
and where it is stored. The three are independent, each key is optional, and
everything above works with none of them.

### What just happened / where state lives

| Volume | Holds | Backed up? |
|--------|-------|-----------|
| `tesserafin_config` → `/config` | config, plugins, SSL, logs | **Yes** |
| `tesserafin_data` → `/data` | databases, metadata, watch state | **Yes** |
| `tesserafin_cache` → `/cache` | regenerable cache | No |
| your media → `/media` (**read-only**) | your files | No (it's yours) |

Named volumes live under Docker's storage (`docker volume ls | grep tesserafin`).
**Back up `/config` + `/data`** with the scripted, verified round-trip in
[`A2-persistent-state.md`](./A2-persistent-state.md#backup-and-restore).

### Stop vs uninstall

```sh
docker compose stop        # pause the server; state kept
docker compose down        # remove the container + network; NAMED VOLUMES KEPT
docker compose down -v      # ALSO delete the named volumes — destroys all state
```

Use `down` (no `-v`) for a non-destructive teardown; only `down -v` (or deleting the
volumes) removes your server state. Take an A2 backup before any destructive step.

## NAS bind-mount variant (host folders instead of named volumes)

NAS users often want the state on a known share rather than in Docker's volume area.
Replace the `volumes:` block with host paths (placeholders — use your own):

```yaml
    volumes:
      - /volume1/docker/tesserafin/config:/config
      - /volume1/docker/tesserafin/data:/data
      - /volume1/docker/tesserafin/cache:/cache
      - /volume1/media:/media:ro
```

Unlike named volumes, **freshly-created host folders are not auto-owned by the
server's uid**. Create them and set ownership once before first start:

```sh
mkdir -p /volume1/docker/tesserafin/{config,data,cache}
# make the config/data/cache dirs writable by the server's non-root identity:
docker run --rm -v /volume1/docker/tesserafin:/w \
  busybox chown -R 10000:10000 /w/config /w/data /w/cache
```

`/media` stays owned by you and is mounted read-only. Bind paths that contain
spaces are supported (quote them). See A2 for the full permission model.

## Unraid

The [Unraid template](../../deployment/unraid/docker-templates/tesserafin.xml)
is suspended until launch. Its repository field is empty. Import resumes when
the template contains a supported immutable image reference.

## Synology DSM (Container Manager)

DSM 7.2+ with **Container Manager** installed.

1. **Select your image.** Import the same accessible immutable reference used
   for Compose. Authentication depends on your selected registry and image.
2. **Create folders.** In *File Station*, under a `docker` shared folder create
   `tesserafin/config`, `tesserafin/data`, `tesserafin/cache`, and note your media
   share.
3. **Set permissions.** The server runs as UID/GID `10000:10000`. Give that
   identity read/write on `config`, `data`, `cache` (via SSH:
   `chown -R 10000:10000 /volume1/docker/tesserafin/{config,data,cache}`). Your
   media share is mounted read-only and can keep its own ownership.
4. **Create the container** from the image. Enable auto-restart.
5. **Port mapping.** Map host `8096` → container `8096` (TCP).
6. **Volume mapping.** Map the three folders to `/config`, `/data`, `/cache`
   (read-write) and your media share to `/media` (**read-only**).
7. **Run**, then open `http://<synology-ip>:8096/` and complete onboarding.

Notes:
- The immutable release reference does **not** auto-update; pull a new tag deliberately and
  back up first — see [`A6-versioning-and-upgrades.md`](./A6-versioning-and-upgrades.md).
- Back up `/config` + `/data` per [`A2-persistent-state.md`](./A2-persistent-state.md).
- Only DSM steps that are standard Container Manager operations are described here;
  no untested DSM-specific integrations are claimed.

## History: why this guide was rewritten (#115)

The first version of this guide pointed at `12.0.0-dev.e2999e4e2feb` and claimed
that step 4 opens a web UI and step 5 completes onboarding in the browser. That
claim was **not true of that image**, and the validation that accepted it was
false-green:

- the image ran with `--nowebclient` and bundled no web client;
- `Tesserafin.Server/Program.cs` therefore redirected `/` to `/api-docs/swagger`;
- opening port 8096 showed API endpoint documentation, not a wizard;
- `docker/compose-smoke.sh` asserted only that `/System/Info/Public` answered —
  API reachability, which is not browser-installability.

A tester started the published container and reported exactly that. The repair is
tracked by #115: the distributable image now bundles a pinned Tesserafin Web
production build and starts with `--webdir`, and the A3 smoke was rewritten to
fail unless a real browser reaches the first-run wizard on pristine volumes and
completes onboarding. Every validation claim tied only to the API-only image was
removed rather than reworded.

## Scope / exclusions

This guide is deliberately bounded to A3. It does **not** cover: GPU / hardware
acceleration (#90), the health endpoint and structured logs (#91 — now shipped, see
[`A5-observability.md`](./A5-observability.md)), the tag/channel policy and upgrade
contract (#92 — now shipped, see
[`A6-versioning-and-upgrades.md`](./A6-versioning-and-upgrades.md)), or
TLS/reverse-proxy. Those are separate roadmap items.
