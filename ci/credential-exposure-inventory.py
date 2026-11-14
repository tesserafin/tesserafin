#!/usr/bin/env python3
"""Authoritative inventory of every place a credential can reach a URL (#153-A0, phase 0).

This is a GATE, not a report. Every category below names something the credential-transport
design has to account for. A category that resolves to zero hits is a FAILURE, because the
failure mode that matters here is a pattern that silently stops matching while the exposure
it described is still in the tree — an inventory that quietly loses a category is worse than
no inventory, since the design is then built against a surface nobody re-derived.

Every route is classified into exactly one of:

  general-api               ordinary API surface; must NEVER accept a playback capability
  playback-media            primary media bytes (direct stream, universal, HLS playlists/segments)
  playback-auxiliary-media  media-adjacent bytes fetched by the player (subtitles, fonts,
                            attachments, trickplay tiles)
  websocket-upgrade         the WebSocket upgrade handshake
  out-of-scope              matched a lexical pattern but is not a credential-in-URL surface

Run from a server checkout root. Emits JSON on stdout; a human summary on stderr.
Exit 0 = every category populated, 1 = a category is empty (or a required file is missing).
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent


def tracked_files() -> list[str]:
    out = subprocess.run(
        ["git", "ls-files"], cwd=REPO, capture_output=True, text=True, check=True
    ).stdout
    return out.splitlines()


FILES = tracked_files()


def read(path: str) -> str:
    p = REPO / path
    return p.read_text(encoding="utf-8", errors="replace") if p.is_file() else ""


def grep(path: str, pattern: str) -> list[dict]:
    """Every line of `path` matching `pattern`, with its 1-based line number."""
    rx = re.compile(pattern)
    return [
        {"file": path, "line": n, "text": line.strip()}
        for n, line in enumerate(read(path).splitlines(), 1)
        if rx.search(line)
    ]


def grep_tree(pattern: str, only: str = r".*") -> list[dict]:
    rx, orx = re.compile(pattern), re.compile(only)
    hits = []
    for f in FILES:
        if not orx.search(f):
            continue
        for n, line in enumerate(read(f).splitlines(), 1):
            if rx.search(line):
                hits.append({"file": f, "line": n, "text": line.strip()})
    return hits


def routes(controller: str) -> list[dict]:
    """(verb, template) for every Http* attribute in a controller."""
    path = f"Tesserafin.Api/Controllers/{controller}.cs"
    rx = re.compile(r'\[Http(Get|Head|Post|Put|Delete)\("([^"]*)"')
    return [
        {"file": path, "line": n, "verb": m.group(1).upper(), "template": m.group(2)}
        for n, line in enumerate(read(path).splitlines(), 1)
        if (m := rx.search(line))
    ]


AUTH_CONTEXT = "Tesserafin.Server.Implementations/Security/AuthorizationContext.cs"
WS_MANAGER = "Tesserafin.Api/WebSockets/WebSocketManager.cs"
SESSION_MANAGER = "Tesserafin.Server.Core/Session/SessionManager.cs"

server: dict[str, dict] = {}

# --- 1. the query-string credential itself -----------------------------------------------
# The set of query keys the general authorization path is allowed to read. This is a GATE, not a
# description. The whole #153-A0 boundary rests on AuthorizationContext never learning a third key:
# if it ever reads `playbackCapability`, a capability becomes a general-API credential and every
# scope, item and expiry binding downstream turns into decoration. No behavioural test can catch
# that on its own — a capability presented to /Items is refused today because it is not a device
# token, so it would go on being refused for the wrong reason while the boundary was gone.
ALLOWED_AUTHORIZATION_QUERY_KEYS = {"ApiKey", "api_key"}

_observed_keys = set(re.findall(r'queryString\["([^"]+)"\]', read(AUTH_CONTEXT)))
_unexpected_keys = sorted(_observed_keys - ALLOWED_AUTHORIZATION_QUERY_KEYS)

server["query-credential-acceptance"] = {
    "classification": "general-api",
    "why": (
        "AuthorizationContext reads the durable session token out of the query string for "
        "EVERY endpoint, not only media. This is the privilege defect: the same value that "
        "plays a file also drives the general API."
    ),
    "hits": grep(AUTH_CONTEXT, r'queryString\["[^"]+"\]'),
    "allowedKeys": sorted(ALLOWED_AUTHORIZATION_QUERY_KEYS),
    "observedKeys": sorted(_observed_keys),
    "unexpectedKeys": _unexpected_keys,
}

# --- 2/3. direct stream + universal ------------------------------------------------------
server["direct-stream-and-universal"] = {
    "classification": "playback-media",
    "hits": routes("VideosController") + routes("AudioController") + routes("UniversalAudioController"),
}

# --- 4. transcoding / PlaybackInfo URL generation ----------------------------------------
server["playbackinfo-url-generation"] = {
    "classification": "playback-media",
    "why": "The server itself puts the credential into the TranscodingUrl it returns.",
    "hits": grep_tree(
        r"TranscodingUrl|api_key=|ApiKey=", only=r"Tesserafin\.Api/Helpers/MediaInfoHelper\.cs$"
    ),
}

# --- 5. HLS master + child ---------------------------------------------------------------
hls = routes("DynamicHlsController") + routes("HlsSegmentController")
server["hls-playlists-and-segments"] = {
    "classification": "playback-media",
    "why": (
        "Both HLS controllers are [ApiExplorerSettings(IgnoreApi = true)], so these routes are "
        "REAL but absent from the canonical OpenAPI. The capability has to reach them without "
        "any contract change describing them."
    ),
    "openapi_visible": False,
    "hits": hls,
}

# --- 6. range / seek ---------------------------------------------------------------------
server["range-and-seek"] = {
    "classification": "playback-media",
    "why": "Range requests reuse the same URL, so they reuse whatever credential it carries.",
    "hits": grep_tree(
        r"EnableRangeProcessing|AcceptRanges|RangeHeader",
        only=r"Tesserafin\.Api/Helpers/FileStreamResponseHelpers\.cs$|Tesserafin\.Api/Controllers/(Videos|Audio)Controller\.cs$",
    ),
}

# --- 7/8/9/10. auxiliary media -----------------------------------------------------------
server["subtitles"] = {
    "classification": "playback-auxiliary-media",
    "hits": [r for r in routes("SubtitleController") if "Subtitle" in r["template"]],
}
server["fallback-fonts"] = {
    "classification": "playback-auxiliary-media",
    "hits": [r for r in routes("SubtitleController") if "FallbackFont" in r["template"]],
}
server["attachments"] = {
    "classification": "playback-auxiliary-media",
    "hits": routes("VideoAttachmentsController"),
}
server["trickplay"] = {
    "classification": "playback-auxiliary-media",
    "why": "The trickplay tile URL is written into a DOM style attribute by the web client.",
    "hits": routes("TrickplayController"),
}

# --- 11. websocket upgrade ---------------------------------------------------------------
server["websocket-upgrade"] = {
    "classification": "websocket-upgrade",
    "why": (
        "The upgrade authenticates through the same AuthorizationContext, so it accepts the "
        "durable token from the query string like any other request."
    ),
    "hits": grep(WS_MANAGER, r"_authService\.Authenticate|IsAuthenticated|SecurityException"),
}

# --- 12. invalidation seams --------------------------------------------------------------
server["session-invalidation-seams"] = {
    "classification": "general-api",
    "why": "Where a capability/ticket must be revoked from. These already exist; do not invent new ones.",
    "hits": grep(
        SESSION_MANAGER,
        r"public async Task Logout\(|public async Task RevokeUserTokens\(|public async ValueTask ReportSessionEnded\(|private async ValueTask OnSessionEnded\(|public event EventHandler<SessionEventArgs> SessionEnded",
    ),
}
server["playsession-termination"] = {
    "classification": "general-api",
    "hits": grep_tree(
        r"public async Task OnPlaybackStopped\(|ClosePlaybackSession|DELETE|HttpDelete",
        only=r"Tesserafin\.Api/Controllers/PlaybackSessionsController\.cs$",
    )
    + grep(SESSION_MANAGER, r"public async Task OnPlaybackStopped\("),
}

# --- 13. credential-bearing output paths -------------------------------------------------
server["credential-emitting-output-paths"] = {
    "classification": "out-of-scope",
    "why": (
        "Log/metric/exception paths that receive a whole request URL or a token. Each has to stay "
        "credential-free once capabilities exist, since short-lived is not non-sensitive."
    ),
    "hits": grep_tree(
        r"LogInformation\(.*\{Url\}|LogDebug\(.*\{Url\}|LogError\(.*\{Url\}|RequestUri|GetDisplayUrl\(",
        only=r"Tesserafin\.(Api|Server\.Core|Server\.Implementations)/.*\.cs$",
    ),
}

WEB = Path("/home/alex/Repos/tesserafin-web")


def web_read(rel: str) -> str:
    p = WEB / rel
    return p.read_text(encoding="utf-8", errors="replace") if p.is_file() else ""


def web_files() -> list[str]:
    out = subprocess.run(
        ["git", "ls-tree", "-r", "--name-only", "origin/main"],
        cwd=WEB, capture_output=True, text=True, check=True,
    ).stdout
    return out.splitlines()


def web_grep(pattern: str, only: str = r".*") -> list[dict]:
    rx, orx = re.compile(pattern), re.compile(only)
    hits = []
    for f in web_files():
        if not orx.search(f):
            continue
        blob = subprocess.run(
            ["git", "cat-file", "blob", f"origin/main:{f}"],
            cwd=WEB, capture_output=True, text=True,
        ).stdout
        for n, line in enumerate(blob.splitlines(), 1):
            if rx.search(line):
                hits.append({"file": f, "line": n, "text": line.strip()[:200]})
    return hits


web: dict[str, dict] = {}
web["apikey-url-construction"] = {
    "classification": "playback-media",
    "why": "Every place the web client puts the durable token into a URL it builds itself.",
    "hits": web_grep(r"ApiKey=|api_key=|['\"]ApiKey['\"]\s*:", only=r"^src/.*\.(js|jsx|ts|tsx)$"),
}
web["playbackmanager-urls"] = {
    "classification": "playback-media",
    "hits": web_grep(r"/stream|universal|directPlayUrl|\.Url\b", only=r"^src/components/playback/playbackmanager\.js$"),
}
web["server-returned-transcoding-urls"] = {
    "classification": "playback-media",
    "hits": web_grep(r"TranscodingUrl", only=r"^src/.*\.(js|jsx|ts|tsx)$"),
}
web["hls-child-requests"] = {
    "classification": "playback-media",
    "hits": web_grep(r"hls\.js|Hls\b|nativeHls|enableHlsJs", only=r"^src/.*\.(js|jsx|ts|tsx)$"),
}
web["auxiliary-media-builders"] = {
    "classification": "playback-auxiliary-media",
    "hits": web_grep(
        r"Subtitles/|FallbackFont|Attachments/|Trickplay",
        only=r"^src/.*\.(js|jsx|ts|tsx)$",
    ),
}
web["dom-style-url-placement"] = {
    "classification": "playback-auxiliary-media",
    "why": "A trickplay URL written into a style attribute leaks through the DOM, not only the network.",
    "hits": web_grep(r"backgroundImage|style\.background|setAttribute\(['\"]style", only=r"^src/.*\.(js|jsx|ts|tsx)$"),
}
web["websocket-open"] = {
    "classification": "websocket-upgrade",
    "why": "jellyfin-apiclient builds the socket URL with api_key; it is a prebuilt dependency bundle.",
    "hits": web_grep(r"openWebSocket|WebSocket\(", only=r"^src/.*\.(js|jsx|ts|tsx)$")
    + [{"file": "node_modules/jellyfin-apiclient (prebuilt bundle)", "line": 0,
        "text": "openWebSocket appends api_key= to the socket URL; patched only for console sinks, not for URL construction"}],
}
web["retry-and-recovery"] = {
    "classification": "general-api",
    "why": "Renewal/retry paths must not silently fall back to the durable token.",
    "hits": web_grep(r"onError|retry|reconnect|refreshToken|401", only=r"^src/components/playback/.*\.(js|ts)$"),
}

report = {
    "generatedFor": "#153-A0 phase 0",
    "serverCommit": subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=REPO, capture_output=True, text=True, check=True
    ).stdout.strip(),
    "webRef": "origin/main",
    "server": server,
    "web": web,
}

empty = [f"server.{k}" for k, v in server.items() if not v["hits"]]
empty += [f"web.{k}" for k, v in web.items() if not v["hits"]]
report["emptyCategories"] = empty
report["unexpectedAuthorizationQueryKeys"] = _unexpected_keys
report["ok"] = not empty and not _unexpected_keys

print(json.dumps(report, indent=2))

for side in ("server", "web"):
    for k, v in report[side].items():
        print(f"{side:7} {k:34} {v['classification']:26} {len(v['hits']):4} hit(s)", file=sys.stderr)
if _unexpected_keys:
    print(
        "\nFAIL: AuthorizationContext reads query key(s) it must never read: "
        + ", ".join(_unexpected_keys)
        + ". The general authorization path may read only "
        + ", ".join(sorted(ALLOWED_AUTHORIZATION_QUERY_KEYS))
        + " — a third key makes the #153 credential a general-API credential.",
        file=sys.stderr,
    )
    sys.exit(1)

if empty:
    print(f"\nFAIL: {len(empty)} category resolved to zero hits: {', '.join(empty)}", file=sys.stderr)
    sys.exit(1)
print("\nOK: every category is populated.", file=sys.stderr)
