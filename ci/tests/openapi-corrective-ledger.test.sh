#!/usr/bin/env bash
#
# Break controls for the one-shot corrective-transition ledger (#226, PR #227;
# finding kinds, tesserafin#129).
#
# A ledger that has never been observed REFUSING is not evidence that it can
# refuse. Every case below mutates one thing about an otherwise-accepted
# transition and requires the gate to stop accepting it. A control that
# unexpectedly passes fails this suite.
#
# Two layers:
#
#   consumer  ci/openapi-corrective-ledger.sh driven directly with synthetic
#             findings. No docker, no engine — these isolate the matching rules
#             from everything else, so a failure names exactly one cause.
#   gate      ci/openapi-compat.sh end to end, which is the only place that can
#             prove "a consumer that cannot run is an error, not an exemption"
#             and "no breaking finding means the ledger is never reached".
#
# Every mutation happens in a throwaway tree. The tracked ledger is copied,
# never edited.
#
# Usage:
#   ./ci/tests/openapi-corrective-ledger.test.sh          # consumer controls only
#   RUN_GATE_CONTROLS=1 ./ci/tests/openapi-corrective-ledger.test.sh
#
# The gate layer needs docker and the real contracts; it is opt-in so the
# matching rules stay testable on a machine without an engine.

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONSUMER="$REPO_ROOT/ci/openapi-corrective-ledger.sh"
LEDGER="$REPO_ROOT/ci/openapi-corrective-transition.json"
COMPAT="$REPO_ROOT/ci/openapi-compat.sh"

PASS=0
FAIL=0
ok()  { echo "  PASS: $*"; PASS=$((PASS + 1)); }
bad() { echo "  FAIL: $*" >&2; FAIL=$((FAIL + 1)); }

command -v jq >/dev/null 2>&1 || { echo "jq is required" >&2; exit 1; }
[ -f "$CONSUMER" ] || { echo "missing $CONSUMER" >&2; exit 1; }
[ -f "$LEDGER" ]   || { echo "missing $LEDGER" >&2; exit 1; }

WORK="$(mktemp -d "${TMPDIR:-/tmp}/tesserafin-corrective-controls.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

BASE_SHA="$(jq -r '.transition.baselineSha256'  "$LEDGER")"
HEAD_SHA="$(jq -r '.transition.candidateSha256' "$LEDGER")"

echo "Corrective-transition ledger break controls"
echo "  fixture tree: $WORK"
echo "  baseline  $BASE_SHA"
echo "  candidate $HEAD_SHA"
echo

# The findings the accepted transition actually produces, rebuilt from the
# ledger's own oasdiff identity fields so this suite has no second copy of the
# truth to drift from.
ACCEPTED_FINDINGS="$WORK/accepted-findings.json"
jq '[.transition.findings[] | {
        id: .ruleId, text, level, operation, operationId, path, section, fingerprint
     }]' "$LEDGER" > "$ACCEPTED_FINDINGS"

# expect <exit> <description> <ledger> <findings> <base-sha> <head-sha>
CASE_LOG=""
expect() {
    local want="$1" desc="$2" ledger="$3" findings="$4" base="$5" head="$6"
    local got=0
    CASE_LOG="$("$CONSUMER" --ledger "$ledger" --findings "$findings" \
                            --base-sha "$base" --head-sha "$head" 2>&1)" || got=$?
    if [ "$got" -eq "$want" ]; then
        ok "$desc (exit $got)"
    else
        bad "$desc — expected exit $want, got $got"
        printf '%s\n' "$CASE_LOG" | sed 's/^/        /' >&2
    fi
}

# ── The positive control ──────────────────────────────────────────────────
# Without this, every RED case below could be passing for the wrong reason.

echo "-- the accepted transition itself --"
expect 0 "the exact recorded transition is consumed" \
    "$LEDGER" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

if ! grep -q 'CORRECTIVE TRANSITION APPLIES' <<< "$CASE_LOG"; then
    bad "the accepted transition did not announce itself"
else
    ok "the accepted transition announces itself"
fi

ACCEPTED_COUNT="$(jq -r 'length' "$ACCEPTED_FINDINGS")"
VISIBLE="$("$CONSUMER" --ledger "$LEDGER" --findings "$ACCEPTED_FINDINGS" \
            --base-sha "$BASE_SHA" --head-sha "$HEAD_SHA" 2>&1 | grep -c '^  ACCEPTED ')"
