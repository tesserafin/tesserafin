#!/usr/bin/env bash
# Negative controls for the Tesserafin FFmpeg runtime gates (F0 / #229).
#
# Usage: ci/tests/ffmpeg-controls.sh --runtime DIR --source-archive FILE
#
# A gate that only passes its intended input is not proven. Each control feeds a
# hostile input to the REAL gate and requires it to reject, and each is checked
# for rejecting for the RIGHT REASON — a control that fails because a script
# crashed on an unrelated error would otherwise look like success.
#
# Everything happens on disposable copies. Nothing here is committed as a
# fixture, and no corrupted artifact is ever written back to the build output.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=ci/ffmpeg/lib.sh
source "${ROOT}/ci/ffmpeg/lib.sh"

RUNTIME=""; SRC_ARCHIVE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --runtime)        RUNTIME="$2"; shift 2 ;;
        --source-archive) SRC_ARCHIVE="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -d "${RUNTIME}" ]] || ff_die "--runtime must be an existing packaged runtime directory"

ff_load_manifest
LAB="$(mktemp -d)"
trap 'rm -rf "${LAB}"' EXIT
PASSED=0; FAILED=0

# <name> <expected message fragment> <command...>
control() {
    local name="$1" expect="$2"; shift 2
    local out rc
    out="$("$@" 2>&1)" && rc=0 || rc=$?
    if [[ "${rc}" -eq 0 ]]; then
        FAILED=$((FAILED + 1)); echo "  FAIL ${name} — the gate ACCEPTED the hostile input"
    elif ! grep -qiF -- "${expect}" <<<"${out}"; then
        FAILED=$((FAILED + 1))
        echo "  FAIL ${name} — rejected, but not for the intended reason (wanted '${expect}')"
        printf '        %s\n' "$(tail -3 <<<"${out}")"
    else
        PASSED=$((PASSED + 1)); echo "  ok   ${name}"
    fi
}

# A writable copy of the policy files for the manifest-level controls.
policy_copy() { # -> echoes a directory holding components.json + flags
    local d; d="$(mktemp -d "${LAB}/policy.XXXX")"
    cp "${FF_COMPONENTS}" "${d}/components.json"
    cp "${FF_FLAGS_FILE}" "${d}/flags.txt"
    printf '%s\n' "${d}"
}
gate_policy() { # <dir>
    "${ROOT}/ci/ffmpeg/verify-components.sh" --components "$1/components.json" --flags "$1/flags.txt"
}

echo "== configuration policy"
d="$(policy_copy)"; echo '--enable-libfdk-aac' >> "${d}/flags.txt"
control "1  --enable-libfdk-aac is refused" "--enable-libfdk-aac is present" gate_policy "${d}"

d="$(policy_copy)"; echo '--enable-nonfree' >> "${d}/flags.txt"
control "2  --enable-nonfree is refused" "--enable-nonfree is present" gate_policy "${d}"

d="$(policy_copy)"
python3 -c '
import json,sys
p=sys.argv[1]; j=json.load(open(p)); j["components"][0]["license"]="Totally-Made-Up-1.0"
json.dump(j, open(p,"w"))' "${d}/components.json"
control "3  an unclassified licence is refused" "unclassified licence" gate_policy "${d}"

d="$(policy_copy)"
python3 -c '
import json,sys
p=sys.argv[1]; j=json.load(open(p))
for c in j["components"]:
    if c["sourceType"]=="tar":
        c["url"]="https://example.invalid/project/main/latest.tar.gz"; break
json.dump(j, open(p,"w"))' "${d}/components.json"
control "4  a moving source reference is refused" "moving reference" gate_policy "${d}"

d="$(policy_copy)"
python3 -c '
import json,sys
p=sys.argv[1]; j=json.load(open(p))
for c in j["components"]:
    if c["sourceType"]=="git": c["commit"]="deadbeef"; break
json.dump(j, open(p,"w"))' "${d}/components.json"
control "4b a truncated commit pin is refused" "not pinned to a full commit" gate_policy "${d}"

echo "== source integrity"
d="$(mktemp -d "${LAB}/fetch.XXXX")"
mkdir -p "${d}/cache/archives" "${d}/policy"
python3 -c '
import json,sys
p,q=sys.argv[1:3]; j=json.load(open(p))
keep=[c for c in j["components"] if c["sourceType"]=="tar"][:1]
keep[0]["sha256"]="0"*64
j["components"]=keep
json.dump(j, open(q,"w"))' "${FF_COMPONENTS}" "${d}/policy/components.json"
fetch_with_bad_sum() {
    FF_COMPONENTS="${d}/policy/components.json" \
        "${ROOT}/ci/ffmpeg/fetch-sources.sh" --cache "${d}/cache"
}
control "5  a source checksum mismatch stops the build" "checksum mismatch" fetch_with_bad_sum

