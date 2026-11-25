#!/usr/bin/env bash
# Redistribution closure for the Tesserafin FFmpeg runtime (F0 / #229).
#
# Usage: ci/ffmpeg/package-runtime.sh --stage DIR --cache DIR --out DIR --arch RID
#
# Produces, deterministically:
#   * LICENSES/ and THIRD_PARTY_NOTICES.md, collected from the ACTUAL fetched
#     source trees rather than transcribed by hand;
#   * SOURCE.json — the source baseline, every component pin, and the name and
#     SHA-256 of the corresponding-source archive;
#   * a CycloneDX SBOM;
#   * the runtime archive, containing only what the packages run;
#   * the corresponding-source archive: the preferred form for modification of
#     everything statically linked into that runtime, plus the build scripts and
#     instructions needed to reproduce it.
#
# The corresponding source is built FIRST, because its digest has to appear
# inside the runtime archive. A recipient must be able to get from the binary to
# the source without consulting anything outside the artifact.

set -euo pipefail

# shellcheck source=ci/ffmpeg/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

STAGE=""; CACHE=""; OUT=""; ARCH=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --stage) STAGE="$2"; shift 2 ;;
        --cache) CACHE="$2"; shift 2 ;;
        --out)   OUT="$2"; shift 2 ;;
        --arch)  ARCH="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -d "${STAGE}" ]] || ff_die "--stage must be an existing staged runtime"
[[ -d "${CACHE}" ]] || ff_die "--cache must be the source cache the build used"
[[ -n "${OUT}" ]]   || ff_die "--out is required"
[[ -n "${ARCH}" ]]  || ff_die "--arch is required"

ff_load_manifest
mkdir -p "${OUT}"
EPOCH="$(ff_source_date_epoch)"
NAME="tesserafin-ffmpeg-${FF_BUILD_REVISION}"

