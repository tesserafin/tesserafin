#!/usr/bin/env bash
#
# One-shot corrective-transition ledger consumer — issue #226, PR #227; finding
# kinds added for tesserafin#129 (inert property removals, owner ruling 2026-11-28).
#
# WHAT THIS IS NOT. It is not a waiver, not an ignore list, not a severity
# override and not a bypass. It cannot lower a check: `ci/openapi-severity-levels.txt`
# is untouched and `ci/openapi-compat.sh` still runs the ordinary pinned oasdiff
# command with no exclusions and keeps its complete structured output. This
# script only ever answers one question, after the fact, about findings that
# have already been produced and recorded in full:
#
#   are these findings EXACTLY the set a named maintainer ruling accepted, for
#   EXACTLY the two named contract digests?
#
# Anything other than "yes, all of them, verbatim" leaves the gate red.
#
# ONE-SHOT BY CONSTRUCTION. The ledger names both digests. Once the candidate
# becomes the committed baseline, `baselineSha256` matches no future comparison,
# so a later pull request can neither need this transition nor silently inherit
# it. There is no wildcard, no regex and no "future findings" clause — those
# shapes are actively rejected below rather than merely unused.
#
# SEPARATE EXECUTABLE ON PURPOSE. `ci/openapi-compat.sh` invokes this as a
# child process and treats EVERY unexpected status — including 126 (not
# executable) and 127 (not found) — as indeterminate, never as "no exception
# applied". A ledger consumer that cannot run must not look like a ledger
# consumer that declined.
#
# Exit codes:
#   0  the transition applies and every observed finding is accounted for
#   1  the transition does not apply (digest mismatch, or the finding set
#      differs by even one added, removed or changed finding)
#   2  the question was not answered (bad invocation, missing tool, malformed
#      or illegitimate ledger)
#
# Usage:
#   ci/openapi-corrective-ledger.sh --ledger <ledger.json> --findings <findings.json> \
#       --base-sha <sha256> --head-sha <sha256>

set -uo pipefail

EXIT_APPLIES=0
EXIT_DOES_NOT_APPLY=1
EXIT_INDETERMINATE=2

LEDGER=""
FINDINGS=""
BASE_SHA=""
HEAD_SHA=""

indeterminate() { printf 'corrective-ledger: INDETERMINATE: %s\n' "$1" >&2; exit "$EXIT_INDETERMINATE"; }
declines()      { printf 'corrective-ledger: DOES NOT APPLY: %s\n' "$1" >&2; exit "$EXIT_DOES_NOT_APPLY"; }
note()          { printf 'corrective-ledger: %s\n' "$1"; }

usage() {
    cat >&2 <<'EOF'
Usage: ci/openapi-corrective-ledger.sh --ledger PATH --findings PATH --base-sha SHA --head-sha SHA

Exit codes: 0 applies, 1 does not apply, 2 question not answered.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --ledger)   LEDGER="${2:-}";   shift 2 || indeterminate "--ledger needs a value" ;;
        --findings) FINDINGS="${2:-}"; shift 2 || indeterminate "--findings needs a value" ;;
        --base-sha) BASE_SHA="${2:-}"; shift 2 || indeterminate "--base-sha needs a value" ;;
        --head-sha) HEAD_SHA="${2:-}"; shift 2 || indeterminate "--head-sha needs a value" ;;
        -h|--help)  usage; exit "$EXIT_INDETERMINATE" ;;
        *)          usage; indeterminate "unknown argument: $1" ;;
    esac
done

for pair in "ledger:$LEDGER" "findings:$FINDINGS" "base-sha:$BASE_SHA" "head-sha:$HEAD_SHA"; do
    [ -n "${pair#*:}" ] || { usage; indeterminate "missing required argument --${pair%%:*}"; }
done

command -v jq >/dev/null 2>&1 || indeterminate "jq is not available"

# A ledger that is absent is not an error: it means this repository currently
# records no corrective transition, so nothing can be consumed and the caller
# keeps its breaking verdict.
[ -e "$LEDGER" ] || declines "no corrective-transition ledger is recorded at $LEDGER"
[ -f "$LEDGER" ] || indeterminate "the ledger is not an ordinary file: $LEDGER"
[ -r "$LEDGER" ] || indeterminate "the ledger is not readable: $LEDGER"
[ -s "$LEDGER" ] || indeterminate "the ledger is empty: $LEDGER"
[ -f "$FINDINGS" ] && [ -r "$FINDINGS" ] && [ -s "$FINDINGS" ] \
    || indeterminate "the findings file is missing, unreadable or empty: $FINDINGS"