if [ "$VISIBLE" -eq "$ACCEPTED_COUNT" ]; then
    ok "every accepted finding is printed visibly ($VISIBLE of $ACCEPTED_COUNT)"
else
    bad "only $VISIBLE of $ACCEPTED_COUNT accepted findings were printed"
fi

# ── A sixth finding ───────────────────────────────────────────────────────

echo "-- an added finding --"
jq '. + [{
      id: "request-parameter-removed", text: "a totally different finding",
      level: 3, operation: "GET", operationId: "GetSomethingElse",
      path: "/Some/Other/Path", section: "paths", fingerprint: "ffffffffffff"
    }]' "$ACCEPTED_FINDINGS" > "$WORK/sixth.json"
expect 1 "a sixth breaking finding is refused" \
    "$LEDGER" "$WORK/sixth.json" "$BASE_SHA" "$HEAD_SHA"

# A sixth finding that duplicates an accepted one must not slip through on a
# count check alone.
jq '. + [.[0]]' "$ACCEPTED_FINDINGS" > "$WORK/sixth-dup.json"
expect 1 "a sixth finding duplicating an accepted one is refused" \
    "$LEDGER" "$WORK/sixth-dup.json" "$BASE_SHA" "$HEAD_SHA"

echo "-- a removed finding --"
jq '.[1:]' "$ACCEPTED_FINDINGS" > "$WORK/five-minus-one.json"
expect 1 "a missing accepted finding is refused" \
    "$LEDGER" "$WORK/five-minus-one.json" "$BASE_SHA" "$HEAD_SHA"

# ── Operation / path / rule-id mismatches ─────────────────────────────────

echo "-- changed finding identity --"
jq '.[0].path = "/Playback/Sessions/{sessionId}"' "$ACCEPTED_FINDINGS" > "$WORK/path.json"
expect 1 "a path mismatch is refused" \
    "$LEDGER" "$WORK/path.json" "$BASE_SHA" "$HEAD_SHA"

jq '.[0].operation = "PATCH"' "$ACCEPTED_FINDINGS" > "$WORK/operation.json"
expect 1 "an operation mismatch is refused" \
    "$LEDGER" "$WORK/operation.json" "$BASE_SHA" "$HEAD_SHA"

jq '.[0].operationId = "DeleteSomethingElse"' "$ACCEPTED_FINDINGS" > "$WORK/operationid.json"
expect 1 "an operationId mismatch is refused" \
    "$LEDGER" "$WORK/operationid.json" "$BASE_SHA" "$HEAD_SHA"

jq '.[0].id = "request-parameter-max-length-decreased"' "$ACCEPTED_FINDINGS" > "$WORK/ruleid.json"
expect 1 "a changed rule id is refused" \
    "$LEDGER" "$WORK/ruleid.json" "$BASE_SHA" "$HEAD_SHA"

jq '.[0].text = "for the `path` request parameter `id`, the `type/format` was changed from `any` to `string/date`"' \
    "$ACCEPTED_FINDINGS" > "$WORK/text.json"
expect 1 "a changed finding text is refused" \
    "$LEDGER" "$WORK/text.json" "$BASE_SHA" "$HEAD_SHA"

jq '.[0].fingerprint = "aaaaaaaaaaaa"' "$ACCEPTED_FINDINGS" > "$WORK/fingerprint.json"
expect 1 "a changed fingerprint is refused" \
    "$LEDGER" "$WORK/fingerprint.json" "$BASE_SHA" "$HEAD_SHA"

# ── Digest scoping — the property that makes this one-shot ────────────────

echo "-- digest scoping --"
expect 1 "a different baseline digest is refused" \
    "$LEDGER" "$ACCEPTED_FINDINGS" \
    "0000000000000000000000000000000000000000000000000000000000000000" "$HEAD_SHA"

expect 1 "a different candidate digest is refused" \
    "$LEDGER" "$ACCEPTED_FINDINGS" "$BASE_SHA" \
    "1111111111111111111111111111111111111111111111111111111111111111"

# THE one-shot property: once the candidate is the committed baseline, the very
# next transition away from it cannot reuse this ledger.
expect 1 "the transition cannot be re-consumed once the candidate is the baseline" \
    "$LEDGER" "$ACCEPTED_FINDINGS" "$HEAD_SHA" \
    "2222222222222222222222222222222222222222222222222222222222222222"

# ── Broad wildcards are rejected, not merely unused ───────────────────────

