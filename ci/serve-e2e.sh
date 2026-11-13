#!/usr/bin/env bash
# Real, TCP-bound Tesserafin server for browser-driven end-to-end tests.
#
# WHY THIS EXISTS — the gap it closes:
#
#   ci/run.sh (the merge gate) and ci/smoke-e2e.sh (PR #32) both exercise the server through
#   Tesserafin.Server.Integration.Tests' WebApplicationFactory, i.e. an IN-PROCESS TestServer. That
#   factory's ConfigureWebHostBuilder never actually binds Kestrel: there is NO listening socket, so
#   no real browser can reach it. reefin-web's playwright.config.ts says as much in its own header
#   and deliberately ships no `webServer` block — which is why
#   `npx playwright test tests/e2e/theme-glass.spec.ts` fails with `connect ECONNREFUSED ::1:8096`.
#
#   This script is the missing link: it boots the REAL Tesserafin.Server executable, bound to a real TCP
#   port, serving a real reefin-web build, with a real admin user and a real movie library — against
#   a throwaway data directory that never touches a developer's actual Tesserafin installation.
#
# WHAT IT DOES, IN ORDER:
#
#   1. Picks a free TCP port (never assumes 8096 is free — override with --port).
#   2. Creates a throwaway data/config/cache/log tree under a mktemp -d directory.
#   3. Synthesizes real, decodable media fixtures with ffmpeg from the testsrc/sine lavfi sources
#      tests/Tesserafin.Server.Integration.Tests/EndToEnd/EndToEndMediaFixtures.cs (PR #32) and
#      HlsSmokeTests already use, so this depends on no committed binary test assets — but with
#      FOUR technically distinct playback scenarios rather than one recipe repeated (see the
#      "MEDIA FIXTURES" section below), each one ffprobe-asserted immediately after it is written.
#   4. Writes network.xml with the chosen port. This is the ONLY way to set the listen port:
#      Tesserafin.Server/Extensions/WebHostBuilderExtensions.cs calls `options.Listen(addr, httpPort)`
#      explicitly from `appHost.HttpPort`, which ApplicationHost reads from
#      NetworkConfiguration.InternalHttpPort. ASPNETCORE_URLS / --urls are IGNORED — don't try.
#   5. Boots Tesserafin.Server with --datadir/--webdir pointed at the throwaway tree and the web bundle.
#   6. Waits for readiness by POLLING /System/Info/Public — never a fixed `sleep`.
#   7. Seeds through the REAL public API, no database surgery and no invented endpoints:
#        GET  /Startup/User          -> initializes the user manager, creates the default user
#        POST /Startup/User          -> renames it to $TESSERAFIN_E2E_USER, sets $TESSERAFIN_E2E_PASSWORD
#        POST /Startup/Complete      -> marks the setup wizard done
#        POST /Users/AuthenticateByName -> obtains an access token
#        POST /Library/VirtualFolders?collectionType=movies&refreshLibrary=false      -> the library
#        POST /Library/VirtualFolders?collectionType=homevideos&refreshLibrary=true   -> the probes
#        POST /Users/New + POST /Users/{id}/Policy -> a second, NON-admin user granted the
#                                       Movies library only (reefin-web #15's access fixture;
#                                       see the "Restricted user" section for its contract)
#      refreshLibrary is requested exactly once, on the LAST folder: it scans the whole library
#      root, so one scan covers both — and two of them race and cancel each other (see
#      add_library). Then POLLS /UserViews until BOTH views actually materialize (the library
#      scan is asynchronous — "server up" is NOT "library visible").
#   8. Verifies through /Items that every fixture actually indexed with the codecs it was built
#      with, and that the movies library still holds EXACTLY two items (see the cross-repo
#      contract below). ffprobe proves the bytes on disk; this proves the server agreed.
#   9. Prints the base URL and the exact environment variables the Playwright specs consume.
#  10. ALWAYS tears down — server process AND temp directory — via a trap, on success, failure,
#      Ctrl-C or SIGTERM alike. Pass --keep to retain the temp tree for debugging.
#
# MEDIA FIXTURES — five scenarios, three libraries:
#
#   library "Movies" (CollectionType=movies) — EXACTLY TWO ITEMS, SEE THE CONTRACT NOTE BELOW
#     Smoke Test Movie (2020)/Smoke Test Movie (2020).mp4      H264 + AAC in MP4    -> DIRECT PLAY
#     Smoke Test Movie (2020)/Smoke Test Movie (2020).en.srt   SubRip sidecar       -> EXTERNAL SUB
#     Transcode Probe (2021)/Transcode Probe (2021).mp4        MPEG-4 Part 2 + AC-3 -> TRANSCODE
#
#   library "Codec Probes" (CollectionType=homevideos)
#     Remux Probe (2022)/Remux Probe (2022).mkv                H264 + AAC in MKV    -> REMUX
#
#   library "Film Probes" (no CollectionType: mixed content)
#     Container Film (2023)/Container Film (2023).ts           H264 + AAC in MPEG-TS -> REMUX, AS A MOVIE
#     Container Film (2023)/Container Film (2023).en.srt       SubRip sidecar       -> EXTERNAL SUB
#
#   WHY A THIRD LIBRARY FOR THE REMUX FILM: "Remux Probe" is a `Video` (homevideos resolves
#   nothing else), and the film playback journey only owns items of type `Movie`. A Movie cannot
#   go into "Movies" (exactly two items, below) and a second movies-typed library is ruled out
#   too, so the film lives in a mixed-content library, where a folder named "Title (Year)"
#   resolves as a Movie. It is 30 s long with a keyframe every second, unlike the 2 s fixtures:
#   a copy can only be cut at keyframes, and a seek and a resume need somewhere to go.
#
#   WHY MPEG-TS AND NOT MATROSKA FOR THE FILM: measured on this rig (2026-11-13), the Chrome
#   channel the specs run under answers canPlayType("video/x-matroska"), so the Web client
#   declares mkv and the server rightly DIRECT PLAYS H264/AAC-in-Matroska - the film played,
#   sought and resumed from the session file route. No browser plays a bare MPEG transport
#   stream in <video>, and the client declares no such container, so the same codecs in .ts
#   are a remux in every channel.
#
#   WHY MPEG-4 PART 2 + AC-3 FOR THE TRANSCODE FIXTURE: Chromium ships no MPEG-4 Part 2 (a.k.a.
#   MPEG-4 Visual / DivX / Xvid, ffprobe codec_name "mpeg4") video decoder and no AC-3 audio
#   decoder in any build — AC-3/E-AC-3 are compiled out of Chromium and only enabled in licensed
#   Google Chrome builds. Neither stream can direct-play, so the server has no choice but to
#   transcode. The container stays .mp4 on purpose: the incompatibility is carried entirely by the
#   codecs, so the item's resolution as a Movie never depends on an exotic container.
#
#   WHY THE REMUX FIXTURE IS PRODUCED WITH `-c copy` FROM THE DIRECT-PLAY MP4: that is what makes
#   it a genuine remux scenario rather than a second encode that merely looks similar. The script
#   asserts the two files' ffprobe stream fingerprints are byte-for-byte equal, so the ONLY
#   difference is the container. The unplayability here is purely container-level: Chromium's
#   <video> accepts MP4, WebM and Ogg, and while WebM and Matroska share a demuxer it only
#   admits the WebM codec set (VP8/VP9/AV1 + Opus/Vorbis) — H264/AAC-in-Matroska is rejected.
#
#   ON "CHROMIUM CAN PLAY H264/AAC": true only of builds compiled with the proprietary codecs,
#   which is Google Chrome and Playwright's `chrome`/`msedge` channels. Vanilla open-source
#   Chromium — including Playwright's own bundled `chromium` — omits H264, AAC, AC-3 and E-AC-3.
#   The transcode fixture is unaffected either way (mpeg4 and ac3 are absent from EVERY build),
#   but the direct-play fixture's direct-playability is a statement about the browser channel
#   the specs actually run under, not about Chromium in general.
#
#   WHY "Codec Probes" IS A SEPARATE, NON-movies LIBRARY: reefin-web's tests/e2e/library.spec.ts
#   asserts `toHaveCount(2)` on the movies grid in four places and indexes cards [0]/[1] by
#   position in its SortName test. Adding ANY third item to the movies library breaks those specs
#   against a perfectly healthy rig. It also resolves its library via
#   `.find(item => item.CollectionType === 'movies')`, which stays unambiguous only while exactly
#   one movies-typed library exists — hence homevideos for the probes library. The /Items check in
#   step 8 enforces both halves of that contract so a future fixture cannot silently break it.
#
# WHY 127.0.0.1 AND NOT localhost: the original failure was `ECONNREFUSED ::1:8096` — `localhost`
# resolved to IPv6 while the server listens on IPv4. The printed base URL is always explicitly
# 127.0.0.1 to remove that ambiguity.
#
# USAGE:
#
#   # Foreground: boot, seed, print the URL, hold the server up until Ctrl-C.
#   ./ci/serve-e2e.sh --webdir ../reefin-web/dist
#
#   # One-shot: boot, seed, run a command with TESSERAFIN_E2E_* exported, tear everything down.
#   ./ci/serve-e2e.sh --webdir ../reefin-web/dist \
#       --exec 'cd ../reefin-web && npx playwright test tests/e2e/theme-glass.spec.ts'
#
# The web bundle must be a `npm run build:production` output (a directory containing index.html).
# See docs/e2e-real-server.md for the full reefin-web build + run recipe.
#
# OPTIONS:
#   --webdir PATH     reefin-web production bundle to serve (default: ../reefin-web/dist)
#   --port N          TCP port to bind (default: an auto-detected free port)
#   --exec CMD        run CMD once the server is seeded and ready, then tear down; exit with its status
#   --user NAME       admin username to create   (default: $TESSERAFIN_E2E_USER or smokeadmin)
#   --password PW     admin password to set      (default: $TESSERAFIN_E2E_PASSWORD or smokepass123)
#   --datadir PATH    use PATH instead of a mktemp -d tree (implies --keep)
#   --no-build        skip `dotnet build`, reuse the existing Tesserafin.Server binary
#   --keep            do not delete the temp tree on exit (prints its path)
#   --timeout N       readiness timeout in seconds (default: 180)
#   -h, --help        show this header
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

