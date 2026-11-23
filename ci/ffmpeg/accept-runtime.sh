#!/usr/bin/env bash
# Functional acceptance for a built Tesserafin FFmpeg runtime (F0 / #229).
#
# Usage: ci/ffmpeg/accept-runtime.sh --archive FILE --arch RID [--evidence OUT.json]
#
# Exercises the EXTRACTED ARCHIVE, never a source-tree binary, in each declared
# environment on the host's native architecture. Every test vector is generated
# by the runtime itself from lavfi, so nothing redistributable is downloaded and
# no sample of unknown provenance enters the repository.
#
# What is proven per environment:
#   software H.264 encode and decode, native AAC encode and decode, HLS
#   segmenting, subtitle burn-in through libass, image extraction, ffprobe JSON,
#   software HEVC Main/Main10, HLS/fMP4 random access, a server-shaped command
#   line, clean SIGTERM, `-hwaccels` returning rather
#   than aborting, and that no ffmpeg is taken from $PATH.

set -euo pipefail

# shellcheck source=ci/ffmpeg/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

ARCHIVE=""; ARCH=""; EVIDENCE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --archive)  ARCHIVE="$2"; shift 2 ;;
        --arch)     ARCH="$2"; shift 2 ;;
        --evidence) EVIDENCE="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -f "${ARCHIVE}" ]] || ff_die "--archive must be an existing runtime archive"
[[ -n "${ARCH}" ]]    || ff_die "--arch is required"

ff_load_manifest
TRIPLET="$(ff_arch_triplet "${ARCH}")"
[[ "$(uname -m)" == "${TRIPLET}" ]] || ff_die \
    "this host is $(uname -m); ${ARCH} acceptance must run on a native ${TRIPLET} machine. \
Emulation is not acceptance."

# The declared support matrix, pinned by multi-architecture INDEX digest so the
# amd64 and arm64 runs each resolve the native image of the same release and an
# environment cannot drift under the evidence. Each digest was read from
# `docker buildx imagetools inspect <tag>`, not copied from prose.
ENVIRONMENTS=(
    "debian:12|debian@sha256:813017f3d62be4b5891a7acca6a01bdcd4b8513daa81b1ab99d3a50385b26931"
    "ubuntu:24.04|ubuntu@sha256:561618e2c15bf2397621dd04f96926663a3b5616c189cf7e38db7e82f5c538ea"
    "rockylinux:9|rockylinux/rockylinux@sha256:8101994123cf3d0a8fee517bee7f39e555c7d92bd2d9eb3303cc988a0eeed00f"
    "fedora:42|fedora@sha256:99e203b80b1c3d8f7e161ec10a68fd02b081ef83a3963553e513c82846b97814"
)

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT
tar -xf "${ARCHIVE}" -C "${WORK}"
ROOT="${WORK}/$(basename "${ARCHIVE}" .tar.xz)"
[[ -x "${ROOT}/bin/ffmpeg" ]] || ff_die "the archive did not unpack to ${ROOT}/bin/ffmpeg"

FAILURES=0
RESULTS=()

# The in-container suite. Deliberately does NOT put the runtime on $PATH: every
# invocation is by absolute path, and one case proves a bare `ffmpeg` is absent,
# so a passing run cannot be a distribution binary answering by accident.
read -r -d '' SUITE <<'SH' || true
set -uo pipefail
FF=/rt/bin/ffmpeg
FP=/rt/bin/ffprobe
T=$(mktemp -d); cd "$T"
ok(){ printf 'PASS %s\n' "$1"; }
no(){ printf 'FAIL %s :: %s\n' "$1" "${2:-}"; }
try(){ name="$1"; shift; out=$("$@" 2>&1); if [ $? -eq 0 ]; then ok "$name"; else no "$name" "$(printf '%s' "$out" | tail -1)"; fi; }

# A command that is EXPECTED to fail, and must fail in a CONTROLLED way rather
# than crash. The distinction is the whole point of Phase 6: the upstream
# portable binary raises SIGABRT when libva is missing a symbol, and a caller
# that only checks for a non-zero exit cannot tell that apart from "no device".
#
# Exit code alone cannot make the distinction here. ffmpeg reports AVERROR
# values by truncating a negative errno into the exit status, so EINVAL leaves
# 234 and EPERM leaves 255 — squarely inside the range a signal death would
# occupy. What separates them is what reaches stderr: a controlled failure
# prints a diagnostic, and a crash prints an abort signature or nothing at all.
ABORT_SIGNATURE='Assertion|assertion failed|Segmentation fault|stack smashing|core dumped|terminated by signal|Aborted'
controlled(){
  name="$1"; shift
  out=$("$@" 2>&1); rc=$?
  if printf '%s' "$out" | grep -qE "$ABORT_SIGNATURE"; then
    no "$name" "crashed: $(printf '%s' "$out" | grep -E "$ABORT_SIGNATURE" | tail -1)"
  elif [ "$rc" -eq 0 ]; then
    ok "$name"
  elif [ -n "$out" ]; then
    # Non-zero with a diagnostic the caller can read and fall back on.
    ok "$name"
  else
    no "$name" "exit $rc with nothing on stderr; a silent death is not a controlled result"
  fi
}