jq -e 'type == "object"' "$LEDGER"   >/dev/null 2>&1 || indeterminate "the ledger is not parsable JSON: $LEDGER"
jq -e 'type == "array"'  "$FINDINGS" >/dev/null 2>&1 || indeterminate "the findings file is not a JSON array: $FINDINGS"

# --- ledger schema -----------------------------------------------------------

SCHEMA_VERSION="$(jq -r '.schemaVersion // empty' "$LEDGER")"
[ "$SCHEMA_VERSION" = "1" ] || indeterminate "unsupported ledger schemaVersion '${SCHEMA_VERSION:-<absent>}' (this consumer implements 1)"

jq -e '(.transition | type) == "object"' "$LEDGER" >/dev/null 2>&1 \
    || indeterminate "the ledger records no \`transition\` object"

# Exactly ONE transition. A ledger that could hold a growing list of accepted
# transitions is a waiver mechanism wearing a different hat.
jq -e '(.transition | type) == "object" and ((.transitions // null) == null)' "$LEDGER" >/dev/null 2>&1 \
    || indeterminate "the ledger declares a \`transitions\` collection; exactly one transition is permitted"

for field in id baselineSha256 candidateSha256 issue pullRequest rationale serverBindingStatement; do
    jq -e --arg f "$field" '(.transition[$f] | type) == "string" and (.transition[$f] | length) > 0' "$LEDGER" >/dev/null 2>&1 \
        || indeterminate "the ledger transition records no non-empty \`$field\`"
done
jq -e '(.transition.engine.version | type) == "string" and (.transition.engine.version | length) > 0' "$LEDGER" >/dev/null 2>&1 \
    || indeterminate "the ledger records no engine version"
jq -e '(.transition.engine.digest | type) == "string" and (.transition.engine.digest | test("^sha256:[0-9a-f]{64}$"))' "$LEDGER" >/dev/null 2>&1 \
    || indeterminate "the ledger records no valid engine digest"
jq -e '.transition.serverBindingUnchanged == true' "$LEDGER" >/dev/null 2>&1 \
    || indeterminate "the ledger does not state that server binding and accepted requests are unchanged"
jq -e '.transition.runtimeWireBreaks == 0' "$LEDGER" >/dev/null 2>&1 \
    || indeterminate "the ledger does not state zero runtime wire breaks"

LEDGER_BASE="$(jq -r '.transition.baselineSha256  // empty' "$LEDGER")"
LEDGER_HEAD="$(jq -r '.transition.candidateSha256 // empty' "$LEDGER")"
for pair in "baselineSha256:$LEDGER_BASE" "candidateSha256:$LEDGER_HEAD"; do
    case "${pair#*:}" in
        [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]) : ;;
        *) indeterminate "the ledger's ${pair%%:*} is not a 64-character lowercase hexadecimal digest" ;;
    esac
done

# --- no wildcard, no regex, no pattern matching ------------------------------
#
# A fingerprint is a literal or it is not a fingerprint. These shapes are
# REJECTED rather than merely unsupported, so a future edit cannot quietly widen
# the ledger into a rule that covers findings nobody reviewed.

if jq -e '[.transition.findings[]? | keys[]] | any(. | ascii_downcase | test("pattern|regex|match|glob|wildcard|prefix|suffix|contains"))' "$LEDGER" >/dev/null 2>&1; then
    indeterminate "the ledger uses a pattern-matching key; a corrective ledger accepts literal values only"
fi

# Scanned on the IDENTITY fields - the ones a pattern could widen. `text` and the
# reviewer-facing prose (`unusedEvidence`, shapes) are compared or printed
# verbatim, never matched, and oasdiff's own texts carry `allOf[#/...]`.
if jq -e '[.transition.findings[]? | (.fingerprint, .ruleId, .section, .operation, .operationId, .path, .parameterIn, .parameterName, .schema, .property) | strings] | any(test("[*?^$|+\\[\\]\\\\]"))' "$LEDGER" >/dev/null 2>&1; then
    indeterminate "the ledger contains a wildcard or regular-expression metacharacter; a corrective ledger accepts literal values only"
fi

# --- declared finding shape --------------------------------------------------

