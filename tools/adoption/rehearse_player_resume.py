#!/usr/bin/env python3
"""Disposable Jellyfin -> Tesserafin adoption rehearsal up to real playback resume.

Never point this at a household server. It starts two throwaway containers on
loopback ports and private volumes:

- the source, the official Jellyfin 10.10.7 image, which sees the media under
  /srv/films and /srv/films-archive;
- the target, the Tesserafin image under test with its bundled production Web
  client, which sees the same media under /library/films.

Two accounts already exist on each side. The source holds a distinct resume
position for each account, an ambiguous duplicate and a state that collides
with what the target already holds.

Phase 1 (state): capture, dry run, reviewed plan, apply, identical re-apply,
container restart, rollback, rollback again. All of it happens before any
playback, because the player itself writes progress.
Phase 2 (player): fresh capture and plan, apply, then Playwright signs in with
each account, opens Continue Watching, presses Resume and measures where the
player really starts.

The evidence written to --output holds image identities, statuses, counts and
measured positions only: no token, password, snapshot or media path.
"""
import argparse
import contextlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import HTTPRedirectHandler, Request, build_opener, ProxyHandler
import uuid

import jellyfin_dry_run as dry
import jellyfin_state_apply as app

SOURCE_IMAGE = "jellyfin/jellyfin:10.10.7"
SECOND = 10_000_000  # ticks
RUNTIME_SECONDS = 420  # >= MinResumeDurationSeconds (300) so a stop keeps the position
TOLERANCE_SECONDS = 5.0
HARNESS = Path(__file__).resolve().parent / "player-resume"

# Fixture accounts: (role, source name, target name, password). The target
# accounts are created by the fixture because account creation is out of scope
# for the import itself; the import only ever maps onto existing accounts.
ACCOUNTS = (("admin", "jf-admin", "tf-admin", "adoption-admin-pass"),
            ("member", "jf-member", "tf-member", "adoption-member-pass"))
RESUME = "Resume Movie (2021)"
PREFILLED = "Prefilled Movie (2018)"
TWIN = "Twin Movie (2019)"
# Per-account resume positions, deliberately far apart so a swapped account
# cannot pass inside the tolerance.
RESUME_SECONDS = {"admin": 100, "member": 250}


class KeepRedirect(HTTPRedirectHandler):
    """Surfaces a redirect as its status instead of following it."""
    def redirect_request(self, *_):
        return None


def docker(*arguments, timeout=900):
    return subprocess.check_output(["docker", *arguments], stderr=subprocess.DEVNULL,
                                   text=True, timeout=timeout).strip()


def host_port(name):
    # The host port is ephemeral, and docker restart allocates a new one.
    return json.loads(docker("inspect", name))[0]["NetworkSettings"]["Ports"]["8096/tcp"][0]["HostPort"]