echo "== the built runtime"
STAGE="${LAB}/stage"
cp -a "${RUNTIME}" "${STAGE}"
run_gate() { "${ROOT}/ci/ffmpeg/verify-runtime.sh" --stage "$1" --arch linux-x64; }

# Simulate FFmpeg's silent pixel-format downgrade: the command succeeds and
# writes a valid HEVC file, but Main10 has become Main/yuv420p. The contract
# must inspect the produced bytes rather than trusting the requested format.
cat > "${LAB}/drop-depth" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
args=("$@")
if [[ " $* " == *' libx265 '* ]]; then
    for i in "${!args[@]}"; do
        case "${args[$i]}" in
            +yuv420p10le) args[$i]=yuv420p ;;
            main10) args[$i]=main ;;
        esac
    done
fi
exec "${HEVC_CONTROL_FFMPEG}" "${args[@]}"
SH
chmod +x "${LAB}/drop-depth"
control "7  successful 8-bit downgrade is refused" "expected hevc/Main 10/yuv420p10le" \
    env HEVC_CONTROL_FFMPEG="$(cd "${STAGE}" && pwd)/bin/ffmpeg" \
    "${ROOT}/ci/ffmpeg/verify-hevc-depth.sh" --ffmpeg "${LAB}/drop-depth" --ffprobe "${STAGE}/bin/ffprobe"

# patchelf runs in the pinned builder rather than relying on a workstation tool.
BUILDER_TAG="tesserafin-ffmpeg-builder:${FF_BUILD_REVISION}"
docker build --quiet --tag "${BUILDER_TAG}" "${ROOT}/ci/ffmpeg/builder" >/dev/null
pe() { docker run --rm --user "$(id -u):$(id -g)" --volume "${LAB}:/lab" "${BUILDER_TAG}" patchelf "$@"; }

s="${LAB}/rpath"; cp -a "${STAGE}" "${s}"
pe --set-rpath '/opt/jellyfin-ffmpeg/lib' "/lab/rpath/bin/ffmpeg"
control "8  an absolute vendor RUNPATH is refused" "expected exactly" run_gate "${s}"

s="${LAB}/needed"; cp -a "${STAGE}" "${s}"
pe --add-needed 'libtotally-undeclared.so.9' "/lab/needed/bin/ffmpeg"
control "9  an undeclared DT_NEEDED is refused" "undocumented libtotally-undeclared.so.9" run_gate "${s}"

s="${LAB}/nobundle"; cp -a "${STAGE}" "${s}"; rm -f "${s}/lib/libva.so.2"
control "8b a required library that is not bundled is refused" "not bundled" run_gate "${s}"

s="${LAB}/buildpath"; cp -a "${STAGE}" "${s}"
# A build path leaks through a section the compiler never rewrote — a generated
# config, a dependency that dropped CFLAGS, a .comment record. Reproduce that
# shape with objcopy rather than by appending bytes to the file: a section is
# what a real leak looks like, and the binary stays loadable, so the gate has to
# catch it by reading the binary rather than by the binary failing to run.
printf '/tmp/tf-ffbuild/x264\n' > "${LAB}/leak.txt"
docker run --rm --user "$(id -u):$(id -g)" --volume "${LAB}:/lab" "${BUILDER_TAG}" \
    objcopy --add-section .tf_leak=/lab/leak.txt /lab/buildpath/bin/ffmpeg
control "11 an embedded build-host path is refused" "a Tesserafin build or dependency directory" run_gate "${s}"

glibc_floor_control() {
    FF_GLIBC_FLOOR=2.17 "${ROOT}/ci/ffmpeg/verify-runtime.sh" --stage "${STAGE}" --arch linux-x64
}
control "10 a GLIBC requirement above the floor is refused" "above the 2.17 floor" glibc_floor_control

s="${LAB}/aborting"; cp -a "${STAGE}" "${s}"
# A real ELF, not a shell script: a script fails the ELF-shape check first and
# never reaches the hardware query, so the control would pass for the wrong
# reason. This stands in for the upstream implib trampoline — a binary whose
# hardware query raises SIGABRT.
cat > "${LAB}/abort-stub.c" <<'STUB'
#include <signal.h>
#include <stdio.h>
#include <string.h>
int main(int argc, char **argv) {
    for (int i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "-hwaccels")) { raise(SIGABRT); }
        if (!strcmp(argv[i], "-L")) {
            puts("GNU General Public License version 3 or later"); return 0;
        }
    }
    return 0;
}
STUB
docker run --rm --user "$(id -u):$(id -g)" --volume "${LAB}:/lab" "${BUILDER_TAG}" \
    gcc -O0 -o /lab/aborting/bin/ffmpeg /lab/abort-stub.c