DECLARED_COUNT="$(jq -r '.transition.findingCount // empty' "$LEDGER")"
ACTUAL_LEDGER_COUNT="$(jq -r '.transition.findings | length' "$LEDGER" 2>/dev/null)" \
    || indeterminate "the ledger records no \`findings\` array"
[ "$DECLARED_COUNT" = "$ACTUAL_LEDGER_COUNT" ] \
    || indeterminate "the ledger declares findingCount $DECLARED_COUNT but lists $ACTUAL_LEDGER_COUNT finding(s)"
[ "$ACTUAL_LEDGER_COUNT" -gt 0 ] 2>/dev/null \
    || indeterminate "the ledger lists no finding at all"

# Every finding must carry the full oasdiff identity, and the human-reviewable
# description its KIND requires:
#   parameter-shape         a corrected parameter wire shape (#226): location,
#                           name, before and after shapes;
#   inert-property-removal  a contract property nothing reads any more
#                           (tesserafin#129): the schema, the property and the
#                           evidence that no client or server code uses it. Only
#                           the two property-removal rules may carry this kind.
for field in kind fingerprint ruleId section operation operationId path text; do
    jq -e --arg f "$field" 'all(.transition.findings[]; (.[$f] | type) == "string" and (.[$f] | length) > 0)' "$LEDGER" >/dev/null 2>&1 \
        || indeterminate "at least one ledger finding records no non-empty \`$field\`"
done
jq -e 'all(.transition.findings[]; .kind == "parameter-shape" or .kind == "inert-property-removal")' "$LEDGER" >/dev/null 2>&1 \
    || indeterminate "at least one ledger finding has an unknown kind (parameter-shape | inert-property-removal)"
for field in parameterIn parameterName beforeShape afterShape; do
    jq -e --arg f "$field" 'all(.transition.findings[] | select(.kind == "parameter-shape"); (.[$f] | type) == "string" and (.[$f] | length) > 0)' "$LEDGER" >/dev/null 2>&1 \
        || indeterminate "at least one parameter-shape finding records no non-empty \`$field\`"
done
for field in schema property unusedEvidence; do
    jq -e --arg f "$field" 'all(.transition.findings[] | select(.kind == "inert-property-removal"); (.[$f] | type) == "string" and (.[$f] | length) > 0)' "$LEDGER" >/dev/null 2>&1 \
        || indeterminate "at least one inert-property-removal finding records no non-empty \`$field\`"
done
jq -e 'all(.transition.findings[] | select(.kind == "inert-property-removal"); .ruleId == "response-optional-property-removed" or .ruleId == "request-property-removed")' "$LEDGER" >/dev/null 2>&1 \
    || indeterminate "an inert-property-removal finding names a rule other than a property removal"
# A finding's text must name the very property it claims to remove.
jq -e 'all(.transition.findings[] | select(.kind == "inert-property-removal"); . as $f | ($f.text | split("`")[1] | split("/") | last) == $f.property)' "$LEDGER" >/dev/null 2>&1 \
    || indeterminate "an inert-property-removal finding's text does not name the property it records"
# Removing properties needs the clients that were checked against the removal.
if jq -e 'any(.transition.findings[]; .kind == "inert-property-removal")' "$LEDGER" >/dev/null 2>&1; then
    jq -e '(.transition.clientsVerified | type) == "array" and (.transition.clientsVerified | length) > 0 and all(.transition.clientsVerified[]; type == "string" and length > 0)' "$LEDGER" >/dev/null 2>&1 \
        || indeterminate "a transition removing properties records no \`clientsVerified\`"
fi
jq -e 'all(.transition.findings[]; (.level | type) == "number")' "$LEDGER" >/dev/null 2>&1 \
    || indeterminate "at least one ledger finding records no numeric \`level\`"
# oasdiff gives one fingerprint to a change and repeats it for each media type,
# with a different text: an entry is unique by fingerprint AND text.
jq -e '([.transition.findings[] | [.fingerprint, .text]] | length) == ([.transition.findings[] | [.fingerprint, .text]] | unique | length)' "$LEDGER" >/dev/null 2>&1 \
    || indeterminate "the ledger lists the same finding twice"

# --- does this transition apply to the comparison actually performed? --------

[ "$BASE_SHA" = "$LEDGER_BASE" ] \
    || declines "baseline digest $BASE_SHA is not the recorded corrective baseline $LEDGER_BASE"