class FixtureApi:
    def __init__(self, port):
        self.url = f"http://127.0.0.1:{port}"
        self.token = ""
        self.opener = build_opener(ProxyHandler({}), dry.NoRedirect())
        self.probe = build_opener(ProxyHandler({}), KeepRedirect())

    def call(self, method, path, body=None):
        # MediaBrowser scheme: Tesserafin refuses the legacy X-Emby-* headers.
        header = 'MediaBrowser Client="adoption-rehearsal", Device="fixture", DeviceId="adoption-fixture", Version="1.0"'
        if self.token:
            header += f', Token="{self.token}"'
        request = Request(self.url + path, method=method,
                          data=json.dumps(body).encode() if body is not None else None,
                          headers={"Authorization": header, "Content-Type": "application/json"})
        with self.opener.open(request, timeout=30) as response:
            raw = response.read(dry.MAX_BYTES + 1)
        return dry.decode(raw) if raw else None

    def status(self, path):
        try:
            with self.probe.open(Request(self.url + path, method="GET"), timeout=10) as response:
                return response.status
        except HTTPError as error:
            return error.code
        except (URLError, OSError):
            return 0

    def wait_ready(self, web=False):
        # /System/Info/Public is answered by the startup server before the
        # application is up; also require a real API answer and, for the
        # browser, the / -> /web/ redirect.
        stable = 0
        for _ in range(180):
            ok = self.status("/System/Info/Public") == 200 and self.status("/Users/Public") not in (0, 503)
            if ok and web:
                ok = self.status("/") == 302 and self.status("/web/") == 200
            stable = stable + 1 if ok else 0
            if stable >= 5:
                return
            time.sleep(2)
        raise ValueError("fixture startup timeout")

    def setup(self, names, libraries, expected_movies):
        (admin_name, admin_pass), (member_name, member_pass) = names
        self.call("GET", "/Startup/User")
        self.call("POST", "/Startup/User", {"Name": admin_name, "Password": admin_pass})
        self.call("POST", "/Startup/Complete")
        auth = self.call("POST", "/Users/AuthenticateByName", {"Username": admin_name, "Pw": admin_pass})
        self.token = auth["AccessToken"]
        member = self.call("POST", "/Users/New", {"Name": member_name, "Password": member_pass})
        for name, path in libraries:
            query = urlencode({"name": name, "collectionType": "movies", "paths": path, "refreshLibrary": "true"})
            self.call("POST", "/Library/VirtualFolders?" + query, {"LibraryOptions": {
                "PathInfos": [{"Path": path}], "EnableRealtimeMonitor": False,
                "EnableInternetProviders": False, "MetadataSavers": [],
                "TypeOptions": [{"Type": kind, "MetadataFetchers": [], "ImageFetchers": []}
                                for kind in ("Movie", "Folder")]}})
        admin = auth["User"]["Id"]
        for _ in range(120):
            movies = self.movies(admin)
            if len(movies) == expected_movies and all(m.get("RunTimeTicks", 0) > 300 * SECOND for m in movies):
                return {"admin": admin, "member": member["Id"]}, movies
            time.sleep(2)
        raise ValueError("fixture media scan timeout")

    def movies(self, user):
        return self.call("GET", "/Items?" + urlencode({
            "userId": user, "recursive": "true", "includeItemTypes": "Movie",
            "fields": "Path,ProviderIds", "enableUserData": "true"}))["Items"]

    def set_state(self, user, item, value):
        self.call("POST", f"/UserItems/{item}/UserData?" + urlencode({"userId": user}), value)


def movie_id(movies, name, folder):
    found = [m["Id"] for m in movies if m["Name"] == name
             and m.get("Path", "").startswith(folder + "/")]
    dry.require(len(found) == 1, "fixture movie not found exactly once")
    return found[0]


def userdata(snapshot, user, item):
    return next(row["UserData"] for row in snapshot["userItems"][user] if row["Id"] == item)


def project(snapshot):
    """The per-user state of every item; what an import may and may not touch."""
    return {user: {row["Id"]: row["UserData"] for row in rows} for user, rows in snapshot["userItems"].items()}


def make_media(root, target_image):
    """Writes the fixture media with the ffmpeg the target image hands its server."""
    cmd = json.loads(docker("image", "inspect", target_image))[0]["Config"].get("Cmd") or []
    if "--ffmpeg" not in cmd[:-1]:
        raise RuntimeError("target image CMD passes no --ffmpeg")
    ffmpeg = cmd[cmd.index("--ffmpeg") + 1]
    films, archive = root / "films", root / "archive"
    for path in (root, films, archive):
        path.mkdir(exist_ok=True)
        path.chmod(0o755)
    font = Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
    mounts = ["-v", f"{root}:/m"]
    video = f"testsrc=duration={RUNTIME_SECONDS}:size=320x240:rate=10"
    if font.is_file():
        # Burn the media timestamp into the picture so a screenshot shows the
        # position the decoder is really at, independent of any client report.
        mounts += ["-v", f"{font}:/font.ttf:ro"]
        video += ",drawtext=fontfile=/font.ttf:fontsize=40:fontcolor=white:box=1:boxcolor=black" \
                 ":x=10:y=10:text='%{pts\\:hms}'"
    docker("run", "--rm", "--user", f"{os.getuid()}:{os.getgid()}", "--entrypoint", ffmpeg, *mounts,
           target_image, "-nostdin", "-y", "-f", "lavfi", "-i", video,
           "-f", "lavfi", "-i", f"sine=frequency=440:duration={RUNTIME_SECONDS}",
           "-c:v", "libx264", "-preset", "veryfast", "-g", "20", "-pix_fmt", "yuv420p",
           "-c:a", "aac", "-b:a", "64k", "-shortest", "-movflags", "+faststart", "/m/fixture.mp4")
    master = root / "fixture.mp4"
    for parent, name in ((films, RESUME), (films, PREFILLED), (films, TWIN), (archive, TWIN)):
        folder = parent / name
        folder.mkdir()
        folder.chmod(0o755)
        shutil.copyfile(master, folder / (name + ".mp4"))
        (folder / (name + ".mp4")).chmod(0o644)
    master.unlink()
    return films, archive