# no ffmpeg from $PATH: the environments must not carry one, and nothing here
# relies on finding one.
if command -v ffmpeg >/dev/null 2>&1; then
  no path-clean "a system ffmpeg exists at $(command -v ffmpeg); this environment cannot prove independence"
else
  ok path-clean
fi

try version        "$FF" -hide_banner -version
try buildconf      "$FF" -hide_banner -buildconf
try hwaccels       "$FF" -hide_banner -hwaccels

# The hardware surface must be a QUERY, never an abort. -hwaccels above already
# has to return; these are the paths that actually touch libva.
#
# The distinction being tested is not "does it work" — no container here has a
# render node — but "does it fail like a program or die like a crash". The
# upstream portable binary raises SIGABRT here, through an implib trampoline
# that turns a missing libva symbol into assert(0), and a server that only
# checks a non-zero exit cannot tell the two apart.
controlled vaapi-probe-no-device "$FF" -hide_banner -loglevel error \
    -init_hw_device vaapi=va:/dev/dri/renderD128 -f lavfi -i nullsrc=s=64x64 -frames:v 1 -f null -
controlled vaapi-probe-absent-path "$FF" -hide_banner -loglevel error \
    -init_hw_device vaapi=va:/dev/dri/renderD999 -f lavfi -i nullsrc=s=64x64 -frames:v 1 -f null -
controlled qsv-probe "$FF" -hide_banner -loglevel error \
    -init_hw_device qsv=hw -f lavfi -i nullsrc=s=64x64 -frames:v 1 -f null -
controlled cuda-probe "$FF" -hide_banner -loglevel error \
    -init_hw_device cuda=gpu -f lavfi -i nullsrc=s=64x64 -frames:v 1 -f null -

# Software fallback after a failed hardware attempt must be a real encode, not a
# crash the caller swallowed. Run the hardware attempt, ignore it, then require
# the software path to produce actual output in the same environment.
hwout=$("$FF" -hide_banner -loglevel error -init_hw_device vaapi=va:/dev/dri/renderD128 \
    -f lavfi -i testsrc=size=64x64:rate=5:duration=1 -c:v h264_vaapi -f null - 2>&1)
if printf '%s' "$hwout" | grep -qE "$ABORT_SIGNATURE"; then
  no software-fallback "the hardware attempt crashed; a fallback here would be hiding it"
else
  "$FF" -hide_banner -loglevel error -f lavfi -i testsrc=size=64x64:rate=5:duration=1 \
      -c:v libx264 -preset ultrafast -f mp4 fallback.mp4 -y >/dev/null 2>&1
  if [ -s fallback.mp4 ]; then ok software-fallback; else no software-fallback "the software path produced nothing"; fi
fi

# H.264 software encode, then decode what was produced.
try h264-encode "$FF" -hide_banner -loglevel error -f lavfi -i testsrc=size=320x240:rate=25:duration=2 \
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p -f mp4 v.mp4 -y
try h264-decode "$FF" -hide_banner -loglevel error -i v.mp4 -f rawvideo -frames:v 10 /dev/null -y

# Native AAC, explicitly the built-in encoder rather than any external one.
try aac-encode "$FF" -hide_banner -loglevel error -f lavfi -i "sine=frequency=440:duration=2" \
    -c:a aac -strict -2 -f mp4 a.m4a -y
try aac-decode "$FF" -hide_banner -loglevel error -i a.m4a -f s16le -frames:a 20 /dev/null -y

# MP3 and Opus, both named in the capability requirement.
try mp3-encode  "$FF" -hide_banner -loglevel error -f lavfi -i "sine=frequency=440:duration=1" -c:a libmp3lame -f mp3 a.mp3 -y
try opus-encode "$FF" -hide_banner -loglevel error -f lavfi -i "sine=frequency=440:duration=1" -c:a libopus -f ogg a.ogg -y

# Run the same real-file depth contract on each declared minimal environment.
# Mounted separately from the archive so the runtime layout stays unchanged.
try hevc-main-main10 bash /hevc-depth.sh --ffmpeg "$FF" --ffprobe "$FP"

# HLS segmenting, the shape the server uses for adaptive playback.
mkdir -p hls
try hls "$FF" -hide_banner -loglevel error -i v.mp4 -c:v libx264 -preset ultrafast \
    -f hls -hls_time 1 -hls_segment_filename hls/seg%d.ts hls/out.m3u8 -y
[ -s hls/out.m3u8 ] && [ -s hls/seg0.ts ] && ok hls-output || no hls-output "no playlist or segment produced"

# Subtitle burn-in through libass.
printf '1\n00:00:00,000 --> 00:00:02,000\nTesserafin subtitle burn-in\n\n' > s.srt
try subtitle-burn-in "$FF" -hide_banner -loglevel error -i v.mp4 -vf "subtitles=s.srt" \
    -c:v libx264 -preset ultrafast -f mp4 burned.mp4 -y

