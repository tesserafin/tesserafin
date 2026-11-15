#!/usr/bin/env bash
# Local CI — single entry point.
#
# Why this exists (issue #62): GitHub refuses to allocate a hosted runner for
# this PRIVATE repo BEFORE the first step — the all3f0r1 account's 2000 free
# Actions minutes/month are account-wide and exhausted (~2646 weighted minutes
# in July 2026, drained mostly by another repo), and the spending limit is $0,
# so the overage is refused rather than billed. No self-hosted runner is
# registered either (total_count: 0). Until a human raises the spending limit,
# makes the repo public, or registers a runner with the labels
# self-hosted,linux,x64,reefin, this script IS the mandatory merge gate.
#
# This script purges every bin/ and obj/ in the checkout ITSELF, before the
# first compilation, and refuses to build if that purge did not complete
# (#94). The caller has no prerequisite to remember and no way to skip it:
# there is no flag for that. See ci/lib/clean-artifacts.sh for the contract
# and docs/local-ci.md ("Porte de référence") for why it matters.
# This script takes no arguments.
#
# What it does: builds the tesserafin-ci image from Dockerfile.ci (repo root),
# then runs `dotnet build` + `dotnet test` for the full solution inside a
# container. The repository is bind-mounted (not copied) so the container
# always tests the CURRENT checkout on disk — whatever branch is out,
# uncommitted changes included — which is what makes this script a valid
# gate for any branch.
#
# PR115d: the test run below excludes anything tagged Category=Smoke - today that is
# tests/Tesserafin.MediaEncoding.Tests/Encoder/HlsSmokeTests.cs, a real ffmpeg/HLS synthesis test that is
# meaningfully heavier than the rest of this suite and was scoped as an OPTIONAL, non-blocking stage.
# Run it explicitly with ./ci/smoke.sh (also Docker-based, same image) - see that script's header for
# exactly what it proves and does not prove.
#
# Issue #36 / openapi: the OpenAPI contract drift check is part of the dotnet test stage below, not a
# separate step here - OpenApiContractTests (tests/Tesserafin.Server.Integration.Tests) fails this gate
# when openapi/openapi.json no longer matches what the server produces, and its failure message
# names the fix (./ci/openapi-generate.sh). Keeping it inside the suite means it reuses the server
# boot the suite already pays for, and leaves this script unchanged in structure and runtime.
# See docs/openapi-contract.md.
#
# Issue #174 / [C3]: the PROVIDER-AUTHENTICATION structural audit is likewise part of the dotnet test
# stage below rather than a separate step. ProviderAuthAuditTests (tests/Tesserafin.Providers.Tests)
# reads ci/provider-auth-inventory.json and the COMPILED Tesserafin.Providers.dll and fails this gate
# when a third-party provider credential is compiled in, when a provider's outbound host or string
# constant is undeclared, or when an inventory entry describes a code path that no longer exists.
# It uses no entropy or length threshold: two of the three credentials this project inherited were
# six and eight characters long, so it identifies a credential by WHERE it is used, not by what it
# looks like. Gitleaks is complementary and does not cover this — it reported the contaminated image
# as clean, because the values were .NET const strings in the UTF-16LE metadata heap.
# See docs/provider-auth-audit.md.
#
# Issue #93 / [A7]: the server<->web RELEASE PAIR is deliberately NOT checked here.
# ci/verify-release-pair.sh needs a published image, a tesserafin-web checkout and a
# real browser; requiring all three would make ordinary server-only development
# depend on a neighbouring web checkout. It is a separate, explicitly invoked
# gate — see the note printed in SUMMARY below and
# docs/container/A7-server-web-release-pair.md.
#
# Issue #153-LTV-R9: the Live TV hostile-control grader proves itself here too, host-side, after the
# container stage - ci/hostile-controls/prove-undeclared-reporting.py and
# ci/hostile-controls/prove-schema-lockdown.py. Both are unconditional and both are folded into the
# exit status. See the banner near the end of this script for what each one replays.
#
# Usage:
#   ./ci/run.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# shellcheck source-path=SCRIPTDIR
# shellcheck source=lib/clean-artifacts.sh
source "$REPO_ROOT/ci/lib/clean-artifacts.sh"

IMAGE_TAG="tesserafin-ci"
NUGET_VOLUME="tesserafin-nuget"
SOLUTION="Tesserafin.sln"

