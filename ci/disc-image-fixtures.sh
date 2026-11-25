#!/usr/bin/env bash
# Builds the disc image fixtures DiscImageEndToEndTests plays through a session (#305):
#
#   dvd.iso  a PAL DVD-Video image (MPEG-2 720x576 + AC-3), authored with dvdauthor
#   bd.iso   a Blu-ray image (H.264 1280x720p23.976 + AC-3), authored with tsMuxer
#
# Both last 200 s - FFmpeg's bluray protocol only selects playlists of at least 180 s - and show
# one solid colour per 10 s band, in the order of COLOURS below. A test reads the first frame a
# session serves for a position and checks its colour: that is the position actually read, not
# only a playlist entry. The tests keep the same list; change both together.
#
# Usage: ci/disc-image-fixtures.sh <output-dir>
#   FFMPEG    the FFmpeg that encodes the streams (default: ffmpeg on PATH)
#   TSMUXER   tsMuxeR 2.7.0 (https://github.com/justdan96/tsMuxer/releases/tag/2.7.0,
#             tsMuxer-2.7.0-linux.zip sha256 ceaaa181ab70e201685b1e45260d337d1cbdb0aba1408d4fbc47e232a7e4c987);
#             without it, bd.iso is not built
#   dvdauthor and genisoimage (or xorriso) on PATH; without them, dvd.iso is not built
#
# Then run the tests with TESSERAFIN_DISC_IMAGE_FIXTURES=<output-dir> and
# TESSERAFIN_PROJECT_FFMPEG=<an FFmpeg that opens disc images, e.g. the F0 runtime>.
set -euo pipefail

out="${1:?usage: $0 <output-dir>}"
ffmpeg="${FFMPEG:-ffmpeg}"
COLOURS=(red lime blue yellow cyan magenta white gray maroon olive green purple teal navy orange pink brown black silver gold)
BAND_SECONDS=10

mkdir -p "${out}"
out="$(cd "${out}" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "${work}"' EXIT

# One lavfi colour source per band, concatenated: a frame's colour names its band.
bands() { # <size> <rate>
    local inputs=() chain="" i
    for i in "${!COLOURS[@]}"; do
        inputs+=(-f lavfi -i "color=c=${COLOURS[$i]}:s=$1:r=$2:d=${BAND_SECONDS}")
        chain+="[$i:v]"
    done
    printf '%s\0' "${inputs[@]}" "-filter_complex" "${chain}concat=n=${#COLOURS[@]}:v=1:a=0[v]"
}
duration=$(( ${#COLOURS[@]} * BAND_SECONDS ))

if command -v dvdauthor >/dev/null && { command -v genisoimage >/dev/null || command -v xorriso >/dev/null; }; then
    mapfile -d '' video < <(bands 720x576 25)
    "${ffmpeg}" -hide_banner -loglevel error -y "${video[@]}" \
        -f lavfi -i "sine=frequency=440:duration=${duration}" \
        -map '[v]' -map "${#COLOURS[@]}:a" -c:v mpeg2video -b:v 1M -g 12 -aspect 4:3 \
        -c:a ac3 -ac 2 -ar 48000 -f dvd "${work}/title.mpg"
    cat >"${work}/dvd.xml" <<EOF
<dvdauthor><vmgm/><titleset><titles><video format="pal" aspect="4:3"/>
<pgc><vob file="${work}/title.mpg" chapters="0,1:00,2:00"/></pgc></titles></titleset></dvdauthor>
EOF
    VIDEO_FORMAT=PAL dvdauthor -o "${work}/dvd" -x "${work}/dvd.xml" >/dev/null 2>&1
    if command -v genisoimage >/dev/null; then
        genisoimage -quiet -dvd-video -udf -V TESSDVD -o "${out}/dvd.iso" "${work}/dvd"
    else
        xorriso -as mkisofs -quiet -dvd-video -udf -V TESSDVD -o "${out}/dvd.iso" "${work}/dvd"
    fi
    echo "built ${out}/dvd.iso"
else
    echo "skipped dvd.iso: needs dvdauthor and genisoimage (or xorriso)" >&2
fi

if [[ -n "${TSMUXER:-}" ]]; then
    mapfile -d '' video < <(bands 1280x720 24000/1001)
    "${ffmpeg}" -hide_banner -loglevel error -y "${video[@]}" -map '[v]' \
        -c:v libx264 -preset veryfast -crf 30 -g 24 -keyint_min 24 -bsf:v h264_mp4toannexb -f h264 "${work}/v.h264"
    "${ffmpeg}" -hide_banner -loglevel error -y -f lavfi -i "sine=frequency=440:duration=${duration}" \
        -c:a ac3 -ac 2 -ar 48000 "${work}/a.ac3"
    printf 'MUXOPT --blu-ray --vbr --label=TESSBD\nV_MPEG4/ISO/AVC, "%s", fps=23.976\nA_AC3, "%s"\n' \
        "${work}/v.h264" "${work}/a.ac3" >"${work}/bd.meta"
    "${TSMUXER}" "${work}/bd.meta" "${out}/bd.iso" >/dev/null
    echo "built ${out}/bd.iso"
else
    echo "skipped bd.iso: set TSMUXER to tsMuxeR 2.7.0" >&2
fi
