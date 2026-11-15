#!/usr/bin/env python3
"""Replay the Live TV hostile controls from ci/hostile-controls/manifest.json (#153-LTV-R1).

Every control mutates a real production line, builds, is graded by the gate that names the
property it breaks, and is reverted. A mutation that does not turn its gate red is a mutation
whose property nothing actually protects.

LTV-R0 finding 4 is what this closes: the #153-LTV-S0 and #153-LTV-S1 rosters ran from python
harnesses that lived outside the tree, hardcoded one worktree path, and mutated it in place.
Nothing a reviewer clones could replay them.

#153-LTV-R5 finding F8 is what the GRADER half closes. Until R5 the `expectedTests` array that
every `dotnet-test` control declares was never read: `grep -n expectedTests run.py` returned
nothing, and a control naming a test that does not exist still graded RED off a localised stdout
summary line. A RED is now decided from a structured TRX result and only when a DECLARED test
really ran and really failed. Text on stdout can no longer decide anything.

Usage:
    ci/hostile-controls/run.py [--commit <rev>] [--out <file>] [--rig <script>]
                               [--self-test | --no-self-test] [id ...]

The runner creates its OWN git worktree from <rev> (default HEAD) and never writes to the
worktree it was invoked from. Controls run serially, in the foreground.

#153-LTV-R6 finding R6-1 is what the REPORTING half closes. A RED naming one declared test read
exactly like a targeted RED even when nine other tests had failed alongside it: `classify_trx` named
only the declared failures, and the console printed `detail.splitlines()[0]`. Every gate now returns
a single-line HEADLINE that is printed in full, a `dotnet-test` gate also returns the counts and the
FULL NAMES of every failure outside `expectedTests`, and a RED that carries an undeclared failure
fails the run. There is NO opt-out from that gate.

#153-LTV-R8 is what the LOCKDOWN half closes. R7 shipped that gate with a generic boolean escape
hatch that any control could set for itself: an ordinary roster line that broke nine tests it did
not declare failed the run without it and exited 0 with it, so the invariant was whatever the
manifest said it was. That boolean is gone, and nothing replaces it. The ONE row that is legitimately allowed to
keep undeclared failures is the grader's own tenth counter-control, and it is allowed to only by
naming them: `expectUndeclaredFailures` is at once the permission and the oracle, it exists only in
the AUTOTEST document, only on that one id, and the run fails if the set it names is not exactly
the set observed. Provenance is not read from a file name the caller controls - it is which arm of
main() loaded the document.

Unless `--no-self-test` is passed, a full roster run first replays
ci/hostile-controls/counter-controls.json — ten situations whose classification is mandated — and
refuses to report a roster result if any of them is classified differently. A roster whose grader
has not been proven on that table is not evidence.

Exit status: 0 only when the self-test held, every selected control matched its expectation, and
every tree was restored byte-identically.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import traceback
import xml.etree.ElementTree as ElementTree

HERE = os.path.dirname(os.path.abspath(__file__))
MANIFEST = os.path.join(HERE, "manifest.json")
COUNTER_MANIFEST = os.path.join(HERE, "counter-controls.json")

TRX_NS = {"trx": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# Every verdict a gate may return. GREEN means the gate HELD - the property the control attacks
# was not broken by the mutation. It is the roster's INERT and the positive controls' PASS.
GREEN, RED, ERROR, HUNG, NO_RIG = "GREEN", "RED", "ERROR", "HUNG", "NO-RIG"

# The two - and only two - provenances a control document may have. This is an INTERNAL mode, set by
# whichever arm of main() loaded the document, never inferred from a path the caller can choose:
# ROSTER is always MANIFEST and AUTOTEST is always COUNTER_MANIFEST, both derived from this file's
# own directory. The distinction is load-bearing, because exactly one shape is legal in one document
# and illegal in the other (#153-LTV-R8).
ROSTER, AUTOTEST = "roster", "autotest"

# The one id in the AUTOTEST document that may keep failures it does not declare, and only while it
# names every one of them. Nothing else, in either document, may.
CC10 = "cc-10-a-declared-red-with-undeclared-collateral-is-reported-as-such"

# The closed schema. Anything not named here is rejected: a key the runner does not read is a key a
# reviewer reads as load-bearing, and R8's generic opt-out was exactly that failure mode in reverse
# - a key the runner DID read, that the manifest was free to set, and that switched off a gate.
DOCUMENT_KEYS = {
    ROSTER: {"$comment", "controls", "grades", "isolation", "restoration"},
    AUTOTEST: {"$comment", "controls", "grades"},
}
CONTROL_REQUIRED = ("id", "stage", "status", "expect", "property", "timeoutSeconds", "gate",
                    "mutations")
CONTROL_OPTIONAL = {
    ROSTER: {"historicalId", "note", "historicalMutations", "supersededReason"},
    AUTOTEST: {"note", "expectUndeclaredFailures"},
}
GATE_REQUIRED = {
    "dotnet-test": ("kind", "project", "filter", "expectedTests"),
    "rig": ("kind", "scenario"),
    "source": ("kind", "file", "absentPattern"),
    "inventory": ("kind", "script"),
}
GATE_OPTIONAL = {
    "dotnet-test": set(),
    "rig": {"requires"},
    "source": set(),
    "inventory": set(),
}
MUTATION_REQUIRED = ("file", "find", "replace")
MUTATION_OPTIONAL = {"count"}


def run(cmd, cwd, timeout, env=None):
    merged = dict(os.environ)
    merged.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    merged.setdefault("DOTNET_NOLOGO", "1")
    if env:
        merged.update(env)
    try:
        done = subprocess.run(
            cmd, cwd=cwd, timeout=timeout, env=merged,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, check=False)
        return done.returncode, done.stdout
    except subprocess.TimeoutExpired as expired:
        return "TIMEOUT", (expired.stdout or "")


def tree_hash(worktree):
    """The worktree's real tree.

    `git add -A -n` is a DRY RUN: it stages nothing, so write-tree would return the same hash for
    a mutated tree as for a clean one and "restored byte-identically" would be true of nothing.
    The index has to be updated for real. The S1 harness shipped with exactly that defect.
    """
    code, out = run(["git", "add", "-A"], worktree, 300)
    if code != 0:
        return "ERROR:add:" + out.strip()[:160]
    code, out = run(["git", "write-tree"], worktree, 120)
    return out.strip() if code == 0 else "ERROR:" + out.strip()[:160]


def revert(worktree):
    # Hard, because tree_hash() stages what it measures; `git checkout --` would leave the
    # mutation in the index. It also restores mtimes to "now", so the next build cannot skip a
    # rebuild and grade the PREVIOUS control's compiled mutation (an R4 Phase 2 rig defect).
    run(["git", "reset", "--hard", "HEAD"], worktree, 300)
    run(["git", "clean", "-fdq"], worktree, 300)


def apply_mutation(worktree, mutation):
    path = os.path.join(worktree, mutation["file"])
    if not os.path.exists(path):
        return f"{mutation['file']} does not exist"
    with open(path, encoding="utf-8") as handle:
        body = handle.read()
    found = body.count(mutation["find"])
    if found != 1:
        return f"anchor occurs {found} times in {mutation['file']}, expected exactly 1"
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(body.replace(mutation["find"], mutation["replace"]))
    return None


def validate_document(document, provenance):
    """The document's own top level, closed to keys nothing reads.

    Returns a reason or None. `controls` is the only key the runner needs; the rest are prose the
    two documents happen to carry. A key that is in neither list is refused rather than ignored.
    """
    if not isinstance(document, dict):
        return f"schema: the {provenance} document is not an object"
    if "controls" not in document:
        return f"schema: the {provenance} document declares no 'controls'"
    if not isinstance(document["controls"], list):
        return f"schema: the {provenance} document's 'controls' is not a list"
    unknown = sorted(set(document) - DOCUMENT_KEYS[provenance])
    if unknown:
        return (f"schema: the {provenance} document declares unknown top-level key(s) "
                f"{', '.join(unknown)}")
    return None


def validate_schema(control, provenance):
    """The control's own shape, checked before it is allowed to decide anything.

    An invalid control is an ERROR, never a grade: a control that cannot say which test decides it
    has no claim to make about the tree. Only `dotnet-test` carries `expectedTests` - the source,
    inventory and rig gates have no test list to validate and must not be failed for lacking one.

    The schema is CLOSED, in both directions, and it is closed PER PROVENANCE (#153-LTV-R8):

      * every key on a control, on its gate and on each of its mutations must be one this runner
        reads. R8's generic opt-out is no longer one of them anywhere, at any value, and an
        unknown key is an ERROR rather than something a reviewer must notice by eye;
      * `expectUndeclaredFailures` is legal ONLY in the AUTOTEST document and ONLY on CC10, where
        it is the permission and the oracle at once. In the ROSTER it is refused outright, so no
        production control can buy itself the exemption by naming what it breaks either.

    `provenance` is an internal mode, not a file name: see ROSTER / AUTOTEST.
    """
    if provenance not in (ROSTER, AUTOTEST):
        return f"schema: unknown provenance '{provenance}'"
    if not isinstance(control, dict):
        return "schema: control is not an object"
    for field in CONTROL_REQUIRED:
        if field not in control:
            return f"schema: control is missing '{field}'"

    unknown = sorted(set(control) - set(CONTROL_REQUIRED) - CONTROL_OPTIONAL[provenance])
    if unknown:
        return (f"schema: a {provenance} control declares unknown key(s) {', '.join(unknown)}; "
                "the control schema is closed")

    if not isinstance(control["gate"], dict):
        return "schema: 'gate' is not an object"
    if not isinstance(control["mutations"], list):
        return "schema: 'mutations' is not a list"
    if not isinstance(control["timeoutSeconds"], int) or control["timeoutSeconds"] <= 0:
        return "schema: timeoutSeconds must be a positive integer"

    gate = control["gate"]
    kind = gate.get("kind")
    if kind not in GATE_REQUIRED:
        return f"schema: unknown gate kind '{kind}'"
    if control["expect"] not in ("PASS", "RED", "INERT", "ERROR", "HUNG"):
        return f"schema: unknown expectation '{control['expect']}'"

    for field in GATE_REQUIRED[kind]:
        if field not in gate:
            return f"schema: a '{kind}' gate is missing '{field}'"
    unknown = sorted(set(gate) - set(GATE_REQUIRED[kind]) - GATE_OPTIONAL[kind])
    if unknown:
        return (f"schema: a '{kind}' gate declares unknown key(s) {', '.join(unknown)}, "
                "which nothing reads")

    for mutation in control["mutations"]:
        if not isinstance(mutation, dict):
            return "schema: a mutation is not an object"
        for field in MUTATION_REQUIRED:
            if field not in mutation:
                return f"schema: a mutation is missing '{field}'"
        unknown = sorted(set(mutation) - set(MUTATION_REQUIRED) - MUTATION_OPTIONAL)
        if unknown:
            return f"schema: a mutation declares unknown key(s) {', '.join(unknown)}"

    invalid = validate_expected_undeclared(control, provenance)
    if invalid:
        return invalid

    if kind != "dotnet-test":
        return None

    for field in ("project", "filter"):
        if not gate.get(field):
            return f"schema: a dotnet-test gate is missing '{field}'"
    declared = gate.get("expectedTests")
    if not isinstance(declared, list) or not declared:
        return "schema: expectedTests must be a non-empty list"
    if any(not isinstance(name, str) or not name.strip() for name in declared):
        return "schema: every expectedTests entry must be a non-empty string"
    if len(set(declared)) != len(declared):
        return "schema: expectedTests contains a duplicate"
    return None


def validate_expected_undeclared(control, provenance):
    """`expectUndeclaredFailures` - the ONE narrow, named exception, and its whole rule.

    It is not an opt-out. It is a list of the exact failures a control must be SEEN to report, and
    the runner compares it against `unexpectedFailures`: amputate it, add a name to it, or perturb
    one character of a name, and `undeclaredReportingHeld` goes False, the autotest is misgraded and
    the row loses the exemption as well. Grading CC10 RED proves nothing about the report - the
    grade was already right before R7 - so the report is what this pins.

    It exists only in the grader's own AUTOTEST document, only on CC10, and CC10 must carry it.
    """
    present = "expectUndeclaredFailures" in control
    is_cc10 = provenance == AUTOTEST and control.get("id") == CC10

    if present and provenance != AUTOTEST:
        return ("schema: expectUndeclaredFailures is not a roster mechanism; a production control "
                "may not keep failures it does not declare, whether or not it names them")
    if present and not is_cc10:
        return (f"schema: expectUndeclaredFailures is allowed on '{CC10}' alone, not on "
                f"'{control.get('id')}'")
    if is_cc10 and not present:
        return (f"schema: '{CC10}' must declare expectUndeclaredFailures; it is the only control "
                "permitted to keep undeclared failures and the list is what permits it")
    if not present:
        return None

    names = control["expectUndeclaredFailures"]
    if not isinstance(names, list) or not names:
        return "schema: expectUndeclaredFailures must be a non-empty list"
    if any(not isinstance(name, str) or not name.strip() for name in names):
        return "schema: every expectUndeclaredFailures entry must be a non-empty string"
    if len(set(names)) != len(names):
        return "schema: expectUndeclaredFailures contains a duplicate"
    if control["gate"].get("kind") != "dotnet-test":
        return "schema: expectUndeclaredFailures is meaningless on a gate that runs no test"
    return None


def build(worktree, project, timeout):
    # The build ALWAYS runs before the gate. A gate that consults a previously compiled dll grades
    # a mutation the code under test never saw, and comes back INERT: the S1 harness graded
    # `remove-the-propagator-call` INERT for exactly that reason before it was fixed.
    return run(
        ["dotnet", "build", project, "-p:UseSharedCompilation=false", "-nodereuse:false",
         "--nologo", "-v:q"],
        worktree, timeout)


def covers(declared, test_name):
    """Does a declared deciding test cover this TRX result?

    `expectedTests` entries are written at the granularity the control decides at: a whole class
    (`…HlsOwnershipMatrixTests`), a theory (`…VideoSegmentFamily`, whose TRX rows carry their data
    row in parentheses) or one fact. Coverage is therefore prefix-based, and one-directional: a
    declared name never matches a SHORTER test name, so declaring a class cannot be satisfied by an
    unrelated test whose name happens to start the same way at a non-boundary.
    """
    return (test_name == declared
            or test_name.startswith(declared + ".")
            or test_name.startswith(declared + "("))


def read_trx(path):
    """(results, summary) from a TRX file, or (None, reason).

    TRX is read rather than stdout because the stdout summary on this host is localised, is a
    single count with no test names in it, and cannot distinguish "the declared test failed" from
    "some other test failed". A grade of RED is a claim about a NAMED test; only the structured
    result carries the names.
    """
    try:
        root = ElementTree.parse(path).getroot()
    except (OSError, ElementTree.ParseError) as failure:
        return None, f"the TRX result is absent or unreadable: {failure}"

    results = []
    for node in root.findall(".//trx:UnitTestResult", TRX_NS):
        name = node.get("testName")
        if name:
            results.append((name, node.get("outcome") or "Unknown"))

    # RunInfos is NOT a run-level error channel here and must not be read as one. xUnit copies its
    # own console lines into it, so every ordinary `[FAIL]` line arrives as a RunInfo whose outcome
    # is an error - and a first draft of this grader classified a perfectly good RED as ERROR for
    # exactly that reason. Whether the RUN aborted is in Counters, which xUnit fills correctly.
    counters = root.find(".//trx:ResultSummary/trx:Counters", TRX_NS)
    summary = {key: int(counters.get(key, 0)) for key in ("total", "executed", "passed", "failed", "error", "aborted")} if counters is not None else {}
    return results, summary


def undeclared_block(unexpected, total_failures):
    """The sentence a RED with collateral is not allowed to omit.

    #153-LTV-R6 finding R6-1: `classify_trx` named only the failures a control DECLARES, so a
    mutation that broke nine extra tests reported itself as a targeted RED. Every name, in full,
    with its count and the run's total - a truncated list would reopen the same finding.
    """
    return (f"UNDECLARED FAILURES ({len(unexpected)} of {total_failures} total failure(s)): "
            + "; ".join(unexpected))


def classify_trx(results, summary, declared):
    """The mission's four-valued classification, decided from the structured result alone.

    Returns (verdict, headline, facts). `facts` is the machine-readable half that the console line
    and the roster JSON both render, and it separates the five things a grade is made of: what the
    control declared, how much of that really ran, which failures it claims, which failures it does
    NOT claim, and how many failures there were altogether. A RED may keep collateral damage - the
    runner must simply never let it read as targeted (#153-LTV-R6 finding R6-1).
    """
    failures = [name for name, outcome in results if outcome == "Failed"]
    expected_failures = sorted(n for n in failures if any(covers(d, n) for d in declared))
    unexpected = sorted(n for n in failures if not any(covers(d, n) for d in declared))
    facts = {
        "expectedTests": list(declared),
        "expectedTestsExecuted": [],
        "expectedFailures": expected_failures,
        "unexpectedFailures": unexpected,
        "totalFailures": len(failures),
        "totalTests": len(results),
    }

    if summary.get("aborted") or summary.get("error"):
        return ERROR, f"the run aborted or errored before it could decide: {summary}", facts
    if not results:
        return ERROR, "the filter matched no tests: the run produced no test result at all", facts

    executed = {name for name, outcome in results if outcome != "NotExecuted"}
    for name in declared:
        matched = [n for n, _ in results if covers(name, n)]
        if not matched:
            return ERROR, (f"the declared deciding test '{name}' does not exist in the run "
                           f"({len(results)} tests ran under this filter)"), facts
        if not any(n in executed for n in matched):
            return ERROR, f"the declared deciding test '{name}' exists but was not executed", facts
        facts["expectedTestsExecuted"].append(name)

    if not failures:
        return GREEN, (f"every declared deciding test ran and stayed green "
                       f"({len(declared)} declared, {len(results)} tests)"), facts

    if not expected_failures:
        return ERROR, ("only tests OUTSIDE expectedTests failed, so nothing this control declares "
                       "decisive was broken: " + undeclared_block(unexpected, len(failures))), facts

    headline = (f"{len(expected_failures)} of {len(declared)} declared deciding test(s) failed, "
                f"{len(failures)} failure(s) in {len(results)} test(s): "
                + "; ".join(expected_failures))
    if unexpected:
        headline += " | " + undeclared_block(unexpected, len(failures))
    return RED, headline, facts


def gate_dotnet_test(worktree, gate, timeout):
    code, log = build(worktree, gate["project"], timeout)
    if code == "TIMEOUT":
        return HUNG, f"the build did not finish within {timeout}s", "TIMEOUT", None
    if code != 0:
        return ERROR, "build failed: the mutated tree does not compile", \
            "build failed\n" + log[-3000:], None

    results_dir = tempfile.mkdtemp(prefix="ltv-trx-")
    try:
        code, log = run(
            ["dotnet", "test", "--project", gate["project"], "--no-build", "--no-progress",
             "--filter", gate["filter"],
             "--report-trx", "--report-trx-filename", "control.trx",
             "--results-directory", results_dir],
            worktree, timeout)
        if code == "TIMEOUT":
            return HUNG, f"the test run did not finish within {timeout}s", "TIMEOUT", None

        trx = os.path.join(results_dir, "control.trx")
        if not os.path.exists(trx):
            return ERROR, "no TRX result was written: the run did not happen", \
                "no TRX result was written: the run did not happen\n" + log[-3000:], None
        results, summary = read_trx(trx)
        if results is None:
            return ERROR, summary, summary + "\n" + log[-2000:], None

        verdict, headline, facts = classify_trx(results, summary, gate["expectedTests"])
        counts = {k: summary.get(k) for k in ("total", "executed", "passed", "failed")}
        facts["trxFile"] = trx
        facts["trxCounters"] = counts
        return verdict, headline, f"{headline}\ntrx {counts}\n" + log[-1200:], facts
    finally:
        shutil.rmtree(results_dir, ignore_errors=True)


def gate_source(worktree, gate, timeout):
    """A source assertion, for a property no behavioural test can separate.

    Used where two variants emit identical bytes for every reachable input. It is recorded as a
    source-level grade rather than allowed to pass as INERT.
    """
    del timeout
    path = os.path.join(worktree, gate["file"])
    with open(path, encoding="utf-8") as handle:
        body = handle.read()
    present = re.search(gate["absentPattern"], body) is not None
    headline = f"pattern {'present' if present else 'absent'} in {gate['file']}"
    return (RED if present else GREEN), headline, headline, None


def first_line(log):
    for line in log.splitlines():
        if line.strip():
            return line.strip()
    return "(no output)"


def gate_inventory(worktree, gate, timeout):
    code, log = run(["bash", gate["script"]], worktree, timeout)
    if code == "TIMEOUT":
        return HUNG, f"{gate['script']} did not finish within {timeout}s", "TIMEOUT", None
    headline = f"{gate['script']} exit {code}: {first_line(log)}"
    return (GREEN if code == 0 else RED), headline, log[-1500:], None


def gate_rig(worktree, gate, timeout, rig):
    if not rig:
        missing = (f"scenario '{gate['scenario']}' needs a live rig; pass --rig <script>. "
                   f"Requires: {'; '.join(gate.get('requires', []))}")
        return NO_RIG, missing, missing, None
    code, log = run([rig, gate["scenario"], worktree], os.path.dirname(rig) or ".", timeout)
    if code == "TIMEOUT":
        return HUNG, f"rig scenario '{gate['scenario']}' did not finish within {timeout}s", \
            "TIMEOUT", None
    if code == 2:
        # The adapter could not decide - the mutated tree did not build, or it produced no
        # evidence. That is an ERROR to attribute, never a RED: grading a build failure as a
        # proven property is exactly the false green this whole runner exists to prevent.
        return ERROR, f"rig scenario '{gate['scenario']}' could not decide (exit 2)", \
            "the rig could not decide (exit 2)\n" + log[-2000:], None
    headline = f"rig scenario '{gate['scenario']}' exit {code}: {first_line(log)}"
    return (GREEN if code == 0 else RED), headline, log[-2000:], None


def evaluate(worktree, gate, timeout, rig):
    kind = gate["kind"]
    if kind == "dotnet-test":
        return gate_dotnet_test(worktree, gate, timeout)
    if kind == "source":
        return gate_source(worktree, gate, timeout)
    if kind == "inventory":
        return gate_inventory(worktree, gate, timeout)
    if kind == "rig":
        return gate_rig(worktree, gate, timeout, rig)
    return ERROR, f"schema: unknown gate kind '{kind}'", f"schema: unknown gate kind '{kind}'", None


def grade(control, verdict):
    """The gate's verdict, read against what this control expects to happen.

    GREEN is the only verdict whose NAME depends on the control: for a positive control it is the
    PASS the control exists to demonstrate; for a mutation it is INERT - the mutation ran and broke
    nothing, which is the finding, not a success.
    """
    if verdict == NO_RIG:
        return "SKIPPED-NO-RIG"
    if verdict in (ERROR, HUNG):
        return verdict
    if control["expect"] == "PASS":
        return "PASS" if verdict == GREEN else "ERROR"
    return "RED" if verdict == RED else "INERT"


def print_record(record):
    """One aligned row per control, and then every field the grade was made of.

    The row used to print `str(detail).splitlines()[0][:88]`. That is what #153-LTV-R6 finding R6-1
    is: the counts and the names of the failures OUTSIDE `expectedTests` lived in `detail`'s later
    lines, so a RED with nine extra failures printed as a targeted RED. The headline is now printed
    in full, the counts on their own line, and every undeclared failure by its complete name.
    """
    pad = f"{'':15} {'':52} {'':9}"
    print(f"{record['grade']:15} {record['id']:52} {record['seconds']:7.1f}s  "
          f"{record.get('headline') or ''}", flush=True)
    if record.get("totalFailures") is not None:
        print(f"{pad} declared {len(record['expectedTestsExecuted'])}"
              f"/{len(record['expectedTests'])} executed, "
              f"{len(record['expectedFailures'])} declared failure(s), "
              f"{len(record['unexpectedFailures'])} undeclared, "
              f"{record['totalFailures']} failure(s) in {record['totalTests']} test(s)", flush=True)
    if record.get("undeclaredReportingHeld") is not None:
        print(f"{pad} undeclared-failure reporting: "
              f"{'HELD' if record['undeclaredReportingHeld'] else 'FAILED'}", flush=True)
    unexpected = record.get("unexpectedFailures") or []
    if unexpected:
        print(f"{pad} UNDECLARED FAILURES ({len(unexpected)} of {record['totalFailures']} "
              f"total failure(s)):", flush=True)
        for name in unexpected:
            print(f"{pad}   - {name}", flush=True)


def undeclared_rows(records, provenance):
    """RED rows that kept a failure nothing declared - the #153-LTV-R6 finding R6-1 invariant.

    There is no opt-out (#153-LTV-R8). The single exemption is CC10, in the AUTOTEST document, whose
    whole subject is collateral damage - and it is exempt only while it NAMES the collateral: the
    moment `undeclaredReportingHeld` is anything but True the row is counted here like any other, on
    top of being misgraded. Rows whose gate runs no test carry no `unexpectedFailures` field at all
    and are not silently counted as clean.
    """
    return [r for r in records
            if r["grade"] == "RED"
            and r.get("unexpectedFailures")
            and not (provenance == AUTOTEST
                     and r["id"] == CC10
                     and r.get("undeclaredReportingHeld") is True)]


def replay(controls, worktree, baseline, rig, provenance, on_record=None):
    """Replay one control document. `provenance` says WHICH document - see ROSTER / AUTOTEST.

    It is a parameter and not a guess: the two documents have different closed schemas, and the one
    shape that is legal in the autotests is illegal in the roster. Deriving it from a path would put
    the choice back in the caller's hands, which is the whole of what #153-LTV-R8 found.
    """
    if provenance not in (ROSTER, AUTOTEST):
        raise SystemExit(f"internal: replay called with unknown provenance '{provenance}'")
    results = []
    for control in controls:
        started = time.time()
        record = {"id": control["id"], "stage": control.get("stage"), "status": control.get("status"),
                  "expect": control.get("expect"), "property": control.get("property")}
        if "note" in control:
            record["note"] = control["note"]

        invalid = validate_schema(control, provenance)
        if invalid:
            record["grade"] = "ERROR"
            record["headline"] = invalid
            record["detail"] = invalid
        else:
            failure = None
            for mutation in control["mutations"]:
                failure = apply_mutation(worktree, mutation)
                if failure:
                    break

            if failure:
                record["grade"] = "ERROR"
                record["headline"] = failure
                record["detail"] = failure
            else:
                facts = None
                try:
                    verdict, headline, detail, facts = evaluate(
                        worktree, control["gate"], control["timeoutSeconds"], rig)
                except Exception:  # noqa: BLE001 - a grader that crashes must ERROR, never kill the run
                    verdict = ERROR
                    headline = "the grader crashed"
                    detail = "the grader crashed\n" + traceback.format_exc()[-2000:]
                record["verdict"] = verdict
                record["headline"] = headline
                record["detail"] = detail
                if facts:
                    record.update(facts)
                record["grade"] = grade(control, verdict)
                if facts and facts.get("trxCounters"):
                    counters = facts["trxCounters"]
                    record["totalsMatchTrx"] = (facts["totalFailures"] == counters["failed"]
                                                and facts["totalTests"] == counters["total"])
                    if not record["totalsMatchTrx"]:
                        # The reported totals ARE the evidence; a report that disagrees with the
                        # TRX it was read from cannot be trusted to have named the failures either.
                        record["grade"] = "ERROR"
                        record["headline"] = (
                            f"the reported counts do not match the TRX summary: "
                            f"{facts['totalFailures']} failure(s) in {facts['totalTests']} "
                            f"test(s) vs {counters}")

        # The oracle. Only for a control whose schema was accepted: a control rejected on its
        # schema never ran, so there is no observed set to compare its list against, and reporting
        # one would be a verdict about a run that did not happen.
        if not invalid and "expectUndeclaredFailures" in control:
            record["undeclaredReportingHeld"] = (
                sorted(record.get("unexpectedFailures") or [])
                == sorted(control["expectUndeclaredFailures"]))

        revert(worktree)
        restored = tree_hash(worktree)
        record["restoredByteIdentically"] = restored == baseline
        if not record["restoredByteIdentically"]:
            record["grade"] = "ERROR"
            record["headline"] = f"tree {restored} != baseline {baseline}"
            record["detail"] = f"tree {restored} != baseline {baseline}; {record.get('detail')}"
        record["seconds"] = round(time.time() - started, 1)

        print_record(record)
        results.append(record)
        if on_record:
            on_record(results)
    return results


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--commit", default="HEAD")
    parser.add_argument("--out", default=None)
    parser.add_argument("--rig", default=None, help="script run as <script> <scenario> <worktree>")
    parser.add_argument("--keep", action="store_true", help="do not delete the temporary worktree")
    parser.add_argument("--self-test", action="store_true",
                        help="replay ONLY the grader counter-controls and stop")
    parser.add_argument("--no-self-test", action="store_true",
                        help="skip the grader counter-controls (the roster is then unproven)")
    parser.add_argument("ids", nargs="*")
    args = parser.parse_args()

    # The ONLY two loads in this program, and each one fixes its own provenance here. MANIFEST and
    # COUNTER_MANIFEST are derived from this file's directory, so no argument, environment variable
    # or file name decides which schema a document is held to (#153-LTV-R8).
    with open(MANIFEST, encoding="utf-8") as handle:
        manifest = json.load(handle)
    with open(COUNTER_MANIFEST, encoding="utf-8") as handle:
        counter = json.load(handle)

    for document, provenance, path in ((manifest, ROSTER, MANIFEST),
                                       (counter, AUTOTEST, COUNTER_MANIFEST)):
        invalid = validate_document(document, provenance)
        if invalid:
            raise SystemExit(f"{path}: {invalid}")

    selected = [] if args.self_test else [
        c for c in manifest["controls"] if not args.ids or c["id"] in args.ids]
    if not selected and not args.self_test:
        raise SystemExit("no control matched")

    # The self-test proves the grader itself. It runs by default on a FULL roster replay, because
    # a roster graded by an unproven grader is what R4 finding F8 was: 45 grades, 33 of them
    # decided by a field nothing read.
    full_roster = not args.ids
    run_self_test = args.self_test or (full_roster and not args.no_self_test)

    # THE ROSTER PRE-FLIGHT. Every selected production control is schema-checked here, before a
    # worktree exists and therefore before any mutation is applied or anything is compiled. A roster
    # is a fixed set of controls of which none may be malformed, so one bad row stops the run rather
    # than costing 40 builds to reach. The AUTOTEST document is deliberately NOT pre-flighted: cc-9
    # IS an invalid control, and it has to reach the per-control path to be graded ERROR there.
    preflight = [(c.get("id"), validate_schema(c, ROSTER)) for c in selected]
    preflight = [(cid, reason) for cid, reason in preflight if reason]
    if preflight:
        print(f"{len(preflight)} roster control(s) are ERROR on their own schema; nothing was "
              "mutated and nothing was built:", flush=True)
        for cid, reason in preflight:
            print(f"  ERROR {cid}: {reason}", flush=True)
        if args.out:
            with open(args.out, "w", encoding="utf-8") as handle:
                json.dump({"selfTest": "skipped", "counterControls": [], "undeclaredFailureRows": [],
                           "results": [{"id": cid, "grade": "ERROR", "headline": reason,
                                        "detail": reason} for cid, reason in preflight]},
                          handle, indent=2)
        return 1

    source = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True).stdout.strip()
    commit = subprocess.run(
        ["git", "rev-parse", args.commit], cwd=source, capture_output=True, text=True, check=True).stdout.strip()

    worktree = tempfile.mkdtemp(prefix="ltv-hostile-controls-")
    os.rmdir(worktree)
    subprocess.run(["git", "worktree", "add", "--detach", worktree, commit], cwd=source, check=True)
    print(f"commit {commit}\nworktree {worktree}", flush=True)

    payload = {"commit": commit, "selfTest": "skipped", "counterControls": [], "results": []}

    def flush():
        if args.out:
            with open(args.out, "w", encoding="utf-8") as handle:
                json.dump(payload, handle, indent=2)

    counter_results = []
    results = []
    try:
        revert(worktree)
        baseline = tree_hash(worktree)
        payload["baselineTree"] = baseline
        print(f"baseline tree {baseline}", flush=True)

        if run_self_test:
            print("\n--- grader counter-controls (classification, not the product) ---", flush=True)
            counter_results = replay(
                counter["controls"], worktree, baseline, args.rig, AUTOTEST,
                on_record=lambda r: (payload.__setitem__("counterControls", r), flush()))
            payload["counterControls"] = counter_results

        misgraded = [r for r in counter_results
                     if r["grade"] != r["expect"] or not r["restoredByteIdentically"]
                     or r.get("undeclaredReportingHeld") is False
                     or r.get("totalsMatchTrx") is False]
        payload["selfTest"] = ("skipped" if not run_self_test
                               else ("HELD" if not misgraded else "FAILED"))
        flush()

        if run_self_test and misgraded:
            print("\nThe grader misclassified "
                  f"{len(misgraded)} counter-control(s); the roster is NOT graded.", flush=True)
            for record in misgraded:
                print(f"  {record['id']}: expected {record['expect']}, got {record['grade']}"
                      + ("" if record.get("undeclaredReportingHeld") is not False
                         else "; the undeclared failures it must report were not reported: "
                              f"got {record.get('unexpectedFailures')}"), flush=True)
        elif selected:
            print("\n--- roster ---", flush=True)
            results = replay(
                selected, worktree, baseline, args.rig, ROSTER,
                on_record=lambda r: (payload.__setitem__("results", r), flush()))
            payload["results"] = results
            flush()
    finally:
        if not args.keep:
            subprocess.run(["git", "worktree", "remove", "--force", worktree], cwd=source, check=False)
            shutil.rmtree(worktree, ignore_errors=True)

    print("")
    for label, records in (("counter-control", counter_results), ("roster", results)):
        if not records:
            continue
        grades = [r["grade"] for r in records]
        print(f"{label}:")
        for name in ("PASS", "RED", "INERT", "ERROR", "HUNG", "SKIPPED-NO-RIG"):
            if grades.count(name):
                print(f"  {grades.count(name):3} {name}")

    def matched(records):
        return all(r["grade"] == r["expect"] and r["restoredByteIdentically"]
                   for r in records if r["grade"] != "SKIPPED-NO-RIG")

    # #153-LTV-R6 finding R6-1, made permanent: a RED that carries a failure outside its own
    # expectedTests is a general breakage wearing a targeted grade. It is reported by name above and
    # it fails the run here, on the roster AND on the counter-controls. Nothing opts out (R8): the
    # sole exemption is CC10, in the AUTOTEST document, for exactly as long as it names what it kept.
    collateral = (undeclared_rows(counter_results, AUTOTEST)
                  + undeclared_rows(results, ROSTER))
    payload["undeclaredFailureRows"] = [
        {"id": r["id"], "unexpectedFailures": r["unexpectedFailures"]} for r in collateral]
    flush()
    if collateral:
        print(f"\n{len(collateral)} RED row(s) kept a failure nothing declared:")
        for record in collateral:
            print(f"  {record['id']}: {undeclared_block(record['unexpectedFailures'], record['totalFailures'])}")

    unresolved = [r for r in results if r["grade"] == "SKIPPED-NO-RIG"]
    if unresolved:
        print(f"\n{len(unresolved)} control(s) were NOT graded: they need a rig. This run is incomplete.")
    if run_self_test:
        print(f"\nself-test: {payload['selfTest']}")
    elif selected:
        print("\nself-test: SKIPPED - this roster result is not admissible on its own.")

    ok = (matched(counter_results) and not collateral
          and (not run_self_test or payload["selfTest"] == "HELD"))
    if selected and not (run_self_test and payload["selfTest"] == "FAILED"):
        ok = ok and matched(results) and not unresolved
    elif selected:
        ok = False
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