# Image extraction, used for chapter and thumbnail images.
try image-extract "$FF" -hide_banner -loglevel error -i v.mp4 -frames:v 1 -f image2 thumb.jpg -y
[ -s thumb.jpg ] && ok image-nonempty || no image-nonempty "thumb.jpg is empty"

# zscale, the required filter zimg provides.
try zscale "$FF" -hide_banner -loglevel error -i v.mp4 -vf "zscale=w=160:h=120" -frames:v 5 -f null -

# ffprobe JSON, which is how the server reads media.
# Checked without python3: the Fedora image does not carry it, and a test that
# depends on the ENVIRONMENT having a JSON parser is testing the wrong thing.
"$FP" -hide_banner -loglevel error -print_format json -show_format -show_streams v.mp4 > probe.json 2>probe.err
if head -c1 probe.json | grep -q '{' \
   && grep -q '"codec_type"' probe.json \
   && grep -q '"format"' probe.json \
   && tail -c2 probe.json | grep -q '}'; then
  ok ffprobe-json
else
  no ffprobe-json "$(tail -1 probe.err 2>/dev/null || head -c 120 probe.json)"
fi

# A server-shaped invocation: the long transcode command line the server builds.
try server-shaped "$FF" -hide_banner -loglevel error -fflags +genpts -i v.mp4 \
    -map 0:0 -c:v libx264 -preset veryfast -crf 23 -maxrate 3000k -bufsize 6000k \
    -profile:v high -level 4.1 -pix_fmt yuv420p -force_key_frames "expr:gte(t,n_forced*3)" \
    -f mp4 -movflags +faststart server.mp4 -y

# Clean SIGTERM: a long encode must exit promptly and not be killed by SIGKILL.
"$FF" -hide_banner -loglevel error -f lavfi -i testsrc=size=320x240:rate=25:duration=600 \
    -c:v libx264 -preset ultrafast -f null - >/dev/null 2>&1 &
pid=$!
sleep 3
kill -TERM "$pid" 2>/dev/null
for i in $(seq 1 50); do kill -0 "$pid" 2>/dev/null || break; sleep 0.2; done
if kill -0 "$pid" 2>/dev/null; then kill -9 "$pid" 2>/dev/null; no sigterm "still running 10s after SIGTERM"; else ok sigterm; fi
SH

for entry in "${ENVIRONMENTS[@]}"; do
    label="${entry%%|*}"; image="${entry#*|}"
    echo "== ${label}"
    # bash, not sh: Debian and Ubuntu ship dash as /bin/sh, which rejects
    # `set -o pipefail`. Under sh the suite died on its first line and the
    # environment silently contributed no results at all.
    output="$(docker run --rm --volume "${ROOT}:/rt:ro" \
                 --volume "${FF_REPO_ROOT}/ci/ffmpeg/verify-hevc-depth.sh:/hevc-depth.sh:ro" "${image}" \
                 bash -c "$(printf '%s' "${SUITE}")" 2>&1 || true)"
    # An environment that produced no verdicts has not been tested. Silence is
    # not a pass.
    if ! grep -qE '^(PASS|FAIL)' <<<"${output}"; then
        echo "  FAIL: ${label} produced no results at all" >&2
        FAILURES=$((FAILURES + 1))
        printf '       %s\n' "$(tail -3 <<<"${output}")"
        RESULTS+=("${label}|FAIL suite-did-not-run")
        continue
    fi
    while read -r line; do
        [[ -n "${line}" ]] || continue
        case "${line}" in
            PASS*) echo "  ok  : ${line#PASS }" ;;
            FAIL*) echo "  FAIL: ${line#FAIL }" >&2; FAILURES=$((FAILURES + 1)) ;;
            *)     echo "       ${line}" ;;
        esac
        RESULTS+=("${label}|${line}")
    done <<<"${output}"
done

if [[ -n "${EVIDENCE}" ]]; then
    printf '%s\n' "${RESULTS[@]}" | python3 -c '
import json, sys
rows = {}
for line in sys.stdin:
    line = line.strip()
    if not line or "|" not in line:
        continue
    env, rest = line.split("|", 1)
    verdict, _, name = rest.partition(" ")
    rows.setdefault(env, {})[name.split(" ::")[0]] = verdict
json.dump({"$comment": "Software functional acceptance of the extracted runtime archive. "
                        "Native architecture only; no hardware claim is made here.",
           "results": rows}, open(sys.argv[1], "w"), indent=2, sort_keys=True)
open(sys.argv[1], "a").write("\n")
' "${EVIDENCE}"
    echo "  evidence written to ${EVIDENCE}"
fi

echo
if [[ "${FAILURES}" -gt 0 ]]; then
    echo "ACCEPTANCE: FAIL — ${FAILURES} check(s) failed" >&2
    exit 1
fi
echo "ACCEPTANCE: PASS — ${ARCH} on every declared environment"