[ "$HEAD_SHA" = "$LEDGER_HEAD" ] \
    || declines "candidate digest $HEAD_SHA is not the recorded corrective candidate $LEDGER_HEAD"

# --- exact, complete finding-set equality ------------------------------------
#
# Set equality on the full oasdiff identity of every breaking finding. An added
# finding, a removed finding, or a finding whose rule id, level, section,
# operation, operationId, path or text differs by one character all fail here.

OBSERVED="$(jq -S -c '[.[] | select((.level // 0) >= 3) | {
      fingerprint: (.fingerprint // ""), ruleId: (.id // ""), level: (.level // 0),
      section: (.section // ""), operation: (.operation // ""),
      operationId: (.operationId // ""), path: (.path // ""), text: (.text // "")
    }] | sort_by(.fingerprint, .operation, .path, .text)' "$FINDINGS")" \
    || indeterminate "cannot project the observed findings"

EXPECTED="$(jq -S -c '[.transition.findings[] | {
      fingerprint, ruleId, level, section, operation, operationId, path, text
    }] | sort_by(.fingerprint, .operation, .path, .text)' "$LEDGER")" \
    || indeterminate "cannot project the ledger findings"

OBSERVED_COUNT="$(printf '%s' "$OBSERVED" | jq -r 'length')"
[ "$OBSERVED_COUNT" = "$DECLARED_COUNT" ] \
    || declines "the comparison produced $OBSERVED_COUNT breaking finding(s); the ledger accounts for exactly $DECLARED_COUNT"

if [ "$OBSERVED" != "$EXPECTED" ]; then
    printf 'corrective-ledger: the observed findings are not the accepted set.\n' >&2
    printf 'corrective-ledger: unaccounted observed finding(s):\n' >&2
    jq -n --argjson o "$OBSERVED" --argjson e "$EXPECTED" \
        -r '($o - $e)[] | "  + \(.operation) \(.path)  \(.ruleId)  [\(.fingerprint)]"' >&2
    printf 'corrective-ledger: accepted finding(s) that did not occur:\n' >&2
    jq -n --argjson o "$OBSERVED" --argjson e "$EXPECTED" \
        -r '($e - $o)[] | "  - \(.operation) \(.path)  \(.ruleId)  [\(.fingerprint)]"' >&2
    declines "the finding set differs from the accepted corrective transition"
fi

# --- accepted: print every consumed finding visibly --------------------------

TRANSITION_ID="$(jq -r '.transition.id' "$LEDGER")"
ISSUE="$(jq -r '.transition.issue' "$LEDGER")"
PULL_REQUEST="$(jq -r '.transition.pullRequest' "$LEDGER")"

note "CORRECTIVE TRANSITION APPLIES — $TRANSITION_ID"
note "  baseline   $LEDGER_BASE"
note "  candidate  $LEDGER_HEAD"
note "  ruling     $ISSUE"
note "  pull req   $PULL_REQUEST"
note "  engine     $(jq -r '.transition.engine.version' "$LEDGER") $(jq -r '.transition.engine.digest' "$LEDGER")"
note ""
note "Accepted corrective findings ($DECLARED_COUNT of $DECLARED_COUNT consumed, none outstanding):"
jq -r '.transition.findings[] |
    if .kind == "inert-property-removal" then
      "  ACCEPTED  \(.operation) \(.path)\n            rule       \(.ruleId) (level \(.level), \(.section))\n            operation  \(.operationId)\n            removed    \(.schema).\(.property)\n            unused     \(.unusedEvidence)\n            text       \(.text)\n            fingerprint \(.fingerprint)"
    else
      "  ACCEPTED  \(.operation) \(.path)\n            rule       \(.ruleId) (level \(.level), \(.section))\n            operation  \(.operationId)\n            parameter  in=\(.parameterIn) name=\(.parameterName)\n            before     \(.beforeShape)\n            after      \(.afterShape)\n            fingerprint \(.fingerprint)"
    end' "$LEDGER"
note ""
note "Rationale: $(jq -r '.transition.rationale' "$LEDGER")"
note "Server binding: $(jq -r '.transition.serverBindingStatement' "$LEDGER")"
if jq -e '(.transition.clientsVerified // []) | length > 0' "$LEDGER" >/dev/null 2>&1; then
    note "Clients verified: $(jq -r '.transition.clientsVerified | join("; ")' "$LEDGER")"
fi

exit "$EXIT_APPLIES"