SERVER_PROJECT="Tesserafin.Server/Tesserafin.Server.csproj"
# Tesserafin.Server.csproj sets <AssemblyName>tesserafin</AssemblyName>, so the built entry
# point is tesserafin.dll — NOT Tesserafin.Server.dll.
SERVER_DLL="Tesserafin.Server/bin/Debug/net10.0/tesserafin.dll"

# The Authorization header shape Tesserafin requires on every request. Mirrors the one
# reefin-web's tests/e2e specs send, so seeding and the specs authenticate identically.
AUTH_CLIENT='MediaBrowser Client="Tesserafin E2E Harness", Device="ci/serve-e2e.sh", DeviceId="tesserafin-ci-serve-e2e", Version="0.0.0"'

PORT=""
WEBDIR=""
EXEC_CMD=""
DATADIR=""
DO_BUILD=1
KEEP=0
READY_TIMEOUT=180
E2E_USER="${TESSERAFIN_E2E_USER:-smokeadmin}"
E2E_PASSWORD="${TESSERAFIN_E2E_PASSWORD:-smokepass123}"
# The reefin-web #15 access-restriction fixture (see the "Restricted user" section below).
RESTRICTED_USER="${TESSERAFIN_E2E_RESTRICTED_USER:-smokerestricted}"
RESTRICTED_PASSWORD="${TESSERAFIN_E2E_RESTRICTED_PASSWORD:-restrictedpass123}"

banner() {
    echo ""
    echo "======================================================================"
    echo "== $*"
    echo "======================================================================"
}

log() { echo "[serve-e2e] $*"; }
fail_usage() { echo "ERROR: $*" >&2; exit 2; }

# ---------------------------------------------------------------------------
# Argument parsing
# ---------------------------------------------------------------------------
while [ $# -gt 0 ]; do
    case "$1" in
        --port)     [ $# -ge 2 ] || fail_usage "--port requires a value";     PORT="$2"; shift 2 ;;
        --webdir)   [ $# -ge 2 ] || fail_usage "--webdir requires a PATH";    WEBDIR="$2"; shift 2 ;;
        --exec)     [ $# -ge 2 ] || fail_usage "--exec requires a command";   EXEC_CMD="$2"; shift 2 ;;
        --user)     [ $# -ge 2 ] || fail_usage "--user requires a value";     E2E_USER="$2"; shift 2 ;;
        --password) [ $# -ge 2 ] || fail_usage "--password requires a value"; E2E_PASSWORD="$2"; shift 2 ;;
        --datadir)  [ $# -ge 2 ] || fail_usage "--datadir requires a PATH";   DATADIR="$2"; KEEP=1; shift 2 ;;
        --timeout)  [ $# -ge 2 ] || fail_usage "--timeout requires seconds";  READY_TIMEOUT="$2"; shift 2 ;;
        --no-build) DO_BUILD=0; shift ;;
        --keep)     KEEP=1; shift ;;
        -h|--help)  sed -n '2,115p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *)          fail_usage "unexpected argument: $1" ;;
    esac
done

command -v curl >/dev/null 2>&1 || fail_usage "curl is required but not on PATH"
command -v ffmpeg >/dev/null 2>&1 || fail_usage "ffmpeg is required to synthesize media fixtures but is not on PATH"
command -v dotnet >/dev/null 2>&1 || fail_usage "dotnet is required but not on PATH"

# ---------------------------------------------------------------------------
# Resolve the web bundle. Serving a real bundle is mandatory: the specs call
# page.goto('/'), so the SERVER has to hand back the reefin-web SPA.
# ---------------------------------------------------------------------------
[ -n "$WEBDIR" ] || WEBDIR="$REPO_ROOT/../reefin-web/dist"
WEBDIR_ABS="$(cd "$WEBDIR" 2>/dev/null && pwd || true)"
[ -n "$WEBDIR_ABS" ] || fail_usage "--webdir path does not exist: $WEBDIR (build it with: cd reefin-web && npm run build:production)"
[ -f "$WEBDIR_ABS/index.html" ] || fail_usage \
    "'$WEBDIR_ABS' has no index.html — not a reefin-web production bundle (build it with: npm run build:production)"