class Rig:
    def __init__(self):
        self.names, self.volumes = [], []

    def start(self, image, media_mounts, target):
        name = "adoption-" + uuid.uuid4().hex[:12]
        self.names.append(name)
        role_volumes = [name + "-" + part for part in ("config", "cache", "data")]
        self.volumes.extend(role_volumes)
        for volume in role_volumes:
            docker("volume", "create", volume)
        mounts = [argument for volume, path in zip(role_volumes, ("/config", "/cache", "/data"))
                  for argument in ("-v", volume + ":" + path)]
        if target:
            docker("run", "--rm", "--user", "0:0", "--entrypoint", "/bin/sh", *mounts,
                   image, "-c", "chown -R 10000:10000 /config /cache /data")
        media = [argument for host, path in media_mounts for argument in ("-v", f"{host}:{path}:ro")]
        docker("run", "-d", "--name", name, "-p", "127.0.0.1::8096", *mounts, *media, image)
        return name

    def remove(self):
        for name in self.names:
            try:
                docker("rm", "-f", name)
            except Exception:
                pass
        for volume in self.volumes:
            try:
                docker("volume", "rm", volume)
            except Exception:
                pass


@contextlib.contextmanager
def _cleanup(path):
    try:
        yield
    finally:
        shutil.rmtree(path, ignore_errors=True)


def record_status(simulation, source_user, source_item):
    rows = [r for r in simulation["records"]
            if r["sourceUserId"] == source_user and r["sourceItemId"] == source_item]
    dry.require(len(rows) == 1, "simulation record missing")
    return rows[0]["status"]