# =============================================================================
# 1. Corresponding source
# =============================================================================
# One archive, architecture-independent: the same source produces both runtimes,
# so shipping it twice would only invite the two copies to diverge.
SRC_ARCHIVE="${OUT}/${NAME}-corresponding-source.tar.zst"
if [[ ! -f "${SRC_ARCHIVE}" ]]; then
    ff_log "assembling the corresponding source"
    SRCDIR="${OUT}/.corresponding-source/${NAME}"
    rm -rf "${OUT}/.corresponding-source"; mkdir -p "${SRCDIR}"

    # The FFmpeg tree and every dependency, as source. Git metadata is dropped:
    # a .git directory is a fetch mechanism, not the preferred form for
    # modification, and it would make the archive non-deterministic.
    mkdir -p "${SRCDIR}/source"
    for d in "${CACHE}/git"/*; do
        [[ -d "${d}" ]] || continue
        cp -a "${d}" "${SRCDIR}/source/$(basename "${d}")"
        find "${SRCDIR}/source/$(basename "${d}")" -name '.git' -maxdepth 2 -exec rm -rf {} + 2>/dev/null || true
    done
    mkdir -p "${SRCDIR}/source-archives"
    cp -a "${CACHE}/archives"/* "${SRCDIR}/source-archives/"

    # The exact build logic, so the instructions are executable rather than
    # descriptive.
    mkdir -p "${SRCDIR}/build"
    cp -a "${FF_REPO_ROOT}/ci/ffmpeg/." "${SRCDIR}/build/"
    rm -rf "${SRCDIR}/build/.stamps"

    cp -a "${FF_REPO_ROOT}/docs/distribution/F0-ffmpeg-runtime.md" "${SRCDIR}/DISTRIBUTION-CONTRACT.md"

    cat > "${SRCDIR}/README.md" <<EOF
# Corresponding source — ${NAME}

This archive is the complete corresponding source for the Tesserafin FFmpeg
runtime archives of the same build revision. It contains the preferred form for
modification of everything statically linked into them: not links, not binaries.

    source/            the FFmpeg tree and every git-sourced dependency, at the pinned commit
    source-archives/   every release tarball, byte-identical to what the build consumed
    build/             the exact scripts, component manifest and configure flags used
    DISTRIBUTION-CONTRACT.md   what is built, under which licences, and why

## Rebuilding

From a checkout of the Tesserafin server repository at the recorded commit:

    ci/ffmpeg/build-runtime.sh --arch linux-x64   --out <dir>
    ci/ffmpeg/build-runtime.sh --arch linux-arm64 --out <dir>

Each architecture builds on a machine of that architecture; the runtime is never
cross-compiled or emulated. The builder environment is pinned by digest and its
packages by a fixed snapshot timestamp, so the toolchain is an input rather than
something the build produces. Two clean builds of the same revision are expected
to be bit-for-bit identical.

## Licences

Every component's licence text is in \`LICENSES/\` in the runtime archive and is
reproduced from the source trees here. FFmpeg is built with \`--enable-gpl\` and
\`--enable-version3\`, so the runtime as a whole is GPL-3.0-or-later.
EOF

    # Licence texts, collected from the real trees rather than transcribed.
    mkdir -p "${SRCDIR}/LICENSES"
    python3 - "${CACHE}" "${SRCDIR}/LICENSES" "${FF_COMPONENTS}" <<'PY'
import json, os, shutil, sys, tarfile
cache, dest, manifest = sys.argv[1:4]
names = ("COPYING", "COPYING.txt", "LICENSE", "LICENSE.txt", "LICENSE.md",
         "COPYING.LIB", "COPYING.LGPLv2.1", "COPYING.GPLv2", "COPYING.GPLv3",
         "LICENSE.TXT", "License.txt", "COPYRIGHT", "NOTICE",
         # British spelling: libunibreak ships LICENCE and was silently missed.
         "LICENCE", "LICENCE.txt", "LICENSE.rst", "COPYING.MIT")

# Components that carry no standalone licence file because their terms live in
# a per-file header. Naming the representative file keeps the extraction honest
# and reviewable instead of leaving the component uncovered.
HEADER_LICENCE = {
    "libdrm": "xf86drm.h",
    "nv-codec-headers": "include/ffnvcodec/nvEncodeAPI.h",
}
found = {}

def take(component, path, data):
    out = os.path.join(dest, component)
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, os.path.basename(path)), "wb") as h:
        h.write(data)
    found.setdefault(component, []).append(os.path.basename(path))

for entry in sorted(os.listdir(os.path.join(cache, "git"))):
    root = os.path.join(cache, "git", entry)
    for n in names:
        p = os.path.join(root, n)
        if os.path.isfile(p):
            take(entry, p, open(p, "rb").read())

for archive in sorted(os.listdir(os.path.join(cache, "archives"))):
    component = archive.split("-")[0] if "-" in archive else archive
    path = os.path.join(cache, "archives", archive)
    try:
        with tarfile.open(path) as tf:
            for m in tf.getmembers():
                base = os.path.basename(m.name)
                depth = m.name.strip("/").count("/")
                if m.isfile() and base in names and depth <= 1:
                    fh = tf.extractfile(m)
                    if fh:
                        take(component, base, fh.read())
    except tarfile.TarError:
        pass

# Fallback: lift the licence block out of the representative header.
for component, relative in HEADER_LICENCE.items():
    if component in found:
        continue
    for base in (os.path.join(cache, "git", component),):
        candidate = os.path.join(base, relative)
        if os.path.isfile(candidate):
            text = open(candidate, "r", errors="replace").read(8000)
            end = text.find("*/")
            if end > 0:
                take(component, f"LICENSE-extracted-from-{os.path.basename(relative)}",
                     text[: end + 2].encode())
    for archive in sorted(os.listdir(os.path.join(cache, "archives"))):
        if not archive.startswith(component + "-"):
            continue
        with tarfile.open(os.path.join(cache, "archives", archive)) as tf:
            for m in tf.getmembers():
                if m.isfile() and m.name.endswith("/" + relative):
                    fh = tf.extractfile(m)
                    if fh:
                        text = fh.read(8000).decode("utf-8", "replace")
                        end = text.find("*/")
                        if end > 0:
                            take(component,
                                 f"LICENSE-extracted-from-{os.path.basename(relative)}",
                                 text[: end + 2].encode())
                    break

