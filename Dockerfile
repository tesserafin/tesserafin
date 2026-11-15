# syntax=docker/dockerfile:1
# check=skip=FromPlatformFlagConstDisallowed
#
# The skipped check fires on the deliberate `FROM --platform=linux/amd64` of the
# web-assets stage below. The constant is the point: it is what guarantees the
# amd64 and arm64 server images copy byte-identical web bytes. See stage 2b.
#
# Tesserafin server — reproducible production image (issue #87 / [A1]).
#
# Multi-stage, multi-architecture (linux/amd64, linux/arm64), non-root,
# framework-dependent .NET 10 publish over a pinned ASP.NET runtime, with the
# accepted Tesserafin FFmpeg runtime (F0) — the SAME archive the native Linux
# packages carry, verified against ci/package/f0-accepted-digests.txt (#342).
#
# Build inputs are pinned by immutable digest / checksum so two builds of the
# same commit produce the same application payload. See docker/ for the build,
# smoke and reproducibility scripts and docs/container/A1-implementation-note.md
# for the full rationale.

# ---- Pinned base images (immutable multi-arch index digests) ----
# mcr.microsoft.com/dotnet/sdk:10.0     -> Ubuntu 24.04 (noble), amd64+arm64
# mcr.microsoft.com/dotnet/aspnet:10.0  -> Ubuntu 24.04 (noble), amd64+arm64
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7

# ---- Pinned Tesserafin Web production bundle (issue #115 / [A1.2]) ----
# Static-only build input produced by tesserafin-web's Dockerfile.web-assets.
# Pinned BY MANIFEST DIGEST, never by tag: the tag is recorded for provenance
# only. WEB_VCS_REF is the exact tesserafin-web commit the digest was built from
# and must be kept in step with it.
ARG WEB_ASSETS_IMAGE=ghcr.io/tesserafin/tesserafin-web-assets@sha256:ecf684b15b26587a7a824e739433da24dca0dbfae7aeb093b1a39793f7bebf41
ARG WEB_ASSETS_TAG=ghcr.io/tesserafin/tesserafin-web-assets:0.3.0-dev.467b27e43eb1
ARG WEB_VCS_REF=467b27e43eb1136d3179864bb512f7c306d5b22d
ARG WEB_VERSION=0.3.0

# ---- FFmpeg: the accepted Tesserafin runtime (F0), never a downloaded binary ----
# The image carries the runtime that ci/ffmpeg/** builds from pinned source and
# that the .deb/.rpm/.tar.gz packages carry (#342). It arrives through the NAMED
# build context `ffmpeg-runtime`: a directory holding the F0 archive(s)
#   tesserafin-ffmpeg-<revision>-linux-x64.tar.xz     (linux/amd64)
#   tesserafin-ffmpeg-<revision>-linux-arm64.tar.xz   (linux/arm64)
# exactly as ci/package/ffmpeg-runtime.sh writes them under <out>/pkg/. Stage 2
# refuses any archive whose SHA-256 is not the accepted one, so the bytes the
# image unpacks are the bytes the native packages ship — whoever built them.
# docker/f0-runtime-context.sh prepares the context; a bare `docker build .`
# without `--build-context ffmpeg-runtime=<dir>` fails on purpose.

# =====================================================================
# Stage 1 — build & publish the server (native per target platform)
# =====================================================================
FROM ${SDK_IMAGE} AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_XMLDOC_MODE=skip

WORKDIR /src
# Copy the full pruned build context (see .dockerignore) in one layer so a
# clean checkout is the only build input; no bin/obj carried in.
COPY . .

# Deterministic, framework-dependent publish of the server host project.
# ContinuousIntegrationBuild normalises embedded paths; Deterministic removes
# non-reproducible compiler output. No RuntimeIdentifier: the emulated SDK's
# own RID yields the correct per-arch apphost ("tesserafin").
RUN dotnet restore Tesserafin.Server/Tesserafin.Server.csproj
RUN dotnet publish Tesserafin.Server/Tesserafin.Server.csproj \
        --configuration Release \
        --no-restore \
        --output /app \
        -p:Deterministic=true \
        -p:ContinuousIntegrationBuild=true \
        -p:UseAppHost=true \
        -p:DebugType=none

ARG SOURCE_DATE_EPOCH=0