# ---------------------------------------------------------------------------
# Pick a free port. Binding :0 and reading back what the kernel assigned is the
# only race-free way to learn a port that is actually free right now.
# ---------------------------------------------------------------------------
if [ -z "$PORT" ]; then
    PORT="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()' 2>/dev/null || true)"
    [ -n "$PORT" ] || fail_usage "could not auto-detect a free port (python3 unavailable?) — pass --port N explicitly"
fi
BASE_URL="http://127.0.0.1:${PORT}"

# ---------------------------------------------------------------------------
# Throwaway data tree. Never the user's real Tesserafin installation.
# ---------------------------------------------------------------------------
CREATED_TMP=0
if [ -z "$DATADIR" ]; then
    DATADIR="$(mktemp -d -t reefin-e2e-XXXXXXXX)"
    CREATED_TMP=1
fi
mkdir -p "$DATADIR"
DATADIR="$(cd "$DATADIR" && pwd)"
CONFIG_DIR="$DATADIR/config"
MEDIA_DIR="$DATADIR/media/movies"
# Second media root, indexed as a separate non-movies library so the movies library keeps the
# exactly-two-items shape reefin-web's library.spec.ts hard-codes (see the header).
PROBE_DIR="$DATADIR/media/probes"
FILM_ROOT="$DATADIR/media/films"
LOG_FILE="$DATADIR/server.log"
mkdir -p "$CONFIG_DIR" "$MEDIA_DIR" "$PROBE_DIR"

SERVER_PID=""
# Populated after AuthenticateByName; api() folds it into the Authorization header once set.
TOKEN=""

cleanup() {
    local status=$?
    trap - EXIT INT TERM

    if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
        log "stopping server (pid $SERVER_PID)"
        # SIGINT first: the host uses UseConsoleLifetime, which treats it as Ctrl-C and
        # shuts down promptly. Plain SIGTERM was observed to take >10s here. SIGKILL is
        # the backstop so this function can never leave an orphan holding the port.
        kill -INT "$SERVER_PID" 2>/dev/null || true
        for _ in $(seq 1 20); do
            kill -0 "$SERVER_PID" 2>/dev/null || break
            sleep 0.5
        done
        if kill -0 "$SERVER_PID" 2>/dev/null; then
            log "server still up after SIGINT — sending SIGKILL"
            kill -KILL "$SERVER_PID" 2>/dev/null || true
        fi
        wait "$SERVER_PID" 2>/dev/null || true
    fi

    if [ "$KEEP" -eq 1 ] || [ "$CREATED_TMP" -eq 0 ]; then
        log "data directory retained at: $DATADIR"
        log "server log: $LOG_FILE"
    else
        rm -rf "$DATADIR"
        log "removed throwaway data directory"
    fi

    exit "$status"
}
# Fires on normal exit, on any `set -e` failure, and on Ctrl-C / SIGTERM alike.
trap cleanup EXIT INT TERM

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
if [ "$DO_BUILD" -eq 1 ]; then
    banner "Building Tesserafin.Server (host dotnet SDK)"
    dotnet build "$SERVER_PROJECT" -c Debug --nologo -clp:ErrorsOnly
fi
[ -f "$SERVER_DLL" ] || fail_usage "server binary not found at $SERVER_DLL (drop --no-build, or run: dotnet build $SERVER_PROJECT)"

# ---------------------------------------------------------------------------
# Media fixtures — real, decodable files, synthesized on the fly, each one a
# TECHNICALLY DISTINCT playback scenario (see the MEDIA FIXTURES section in the
# header for the full rationale). Named "<Title> (<Year>).<ext>" inside a
# matching folder so Tesserafin's resolvers actually register them.
#
# EVERY fixture is ffprobe-asserted immediately after it is written. This is not
# ceremony: until 2026-07 this script built BOTH fixtures with one identical
# H264/AAC recipe, so "Transcode Probe" was a transcode scenario in name only and
# the transcode E2E path was silently exercising direct play. A fixture whose
# actual codec and container are unproven is worthless, and a filename is not
# proof of anything.
# ---------------------------------------------------------------------------
command -v ffprobe >/dev/null 2>&1 || fail_usage "ffprobe is required to verify the media fixtures but is not on PATH"

# Single scalar stream property, or empty if the stream does not exist.
# SELECTOR is an ffprobe -select_streams value: v:0 (first video), a:0, s:0.
probe_stream() {
    ffprobe -v error -select_streams "$2" -show_entries "stream=$3" \
        -of default=noprint_wrappers=1:nokey=1 "$1" 2>/dev/null | head -1
}

# The demuxer's format_name, which ffprobe reports as a comma-joined family list
# ("mov,mp4,m4a,3gp,3g2,mj2" for MP4, "matroska,webm" for MKV) — never a single name.
probe_format() {
    ffprobe -v error -show_entries format=format_name \
        -of default=noprint_wrappers=1:nokey=1 "$1" 2>/dev/null
}

# Container-independent identity of every stream in a file. Two files with equal
# fingerprints carry the same elementary streams — which is exactly what makes a
# remux a remux rather than a re-encode.
probe_fingerprint() {
    ffprobe -v error -show_entries stream=codec_name,profile,width,height,sample_rate,channels \
        -of csv=p=0 "$1" 2>/dev/null
}

fixture_fail() { echo "ERROR: fixture assertion failed: $*" >&2; exit 1; }

assert_codec() {
    # assert_codec FILE SELECTOR EXPECTED_CODEC_NAME
    local actual
    actual="$(probe_stream "$1" "$2" codec_name)"
    [ "$actual" = "$3" ] || fixture_fail "$1: stream $2 is codec_name='${actual:-<absent>}', expected '$3'"
    log "    ffprobe ok: stream $2 codec_name=$actual"
}

assert_container() {
    # assert_container FILE EXPECTED_DEMUXER — membership test against the family list.
    local actual
    actual="$(probe_format "$1")"
    case ",${actual}," in
        *",$2,"*) ;;
        *) fixture_fail "$1: format_name='${actual:-<absent>}' does not contain '$2'" ;;
    esac
    log "    ffprobe ok: format_name=$actual (contains '$2')"
}

assert_under_1mib() {
    local bytes
    bytes="$(stat -c %s "$1" 2>/dev/null || echo 0)"
    [ "$bytes" -gt 0 ] || fixture_fail "$1: file is empty or missing"
    [ "$bytes" -lt 1048576 ] || fixture_fail "$1: ${bytes} bytes — fixtures must stay under 1 MiB"
    log "    size ok: ${bytes} bytes"
}