policy = json.load(open(manifest))
missing = [c["name"] for c in policy["components"]
           if not any(k.startswith(c["name"].split("-")[0]) for k in found)]
json.dump({"collected": found, "withoutLicenceFileInTree": sorted(missing)},
          open(os.path.join(dest, "index.json"), "w"), indent=2, sort_keys=True)
print(f"  licence texts collected for {len(found)} components; "
      f"{len(missing)} carry none in-tree: {', '.join(sorted(missing)) or 'none'}")
PY

    ff_clamp_mtimes "${OUT}/.corresponding-source"
    ff_normalize_modes "${OUT}/.corresponding-source"
    ff_deterministic_tar "${OUT}/.corresponding-source" "${SRC_ARCHIVE}" zstd -19 -T1 -q
    rm -rf "${OUT}/.corresponding-source"
fi
SRC_SHA="$(ff_sha256 "${SRC_ARCHIVE}")"
ff_log "corresponding source ${SRC_SHA}"

# =============================================================================
# 2. Runtime payload: notices, provenance, licences
# =============================================================================
RT="${OUT}/${NAME}-${ARCH}"
# The packaged runtime is built fresh, which means clearing RT. If the caller
# passed an --out whose ${NAME}-${ARCH} subdirectory IS the --stage, that clear
# deletes the very binaries about to be copied out of it. Refuse rather than
# self-destruct: this is a caller mistake with a silent, confusing symptom
# ("cannot stat .../bin/ffmpeg") that looks like a build failure.
if [[ "$(cd "${STAGE}" && pwd -P)" == "$(cd "$(dirname "${RT}")" && pwd -P)/$(basename "${RT}")" ]]; then
    ff_die "--out ${OUT} would place the packaged runtime on top of --stage ${STAGE}; give --out a directory of its own"
fi
rm -rf "${RT}"; mkdir -p "${RT}/bin" "${RT}/LICENSES"
cp -a "${STAGE}/bin/ffmpeg" "${STAGE}/bin/ffprobe" "${RT}/bin/"
# The bundled shared libraries the $ORIGIN RUNPATH resolves to. Without these
# the archive is a binary that cannot load — which is exactly what shipped the
# first time this ran.
if [[ -d "${STAGE}/lib" ]]; then
    mkdir -p "${RT}/lib"
    cp -a "${STAGE}/lib/." "${RT}/lib/"
fi
# The capability record, written by verify-runtime.sh before packaging.
[[ -f "${STAGE}/capability.json" ]] && cp -a "${STAGE}/capability.json" "${RT}/capability.json"

# Re-collect the licence texts straight into the runtime, from the same trees.
tar --extract --file "${SRC_ARCHIVE}" --directory "${RT}/.." \
    --wildcards "./${NAME}/LICENSES/*" 2>/dev/null || true
if [[ -d "${RT}/../${NAME}/LICENSES" ]]; then
    cp -a "${RT}/../${NAME}/LICENSES/." "${RT}/LICENSES/"
    rm -rf "${RT:?}/../${NAME:?}/LICENSES"
    rmdir "${RT}/../${NAME}" 2>/dev/null || true
fi

"${RT}/bin/ffmpeg" -hide_banner -buildconf 2>/dev/null > "${RT}/build-configuration.txt" || true

python3 - "${FF_COMPONENTS}" "${ARCH}" "${SRC_SHA}" "$(basename "${SRC_ARCHIVE}")" \
         "${EPOCH}" "${RT}" "${FF_REPO_ROOT}/ci/ffmpeg/fork-patches.json" \
         "${FF_REPO_ROOT}/ci/ffmpeg/backports" <<'PY'
