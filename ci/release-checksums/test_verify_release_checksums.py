import json
import tempfile
import unittest
from pathlib import Path

import verify_release_checksums as v

A = "a" * 64
B = "b" * 64
C = "c" * 64


def asset(name, digest=None):
    return {"name": name, "digest": f"sha256:{digest}" if digest else None}


class ParseTests(unittest.TestCase):
    def test_text_and_binary_markers(self):
        self.assertEqual(v.parse_sums(f"{A}  x.deb\n{B} *y.zip\n", "s"), {"x.deb": A, "y.zip": B})

    def test_rejects_garbage(self):
        with self.assertRaises(ValueError):
            v.parse_sums("not a line\n", "s")

    def test_rejects_uppercase_digest(self):
        with self.assertRaises(ValueError):
            v.parse_sums(f"{A.upper()}  x\n", "s")

    def test_cross_file_conflict_reported(self):
        merged, conflicts = v.merge_sums({"SHA256SUMS-a.txt": f"{A}  x\n", "SHA256SUMS-b.txt": f"{B}  x\n"})
        self.assertEqual(len(conflicts), 1)


class ClassifyTests(unittest.TestCase):
    def status(self, rows):
        return {name: s for s, name, _ in rows}

    def test_ok_mismatch_unlisted_absent(self):
        assets = [asset("ok.deb", A), asset("bad.deb", B), asset("new.zip", C), asset("SHA256SUMS-x.txt", A)]
        sums = {"ok.deb": A, "bad.deb": A, "gone.rpm": C}
        got = self.status(v.classify(assets, sums, {}))
        self.assertEqual(got, {"ok.deb": "OK", "bad.deb": "MISMATCH", "new.zip": "UNLISTED", "gone.rpm": "ABSENT"})

    def test_download_digest_can_mismatch_even_when_github_agrees(self):
        rows = v.classify([asset("x", A)], {"x": A}, {"x": B})
        self.assertEqual(rows[0][0], "MISMATCH")

    def test_no_digest_without_download(self):
        self.assertEqual(v.classify([asset("x")], {"x": A}, {})[0][0], "NODIGEST")

    def test_exit_codes(self):
        mism = [("MISMATCH", "x", "")]
        unl = [("UNLISTED", "x", "")]
        absent = [("ABSENT", "x", "")]
        self.assertEqual(v.exit_code(mism, [], False, False), 1)
        self.assertEqual(v.exit_code(unl, [], False, False), 0)
        self.assertEqual(v.exit_code(unl, [], True, False), 1)
        self.assertEqual(v.exit_code(absent, [], False, False), 0)
        self.assertEqual(v.exit_code(absent, [], False, True), 1)
        self.assertEqual(v.exit_code([("OK", "x", "")], ["c"], False, False), 1)


class OfflineMainTests(unittest.TestCase):
    def test_offline_run(self):
        with tempfile.TemporaryDirectory() as tmp:
            d = Path(tmp)
            (d / "release.json").write_text(json.dumps({"tag_name": "t", "assets": [asset("x", A), asset("y", B)]}))
            (d / "SHA256SUMS-linux.txt").write_text(f"{A}  x\n")
            self.assertEqual(v.main(["--release-json", str(d / "release.json")]), 0)
            self.assertEqual(v.main(["--release-json", str(d / "release.json"), "--require-complete"]), 1)


if __name__ == "__main__":
    unittest.main()