banner() {
    echo ""
    echo "======================================================================"
    echo "== $*"
    echo "======================================================================"
}

# ── THE W1 RETENTION TRUST ROOT (#236, W1-A4-R4) ────────────────────────────
#
# THIS SCRIPT IS THE TRUST ROOT FOR THE W1 RETENTION CONTRACT, and these three
# constants are the part of it that no other file may restate. They are the
# THIRD party to an agreement that used to have two.
#
# Until R4 the expected roster lived in exactly two places: the orchestrator
# `ci/windows/runtime-retention/retention_gates.py`, which declares what it
# runs, and `ci/windows/verify-retention-gate-pinned.py`, which froze what it
# must run. Independent review (R3, finding F3) showed that is a bilateral
# agreement and can be weakened on both sides at once: delete a member from the
# orchestrator, delete the matching entry from the verifier's frozen tuple, and
# every check still agrees with every other check.
#
# The roster is now a versioned canonical manifest — a data file, outside both
# of those — and the values that AUTHENTICATE that manifest are here:
#
#   W1A4_ROSTER_MANIFEST_SHA256  the manifest's exact content digest.
#   W1A4_ROSTER_IDS              the fourteen member identities, in order.
#   W1A4_ROSTER_PROPERTIES       every named property a member must still
#                                REPORT, in manifest order (W1-A5-V1-R3).
#
# The third constant is finding B3 of W1-A5-V1-R2. Member identity is not
# member behaviour: `publication_policy.py::check_all` kept its name, its file
# and its position while one deleted line stopped it reporting
# `summary.frozen-prose-drift`, and co-removing the implementation and its
# controls left the roster passing 14/14. The manifest now records which
# properties each member owes, the verifier DEMONSTRATES each one by calling
# the member against a witness violation, and the set of obligations is pinned
# here so that dropping one is an edit to this file rather than to the subtree
# that owes it.
#
# The verifier CONSUMES the manifest and READS these two constants out of this
# file. It cannot redefine either, because neither is written down where it can
# reach. Editing the manifest without editing this file is a digest mismatch;
# editing both is an edit to ci/run.sh, which is the merge gate for every
# branch and is reviewed as one.
#
# Nothing pins ci/run.sh in turn, and nothing should: a chain of scripts each
# pinning the next has no last link. That is the trust boundary, and it is
# stated here rather than hidden behind one more file.
W1A4_ROSTER_MANIFEST="ci/windows/w1a4-roster-manifest.v1.json"
W1A4_ROSTER_MANIFEST_SHA256="312d1eceaf5690fb57caa7e65b9a72c01f86c07ff3dde1f5b054743966a1b841"
W1A4_ROSTER_IDS=(
    accepted-contract
    deterministic-layout
    publication-policy
    excluded-subtree-ownership
    proof-trigger
    registry-authority
    ownership-self-proof
    publication-self-proof
    reusable-workflow-self-proof
    trusted-source-self-proof
    reference-grammar-self-proof
    hostile-controls-self-proof
    hostile-controls-ablation
    gate-roster-self-proof
)
W1A4_ROSTER_PROPERTIES=(
    summary.frozen-prose-drift
    publication.frozen-workflow-drift
)