# CROSS-REPO CONTRACT — these two TITLES are consumed by name in reefin-web:
#   tests/e2e/library.spec.ts:31-32  MOVIE_TITLE / OTHER_MOVIE_TITLE
# The specs assert on the displayed title (Tesserafin strips the parenthesised year), and
# library.spec.ts:108-109 additionally relies on "Smoke Test Movie" sorting BEFORE
# "Transcode Probe" under SortName ascending. Renaming either side in isolation silently
# breaks those specs — the harness still boots, seeds and serves, so the failure looks like
# an application bug rather than a fixture mismatch. That is exactly what happened with the
# previous names ("Tesserafin E2E Fixture" / "Tesserafin E2E Second Fixture"): 2 of 7 specs failed
# on a fully healthy rig. Change both repos together, or neither.
#
# The contract is over the TITLES only. "Transcode Probe"'s codecs are deliberately NOT
# H264/AAC (that was the bug); its title, extension and sort position are unchanged, so the
# specs above are unaffected.
DIRECTPLAY_DIR="$MEDIA_DIR/Smoke Test Movie (2020)"
DIRECTPLAY_FILE="$DIRECTPLAY_DIR/Smoke Test Movie (2020).mp4"
# Prefix + a MediaFlagDelimiter + language, which is what Tesserafin.Providers.MediaInfo's
# MediaInfoResolver.GetExternalFiles matches on: same directory, filename starting with the
# video's own filename-without-extension, remainder parsed by Tesserafin.Naming's ExternalPathParser.
SUBTITLE_FILE="$DIRECTPLAY_DIR/Smoke Test Movie (2020).en.srt"
TRANSCODE_DIR="$MEDIA_DIR/Transcode Probe (2021)"
TRANSCODE_FILE="$TRANSCODE_DIR/Transcode Probe (2021).mp4"
REMUX_DIR="$PROBE_DIR/Remux Probe (2022)"
REMUX_FILE="$REMUX_DIR/Remux Probe (2022).mkv"
FILM_DIR="$FILM_ROOT/Container Film (2023)"
FILM_FILE="$FILM_DIR/Container Film (2023).ts"
FILM_SUBTITLE_FILE="$FILM_DIR/Container Film (2023).en.srt"
mkdir -p "$DIRECTPLAY_DIR" "$TRANSCODE_DIR" "$REMUX_DIR" "$FILM_DIR"

banner "Synthesizing media fixtures with ffmpeg (each one ffprobe-verified)"

# --- 1. DIRECT PLAY: H264 + AAC in MP4 -------------------------------------
log "fixture: $DIRECTPLAY_FILE (direct play — H264 + AAC in MP4)"
ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i "testsrc=size=320x240:rate=15:duration=2" \
    -f lavfi -i "sine=frequency=1000:duration=2" \
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
    -c:a aac -movflags +faststart \
    "$DIRECTPLAY_FILE"
assert_container "$DIRECTPLAY_FILE" mp4
assert_codec     "$DIRECTPLAY_FILE" v:0 h264
assert_codec     "$DIRECTPLAY_FILE" a:0 aac
assert_under_1mib "$DIRECTPLAY_FILE"

# --- 2. EXTERNAL SUBTITLE: SubRip sidecar next to fixture 1 ----------------
log "fixture: $SUBTITLE_FILE (external subtitle — SubRip sidecar)"
# Real cues inside the movie's 2s runtime. An empty or cue-less .srt probes as zero
# streams and would attach nothing, so the assertion below is the point of the file.
cat > "$SUBTITLE_FILE" <<'SRT'
1
00:00:00,200 --> 00:00:01,000
Tesserafin E2E external subtitle, cue one.

2
00:00:01,000 --> 00:00:01,900
Tesserafin E2E external subtitle, cue two.
SRT
assert_container "$SUBTITLE_FILE" srt
assert_codec     "$SUBTITLE_FILE" s:0 subrip
assert_under_1mib "$SUBTITLE_FILE"

# --- 3. TRANSCODE: MPEG-4 Part 2 + AC-3 in MP4 -----------------------------
log "fixture: $TRANSCODE_FILE (transcode — MPEG-4 Part 2 + AC-3 in MP4)"
# Do NOT "fix" this back to libx264/aac: identical codecs are precisely what made this
# fixture a transcode scenario in name only. Both streams must stay undecodable by Chromium.
ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i "testsrc=size=320x240:rate=15:duration=2" \
    -f lavfi -i "sine=frequency=1000:duration=2" \
    -c:v mpeg4 -pix_fmt yuv420p \
    -c:a ac3 -b:a 96k -movflags +faststart \
    "$TRANSCODE_FILE"
assert_container "$TRANSCODE_FILE" mp4
assert_codec     "$TRANSCODE_FILE" v:0 mpeg4
assert_codec     "$TRANSCODE_FILE" a:0 ac3
assert_under_1mib "$TRANSCODE_FILE"

# --- 4. REMUX: the SAME H264 + AAC streams, rewrapped as Matroska ----------
log "fixture: $REMUX_FILE (remux — H264 + AAC in Matroska)"
# `-c copy` from fixture 1: no re-encode, so the elementary streams are provably identical
# and only the container differs. That is the whole definition of a remux scenario.
ffmpeg -hide_banner -loglevel error -y -i "$DIRECTPLAY_FILE" -c copy -f matroska "$REMUX_FILE"
assert_container "$REMUX_FILE" matroska
assert_codec     "$REMUX_FILE" v:0 h264
assert_codec     "$REMUX_FILE" a:0 aac
assert_under_1mib "$REMUX_FILE"
# format_name is "matroska,webm" for both Matroska and WebM — the family name alone cannot
# tell them apart. H264 and AAC are not legal WebM codecs, so the codec assertions above
# already prove this is Matroska; the fingerprint equality proves it is a remux of fixture 1.
DIRECTPLAY_FP="$(probe_fingerprint "$DIRECTPLAY_FILE")"
REMUX_FP="$(probe_fingerprint "$REMUX_FILE")"
[ -n "$DIRECTPLAY_FP" ] || fixture_fail "$DIRECTPLAY_FILE: ffprobe returned no streams"
[ "$DIRECTPLAY_FP" = "$REMUX_FP" ] || fixture_fail \
    "remux fixture is not a remux — stream fingerprints differ.
    $DIRECTPLAY_FILE: $DIRECTPLAY_FP
    $REMUX_FILE: $REMUX_FP"
log "    remux ok: stream fingerprints identical to the direct-play MP4 ($(printf '%s' "$DIRECTPLAY_FP" | tr '\n' ' '))"

# --- 5. REMUX, AS A MOVIE: H264 + AAC in MPEG-TS, 30 s, a keyframe every second -----------
log "fixture: $FILM_FILE (remux film — H264 + AAC in MPEG-TS, 30 s)"
# -g/-keyint_min 15 at 15 fps: one keyframe per second, so a stream copy can be cut into
# several HLS segments and a seek lands inside the film rather than at its only keyframe.
ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i "testsrc=size=320x240:rate=15:duration=30" \
    -f lavfi -i "sine=frequency=1000:duration=30" \
    -c:v libx264 -preset ultrafast -crf 36 -pix_fmt yuv420p \
    -g 15 -keyint_min 15 -sc_threshold 0 \
    -c:a aac -b:a 64k -f mpegts \
    "$FILM_FILE"
assert_container "$FILM_FILE" mpegts
assert_codec     "$FILM_FILE" v:0 h264
assert_codec     "$FILM_FILE" a:0 aac
assert_under_1mib "$FILM_FILE"
log "fixture: $FILM_SUBTITLE_FILE (external subtitle — SubRip sidecar of the remux film)"
cat > "$FILM_SUBTITLE_FILE" <<'SRT'
1
00:00:00,500 --> 00:00:10,000
Tesserafin E2E remux film subtitle, cue one.

2
00:00:10,000 --> 00:00:29,000
Tesserafin E2E remux film subtitle, cue two.
SRT
assert_container "$FILM_SUBTITLE_FILE" srt
assert_codec     "$FILM_SUBTITLE_FILE" s:0 subrip

