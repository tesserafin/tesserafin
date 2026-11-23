#!/usr/bin/env bash
# Functional software HEVC contract: compiled pixel formats are not evidence.
# Generates lossless 10-bit input independently of x265, encodes both depths,
# probes and decodes the result, then checks Main10 HLS/fMP4 and seeking.
# Runs without Python or a GPU, including inside F0's minimal distro containers.
set -euo pipefail

FF=""; FP=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --ffmpeg) FF="$2"; shift 2 ;;
        --ffprobe) FP="$2"; shift 2 ;;
        *) echo "hevc-depth: unknown argument: $1" >&2; exit 2 ;;
    esac
done
[[ -x "${FF}" && -x "${FP}" ]] || { echo 'hevc-depth: executable --ffmpeg and --ffprobe required' >&2; exit 2; }
# Resolve before cd: callers may supply a relative staged runtime path.
FF="$(cd "$(dirname "${FF}")" && pwd)/$(basename "${FF}")"
FP="$(cd "$(dirname "${FP}")" && pwd)/$(basename "${FP}")"
WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT
cd "${WORK}"

run() {
    if ! "$@" >command.log 2>&1; then
        tail -12 command.log >&2
        echo 'hevc-depth: command failed' >&2
        exit 1
    fi
}
probe() { # file, profile, pixel format
    local got
    got="$("${FP}" -v error -select_streams v:0 \
        -show_entries stream=codec_name,profile,pix_fmt -of default=noprint_wrappers=1 "$1")"
    if ! grep -qxF 'codec_name=hevc' <<<"${got}" \
        || ! grep -qxF "profile=$2" <<<"${got}" \
        || ! grep -qxF "pix_fmt=$3" <<<"${got}"; then
        echo "hevc-depth: $1 expected hevc/$2/$3; got ${got//$'\n'/, }" >&2
        exit 1
    fi
}

# FFV1 can produce this fixture even when libx265 is accidentally 8-bit-only.
run "${FF}" -nostdin -hide_banner -loglevel error -filter_threads 1 \
    -f lavfi -i 'testsrc2=size=160x96:rate=8:duration=2,format=yuv420p10le' \
    -c:v ffv1 -y input10.mkv
run "${FF}" -nostdin -hide_banner -loglevel error -filter_threads 1 \
    -i input10.mkv -vf format=yuv420p -c:v ffv1 -y input8.mkv
X265=( -c:v libx265 -preset ultrafast -x265-params 'pools=1:frame-threads=1:log-level=error' )
for depth in 8 10; do
    pix=yuv420p; profile=main; expected=Main
    if [[ "${depth}" == 10 ]]; then pix=yuv420p10le; profile=main10; expected='Main 10'; fi
    # Require the exact encoder format, rather than accepting FFmpeg's
    # automatic downgrade to yuv420p. Each input already has that format.
    run "${FF}" -nostdin -hide_banner -loglevel error -filter_threads 1 \
        -i "input${depth}.mkv" "${X265[@]}" \
        -profile:v "${profile}" -pix_fmt "+${pix}" -y "hevc${depth}.mkv"
    probe "hevc${depth}.mkv" "${expected}" "${pix}"
    run "${FF}" -nostdin -v error -xerror -err_detect explode \
        -i "hevc${depth}.mkv" -map 0:v:0 -f null -
    echo "HEVC ${depth}-bit: encoded, probed ${expected}/${pix}, decoded"
done

# Re-encode the source (not stream-copy); exercise the same fMP4 packaging
# needed by an HEVC-capable client without claiming browser playback evidence.
run "${FF}" -nostdin -hide_banner -loglevel error -filter_threads 1 \
    -i input10.mkv "${X265[@]}" -profile:v main10 -pix_fmt +yuv420p10le \
    -tag:v hvc1 -g 4 -flags +cgop -f hls -hls_time 0.5 -hls_playlist_type vod \
    -hls_flags independent_segments \
    -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 \
    -hls_segment_filename 'hevc-%d.m4s' -y main10.m3u8
probe main10.m3u8 'Main 10' yuv420p10le
run "${FF}" -nostdin -v error -xerror -err_detect explode -i main10.m3u8 -map 0:v:0 -f null -
# An HLS client seeks by fetching the init and the selected fragment, not by
# asking FFmpeg's HLS demuxer to seek inside a two-second local playlist.
cat init.mp4 hevc-2.m4s > seek.mp4
run "${FF}" -nostdin -v error -xerror -err_detect explode -i seek.mp4 \
    -map 0:v:0 -frames:v 2 -f framemd5 seek.md5
frames="$(grep -cEv '^(#|$)' seek.md5 || true)"
[[ "${frames}" == 2 ]] || { echo "hevc-depth: seek decoded ${frames} frames, expected 2" >&2; exit 1; }
run "${FF}" -nostdin -v error -xerror -i main10.m3u8 -map 0:v:0 \
    -vf trim=start_frame=8:end_frame=10 -fps_mode passthrough -f framemd5 linear.md5
hashes() { awk -F, '!/^#/ && NF > 1 { gsub(/ /, "", $NF); print $NF }' "$1"; }
[[ "$(hashes seek.md5)" == "$(hashes linear.md5)" ]] \
    || { echo 'hevc-depth: seek frames differ from linear playback at 1 second' >&2; exit 1; }
echo 'HEVC Main10 HLS/fMP4: probed, decoded, seek decoded 2 frames'
echo 'HEVC DEPTH: PASS'