# WHY THIS BLOCK IS HERE, AT THE TOP, AND NOT NEXT TO THE OTHER SELF-PROOFS.
#
# R3 finding F4: the block used to sit near the end of this script, after
# `STATUS=$?`, and the only thing that proved it ran was a control that
# EXTRACTED it and executed the extracted text on its own. Extracting a block
# and running it separately proves the block works. It proves nothing about
# whether ci/run.sh reaches it — moving the intact block below `exit "$STATUS"`
# passed that control unchanged.
#
# So the block is now the FIRST thing this script executes after its prologue.
# Only comments, blank lines, simple assignments, `cd`, `source` and closed
# function definitions may precede it, and `verify-retention-gate-pinned.py`
# refuses this file if anything else does. Nothing before it can exit, return,
# disable errexit, open a conditional, open a subshell or dispatch a test, so
# there is no reachable path through this script that skips it.
#
# It is fail-closed by `exit 1`, not by folding into $STATUS: $STATUS does not
# exist yet, and an accumulated status can be reassigned by a later stage.
#
# The interpreter is absolute and the environment is emptied by `/usr/bin/env
# -i` before python3 starts, so neither a shell function named `python3`, nor
# PATH, nor BASH_ENV, nor any PYTHON* variable inherited from the caller can
# decide what actually runs.
#
# `gate-roster-controls.py` proves both halves: STRUCTURALLY, that this block
# is top-level and unconditional and that nothing precedes it; and DYNAMICALLY,
# by running this script with a stubbed verifier and requiring the sentinel
# below to appear, the run to fail, and the build stage never to be reached.
# Keep the markers.
# >>> W1A4-PIN-BLOCK
banner "The W1 retention gate command, roster and anchor are pinned (#236, W1-A4-R4)"
echo "W1A4-PIN-BLOCK-REACHED"
W1A4_ANCHOR_STATUS=0
W1A4_MANIFEST_ACTUAL="$(/usr/bin/sha256sum "$REPO_ROOT/$W1A4_ROSTER_MANIFEST" | /usr/bin/cut -d' ' -f1)"
if [ "$W1A4_MANIFEST_ACTUAL" != "$W1A4_ROSTER_MANIFEST_SHA256" ]; then
    echo "W1-A4 ANCHOR HARD STOP: $W1A4_ROSTER_MANIFEST hashes to ${W1A4_MANIFEST_ACTUAL};" >&2
    echo "this file pins ${W1A4_ROSTER_MANIFEST_SHA256}. The canonical roster manifest was" >&2
    echo "changed without the authority that authenticates it." >&2
    W1A4_ANCHOR_STATUS=1
fi
if ! /usr/bin/env -i         PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin         HOME="$HOME" LC_ALL=C TZ=UTC         /usr/bin/python3 "$REPO_ROOT/ci/windows/verify-retention-gate-pinned.py"; then
    W1A4_ANCHOR_STATUS=1
fi
if [ "$W1A4_ANCHOR_STATUS" -ne 0 ]; then
    echo "The W1 retention gate command, roster or anchor is not pinned; this gate FAILS." >&2
    exit 1
fi
echo "W1A4-PIN-BLOCK-PASSED"
# <<< W1A4-PIN-BLOCK

START_TS=$(date +%s)

banner "Building ${IMAGE_TAG} from Dockerfile.ci"
# Build against an empty context: the image needs no repo files baked in
# (the repo is bind-mounted below), so building with the repo root as
# context would just tar up the whole working tree for nothing.
BUILD_CTX="$(mktemp -d)"
trap 'rm -rf "$BUILD_CTX"' EXIT
docker build -f Dockerfile.ci -t "$IMAGE_TAG" "$BUILD_CTX"

banner "Purging every bin/ and obj/ before the first compilation"
# Deliberately OUTSIDE the `set +e` region below: a cleanup failure must abort
# the gate before anything is compiled, not be folded into $STATUS and
# reported alongside build output. `set -e` is still in force here, and
# ci_clean_artifacts asserts from the host that nothing survived, so a purge
# that reports success without emptying the bind mount is still fatal.
# This is why the gate no longer depends on the caller purging by hand: a
# warm obj/ makes MSBuild skip projects, and a skipped project skips its
# Roslyn analyzers.
ci_clean_artifacts "$REPO_ROOT" "$IMAGE_TAG"
echo "No bin/ or obj/ directory remains under $REPO_ROOT."

banner "dotnet build + dotnet test (${SOLUTION}, full suite) inside container"
set +e
docker run --rm \
    -v "$PWD":/repo \
    -v "${NUGET_VOLUME}":/nuget \
    -w /repo \
    "$IMAGE_TAG" \
    bash -c "
        set -euo pipefail
        echo '-- dotnet restore --'
        dotnet restore '${SOLUTION}'
        echo '-- dotnet build (fail fast on errors, non-incremental) --'
        # --no-incremental is the second belt here, not the first. The repo is bind-mounted, so
        # obj/ survives between runs and across host-side builds - including ./ci/openapi-generate.sh,
        # which every PR touching a serialized surface is required to run FIRST. With a warm obj/,
        # MSBuild considers a project up to date and skips it, and skipping a project skips its
        # Roslyn analyzers: the gate then prints PASS for a tree that fails from a cold build.
        # That is not hypothetical - PRs #46 and #45 were both merged on a PASS obtained this way,
        # and the resulting master failed CA1034 on the first clean checkout.
        # The artifact purge above is what actually guarantees the cold state (#94); this flag
        # keeps the build honest even if a stage between the purge and here writes into obj/.
        dotnet build '${SOLUTION}' --no-restore --no-incremental -clp:ErrorsOnly
        echo '-- dotnet test (full suite, excluding the optional PR115d smoke stage) --'
        dotnet test --solution '${SOLUTION}' --no-build --filter 'Category!=Smoke' --no-progress
    "
