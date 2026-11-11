#!/usr/bin/env python3
"""Check a GitHub Release's assets against the SHA256SUMS files attached to it.

Read-only. It downloads nothing but the small `SHA256SUMS*.txt` files and compares
three things for every asset:

  * the digest GitHub computed server-side for the uploaded bytes (`digest` field of
    the release asset API, `sha256:<hex>`);
  * the digest the release's own SHA256SUMS files claim for that file name;
  * with --download, the digest of the bytes actually downloaded (slow: the release
    is several hundred MB).

Outcome per asset:

  OK         listed, and every available digest agrees
  MISMATCH   listed, but a digest disagrees            -> exit 1, always
  UNLISTED   attached, but in no SHA256SUMS file        -> exit 1 with --require-complete
  ABSENT     listed in a SHA256SUMS file, not attached  -> reported, exit 1 with --strict-absent
  NODIGEST   GitHub returned no digest (old upload) and --download was not given

SHA256SUMS files themselves are excluded from the coverage requirement.

Usage:
  verify_release_checksums.py --repo tesserafin/tesserafin --tag v1.1.0
  verify_release_checksums.py --release-json release.json --sums-dir ./sums   # offline
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

SUMS_NAME = re.compile(r"^SHA256SUMS[^/]*\.txt$")
SUMS_LINE = re.compile(r"^([0-9a-f]{64}) [ *](\S.*)$")


def parse_sums(text: str, source: str) -> dict[str, str]:
    """Parse `sha256sum` output. A name listed twice with different digests is an error."""
    out: dict[str, str] = {}
    for number, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        match = SUMS_LINE.match(line)
        if not match:
            raise ValueError(f"{source}:{number}: not a sha256sum line: {raw!r}")
        digest, name = match.group(1), match.group(2)
        if name in out and out[name] != digest:
            raise ValueError(f"{source}:{number}: {name} listed twice with different digests")
        out[name] = digest
    return out


def merge_sums(files: dict[str, str]) -> tuple[dict[str, str], list[str]]:
    """Merge several sums files; return (name -> digest, conflict messages)."""
    merged: dict[str, str] = {}
    origin: dict[str, str] = {}
    conflicts: list[str] = []
    for source in sorted(files):
        for name, digest in parse_sums(files[source], source).items():
            if name in merged and merged[name] != digest:
                conflicts.append(f"{name}: {origin[name]} says {merged[name]}, {source} says {digest}")
                continue
            merged[name] = digest
            origin[name] = source
    return merged, conflicts


def classify(assets: list[dict], sums: dict[str, str], local: dict[str, str]) -> list[tuple[str, str, str]]:
    """Return (status, name, detail) rows. `local` maps name -> locally computed sha256."""
    rows: list[tuple[str, str, str]] = []
    attached = {a["name"] for a in assets}
    for asset in sorted(assets, key=lambda a: a["name"]):
        name = asset["name"]
        if SUMS_NAME.match(name):
            continue
        remote = (asset.get("digest") or "").removeprefix("sha256:") or None
        computed = local.get(name)
        claimed = sums.get(name)
        observed = {k: v for k, v in (("github", remote), ("download", computed)) if v}
        if claimed is None:
            rows.append(("UNLISTED", name, ", ".join(f"{k}={v}" for k, v in observed.items()) or "no digest"))
            continue
        if not observed:
            rows.append(("NODIGEST", name, f"listed={claimed}; rerun with --download"))
            continue
        bad = {k: v for k, v in observed.items() if v != claimed}
        if bad:
            rows.append(("MISMATCH", name, f"listed={claimed} " + " ".join(f"{k}={v}" for k, v in bad.items())))
        else:
            rows.append(("OK", name, claimed))
    for name in sorted(set(sums) - attached):
        rows.append(("ABSENT", name, f"listed={sums[name]}"))
    return rows


def exit_code(rows, conflicts, require_complete: bool, strict_absent: bool) -> int:
    statuses = {r[0] for r in rows}
    if conflicts or "MISMATCH" in statuses:
        return 1
    if require_complete and ({"UNLISTED", "NODIGEST"} & statuses):
        return 1
    if strict_absent and "ABSENT" in statuses:
        return 1
    return 0


def gh(*args: str) -> bytes:
    return subprocess.run(["gh", *args], check=True, capture_output=True).stdout


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--repo")
    p.add_argument("--tag")
    p.add_argument("--release-json", type=Path, help="offline: saved `gh api repos/R/releases/tags/T` output")
    p.add_argument("--sums-dir", type=Path, help="offline: directory holding the SHA256SUMS*.txt files")
    p.add_argument("--download", action="store_true", help="download every asset and hash it locally")
    p.add_argument("--require-complete", action="store_true", help="fail when an asset is not covered")
    p.add_argument("--strict-absent", action="store_true", help="fail when a listed file is not attached")
    args = p.parse_args(argv)

    with tempfile.TemporaryDirectory() as tmp:
        work = Path(tmp)
        if args.release_json:
            release = json.loads(args.release_json.read_text())
            sums_dir = args.sums_dir or args.release_json.parent
        elif args.repo and args.tag:
            release = json.loads(gh("api", f"repos/{args.repo}/releases/tags/{args.tag}"))
            sums_dir = work / "sums"
            sums_dir.mkdir()
            if any(SUMS_NAME.match(a["name"]) for a in release["assets"]):
                gh("release", "download", args.tag, "-R", args.repo, "-p", "SHA256SUMS*.txt", "-D", str(sums_dir))
        else:
            p.error("give --repo and --tag, or --release-json")

        files = {f.name: f.read_text() for f in sorted(sums_dir.glob("SHA256SUMS*.txt"))}
        sums, conflicts = merge_sums(files)

        local: dict[str, str] = {}
        if args.download:
            if not (args.repo and args.tag):
                p.error("--download needs --repo and --tag")
            dl = work / "assets"
            dl.mkdir()
            for asset in release["assets"]:
                if SUMS_NAME.match(asset["name"]):
                    continue
                gh("release", "download", args.tag, "-R", args.repo, "-p", asset["name"], "-D", str(dl))
                local[asset["name"]] = sha256_file(dl / asset["name"])

    rows = classify(release["assets"], sums, local)
    print(f"release {release.get('tag_name', '?')}: sums files {sorted(files) or 'NONE'}")
    for status, name, detail in rows:
        print(f"{status:9} {name}  {detail}")
    for conflict in conflicts:
        print(f"CONFLICT  {conflict}")
    counts = {s: sum(1 for r in rows if r[0] == s) for s in ("OK", "MISMATCH", "UNLISTED", "ABSENT", "NODIGEST")}
    print("summary " + " ".join(f"{k}={v}" for k, v in counts.items()) + f" CONFLICT={len(conflicts)}")
    return exit_code(rows, conflicts, args.require_complete, args.strict_absent)


if __name__ == "__main__":
    sys.exit(main())