def run_player(base_url, accounts, item_id, private):
    """Runs the Playwright journey and returns its per-account measurements."""
    results = private / "player.json"
    env = dict(os.environ, ADOPTION_BASE_URL=base_url, ADOPTION_RESULTS=str(results),
               ADOPTION_ARTIFACTS=str(private / "player-artifacts"),
               ADOPTION_ACCOUNTS=json.dumps(accounts), ADOPTION_ITEM_ID=item_id,
               ADOPTION_ITEM_NAME=RESUME)
    completed = subprocess.run(["npx", "--no-install", "playwright", "test"], cwd=HARNESS, env=env,
                               timeout=1200, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    # Playwright's own report carries no secret: fixture names and a loopback URL.
    print(completed.stdout[-6000:])
    dry.require(completed.returncode == 0 and results.is_file(), "player journey failed")
    return json.loads(results.read_text(encoding="utf-8"))


def run(args):
    dry.require(re.fullmatch(r"[0-9a-f]{40}", args.expected_source) is not None, "exact source commit required")
    dry.require(re.fullmatch(r"sha256:[0-9a-f]{64}", args.target_image) is not None, "immutable target image required")
    target_meta = json.loads(docker("image", "inspect", args.target_image))[0]
    labels = target_meta["Config"]["Labels"]
    dry.require(labels["org.opencontainers.image.revision"] == args.expected_source, "target source/image mismatch")
    docker("pull", SOURCE_IMAGE)
    source_meta = json.loads(docker("image", "inspect", SOURCE_IMAGE))[0]
    source_digest = next(v for v in source_meta["RepoDigests"] if v.startswith("jellyfin/jellyfin@sha256:"))
    rig = Rig()
    stage = "prepare"
    scenarios = {}
    try:
        # --keep leaves the containers running for inspection, so their media
        # and the private journals must outlive this function too.
        temporary = tempfile.mkdtemp(prefix="adoption-resume-")
        with contextlib.nullcontext() if args.keep else _cleanup(temporary):
            private = Path(temporary)
            private.chmod(0o700)
            stage = "media"
            films, archive = make_media(private / "media", args.target_image)

            stage = "source-setup"
            source_name = rig.start(source_meta["Id"], [(films, "/srv/films"), (archive, "/srv/films-archive")], False)
            source = FixtureApi(host_port(source_name))
            source.wait_ready()
            source_users, source_movies = source.setup(
                [(a[1], a[3]) for a in ACCOUNTS], [("Films", "/srv/films"), ("Archive", "/srv/films-archive")], 4)

            stage = "target-setup"
            target_name = rig.start(args.target_image, [(films, "/library/films")], True)
            target = FixtureApi(host_port(target_name))
            target.wait_ready(web=True)
            target_users, target_movies = target.setup(
                [(a[2], a[3]) for a in ACCOUNTS], [("Movies", "/library/films")], 3)

            s_resume = movie_id(source_movies, RESUME, "/srv/films")
            s_prefilled = movie_id(source_movies, PREFILLED, "/srv/films")
            s_twin = movie_id(source_movies, TWIN, "/srv/films")
            s_twin_archive = movie_id(source_movies, TWIN, "/srv/films-archive")
            t_resume = movie_id(target_movies, RESUME, "/library/films")
            t_prefilled = movie_id(target_movies, PREFILLED, "/library/films")
            t_twin = movie_id(target_movies, TWIN, "/library/films")

            stage = "source-seed"
            seed = {
                ("admin", s_resume): dict(IsFavorite=True, Played=False, PlayCount=0,
                                          PlaybackPositionTicks=RESUME_SECONDS["admin"] * SECOND),
                ("member", s_resume): dict(IsFavorite=False, Played=False, PlayCount=1,
                                           PlaybackPositionTicks=RESUME_SECONDS["member"] * SECOND),
                ("admin", s_prefilled): dict(IsFavorite=True, Played=True, PlayCount=2, PlaybackPositionTicks=0),
                ("member", s_prefilled): dict(IsFavorite=False, Played=False, PlayCount=0,
                                              PlaybackPositionTicks=200 * SECOND),
                ("admin", s_twin): dict(IsFavorite=False, Played=True, PlayCount=1, PlaybackPositionTicks=0),
            }
            for (role, item), value in seed.items():
                source.set_state(source_users[role], item, value)
            stage = "target-seed"
            # State the target already holds: a favourite alone for the admin
            # (agrees with the source, still nonempty) and a different resume
            # position for the member. Neither may be overwritten.
            target.set_state(target_users["admin"], t_prefilled, {"IsFavorite": True})
            target.set_state(target_users["member"], t_prefilled, {"PlaybackPositionTicks": 60 * SECOND})

            stage = "capture"
            capture_source = lambda: dry.capture(dry.Reader(source.url, source.token), "jellyfin")
            capture_target = lambda: dry.capture(dry.Reader(target.url, target.token), "tesserafin")
            source_snapshot = capture_source()
            target_snapshot = capture_target()
            initial = project(target_snapshot)
            mapping = {"users": {source_users[r]: target_users[r] for r, *_ in ACCOUNTS},
                       "paths": [{"source": "/srv/films", "target": "/library/films"},
                                 {"source": "/srv/films-archive", "target": "/library/films"}]}

            stage = "dry-run"
            simulation = dry.plan(source_snapshot, target_snapshot, mapping)
            statuses = {
                "admin-resume": record_status(simulation, source_users["admin"], s_resume),
                "member-resume": record_status(simulation, source_users["member"], s_resume),
                "admin-prefilled": record_status(simulation, source_users["admin"], s_prefilled),
                "member-prefilled": record_status(simulation, source_users["member"], s_prefilled),
                "admin-twin": record_status(simulation, source_users["admin"], s_twin),
                "admin-twin-archive": record_status(simulation, source_users["admin"], s_twin_archive),
            }
            dry.require(statuses == {
                "admin-resume": "state-candidate", "member-resume": "state-candidate",
                "admin-prefilled": "state-candidate", "member-prefilled": "target-state-conflict",
                "admin-twin": "ambiguous-source-path", "admin-twin-archive": "no-source-state"},
                "unexpected dry-run classification")
            plan = app.prepare(source_snapshot, target_snapshot, mapping)
            planned = sorted((op["userId"], op["item"]["Id"]) for op in plan["operations"])
            dry.require(planned == sorted((target_users[r], t_resume) for r in RESUME_SECONDS),
                        "apply plan must hold exactly the two resume operations")
            omitted = plan["simulationCounts"]
            dry.require(omitted.get("nonempty-target-not-supported") == 1
                        and omitted.get("target-state-conflict") == 1
                        and omitted.get("ambiguous-source-path") == 1, "omissions not reported")
            scenarios["dry-run"] = {"statuses": statuses, "operations": len(planned),
                                    "omitted": {k: omitted[k] for k in ("ambiguous-source-path",
                                                "nonempty-target-not-supported", "target-state-conflict")}}

            def expect_applied(snapshot):
                state = project(snapshot)
                for role in RESUME_SECONDS:
                    user = target_users[role]
                    want = seed[(role, s_resume)]
                    dry.require(app.state(state[user][t_resume]) == want, "applied state mismatch")
                    for item in (t_prefilled, t_twin):
                        dry.require(state[user][item] == initial[user][item], "out-of-plan target state changed")

            stage = "apply"
            journal = private / "journal-state"
            approved = app.digest(plan)
            app.execute(app.Api(target.url, target.token), plan, approved, journal, quiescent=True)
            applied = capture_target()
            expect_applied(applied)
            journal_entries = sorted(p.name for p in journal.iterdir())
            stage = "reapply"
            app.execute(app.Api(target.url, target.token), plan, approved, journal, quiescent=True)
            dry.require(project(capture_target()) == project(applied), "re-apply changed target state")
            dry.require(sorted(p.name for p in journal.iterdir()) == journal_entries, "re-apply wrote the journal")
            stage = "restart"
            docker("restart", target_name)
            target.url = FixtureApi(host_port(target_name)).url
            target.wait_ready(web=True)
            dry.require(project(capture_target()) == project(applied), "state lost after restart")
            stage = "rollback"
            for _ in range(2):
                app.execute(app.Api(target.url, target.token), plan, approved, journal,
                            rollback=True, quiescent=True)
                dry.require(project(capture_target()) == initial, "rollback did not restore the initial state")
            dry.require(capture_source() == source_snapshot, "source observation changed")
            scenarios["state"] = {"apply": True, "reapply_no_effect": True, "restart_persisted": True,
                                  "rollback_restored_initial": True, "rollback_repeat_no_effect": True,
                                  "ambiguous_untouched": True, "prefilled_untouched": True,
                                  "source_unchanged": True}

            stage = "player-apply"
            # A fresh, separately reviewed import after the rollback proof; the
            # rolled-back journal refuses to apply again by design.
            target_snapshot = capture_target()
            dry.require(project(target_snapshot) == initial, "target drifted before the player phase")
            plan = app.prepare(source_snapshot, target_snapshot, mapping)
            app.execute(app.Api(target.url, target.token), plan, app.digest(plan),
                        private / "journal-player", quiescent=True)
            expect_applied(capture_target())
            dry.require(capture_source() == source_snapshot, "source observation changed")

            stage = "player"
            accounts = [{"role": role, "user": tname, "password": password, "userId": target_users[role],
                         "expectedSeconds": RESUME_SECONDS[role]} for role, _, tname, password in ACCOUNTS]
            measured = run_player(target.url, accounts, t_resume, private)
            rows = []
            for account in accounts:
                row = next(r for r in measured if r["role"] == account["role"])
                dry.require(row["userId"] == account["userId"] and row["itemId"] == t_resume,
                            "player ran under the wrong account or item")
                start = row["startSeconds"]
                dry.require(abs(start - account["expectedSeconds"]) <= TOLERANCE_SECONDS,
                            "player did not start near the imported position")
                dry.require(row["laterSeconds"] > start + 1, "player did not advance")
                rows.append({key: row[key] for key in ("role", "expectedSeconds", "startSeconds", "laterSeconds",
                                                       "reportedStartSeconds", "playMethod", "resumeLabel")})
            scenarios["player"] = {"toleranceSeconds": TOLERANCE_SECONDS, "accounts": rows}
            if args.artifacts:
                shutil.copytree(private / "player-artifacts", args.artifacts, dirs_exist_ok=True)
            return {"schema": 1, "accepted": True, "candidate_source": args.expected_source,
                    "target_image_id": args.target_image,
                    "web_vcs_ref": labels.get("org.tesserafin.web.revision"),
                    "source_image_id": source_meta["Id"], "source_repo_digest": source_digest,
                    "source_version": dry.VERSIONS["jellyfin"][1], "target_version": dry.VERSIONS["tesserafin"][1],
                    "path_mapping": "distinct (/srv/films + /srv/films-archive -> /library/films)",
                    "scenarios": scenarios,
                    "scope": "existing-account four-field state and real Web player resume; "
                             "no account, password, permission or media transfer"}
    except Exception as error:
        # Do not expose tokens, auth responses, snapshots, media paths or raw API errors.
        status = getattr(error, "code", None)
        detail = f" (HTTP {status})" if type(status) is int else ""
        reason = f": {error}" if isinstance(error, dry.InvalidInput) else ""
        raise RuntimeError("adoption player-resume rehearsal failed at " + stage + detail + reason) from None
    finally:
        if not args.keep:
            rig.remove()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--target-image", required=True)
    parser.add_argument("--expected-source", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, help="copy player screenshots here")
    parser.add_argument("--keep", action="store_true", help="local debugging: leave the containers running")
    args = parser.parse_args()
    try:
        evidence = run(args)
    except Exception as error:
        parser.exit(1, (str(error) if isinstance(error, RuntimeError) else "adoption rehearsal refused") + "\n")
    dry.write_private(args.output, evidence)
    print(json.dumps(evidence, sort_keys=True, indent=1))


if __name__ == "__main__":
    main()