# ---------------------------------------------------------------------------
# network.xml — the ONLY way to set the listen port (see this script's header).
# Unspecified elements fall back to NetworkConfiguration's own defaults.
# ---------------------------------------------------------------------------
cat > "$CONFIG_DIR/network.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <InternalHttpPort>${PORT}</InternalHttpPort>
  <PublicHttpPort>${PORT}</PublicHttpPort>
  <EnableHttps>false</EnableHttps>
  <RequireHttps>false</RequireHttps>
  <EnableIPv4>true</EnableIPv4>
  <EnableIPv6>false</EnableIPv6>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
  <EnableRemoteAccess>true</EnableRemoteAccess>
</NetworkConfiguration>
XML

# ---------------------------------------------------------------------------
# encoding.xml — pin SOFTWARE transcoding. This is a determinism requirement,
# not a preference.
#
# Without this file the server AUTO-DETECTS hardware acceleration at startup and
# persists the result: on a host with /dev/dri it writes
# <HardwareAccelerationType>vaapi</HardwareAccelerationType> and every transcode
# then runs through h264_vaapi. The transcode arguments Tesserafin builds include
# `-rc_mode VBR -b:v 0 -maxrate 0 -bufsize 0`; libx264 accepts a zero bitrate,
# but VAAPI rejects it outright:
#
#   [h264_vaapi] Bitrate must be set for VBR RC mode.
#   [vost#0:0/h264_vaapi] Error while opening encoder
#   Conversion failed!
#
# So the SAME fixture transcodes on a GPU-less machine and fails on a GPU-bearing
# one. ci/run.sh never saw it because its container has no /dev/dri. This harness
# is supposed to be reproducible; leaving the encoder to whatever hardware the
# host happens to expose defeats that.
#
# Pinning `none` makes the rig match the gate and keeps the E2E scope where it
# belongs: the URL/descriptor contract, not the encoder. The underlying VAAPI
# zero-bitrate defect is a real server bug, tracked separately — it is NOT fixed
# or hidden by this line, only kept out of a contract test that cannot diagnose it.
cat > "$CONFIG_DIR/encoding.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<EncodingOptions xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <HardwareAccelerationType>none</HardwareAccelerationType>
  <EnableHardwareEncoding>false</EnableHardwareEncoding>
  <EnableTonemapping>false</EnableTonemapping>
  <EnableThrottling>false</EnableThrottling>
</EncodingOptions>
XML

# ---------------------------------------------------------------------------
# Boot
# ---------------------------------------------------------------------------
banner "Starting Tesserafin.Server on ${BASE_URL}"
log "data directory: $DATADIR"
log "web bundle:     $WEBDIR_ABS"
log "server log:     $LOG_FILE"

dotnet "$SERVER_DLL" \
    --datadir "$DATADIR" \
    --configdir "$CONFIG_DIR" \
    --cachedir "$DATADIR/cache" \
    --logdir "$DATADIR/logs" \
    --webdir "$WEBDIR_ABS" \
    --nonetchange \
    >"$LOG_FILE" 2>&1 &
SERVER_PID=$!

# ---------------------------------------------------------------------------
# Readiness — polling, never a fixed sleep. Also fails fast if the process dies.
# ---------------------------------------------------------------------------
# CAREFUL — /System/Info/Public is NOT a valid readiness probe. Tesserafin.Server binds the
# configured port TWICE in sequence: first with ServerSetupApp/SetupServer.cs (a placeholder
# host shown while the real app boots), then with the real application. SetupServer explicitly
# maps /System/Info/Public and answers it 200 with StartupWizardCompleted=false, while 503-ing
# every OTHER route. So a probe on /System/Info/Public goes green during startup and every
# subsequent seeding call then fails with 503.
#
# /Startup/User is the discriminator: SetupServer 503s it (its catch-all), the real app answers
# it 200. Polling it until 200 is therefore a true "the real app is serving" signal — and it is
# the first seeding step anyway.
banner "Waiting for the real application to serve (not the startup placeholder)"
ready=0
for _ in $(seq 1 "$READY_TIMEOUT"); do
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        echo "ERROR: server process exited before becoming ready. Last 40 log lines:" >&2
        tail -40 "$LOG_FILE" >&2 || true
        exit 1
    fi
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 \
        -H "Authorization: ${AUTH_CLIENT}" "${BASE_URL}/Startup/User" 2>/dev/null || echo 000)"
    if [ "$code" = "200" ]; then
        ready=1
        break
    fi
    sleep 1
done
if [ "$ready" -ne 1 ]; then
    echo "ERROR: the real application did not start serving within ${READY_TIMEOUT}s (last /Startup/User status: ${code:-none})." >&2
    echo "       A persistent 503 means the startup placeholder never handed over." >&2
    tail -40 "$LOG_FILE" >&2 || true
    exit 1
fi
log "real application is serving on ${BASE_URL}"

# ---------------------------------------------------------------------------
# Seeding, entirely through the real public API.
# ---------------------------------------------------------------------------
banner "Seeding admin user '${E2E_USER}' and the two fixture libraries"

# Tesserafin authenticates via the Authorization header, with the access token folded into
# it as a Token="..." field — the exact shape reefin-web's specs send. A bare
# `X-Emby-Token` alongside a tokenless Authorization header is rejected with 401, so
# once TOKEN is known every call must carry it *inside* Authorization.
api() {
    # api METHOD PATH [curl args...]
    local method="$1" path="$2"; shift 2
    local auth="$AUTH_CLIENT"
    if [ -n "$TOKEN" ]; then
        auth="${AUTH_CLIENT}, Token=\"${TOKEN}\""
    fi
    curl -fsS --max-time 30 -X "$method" "${BASE_URL}${path}" \
        -H "Authorization: ${auth}" "$@"
}

# GET /Startup/User initializes the user manager and creates the default user.
api GET /Startup/User >/dev/null
log "default user initialized"

# POST /Startup/User renames it and sets the password.
api POST /Startup/User \
    -H 'Content-Type: application/json' \
    -d "{\"Name\":$(printf '%s' "$E2E_USER" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))'),\"Password\":$(printf '%s' "$E2E_PASSWORD" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')}" \
    >/dev/null
log "admin user configured: ${E2E_USER}"

# Mark the wizard complete so the normal auth path is the one under test.
api POST /Startup/Complete >/dev/null
log "startup wizard completed"

# Authenticate — this both proves the credentials the specs use actually work and
# yields the token needed to create the library.
AUTH_JSON="$(api POST /Users/AuthenticateByName \
    -H 'Content-Type: application/json' \
    -d "{\"Username\":$(printf '%s' "$E2E_USER" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))'),\"Pw\":$(printf '%s' "$E2E_PASSWORD" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')}")"
TOKEN="$(printf '%s' "$AUTH_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin)["AccessToken"])')"
# From here on api() automatically authenticates with this token.
USER_ID="$(printf '%s' "$AUTH_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin)["User"]["Id"])')"
[ -n "$TOKEN" ] || { echo "ERROR: authentication returned no access token" >&2; exit 1; }
log "authenticated — userId=${USER_ID}"

urlenc() { printf '%s' "$1" | python3 -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.stdin.read(), safe=""))'; }