STATUS=$?
set -e

# The container runs as root (the dotnet SDK image default) and the repo is
# bind-mounted read-write, so bin/ and obj/ directories written back into the
# working tree end up root-owned. They're gitignored so this never affects
# `git status`/commits, but a root-owned obj/ WILL make the *next* host-side
# (or container-side) `dotnet build` fail with "Access denied" — so hand
# ownership of the working tree back to the invoking user regardless of
# whether the run above passed or failed. This is what makes ci/run.sh safe
# to run repeatedly against the same checkout.
banner "Restoring working tree ownership to $(id -u):$(id -g)"
docker run --rm -v "$PWD":/repo "$IMAGE_TAG" chown -R "$(id -u):$(id -g)" /repo || true

banner "Hostile-control grader self-proofs (#153-LTV-R7, #153-LTV-R9)"
# Issue #153-LTV-R9: these two probes are the ONLY thing that proves the Live TV hostile-control
# grader can still fail. Until R9 they existed in the tree and no permanent gate ran them, which is
# the same defect one level up as the one they exist to close: evidence nobody replays.
#
#   prove-undeclared-reporting.py  runs cc-10 twice against the same tree - once through run.py as
#                                  committed, once through a copy whose ONE mutated line drops the
#                                  undeclared failures from the classification - and requires the
#                                  second to FAIL while the grade stays RED. Remove the reporting
#                                  #153-LTV-R6 finding R6-1 added and this goes red.
#   prove-schema-lockdown.py       replays the eight situations of #153-LTV-R9 step 3: the R8 opt-out
#                                  at either value, `expectUndeclaredFailures` on a roster line, any
#                                  unknown key, that same list moved off cc-10, and cc-10's list
#                                  amputated / padded / perturbed. All eight must be refused.
#
# They run on the HOST, not in the container: run.py creates its own detached git worktree from HEAD
# and builds it with the host SDK, so a container would need the repository's git directory and a
# second SDK for nothing. They mutate nothing in this checkout - every build happens under a
# temporary worktree in $TMPDIR, which they remove - and they are graded against HEAD, so an
# uncommitted change to ci/hostile-controls is not what they measure.
#
# There is deliberately no flag, environment variable or condition that skips either one: a gate the
# caller can turn off is the finding, not the fix. Their exit status is folded into $STATUS below.
set +e
python3 "$REPO_ROOT/ci/hostile-controls/prove-undeclared-reporting.py"
PROBE_UNDECLARED_STATUS=$?
python3 "$REPO_ROOT/ci/hostile-controls/prove-schema-lockdown.py"
PROBE_SCHEMA_STATUS=$?
set -e

echo ""
echo "prove-undeclared-reporting.py exit ${PROBE_UNDECLARED_STATUS}"
echo "prove-schema-lockdown.py exit ${PROBE_SCHEMA_STATUS}"
if [ "$PROBE_UNDECLARED_STATUS" -ne 0 ] || [ "$PROBE_SCHEMA_STATUS" -ne 0 ]; then
    echo "The hostile-control grader did not prove itself; this gate FAILS." >&2
    STATUS=1
fi
END_TS=$(date +%s)
ELAPSED=$((END_TS - START_TS))

banner "SUMMARY"
if [ "$STATUS" -eq 0 ]; then
    echo "RESULT: PASS — build succeeded, full test suite green (${ELAPSED}s wall time)"
    echo ""
    echo "This gate covers the SERVER ONLY. It says nothing about whether a published"
    echo "image and a tesserafin-web commit are the same release. That is #93 / [A7]:"
    echo ""
    echo "  ci/verify-release-pair.sh --server-image <ref>@sha256:<digest> \\"
    echo "      --server-source <40-char commit> \\"
    echo "      --web-repo <path to tesserafin-web> --web-source <40-char commit>"
    echo ""
    echo "Run it explicitly before a release. Neither gate is hosted CI (#62, #94)."
else
    echo "RESULT: FAIL — see the first failing stage above (dotnet restore/build/test) (${ELAPSED}s wall time)" >&2
fi

exit "$STATUS"