import json, os, sys
manifest, arch, src_sha, src_name, epoch, rt, patch_catalogue, backport_dir = sys.argv[1:9]
policy = json.load(open(manifest))
fork_patches = json.load(open(patch_catalogue))

# Each backport is `git format-patch` output: its first line names the upstream commit and its
# Subject line what it fixes.
backports = []
for line in open(os.path.join(backport_dir, "series")):
    name = line.strip()
    if not name or name.startswith("#"):
        continue
    text = open(os.path.join(backport_dir, name), encoding="utf-8").read().splitlines()
    commit = text[0].split()[1]
    start = next(i for i, l in enumerate(text) if l.startswith("Subject: "))
    subject = text[start][len("Subject: "):]
    for continuation in text[start + 1:]:
        if not continuation.startswith(" "):
            break
        subject += continuation
    subject = subject.removeprefix("[PATCH] ")
    backports.append({"patch": name, "upstreamCommit": commit, "subject": subject})

source = {
    "buildRevision": policy["buildRevision"],
    "architecture": arch,
    "ffmpeg": policy["ffmpeg"],
    "components": policy["components"],
    "excluded": policy["excluded"],
    "sourceDateEpoch": int(epoch),
    "correspondingSource": {
        "archive": src_name,
        "sha256": src_sha,
        "$comment": "The complete corresponding source for this runtime. It carries the "
                    "preferred form for modification of every statically linked component, "
                    "the exact build scripts and instructions sufficient to rebuild both "
                    "architectures.",
    },
    # Which of the fork's changes this binary actually contains. The
    # corresponding-source archive carries the patch files themselves; this
    # records what was DONE with them, so a recipient can tell an applied series
    # from a series that merely shipped alongside an unpatched tree — the exact
    # confusion that once produced a binary branded as the fork with none of the
    # fork's work in it.
    "forkPatches": {
        "seriesLength": len(fork_patches["patches"]),
        "applied": sorted(e["patch"] for e in fork_patches["patches"] if e["applied"]),
        "excluded": [
            {"patch": e["patch"], "classification": e["classification"],
             "rationale": e["rationale"]}
            for e in fork_patches["patches"] if not e["applied"]
        ],
        "classificationCounts": fork_patches["counts"],
        "$comment": "Applied in series order at zero fuzz. Only the 'unsafe' class is skipped.",
    },
    # Upstream FFmpeg commits applied after the fork's series (ci/ffmpeg/backports/series).
    "upstreamBackports": backports,
    "$comment": "Engineering redistribution closure. Not a legal ruling.",
}
with open(os.path.join(rt, "SOURCE.json"), "w") as h:
    json.dump(source, h, indent=2, sort_keys=True); h.write("\n")

# CycloneDX, because it expresses a build-time dependency graph with licences
# and source locations in one document that tooling already reads.
bom = {
    "bomFormat": "CycloneDX",
    "specVersion": "1.5",
    "version": 1,
    "metadata": {
        "component": {
            "type": "application",
            "name": "tesserafin-ffmpeg",
            "version": policy["buildRevision"],
            "licenses": [{"expression": "GPL-3.0-or-later"}],
            "description": f"Tesserafin FFmpeg runtime for {arch}",
        },
        "properties": [
            {"name": "tesserafin:sourceDateEpoch", "value": epoch},
            {"name": "tesserafin:correspondingSource", "value": src_name},
            {"name": "tesserafin:correspondingSourceSha256", "value": src_sha},
        ],
    },
    "components": [
        {
            "type": "library",
            "name": "jellyfin-ffmpeg",
            "version": policy["ffmpeg"]["baseline"],
            "licenses": [{"expression": "GPL-3.0-or-later"}],
            "externalReferences": [
                {"type": "vcs", "url": policy["ffmpeg"]["repository"]},
            ],
            "properties": [{"name": "tesserafin:commit", "value": policy["ffmpeg"]["commit"]}],
        }
    ] + [
        {
            "type": "library",
            "name": c["name"],
            "version": c.get("ref", c.get("commit", ""))[:12] or "pinned",
            "licenses": [{"expression": c["license"]}],
            "externalReferences": [
                {"type": "distribution" if c["sourceType"] == "tar" else "vcs",
                 "url": c.get("url") or c["repository"],
                 **({"hashes": [{"alg": "SHA-256", "content": c["sha256"]}]}
                    if c.get("sha256") else {})}
            ],
            "description": c["requiredBy"],
            **({"properties": [{"name": "tesserafin:commit", "value": c["commit"]}]}
               if c.get("commit") else {}),
        }
        for c in policy["components"]
    ],
}
with open(os.path.join(rt, "sbom.cdx.json"), "w") as h:
    json.dump(bom, h, indent=2, sort_keys=True); h.write("\n")