add_library() {
    # add_library DISPLAY_NAME COLLECTION_TYPE MEDIA_PATH REFRESH(true|false)
    #
    # EXACTLY ONE call in this script may pass REFRESH=true, and it must be the last one.
    # refreshLibrary=true makes LibraryManager.AddVirtualFolder call StartScanInBackground(),
    # which is a fire-and-forget Task.Run(ValidateMediaLibrary) over the WHOLE library root —
    # not just the folder being added. Two such calls race: the second scan cancels the first
    # mid-flight (visible in the server log as "Attempting to cancel Scheduled Task Scan Media
    # Library" followed by an OperationCanceledException out of
    # ItemPersistenceService.UpdateOrInsertItems), and the cancelled scan can leave a library
    # with no items at all. That is a genuine, intermittent seeding failure — the rig then
    # burns the full --timeout and dies with correct fixtures that were simply never indexed.
    # One scan, fired after every folder is registered, covers every root.
    api POST "/Library/VirtualFolders?name=$(urlenc "$1")&collectionType=$2&paths=$(urlenc "$3")&refreshLibrary=$4" \
        -H 'Content-Type: application/json' \
        -d '{"LibraryOptions":{"EnableRealtimeMonitor":false,"EnableChapterImageExtraction":false,"ExtractChapterImagesDuringLibraryScan":false}}' \
        >/dev/null
    log "library '$1' (collectionType=$2, refreshLibrary=$4) created over $3"
}

# The specs specifically look for a view with CollectionType == "movies", so the
# library MUST be created as a movies collection — and must remain the ONLY one, because
# library.spec.ts and theme-glass.spec.ts both resolve it with a first-match `.find()`.
add_library "Movies" movies "$MEDIA_DIR" false
# The remux FILM: a mixed-content library (no collection type), where it resolves as a Movie
# without becoming a third card of the movies grid or a second movies-typed library.
add_library "Film Probes" "" "$FILM_ROOT" false
# The remux fixture lives here rather than in Movies so the movies grid keeps exactly two
# cards. homevideos still resolves to Video items, so ffprobe metadata and external-subtitle
# resolution work identically — only the card count of the contract library is protected.
# Last folder registered, so this is the one that triggers the single library scan.
add_library "Codec Probes" homevideos "$PROBE_DIR" true

# ---------------------------------------------------------------------------
# Wait for the library scan — asynchronous, so "server up" is NOT "view present".
# ---------------------------------------------------------------------------
banner "Waiting for the three library views to materialize in /UserViews"
views_ready=0
for _ in $(seq 1 "$READY_TIMEOUT"); do
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        echo "ERROR: server exited while waiting for the library scan. Last 40 log lines:" >&2
        tail -40 "$LOG_FILE" >&2 || true
        exit 1
    fi
    VIEWS="$(curl -fsS --max-time 10 "${BASE_URL}/UserViews?userId=${USER_ID}" \
        -H "Authorization: ${AUTH_CLIENT}, Token=\"${TOKEN}\"" 2>/dev/null || true)"
    if [ -n "$VIEWS" ] && printf '%s' "$VIEWS" | python3 -c '
import json,sys
try:
    items = json.load(sys.stdin).get("Items", [])
except Exception:
    sys.exit(1)
types = [i.get("CollectionType") for i in items]
names = [i.get("Name") for i in items]
sys.exit(0 if types.count("movies") == 1 and "homevideos" in types and "Film Probes" in names else 1)
' 2>/dev/null; then
        views_ready=1
        break
    fi
    sleep 1
done
if [ "$views_ready" -ne 1 ]; then
    echo "ERROR: /UserViews did not settle on exactly one movies view plus a homevideos view and the Film Probes view within ${READY_TIMEOUT}s." >&2
    echo "Last /UserViews response: ${VIEWS:-<none>}" >&2
    tail -40 "$LOG_FILE" >&2 || true
    exit 1
fi
log "the three library views are visible in /UserViews"

MOVIES_VIEW_ID="$(printf '%s' "$VIEWS" | python3 -c 'import json,sys; print(next(i["Id"] for i in json.load(sys.stdin)["Items"] if i.get("CollectionType") == "movies"))')"
PROBES_VIEW_ID="$(printf '%s' "$VIEWS" | python3 -c 'import json,sys; print(next(i["Id"] for i in json.load(sys.stdin)["Items"] if i.get("CollectionType") == "homevideos"))')"
FILMS_VIEW_ID="$(printf '%s' "$VIEWS" | python3 -c 'import json,sys; print(next(i["Id"] for i in json.load(sys.stdin)["Items"] if i.get("Name") == "Film Probes"))')"

# ---------------------------------------------------------------------------
# Fixture materialization — ffprobe proved the bytes on disk; this proves the SERVER
# agreed. Without it a fixture that failed to resolve, or resolved with the wrong
# streams, would leave the rig looking perfectly healthy while the scenario it is
# supposed to exercise silently does not exist.
#
# The item count assertion on the movies library is the machine-checked half of the
# cross-repo contract documented in this script's header: reefin-web's library.spec.ts
# hard-codes `toHaveCount(2)`. If you add a movie fixture, this fails here — loudly,
# in this repo — instead of failing there as a mystery UI bug.
# ---------------------------------------------------------------------------
banner "Verifying every fixture indexed with the codecs it was built with"
fetch_items() {
    # fetch_items PARENT_ID — echoes the /Items body, or nothing on any transport/HTTP error.
    curl -fsS --max-time 15 \
        "${BASE_URL}/Items?userId=${USER_ID}&parentId=$1&recursive=true&fields=MediaStreams,MediaSources,Path" \
        -H "Authorization: ${AUTH_CLIENT}, Token=\"${TOKEN}\"" 2>/dev/null || true
}
items_verified=0
# The last iteration's real reasons, so the failure message explains what was actually wrong
# instead of re-parsing a possibly-empty response after the fact.
CHECK_REPORT="<the fixture check never produced a result>"
for _ in $(seq 1 "$READY_TIMEOUT"); do
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        echo "ERROR: server exited while verifying fixtures. Last 40 log lines:" >&2
        tail -40 "$LOG_FILE" >&2 || true
        exit 1
    fi
    ITEMS_MOVIES="$(fetch_items "$MOVIES_VIEW_ID")"
    ITEMS_PROBES="$(fetch_items "$PROBES_VIEW_ID")"
    ITEMS_FILMS="$(fetch_items "$FILMS_VIEW_ID")"
    if [ -z "$ITEMS_MOVIES" ] || [ -z "$ITEMS_PROBES" ] || [ -z "$ITEMS_FILMS" ]; then
        CHECK_REPORT="  /Items returned nothing (movies body: ${#ITEMS_MOVIES} bytes, probes body: ${#ITEMS_PROBES} bytes, films body: ${#ITEMS_FILMS} bytes) — curl failed or the server answered non-2xx"
        sleep 1
        continue
    fi
    if CHECK_REPORT="$(TESSERAFIN_ITEMS_MOVIES="$ITEMS_MOVIES" TESSERAFIN_ITEMS_PROBES="$ITEMS_PROBES" TESSERAFIN_ITEMS_FILMS="$ITEMS_FILMS" python3 -c '
import json, os, sys

def load(var):
    try:
        return json.loads(os.environ[var]).get("Items", [])
    except Exception:
        sys.exit(1)