control "14 a libva probe that aborts is refused" "did not return cleanly" run_gate "${s}"

echo "== redistribution closure"
if [[ -n "${SRC_ARCHIVE}" && -f "${SRC_ARCHIVE}" ]]; then
    closure() { "${ROOT}/ci/ffmpeg/verify-closure.sh" --runtime "$1" --source-archive "$2"; }

    s="${LAB}/nolicences"; cp -a "${RUNTIME}" "${s}"; rm -rf "${s}/LICENSES"
    control "7  a runtime with no licence texts is refused" "LICENSES" closure "${s}" "${SRC_ARCHIVE}"

    s="${LAB}/wrongsrc"; cp -a "${RUNTIME}" "${s}"
    python3 -c '
import json,sys
p=sys.argv[1]+"/SOURCE.json"; j=json.load(open(p))
j["correspondingSource"]["sha256"]="0"*64
json.dump(j, open(p,"w"), indent=2, sort_keys=True)' "${s}"
    control "6  a corresponding-source digest that does not match is refused" \
            "the archive hashes to" closure "${s}" "${SRC_ARCHIVE}"

    # Control 6 doctors the provenance and leaves the archive alone. This is the
    # other direction: the provenance is exactly as generated, and the SOURCE a
    # recipient would receive has been edited afterwards. Both sides have to be
    # proven, because a gate that only compared SOURCE.json against itself would
    # pass control 6 and still ship altered source.
    altered_source() {
        local lab="${LAB}/altered" arch
        rm -rf "${lab}"; mkdir -p "${lab}/x"
        tar --use-compress-program=unzstd -xf "${SRC_ARCHIVE}" -C "${lab}/x"
        # A real edit to a real source file, not a corrupted byte: this is the
        # scenario where someone patches the shipped source and reships it.
        local victim
        victim="$(find "${lab}/x" -name 'configure' -path '*ffmpeg*' -print -quit)"
        [[ -n "${victim}" ]] || victim="$(find "${lab}/x" -type f -name '*.c' -print -quit)"
        printf '\n# altered after provenance generation\n' >> "${victim}"
        arch="${lab}/$(basename "${SRC_ARCHIVE}")"
        tar -C "${lab}/x" -cf - . | zstd -q -o "${arch}"
        "${ROOT}/ci/ffmpeg/verify-closure.sh" --runtime "${RUNTIME}" --source-archive "${arch}"
    }
    control "13 source altered after provenance generation is refused" \
            "the archive hashes to" altered_source

    s="${LAB}/hwclaim"; cp -a "${RUNTIME}" "${s}"
    python3 -c '
import json,sys
p=sys.argv[1]+"/capability.json"; j=json.load(open(p))
j["hardwareRuntimeEvidence"]["nvenc"]="works on all NVIDIA GPUs"
json.dump(j, open(p,"w"), indent=2, sort_keys=True)' "${s}"
    control "15 a hardware claim inside the archive is refused" \
            "may only say 'not runtime-tested'" closure "${s}" "${SRC_ARCHIVE}"
else
    echo "  note: no --source-archive given; controls 6, 7, 13 and 15 not run"
fi

echo "== determinism"
epoch_control() {
    # SOURCE_DATE_EPOCH must come from the pinned baseline, never the clock. If
    # ff_source_date_epoch ever returned "now", two builds minutes apart would
    # differ; this asserts the value is fixed rather than trusting the comment.
    local a b
    a="$(ff_source_date_epoch)"; sleep 1; b="$(ff_source_date_epoch)"
    [[ "${a}" == "${b}" ]] || { echo "SOURCE_DATE_EPOCH moved: ${a} then ${b}"; return 1; }
    [[ "${a}" -lt "$(date +%s)" ]] || { echo "SOURCE_DATE_EPOCH is in the future"; return 1; }
    return 0
}
if epoch_control; then
    PASSED=$((PASSED + 1)); echo "  ok   12 SOURCE_DATE_EPOCH is fixed, not the clock"
else
    FAILED=$((FAILED + 1)); echo "  FAIL 12 SOURCE_DATE_EPOCH is not stable"
fi

echo
echo "negative controls: ${PASSED} passed, ${FAILED} failed"
[[ "${FAILED}" -eq 0 ]] || exit 1