lines = [
    "# Third-party notices — Tesserafin FFmpeg runtime",
    "",
    f"Build revision `{policy['buildRevision']}` for `{arch}`.",
    "",
    "This runtime is a single program built from FFmpeg and the components below,",
    "each linked statically. It is distributed under **GPL-3.0-or-later**: FFmpeg is",
    "configured with `--enable-gpl`, required by x264 and x265, and with",
    "`--enable-version3`, required because OpenSSL is Apache-2.0.",
    "",
    "The complete corresponding source for everything listed here is distributed",
    f"alongside it as `{src_name}` (SHA-256 `{src_sha}`).",
    "",
    "Full licence texts are in `LICENSES/`.",
    "",
    "| Component | Licence | Source |",
    "| --- | --- | --- |",
    f"| jellyfin-ffmpeg {policy['ffmpeg']['baseline']} | GPL-3.0-or-later | "
    f"{policy['ffmpeg']['repository']} @ `{policy['ffmpeg']['commit'][:12]}` |",
]
for c in policy["components"]:
    ref = c.get("sha256", c.get("commit", ""))[:12]
    lines.append(f"| {c['name']} | {c['license']} | {c.get('url') or c['repository']} @ `{ref}` |")
lines += [
    "",
    "## Deliberately absent",
    "",
    "| Component | Why |",
    "| --- | --- |",
]
for name, why in sorted(policy["excluded"].items()):
    lines.append(f"| {name} | {why} |")
lines.append("")
with open(os.path.join(rt, "THIRD_PARTY_NOTICES.md"), "w") as h:
    h.write("\n".join(lines))
print("  SOURCE.json, sbom.cdx.json and THIRD_PARTY_NOTICES.md written")
PY

# =============================================================================
# 3. The runtime archive
# =============================================================================
find "${RT}" -type d -exec chmod 0755 {} +
find "${RT}" -type f -exec chmod 0644 {} +
chmod 0755 "${RT}/bin/ffmpeg" "${RT}/bin/ffprobe"
ff_clamp_mtimes "${RT}"

ARCHIVE="${OUT}/${NAME}-${ARCH}.tar.xz"
# Modes normalised for the same reason owner and mtime are: otherwise the
# archive records the packaging machine's umask. See ff_normalize_modes.
ff_normalize_modes "${RT}"
( cd "${OUT}" && tar --create \
    --sort=name --owner=0 --group=0 --numeric-owner \
    --mtime="@${EPOCH}" --format=gnu \
    "$(basename "${RT}")" | xz -9 -T1 > "${ARCHIVE}" )
touch --date="@${EPOCH}" "${ARCHIVE}" "${SRC_ARCHIVE}"

{
    printf '%s  %s\n' "$(ff_sha256 "${ARCHIVE}")"     "$(basename "${ARCHIVE}")"
    printf '%s  %s\n' "${SRC_SHA}"                     "$(basename "${SRC_ARCHIVE}")"
} > "${OUT}/SHA256SUMS-${ARCH}.txt"

ff_log "packaged"
cat "${OUT}/SHA256SUMS-${ARCH}.txt"
printf '%s\n' "${ARCHIVE}"