movies = [i for i in load("TESSERAFIN_ITEMS_MOVIES") if i.get("Type") == "Movie"]
probes = [i for i in load("TESSERAFIN_ITEMS_PROBES") if i.get("Type") in ("Video", "Movie")]
films = [i for i in load("TESSERAFIN_ITEMS_FILMS") if i.get("Type") in ("Video", "Movie")]

problems = []

# CROSS-REPO CONTRACT: reefin-web tests/e2e/library.spec.ts asserts toHaveCount(2).
if len(movies) != 2:
    problems.append("movies library holds %d items, expected exactly 2 (names: %s)"
                    % (len(movies), [i.get("Name") for i in movies]))

# Prefix match, not equality: without an external metadata provider the resolver keeps the
# parenthesised year in Name ("Smoke Test Movie (2020)"), and reefin-web matches these titles
# with a substring `getByText` for exactly that reason. Asserting equality here would fail on
# a perfectly correct library.
def find(items, name):
    return next((i for i in items if (i.get("Name") or "").startswith(name)), None)

def codecs(item, stream_type):
    return [ (s.get("Codec") or "").lower()
             for s in item.get("MediaStreams", []) or []
             if s.get("Type") == stream_type ]

def containers(item):
    return [ (m.get("Container") or "").lower() for m in item.get("MediaSources", []) or [] ]

def check(items, name, want_video, want_audio, want_container):
    item = find(items, name)
    if item is None:
        problems.append("item %r did not materialize (present: %s)"
                        % (name, [i.get("Name") for i in items]))
        return None
    v, a, c = codecs(item, "Video"), codecs(item, "Audio"), containers(item)
    if want_video not in v:
        problems.append("%r video codecs %s do not include %r" % (name, v, want_video))
    if want_audio not in a:
        problems.append("%r audio codecs %s do not include %r" % (name, a, want_audio))
    if not any(want_container in c_ for c_ in c):
        problems.append("%r containers %s do not include %r" % (name, c, want_container))
    return item

smoke = check(movies, "Smoke Test Movie", "h264", "aac", "mp4")
check(movies, "Transcode Probe", "mpeg4", "ac3", "mp4")
check(probes, "Remux Probe", "h264", "aac", "mkv")

# The remux film must be a MOVIE: the film playback journey owns nothing else, and a fixture
# that resolved as a plain Video would leave that journey untested behind a green rig.
film = check(films, "Container Film", "h264", "aac", "ts")
if len(films) != 1:
    problems.append("film probes library holds %d items, expected exactly 1 (names: %s)"
                    % (len(films), [i.get("Name") for i in films]))
if film is not None:
    if film.get("Type") != "Movie":
        problems.append("Container Film resolved as %r, expected a Movie" % film.get("Type"))
    if not any(s.get("Type") == "Subtitle" and s.get("IsExternal") and (s.get("Codec") or "").lower() == "subrip"
               for s in film.get("MediaStreams", []) or []):
        problems.append("Container Film has no external subrip subtitle stream")

# The external subtitle must arrive as an IsExternal subtitle stream, not merely as a
# file sitting in the folder.
if smoke is not None:
    ext = [ s for s in smoke.get("MediaStreams", []) or []
            if s.get("Type") == "Subtitle" and s.get("IsExternal") ]
    if not ext:
        problems.append("Smoke Test Movie has no external subtitle stream (streams: %s)"
                        % [(s.get("Type"), s.get("Codec"), s.get("IsExternal"))
                           for s in smoke.get("MediaStreams", []) or []])
    elif not any((s.get("Codec") or "").lower() == "subrip" for s in ext):
        problems.append("Smoke Test Movie external subtitle codecs %s do not include \"subrip\""
                        % [s.get("Codec") for s in ext])

if problems:
    for p in problems:
        print("  not yet satisfied: %s" % p)
    sys.exit(1)

print("  movies library: %s" % sorted(i.get("Name") for i in movies))
print("  probes library: %s" % sorted(i.get("Name") for i in probes))
print("  film probes library: %s" % sorted(i.get("Name") for i in films))
' 2>&1)"; then
        items_verified=1
        break
    fi
    sleep 1
done
if [ "$items_verified" -ne 1 ]; then
    echo "ERROR: fixtures did not materialize as expected within ${READY_TIMEOUT}s." >&2
    echo "Reasons reported by the final attempt:" >&2
    printf '%s\n' "$CHECK_REPORT" >&2
    echo "Everything the server did return:" >&2
    EMPTY_JSON='{}'
    TESSERAFIN_ITEMS_MOVIES="${ITEMS_MOVIES:-$EMPTY_JSON}" TESSERAFIN_ITEMS_PROBES="${ITEMS_PROBES:-$EMPTY_JSON}" python3 -c '
import json, os, sys
for var in ("TESSERAFIN_ITEMS_MOVIES", "TESSERAFIN_ITEMS_PROBES"):
    print("== %s ==" % var, file=sys.stderr)
    try:
        for i in json.loads(os.environ[var]).get("Items", []):
            print("  %s [%s] path=%s" % (i.get("Name"), i.get("Type"), i.get("Path")), file=sys.stderr)
            for s in i.get("MediaStreams", []) or []:
                print("      stream type=%s codec=%s external=%s"
                      % (s.get("Type"), s.get("Codec"), s.get("IsExternal")), file=sys.stderr)
            for m in i.get("MediaSources", []) or []:
                print("      source container=%s" % m.get("Container"), file=sys.stderr)
    except Exception as exc:
        print("  <unparseable: %s>" % exc, file=sys.stderr)
' >&2 || true
    tail -40 "$LOG_FILE" >&2 || true
    exit 1
fi
printf '%s\n' "$CHECK_REPORT"
log "all four fixtures verified against the running server"

# ---------------------------------------------------------------------------
# Restricted user — the reefin-web #15 access-restriction fixture.
#
# A second, NON-admin user whose policy grants access to the Movies library only:
# EnableAllFolders=false, EnabledFolders=[Movies]. The "Codec Probes" library exists on
# the server but is deliberately NOT granted, which is what lets a browser test observe
# the server's REAL semantics for a library that exists-but-is-invisible (reefin-web
# library.spec.ts explicitly disclaims this case today for want of exactly this fixture).
#
# Everything below goes through the real public API, like the rest of this script:
#   POST /Users/New                  -> creates the user (RequiresElevation, so admin token)
#   GET  /Users/{id}                 -> fetches the REAL current policy
#   POST /Users/{id}/Policy          -> posts that policy back, modified only in
#                                       IsAdministrator/EnableAllFolders/EnabledFolders.
#                                       Posting a hand-built partial policy instead would
#                                       silently reset every unmentioned field to its
#                                       serializer default — this mutation keeps the
#                                       server's own defaults for everything else.
#   POST /Users/AuthenticateByName   -> proves the credentials the specs will use
#   GET  /UserViews (restricted)     -> proves the policy actually took: movies visible,
#                                       homevideos ABSENT. This is the fixture's contract.
#
# What this deliberately does NOT assert: the HTTP status of directly addressing the
# hidden library with the restricted token (404 vs empty vs 403). That is the very
# semantics the browser test exists to pin down — the rig OBSERVES and prints it, so a
# server-side change shows up here in the log, but the assertion belongs to the spec.
# ---------------------------------------------------------------------------
banner "Seeding restricted user '${RESTRICTED_USER}' (Movies only, Codec Probes withheld)"

