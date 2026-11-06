#!/usr/bin/env python3
"""Disposable native-server HEVC proof. Uses the supplied runtime, never a host ffmpeg.

Run after building the server:
  hevc_main10.py --server Tesserafin.Server/bin/Release/net10.0 --ffmpeg /path/bin/ffmpeg --out /empty/path
Requires dotnet on PATH. No browser/hardware/HDR-display claim is made.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shlex
import subprocess
import sys
import time
import urllib.parse
import uuid

import pcrlib as api


def run(args):
    p = subprocess.run([str(a) for a in args], capture_output=True, timeout=120)
    if p.returncode:
        raise RuntimeError(p.stderr.decode(errors="replace")[-1500:])
    return p.stdout


def probe(ffprobe, path):
    data = json.loads(run([ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", path]))
    return next(s for s in data["streams"] if s["codec_type"] == "video")


def seconds(timestamp):
    value = 0.0
    for part in timestamp.split(":"):
        value = value * 60 + float(part)
    return value


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--server", type=Path, required=True)
    ap.add_argument("--ffmpeg", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--port", type=int, default=18203)
    a = ap.parse_args()
    root = a.out.resolve()
    root.mkdir(parents=True, exist_ok=False)
    ff = a.ffmpeg.resolve()
    fp = ff.with_name("ffprobe")
    media = root / "media"
    media.mkdir()
    for name, hdr in (("SDR Ten", False), ("HDR Ten", True)):
        d = media / f"{name} (2021)"
        d.mkdir()
        run([ff, "-nostdin", "-v", "error", "-f", "lavfi", "-i",
             "testsrc2=size=320x180:rate=25:duration=90,format=yuv420p10le", "-c:v", "libx265",
             "-preset", "ultrafast", "-x265-params",
             "pools=1:frame-threads=1:log-level=error:" + ("colorprim=9:transfer=16:colormatrix=9" if hdr else "colorprim=1:transfer=1:colormatrix=1"),
             "-pix_fmt", "yuv420p10le", "-color_trc", "smpte2084" if hdr else "bt709",
             "-color_primaries", "bt2020" if hdr else "bt709", "-colorspace", "bt2020nc" if hdr else "bt709",
             d / f"{name} (2021).mkv"])
        source = probe(fp, d / f"{name} (2021).mkv")
        assert source["pix_fmt"] == "yuv420p10le" and source["profile"] == "Main 10", source
        assert source["color_transfer"] == ("smpte2084" if hdr else "bt709"), source
    pgs = root / "rectangle.sup"
    run([sys.executable, Path(__file__).resolve().parents[2] / "ci/media-bench/gen-pgs.py", pgs,
         "--width", 320, "--height", 180, "--box", "160x30+80+130", "--start", 1, "--end", 29])
    subdir = media / "PGS Ten (2021)"
    subdir.mkdir()
    run([ff, "-nostdin", "-v", "error", "-i", media / "SDR Ten (2021)/SDR Ten (2021).mkv",
         "-i", pgs, "-map", "0:v:0", "-map", "1:s:0", "-c", "copy", subdir / "PGS Ten (2021).mkv"])
    state = root / "state"
    for d in ("config", "cache", "data", "log", "bin"):
        (state / d).mkdir(parents=True)
    (state / "config/network.xml").write_text(
        f'<NetworkConfiguration><InternalHttpPort>{a.port}</InternalHttpPort><EnableIPv6>false</EnableIPv6></NetworkConfiguration>')
    wrapper = state / "bin/ffmpeg"
    # Pacing keeps a real job alive long enough for seek/stop. Probes are unchanged.
    wrapper.write_text('#!/bin/bash\ncase " $* " in *" -hls_time "*) exec '
                       + shlex.quote(str(ff)) + ' -readrate 1 "$@";; esac\nexec '
                       + shlex.quote(str(ff)) + ' "$@"\n')
    wrapper.chmod(0o755)
    wrapper.with_name("ffprobe").symlink_to(fp)
    log_path = root / "server.log"
    log = log_path.open("wb")
    proc = subprocess.Popen(["dotnet", str(a.server.resolve() / "tesserafin.dll"), "--nowebclient", "--nonetchange",
                             "--ffmpeg", str(wrapper), "--datadir", str(state / "data"),
                             "--configdir", str(state / "config"), "--cachedir", str(state / "cache"),
                             "--logdir", str(state / "log")], stdout=log, stderr=subprocess.STDOUT)
    results = []
    api.BASE = f"http://127.0.0.1:{a.port}"
    token = None
    try:
        for _ in range(120):
            if proc.poll() is not None:
                raise RuntimeError("server exited during startup; see server.log")
            try:
                if api.http("GET", "/Startup/User")[0] == 200:
                    break
            except OSError:
                pass
            time.sleep(1)
        else:
            raise RuntimeError("server did not reach startup wizard")
        assert api.http("POST", "/Startup/User", {"Name": "rigadmin", "Password": api.PW})[0] in (200, 204)
        assert api.http("POST", "/Startup/Complete")[0] in (200, 204)
        token, uid = api.login("rigadmin", "main10-proof")
        code, options = api.http("GET", "/System/Configuration/encoding", tok=token)
        assert code == 200
        options.update({"HardwareAccelerationType": "none", "EnableHardwareEncoding": False,
                        "AllowHevcEncoding": True, "EncoderPreset": "ultrafast", "EncodingThreadCount": 2})
        assert api.http("POST", "/System/Configuration/encoding", options, tok=token)[0] in (200, 204)
        q = urllib.parse.urlencode({"name": "Main10 proof", "collectionType": "movies", "paths": str(media), "refreshLibrary": "true"})
        assert api.http("POST", "/Library/VirtualFolders?" + q, {"LibraryOptions": {}}, tok=token)[0] in (200, 204)
        items = {}
        for _ in range(90):
            items = api.items(token)
            if len(items) >= 3 and all(api.probed(token, i) for i in items.values()):
                break
            time.sleep(1)
        assert len(items) == 3, "expected all three synthetic fixtures"
        assert "Software HEVC Main10 probe: True" in log_path.read_text(), "startup did not prove Main10"

        def play(name, profile, ceiling, expected, first=0, subtitle=False):
            item = next(v for k, v in items.items() if name in k)
            ps = uuid.uuid4().hex
            query = {"MediaSourceId": item, "VideoCodec": "hevc", "AudioCodec": "aac", "SegmentContainer": "mp4",
                     "AllowVideoStreamCopy": "false", "VideoBitRate": 500000, "MaxWidth": 320, "MaxHeight": 180,
                     "PlaySessionId": ps, "DeviceId": "main10-proof", "SegmentLength": 2, "MinSegments": 1,
                     "hevc-profile": profile, "MaxVideoBitDepth": ceiling}
            if subtitle:
                query.update({"SubtitleStreamIndex": 1, "SubtitleMethod": "Encode"})
            code, master = api.http("GET", f"/Videos/{item}/master.m3u8?" + urllib.parse.urlencode(query), tok=token, dev="main10-proof")
            assert code == 200, f"master returned {code}"
            master = master.decode()
            assert ("hvc1.2." if expected == 10 else "hvc1.1.") in master, master
            main_url = next(l for l in master.splitlines() if l and not l.startswith("#"))
            base = f"/Videos/{item}/"
            code, playlist = api.http("GET", base + main_url, tok=token, dev="main10-proof")
            assert code == 200, f"playlist returned {code}"
            playlist = playlist.decode()
            segments = [l for l in playlist.splitlines() if l and not l.startswith("#")]
            init = re.search(r'#EXT-X-MAP:URI="([^"]+)"', playlist)
            assert init and len(segments) > first + 1
            parts = [init[1], *segments[first:first + 2]]
            body = bytearray()
            main_dir = urllib.parse.urlsplit(base + main_url).path.rsplit("/", 1)[0] + "/"
            for rel in parts:
                path = urllib.parse.urljoin(main_dir, rel)
                code, chunk = api.http("GET", path, tok=token, dev="main10-proof", timeout=60)
                assert code == 200, f"segment returned {code}"
                body += chunk
            output = root / f"{name.replace(' ', '-')}-{profile}-{ceiling}-{first}.mp4"
            output.write_bytes(body)
            v = probe(fp, output)
            assert v["codec_name"] == "hevc"
            assert v["pix_fmt"] == ("yuv420p10le" if expected == 10 else "yuv420p"), v
            assert v["profile"] == ("Main 10" if expected == 10 else "Main"), v
            run([ff, "-nostdin", "-v", "error", "-xerror", "-err_detect", "explode", "-i", output, "-map", "0:v:0", "-f", "null", "-"])
            if name == "HDR Ten":
                assert v.get("color_transfer") == "bt709", v
            white_fraction = None
            if subtitle:
                pixels = run([ff, "-nostdin", "-v", "error", "-ss", 2, "-i", output,
                              "-vf", "crop=156:26:82:132,format=gray", "-frames:v", 1,
                              "-f", "rawvideo", "-"])
                assert len(pixels) == 156 * 26, "missing subtitle sample frame"
                white_fraction = sum(p > 220 for p in pixels) / len(pixels)
                assert white_fraction > 0.95, f"PGS rectangle absent: {white_fraction} white pixels"
            def jobs():
                found = []
                for p in Path("/proc").glob("[0-9]*/cmdline"):
                    try:
                        args = p.read_bytes().decode(errors="replace").split("\0")
                    except OSError:
                        continue
                    if "-hls_time" in args and any(str(state) in arg for arg in args):
                        found.append(int(p.parent.name))
                return found
            before = jobs()
            assert before, "no live paced transcode before stop"
            commands = [Path(f"/proc/{pid}/cmdline").read_bytes().decode().rstrip("\0").split("\0") for pid in before]
            if first:
                assert any("-ss" in c and seconds(c[c.index("-ss") + 1]) >= first * 2
                           and c[c.index("-start_number") + 1] == str(first) for c in commands), commands
            code, _ = api.http("DELETE", f"/Videos/ActiveEncodings?deviceId=main10-proof&playSessionId={ps}", tok=token, dev="main10-proof")
            assert code in (200, 204), code
            for _ in range(30):
                if not jobs():
                    break
                time.sleep(0.1)
            assert not jobs(), "transcode still alive after stop"
            results.append({"source": name, "requested_profile": profile, "ceiling": ceiling, "first_segment": first,
                            "manifest_main10": "hvc1.2." in master, "served_codec": v["codec_name"],
                            "served_profile": v["profile"], "served_pixel_format": v["pix_fmt"],
                            "color_transfer": v.get("color_transfer"), "bytes": len(body), "decoded": True,
                            "subtitle_white_fraction": white_fraction, "ffmpeg_commands": commands,
                            "stop_http": code, "live_jobs_before_stop": len(before), "live_jobs_after_stop": 0})

        play("SDR Ten", "main10", 10, 10)
        play("SDR Ten", "main", 10, 8)
        play("SDR Ten", "main10", 8, 8)
        play("SDR Ten", "main10", 10, 10, first=25)
        play("HDR Ten", "main10", 10, 10)
        play("PGS Ten", "main10", 10, 10, subtitle=True)
        evidence = {"runtime_sha256": hashlib.sha256(ff.read_bytes()).hexdigest(),
                    "server_sha256": hashlib.sha256((a.server.resolve() / "tesserafin.dll").read_bytes()).hexdigest(),
                    "results": results}
        (root / "results.json").write_text(json.dumps(evidence, indent=2) + "\n")
        print(json.dumps(evidence, indent=2))
    finally:
        if token and proc.poll() is None:
            try:
                api.http("DELETE", "/Videos/ActiveEncodings?deviceId=main10-proof", tok=token, dev="main10-proof", timeout=10)
            except OSError:
                pass
        proc.terminate()
        try:
            proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)
        log.close()


if __name__ == "__main__":
    main()