echo "-- wildcard and pattern rejection --"
jq '.transition.findings[0].path = "/Playback/Sessions/*"' "$LEDGER" > "$WORK/wildcard-path.json"
expect 2 "a wildcard in a path is rejected" \
    "$WORK/wildcard-path.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

jq '.transition.findings[0].ruleId = ".*"' "$LEDGER" > "$WORK/wildcard-rule.json"
expect 2 "a regular expression in a rule id is rejected" \
    "$WORK/wildcard-rule.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

jq '.transition.findings[0].operation = "GET|PUT|DELETE"' "$LEDGER" > "$WORK/wildcard-alt.json"
expect 2 "an alternation in an operation is rejected" \
    "$WORK/wildcard-alt.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

jq '.transition.findings[0] += {pathPattern: "/Playback/Sessions/"}' "$LEDGER" > "$WORK/pattern-key.json"
expect 2 "a pattern-matching key is rejected" \
    "$WORK/pattern-key.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

jq '.transition.findings[0] += {ruleIdRegex: "request-parameter-"}' "$LEDGER" > "$WORK/regex-key.json"
expect 2 "a regex key is rejected" \
    "$WORK/regex-key.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

# ── A ledger that is not one reviewed transition ──────────────────────────

echo "-- ledger integrity --"
jq '.transition.findingCount = 4' "$LEDGER" > "$WORK/miscount.json"
expect 2 "a findingCount that disagrees with the list is rejected" \
    "$WORK/miscount.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

jq '. + {transitions: []}' "$LEDGER" > "$WORK/collection.json"
expect 2 "a growable transitions collection is rejected" \
    "$WORK/collection.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

jq '.transition.serverBindingUnchanged = false' "$LEDGER" > "$WORK/binding.json"
expect 2 "a ledger not asserting unchanged server binding is rejected" \
    "$WORK/binding.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

jq '.transition.runtimeWireBreaks = 1' "$LEDGER" > "$WORK/wirebreak.json"
expect 2 "a ledger admitting a runtime wire break is rejected" \
    "$WORK/wirebreak.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

# ── Finding kinds ─────────────────────────────────────────────────────────
# Each kind requires its own human-reviewable description; a finding cannot
# claim a kind it does not document, nor a kind its rule does not allow.

echo "-- finding kinds --"
jq 'del(.transition.findings[0].kind)' "$LEDGER" > "$WORK/nokind.json"
expect 2 "a finding without a kind is rejected" \
    "$WORK/nokind.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

jq '.transition.findings[0].kind = "anything-goes"' "$LEDGER" > "$WORK/badkind.json"
expect 2 "an unknown kind is rejected" \
    "$WORK/badkind.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

# A parameter-shape finding must describe the parameter it corrects.
jq '.transition.findings[0].kind = "parameter-shape"' "$LEDGER" > "$WORK/noparam.json"
expect 2 "a parameter-shape finding without a parameter name and shapes is rejected" \
    "$WORK/noparam.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

for field in schema property unusedEvidence; do
    jq --arg f "$field" '.transition.findings[0] |= del(.[$f])' "$LEDGER" > "$WORK/no-$field.json"
    expect 2 "an inert-property-removal finding without its $field is rejected" \
        "$WORK/no-$field.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"
done

# The kind is for property removals only: an operation removal cannot ride it.
jq '.transition.findings[0].ruleId = "api-path-removed-without-deprecation"' "$LEDGER" > "$WORK/kind-rule.json"
expect 2 "an inert-property-removal finding on a non-property rule is rejected" \
    "$WORK/kind-rule.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

# The recorded property must be the one the finding's own text removes.
jq '.transition.findings[0].property = "SomethingElse"' "$LEDGER" > "$WORK/kind-prop.json"
expect 2 "an inert-property-removal finding naming another property than its text is rejected" \
    "$WORK/kind-prop.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

jq 'del(.transition.clientsVerified)' "$LEDGER" > "$WORK/noclients.json"
expect 2 "a property-removing transition without verified clients is rejected" \
    "$WORK/noclients.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

jq '.transition.clientsVerified = []' "$LEDGER" > "$WORK/emptyclients.json"
expect 2 "a property-removing transition with an empty client list is rejected" \
    "$WORK/emptyclients.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

# A metacharacter in an identity field still widens nothing: it is rejected.
jq '.transition.findings[0].property = "Cast.*"' "$LEDGER" > "$WORK/kind-wild.json"
expect 2 "a wildcard in a removed property name is rejected" \
    "$WORK/kind-wild.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