RESTRICTED_JSON="$(api POST /Users/New \
    -H 'Content-Type: application/json' \
    -d "{\"Name\":$(printf '%s' "$RESTRICTED_USER" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))'),\"Password\":$(printf '%s' "$RESTRICTED_PASSWORD" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')}")"
RESTRICTED_ID="$(printf '%s' "$RESTRICTED_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin)["Id"])')"
[ -n "$RESTRICTED_ID" ] || { echo "ERROR: /Users/New returned no user id" >&2; exit 1; }
log "restricted user created — userId=${RESTRICTED_ID}"

# Real current policy, mutated only where the restriction lives.
RESTRICTED_POLICY="$(api GET "/Users/${RESTRICTED_ID}" \
    | TESSERAFIN_MOVIES_VIEW_ID="$MOVIES_VIEW_ID" python3 -c '
import json, os, sys
policy = json.load(sys.stdin)["Policy"]
policy["IsAdministrator"] = False
policy["EnableAllFolders"] = False
policy["EnabledFolders"] = [os.environ["TESSERAFIN_MOVIES_VIEW_ID"]]
print(json.dumps(policy))')"
api POST "/Users/${RESTRICTED_ID}/Policy" \
    -H 'Content-Type: application/json' \
    -d "$RESTRICTED_POLICY" >/dev/null
log "restricted policy applied: EnableAllFolders=false, EnabledFolders=[${MOVIES_VIEW_ID}]"

# The credentials the specs will use must actually authenticate.
RESTRICTED_AUTH="$(api POST /Users/AuthenticateByName \
    -H 'Content-Type: application/json' \
    -d "{\"Username\":$(printf '%s' "$RESTRICTED_USER" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))'),\"Pw\":$(printf '%s' "$RESTRICTED_PASSWORD" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')}")"
RESTRICTED_TOKEN="$(printf '%s' "$RESTRICTED_AUTH" | python3 -c 'import json,sys; print(json.load(sys.stdin)["AccessToken"])')"
[ -n "$RESTRICTED_TOKEN" ] || { echo "ERROR: restricted user authentication returned no access token" >&2; exit 1; }

# The fixture's contract: through the restricted user's own eyes, movies is there and
# homevideos is not. Polled briefly — policy application is synchronous, but the view
# endpoint goes through the same caches as everything else.
restricted_ok=0
for _ in $(seq 1 30); do
    RVIEWS="$(curl -fsS --max-time 10 "${BASE_URL}/UserViews?userId=${RESTRICTED_ID}" \
        -H "Authorization: ${AUTH_CLIENT}, Token=\"${RESTRICTED_TOKEN}\"" 2>/dev/null || true)"
    if [ -n "$RVIEWS" ] && printf '%s' "$RVIEWS" | python3 -c '
import json,sys
items = json.load(sys.stdin).get("Items", [])
types = [i.get("CollectionType") for i in items]
names = [i.get("Name") for i in items]
sys.exit(0 if types.count("movies") == 1 and "homevideos" not in types and "Film Probes" not in names else 1)
' 2>/dev/null; then
        restricted_ok=1
        break
    fi
    sleep 1
done
if [ "$restricted_ok" -ne 1 ]; then
    echo "ERROR: the restricted user still sees the wrong views. Last /UserViews: ${RVIEWS:-<none>}" >&2
    exit 1
fi
log "restricted /UserViews: movies visible, homevideos and Film Probes absent — fixture contract holds"

# OBSERVED, not asserted (see the header note): what direct addressing of the withheld
# library answers for this user, so the semantics the browser spec pins are on record here.
OBSERVED_STATUS="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 \
    "${BASE_URL}/Items?userId=${RESTRICTED_ID}&parentId=${PROBES_VIEW_ID}&recursive=true" \
    -H "Authorization: ${AUTH_CLIENT}, Token=\"${RESTRICTED_TOKEN}\"" || echo 000)"
OBSERVED_BODY_LEN="$(curl -fsS --max-time 10 \
    "${BASE_URL}/Items?userId=${RESTRICTED_ID}&parentId=${PROBES_VIEW_ID}&recursive=true" \
    -H "Authorization: ${AUTH_CLIENT}, Token=\"${RESTRICTED_TOKEN}\"" 2>/dev/null | python3 -c 'import json,sys
try:
    body = json.load(sys.stdin)
    print("Items=%d TotalRecordCount=%s" % (len(body.get("Items", [])), body.get("TotalRecordCount")))
except Exception:
    print("unparseable")' || echo 'no body')"
log "observed: GET /Items on the withheld library as the restricted user -> HTTP ${OBSERVED_STATUS} (${OBSERVED_BODY_LEN})"

# ---------------------------------------------------------------------------
# Ready
# ---------------------------------------------------------------------------
banner "READY"
cat <<EOF
Base URL:  ${BASE_URL}
User:      ${E2E_USER}
Password:  ${E2E_PASSWORD}
Data dir:  ${DATADIR}
Web bundle:${WEBDIR_ABS}
Server log:${LOG_FILE}

Point the reefin-web Playwright specs at it with:

  export TESSERAFIN_E2E_BASE_URL='${BASE_URL}'
  export TESSERAFIN_E2E_USER='${E2E_USER}'
  export TESSERAFIN_E2E_PASSWORD='${E2E_PASSWORD}'
  export TESSERAFIN_E2E_RESTRICTED_USER='${RESTRICTED_USER}'
  export TESSERAFIN_E2E_RESTRICTED_PASSWORD='${RESTRICTED_PASSWORD}'
  export TESSERAFIN_E2E_RESTRICTED_LIBRARY_ID='${PROBES_VIEW_ID}'
  cd <reefin-web> && npx playwright test tests/e2e/theme-glass.spec.ts
EOF

export TESSERAFIN_E2E_BASE_URL="$BASE_URL"
export TESSERAFIN_E2E_USER="$E2E_USER"
export TESSERAFIN_E2E_PASSWORD="$E2E_PASSWORD"
export TESSERAFIN_E2E_RESTRICTED_USER="$RESTRICTED_USER"
export TESSERAFIN_E2E_RESTRICTED_PASSWORD="$RESTRICTED_PASSWORD"
# The library the restricted user is NOT granted — what the reefin-web access spec addresses
# directly to pin the server's exists-but-invisible semantics.
export TESSERAFIN_E2E_RESTRICTED_LIBRARY_ID="$PROBES_VIEW_ID"

if [ -n "$EXEC_CMD" ]; then
    banner "Running: ${EXEC_CMD}"
    set +e
    bash -c "$EXEC_CMD"
    EXEC_STATUS=$?
    set -e
    banner "Command exited with status ${EXEC_STATUS}"
    exit "$EXEC_STATUS"
fi

banner "Server is up — press Ctrl-C to stop and clean up"
# `wait` would swallow the INT trap until the child exits; poll instead so Ctrl-C
# is handled promptly and cleanup always runs.
while kill -0 "$SERVER_PID" 2>/dev/null; do
    sleep 1
done
echo "ERROR: server process exited unexpectedly. Last 40 log lines:" >&2
tail -40 "$LOG_FILE" >&2 || true
exit 1