# The ASP.NET static-web-assets manifest stamps each pre-compressed asset's
# `Last-Modified` header with the build-time compression clock, which is the one
# non-deterministic artefact in the publish output. Normalise every such value to
# the commit time so the manifest — and therefore the layer — is reproducible.
# (ETags are content hashes and are already stable.)
RUN set -eux; \
    fixed="$(date -u -d "@${SOURCE_DATE_EPOCH}" +'%a, %d %b %Y %H:%M:%S GMT')"; \
    f=/app/tesserafin.staticwebassets.endpoints.json; \
    if [ -f "$f" ]; then \
        sed -i -E 's/("Name":"Last-Modified","Value":")[^"]*(")/\1'"${fixed}"'\2/g' "$f"; \
    fi

# Clamp every published file's mtime to the commit time for reproducible layers.
RUN find /app -exec touch --no-dereference --date=@${SOURCE_DATE_EPOCH} {} +

# =====================================================================
# Stage 2 — verify & unpack the accepted F0 runtime for the target arch
# =====================================================================
# The accepted-digest list is a COMPARISON ORACLE (see its header): nothing is
# fetched by it. This stage only checks that the archive handed in through the
# `ffmpeg-runtime` context is byte-identical to the accepted one, then unpacks
# it. A ci/ffmpeg/** change therefore cannot reach an image until the oracle is
# rebaselined in the same reviewed change — which is already the F0 rule.
#
# FFMPEG_ALLOW_COMPRESSOR_DRIFT=1 is the image-side twin of the packages'
# PKG_ALLOW_COMPRESSOR_DRIFT: zstd does not promise identical compressed bytes
# across library builds, so a workstation reproduces bin/ffmpeg, bin/ffprobe and
# capability.json byte-for-byte but not the corresponding-source container, nor
# the archive and three manifests that quote its digest. With the flag those
# four are not compared; the binaries always are. The image is marked
# (COMPRESSOR-DRIFT, and ARCHIVE.sha256 names a non-accepted archive), so
# docker/assert-ffmpeg-licence.sh refuses it: a drift image is for local
# validation only and can never pass the gate a published image goes through.
FROM ${RUNTIME_IMAGE} AS ffmpeg-runtime-unpack
ARG TARGETARCH
ARG FFMPEG_ALLOW_COMPRESSOR_DRIFT=0
RUN apt-get update \
 && apt-get install -y --no-install-recommends xz-utils \
 && rm -rf /var/lib/apt/lists/*
COPY ci/package/f0-accepted-digests.txt /f0/accepted-digests.txt
COPY --from=ffmpeg-runtime . /f0/in/
RUN set -eu; \
    die() { echo "ffmpeg-runtime: $*" >&2; exit 1; }; \
    case "${TARGETARCH}" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) die "unsupported TARGETARCH: ${TARGETARCH}" ;; \
    esac; \
    cd /f0/in; \
    set -- tesserafin-ffmpeg-*-"${rid}".tar.xz; \
    [ "$#" -eq 1 ] && [ -f "$1" ] || die "expected exactly one tesserafin-ffmpeg-*-${rid}.tar.xz in the ffmpeg-runtime context, found: $*"; \
    archive="$1"; dir="${archive%.tar.xz}"; \
    accepted() { awk -v f="$1" '$2 == f { print $1 }' /f0/accepted-digests.txt; }; \
    want="$(accepted "${archive}")"; \
    [ -n "${want}" ] || die "${archive} is not an accepted F0 archive name (ci/package/f0-accepted-digests.txt)"; \
    drift=0; \
    if ! echo "${want}  ${archive}" | sha256sum -c - ; then \
      [ "${FFMPEG_ALLOW_COMPRESSOR_DRIFT}" = "1" ] || die "${archive} is not the accepted F0 archive"; \
      drift=1; echo "ffmpeg-runtime: COMPRESSOR DRIFT accepted for ${archive}: binaries still compared" >&2; \
    fi; \
    mkdir -p /f0/x; \
    tar -xJf "${archive}" -C /f0/x --no-same-owner; \
    [ -d "/f0/x/${dir}" ] || die "archive does not unpack to ${dir}/"; \
    files="bin/ffmpeg bin/ffprobe capability.json"; \
    [ "${drift}" = "1" ] || files="${files} SOURCE.json THIRD_PARTY_NOTICES.md sbom.cdx.json"; \
    for f in ${files}; do \
      w="$(accepted "${dir}/${f}")"; [ -n "${w}" ] || die "no accepted digest for ${dir}/${f}"; \
      echo "${w}  /f0/x/${dir}/${f}" | sha256sum -c - || die "${dir}/${f} differs from the accepted runtime"; \
    done; \
    mv "/f0/x/${dir}" /opt/tesserafin-ffmpeg; \
    sha256sum "${archive}" > /opt/tesserafin-ffmpeg/ARCHIVE.sha256; \
    if [ "${drift}" = "1" ]; then \
      echo "compressor drift: archive is not the accepted ${want}; local validation only" > /opt/tesserafin-ffmpeg/COMPRESSOR-DRIFT; \
    fi

# =====================================================================
# Stage 2b — the pinned Tesserafin Web production bundle (#115 / [A1.2])
# =====================================================================
# `--platform=linux/amd64` is deliberate and load-bearing. Without it,
# `COPY --from=` resolves the source image per TARGET platform, so the amd64 and
# arm64 server builds could copy different web bytes — one of the explicit hard
# stops for this work. The payload is architecture-neutral static content and
# this stage is never executed, so pinning the platform costs no emulation.
FROM --platform=linux/amd64 ${WEB_ASSETS_IMAGE} AS webassets

# =====================================================================
# Stage 3 — minimal runtime image
# =====================================================================
FROM ${RUNTIME_IMAGE} AS runtime

ARG VERSION=0.3.0
ARG VCS_REF=unknown
ARG SOURCE_DATE_EPOCH=0
ARG UID=10000
ARG GID=10000
# Re-declared so the paired-web provenance labels below can interpolate them
# (pre-FROM ARGs are only in scope for FROM lines).
ARG WEB_ASSETS_IMAGE
ARG WEB_ASSETS_TAG
ARG WEB_VCS_REF
ARG WEB_VERSION

# Runtime native dependencies:
#   - the GPU user-space the F0 runtime dlopen()s but deliberately does not ship
#     (#342). F0 bundles libva 2.24 + libdrm and links no Vulkan loader; the VA
#     drivers, the Vulkan loader and the Vulkan ICDs are properties of the
#     machine, so the image supplies them from the base distribution:
#       mesa-va-drivers       radeonsi (AMD) VA driver — the validated path
#       mesa-vulkan-drivers   RADV/ANV ICDs for libplacebo + the *_vulkan filters
#       libvulkan1            the Vulkan loader
#       intel-media-va-driver iHD (Intel), amd64 only — present, NOT validated
#       libmfx-gen1.2         Intel oneVPL GPU runtime for QSV, amd64 only —
#                             present, NOT validated
#     F0's libva searches /usr/lib/<triplet>/dri first (ci/ffmpeg build), which
#     is where these drivers land.
#   - libfontconfig1 + a base font: SkiaSharp image rendering
#   - curl: the HEALTHCHECK below runs INSIDE the container, so the image has to
#     carry one HTTP client of its own (#91 / [A5]). Nothing else uses it.
#   - ICU is already present in the .NET runtime image (globalization on)
ARG TARGETARCH
RUN case "${TARGETARCH}" in \
      amd64) arch_pkgs="intel-media-va-driver libmfx-gen1.2" ;; \
      *)     arch_pkgs="" ;; \
    esac \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
        mesa-va-drivers \
        mesa-vulkan-drivers \
        libvulkan1 \
        ${arch_pkgs} \
        libfontconfig1 \
        fonts-dejavu-core \
        curl \
 # Strip apt/dpkg artefacts that embed wall-clock timestamps, so the layer
 # content is reproducible across builds (mtimes are separately clamped by
 # buildx rewrite-timestamp).
 && rm -rf /var/lib/apt/lists/* \
           /var/cache/apt/* \
           /var/log/apt/* \
           /var/log/dpkg.log \
           /var/log/alternatives.log \
           /var/lib/dpkg/*-old \
           /var/cache/ldconfig/aux-cache

# Fixed non-root identity and the writable runtime directories.
RUN groupadd --gid ${GID} tesserafin \
 && useradd --uid ${UID} --gid ${GID} --no-create-home \
        --home-dir /data --shell /usr/sbin/nologin tesserafin \
 && mkdir -p /config /cache /data /media \
 && chown -R ${UID}:${GID} /config /cache /data

# Application payload only — no SDK, compiler, source tree or package cache.
COPY --from=build /app /opt/tesserafin
COPY LICENSE /opt/tesserafin/LICENSE

# The accepted F0 runtime, unpacked whole: bin/, lib/, LICENSES/,
# THIRD_PARTY_NOTICES.md, SOURCE.json (names the corresponding-source archive and
# its digest), sbom.cdx.json, capability.json, plus ARCHIVE.sha256 recording the
# exact archive it came from. Same RUNPATH ($ORIGIN/../lib) as the native packages.
COPY --from=ffmpeg-runtime-unpack /opt/tesserafin-ffmpeg /opt/tesserafin-ffmpeg

# The pinned Tesserafin Web production bundle (#115 / [A1.2]). Read-only for the
# runtime user: it is application content, never writable state. The server is
# pointed at it with `--webdir` (see CMD), which makes `/` serve the web client
# and the first-run onboarding wizard instead of redirecting to the API docs.
# `/opt/tesserafin-web.revision.json` records the exact paired tesserafin-web
# commit in the image content itself, so the pairing is auditable from a pulled
# image with no registry or label lookup.
COPY --from=webassets /web /opt/tesserafin-web
COPY --from=webassets /licenses /opt/tesserafin-web-licenses
COPY --from=webassets /metadata/web-revision.json /opt/tesserafin-web.revision.json

# Neutralise misleading inherited base-image env: the Tesserafin server binds its
# own port (8096) via NetworkConfiguration and ignores ASPNETCORE_HTTP_PORTS; the
# runtime identity is the fixed USER below, not the base image's APP_UID.
#
# TESSERAFIN_LOG_FORMAT=json is the container default (#91 / [A5]): in a container
# stdout IS the log transport, so every application log line is emitted as one JSON
# object. Running the server outside a container leaves this unset and keeps the
# human-readable console format. TESSERAFIN_LOG_LEVEL is deliberately NOT set here,
# so the level stays whatever logging.json says (Information) and an operator can
# override it per-run without rebuilding the image.
ENV TESSERAFIN_DATA_DIR=/data \
    TESSERAFIN_CONFIG_DIR=/config \
    TESSERAFIN_CACHE_DIR=/cache \
    TESSERAFIN_LOG_FORMAT=json \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    ASPNETCORE_HTTP_PORTS="" \
    APP_UID=""

# Real HTTP port (HTTPS 8920 stays off until a certificate is configured).
EXPOSE 8096
VOLUME ["/config", "/cache", "/data"]

# OCI provenance labels (all inputs deterministic per commit).
LABEL org.opencontainers.image.title="Tesserafin Server" \
      org.opencontainers.image.description="Reproducible distributable Tesserafin media server" \
      org.opencontainers.image.source="https://github.com/tesserafin/tesserafin" \
      org.opencontainers.image.url="https://github.com/tesserafin/tesserafin" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${VCS_REF}" \
      org.opencontainers.image.licenses="GPL-3.0-or-later" \
      org.opencontainers.image.vendor="Tesserafin"

# Licence (#342): the server is GPL-2.0-or-later and the bundled F0 FFmpeg is
# GPL-3.0-or-later (--enable-gpl --enable-version3, never nonfree, never
# libfdk_aac). The image as distributed is therefore offered under
# GPL-3.0-or-later. docker/assert-ffmpeg-licence.sh checks this label against
# the binary's own -buildconf.

# Paired Tesserafin Web provenance (#115 / [A1.2]).
LABEL org.tesserafin.web.revision="${WEB_VCS_REF}" \
      org.tesserafin.web.version="${WEB_VERSION}" \
      org.tesserafin.web.assets.image="${WEB_ASSETS_IMAGE}" \
      org.tesserafin.web.assets.tag="${WEB_ASSETS_TAG}" \
      org.tesserafin.web.path="/opt/tesserafin-web"

USER ${UID}:${GID}
WORKDIR /opt/tesserafin

# #91 / [A5]. /health answers 200 only once the real pipeline is up AND a real
# `SELECT 1` against the embedded SQLite database succeeded; while the startup
# server still owns the route it answers 503, which is what start-period covers.
# `-f` makes curl exit non-zero on that 503 without needing to parse the body.
HEALTHCHECK --interval=30s --timeout=5s --start-period=120s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8096/health >/dev/null || exit 1

# The apphost runs as PID 1 so .NET's ConsoleLifetime receives SIGTERM directly
# and shuts the server down gracefully. ffmpeg is pinned by absolute path, to
# the F0 runtime (ffprobe is found beside it).
#
# `--webdir` (NOT `--nowebclient`, see #115) hosts the bundled web client. With
# the web client hosted, Program.cs leaves DefaultRedirectPath at its `web/`
# default, so `/` redirects to `/web/` and serves the first-run wizard; the
# Swagger UI stays on its own `/api-docs/swagger` route and no longer captures
# `/`. docker/smoke.sh asserts both halves of that.
ENTRYPOINT ["/opt/tesserafin/tesserafin"]
CMD ["--webdir", "/opt/tesserafin-web", "--ffmpeg", "/opt/tesserafin-ffmpeg/bin/ffmpeg"]