jq '.schemaVersion = 99' "$LEDGER" > "$WORK/schema.json"
expect 2 "an unsupported schemaVersion is rejected" \
    "$WORK/schema.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

printf 'not json' > "$WORK/garbage.json"
expect 2 "an unparsable ledger is rejected" \
    "$WORK/garbage.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

# An ABSENT ledger is not an error — it means nothing is recorded, so nothing
# can be consumed and the caller keeps its breaking verdict.
expect 1 "an absent ledger declines rather than erroring" \
    "$WORK/does-not-exist.json" "$ACCEPTED_FINDINGS" "$BASE_SHA" "$HEAD_SHA"

# ── The gate layer ────────────────────────────────────────────────────────

if [ "${RUN_GATE_CONTROLS:-0}" != "1" ]; then
    echo
    echo "-- gate controls skipped (set RUN_GATE_CONTROLS=1 to run; needs docker) --"
else
    echo
    echo "-- gate: a consumer that cannot run is an error, never an exemption --"

    command -v docker >/dev/null 2>&1 || { echo "docker is required for the gate controls" >&2; exit 1; }
    command -v git    >/dev/null 2>&1 || { echo "git is required for the gate controls" >&2; exit 1; }

    # The two real contracts this transition names, taken from git so the
    # controls compare exactly the documents the ledger claims.
    G="$WORK/gate"; mkdir -p "$G"
    BASE_COMMIT="$(git -C "$REPO_ROOT" log --format=%H --all -1 \
        --grep='^\[CI\] Finalize the ContentPack SDK provenance pair' 2>/dev/null)"
    resolve_by_digest() {
        # Find a commit whose openapi/openapi.json hashes to $1, walking HEAD's history.
        local want="$1" c
        while read -r c; do
            if [ "$(git -C "$REPO_ROOT" show "$c:openapi/openapi.json" 2>/dev/null | sha256sum | cut -d' ' -f1)" = "$want" ]; then
                printf '%s' "$c"; return 0
            fi
        done < <(git -C "$REPO_ROOT" rev-list HEAD --max-count=40 -- openapi/openapi.json)
        return 1
    }
    BASE_COMMIT="$(resolve_by_digest "$BASE_SHA")" \
        || { echo "cannot find a commit carrying baseline $BASE_SHA" >&2; exit 1; }
    HEAD_COMMIT="$(resolve_by_digest "$HEAD_SHA")" \
        || { echo "cannot find a commit carrying candidate $HEAD_SHA" >&2; exit 1; }
    echo "  baseline  contract from $BASE_COMMIT"
    echo "  candidate contract from $HEAD_COMMIT"

    git -C "$REPO_ROOT" show "$BASE_COMMIT:openapi/openapi.json"       > "$G/base.json"
    git -C "$REPO_ROOT" show "$BASE_COMMIT:openapi/contract.lock.json" > "$G/base.lock.json"
    git -C "$REPO_ROOT" show "$HEAD_COMMIT:openapi/openapi.json"       > "$G/head.json"
    git -C "$REPO_ROOT" show "$HEAD_COMMIT:openapi/contract.lock.json" > "$G/head.lock.json"

    GATE_LOG=""
    gate_expect() {
        local want="$1" desc="$2"; shift 2
        local got=0
        GATE_LOG="$("$COMPAT" "$@" 2>&1)" || got=$?
        if [ "$got" -eq "$want" ]; then
            ok "$desc (exit $got)"
        else
            bad "$desc — expected exit $want, got $got"
            printf '%s\n' "$GATE_LOG" | tail -20 | sed 's/^/        /' >&2
        fi
    }

    # Exit 127 — the consumer is not there at all.
    gate_expect 2 "a missing ledger consumer is INDETERMINATE, not a pass" \
        --base "$G/base.json" --base-lock "$G/base.lock.json" \
        --head "$G/head.json" --head-lock "$G/head.lock.json" \
        --report "$G/g127.md" --corrective-consumer "$G/no-such-consumer.sh"

    # Exit 126 — the consumer is present but not executable.
    printf '#!/usr/bin/env bash\nexit 0\n' > "$G/not-executable.sh"
    chmod a-x "$G/not-executable.sh"
    gate_expect 2 "a non-executable ledger consumer is INDETERMINATE, not a pass" \
        --base "$G/base.json" --base-lock "$G/base.lock.json" \
        --head "$G/head.json" --head-lock "$G/head.lock.json" \
        --report "$G/g126.md" --corrective-consumer "$G/not-executable.sh"

    # A consumer that returns an unclassifiable status is not a verdict either.
    printf '#!/usr/bin/env bash\nexit 42\n' > "$G/weird.sh"; chmod +x "$G/weird.sh"
    gate_expect 2 "an unclassifiable consumer status is INDETERMINATE" \
        --base "$G/base.json" --base-lock "$G/base.lock.json" \
        --head "$G/head.json" --head-lock "$G/head.lock.json" \
        --report "$G/g42.md" --corrective-consumer "$G/weird.sh"

    # With NO ledger recorded, the very same comparison stays BREAKING. This is
    # the control proving the transition — not some other change — is what makes
    # the accepted run green.
    gate_expect 1 "without the ledger the same comparison is still BREAKING" \
        --base "$G/base.json" --base-lock "$G/base.lock.json" \
        --head "$G/head.json" --head-lock "$G/head.lock.json" \
        --report "$G/gnoledger.md" --corrective-ledger "$G/absent-ledger.json"

    echo "-- gate: the accepted transition, end to end --"
    gate_expect 0 "the recorded transition makes the real comparison COMPATIBLE" \
        --base "$G/base.json" --base-lock "$G/base.lock.json" \
        --head "$G/head.json" --head-lock "$G/head.lock.json" \
        --report "$G/gaccept.md"
    if grep -q 'CORRECTIVE TRANSITION' <<< "$GATE_LOG"; then
        ok "the accepted run names the corrective transition in its verdict"
    else
        bad "the accepted run did not name the corrective transition"
    fi

    # ── The corrected contract against ITSELF ─────────────────────────────
    # It must pass on its own merits, WITHOUT reaching the ledger at all. An
    # exit of 0 alone would not prove that, so assert the ledger was never
    # consumed — and prove the assertion is meaningful by pointing the run at a
    # consumer that would explode if it were ever invoked.
    echo "-- gate: the corrected contract compared with itself --"
    printf '#!/usr/bin/env bash\necho "CONSUMER WAS INVOKED" >&2\nexit 42\n' > "$G/tripwire.sh"
    chmod +x "$G/tripwire.sh"
    gate_expect 0 "the corrected contract versus itself is COMPATIBLE" \
        --base "$G/head.json" --base-lock "$G/head.lock.json" \
        --head "$G/head.json" --head-lock "$G/head.lock.json" \
        --report "$G/gself.md" --corrective-consumer "$G/tripwire.sh"

    if grep -q 'CONSUMER WAS INVOKED' <<< "$GATE_LOG"; then
        bad "the self-comparison reached the ledger consumer; it must never be consulted with no breaking finding"
    else
        ok "the self-comparison never reached the ledger consumer"
    fi
    if grep -q 'CORRECTIVE TRANSITION' <<< "$GATE_LOG"; then
        bad "the self-comparison consumed a corrective transition"
    else
        ok "the self-comparison consumed no corrective transition"
    fi

    # The same, with the REAL consumer in place: still no consumption.
    gate_expect 0 "the corrected contract versus itself passes with the real consumer too" \
        --base "$G/head.json" --base-lock "$G/head.lock.json" \
        --head "$G/head.json" --head-lock "$G/head.lock.json" \
        --report "$G/gself2.md"
    if grep -q 'CORRECTIVE TRANSITION' <<< "$GATE_LOG"; then
        bad "the self-comparison consumed a corrective transition with the real consumer"
    else
        ok "the self-comparison consumed nothing with the real consumer either"
    fi

    # After the candidate becomes the committed baseline, a FUTURE breaking
    # change must not be able to reuse this transition. Mutate the candidate
    # into a genuinely breaking successor and require red.
    echo "-- gate: a later transition cannot reuse the ledger --"
    VICTIM="$(jq -r '.paths | keys[0]' "$G/head.json")"
    jq --arg p "$VICTIM" 'del(.paths[$p])' "$G/head.json" > "$G/future.json"
    printf '{\n  "algorithm": "sha256",\n  "sha256": "%s",\n  "spec": "openapi/openapi.json",\n  "version": "1.0.0"\n}\n' \
        "$(sha256sum "$G/future.json" | cut -d' ' -f1)" > "$G/future.lock.json"
    gate_expect 1 "a later breaking change from the corrected baseline is still BREAKING" \
        --base "$G/head.json" --base-lock "$G/head.lock.json" \
        --head "$G/future.json" --head-lock "$G/future.lock.json" \
        --report "$G/gfuture.md"
fi

echo
echo "corrective-ledger controls: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ] || exit 1
exit 0
