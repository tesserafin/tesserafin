#!/usr/bin/env bash
# Deterministic controls for the fail-closed ABI gate (#94).
#
# Every control below is a synthetic class library built in a throwaway tree
# under $TMPDIR — never in the repository checkout, and never part of
# Tesserafin.sln, the server build graph or the public ABI. That isolation is
# deliberate: the repository root Directory.Build.props turns all warnings
# into errors and, in Debug, injects the Tesserafin.CodeAnalysis analyzer, so
# a fixture project generated inside the checkout would fail for reasons that
# have nothing to do with ABI compatibility.
#
# The point of these controls is that a green ABI job means something. The
# previous workflow ran ApiCompat with `|| true`, so it stayed green through
# missing files, tool crashes and real breaking changes alike. Each RED case
# here proves one specific way the gate must refuse to pass; each GREEN case
# proves it does not cry wolf.
#
# Usage:
#   ./ci/tests/abi-compat.test.sh
#
# Requires the .NET SDK and the pinned tool manifest (`dotnet tool restore`).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ABI_COMPAT="$REPO_ROOT/ci/abi-compat.sh"
ABI_STAGE="$REPO_ROOT/ci/abi-stage.sh"

# shellcheck source-path=SCRIPTDIR
# shellcheck source=../lib/abi-manifest.sh
source "$REPO_ROOT/ci/lib/abi-manifest.sh"

PASS=0
FAIL=0

ok()  { echo "  PASS: $*"; PASS=$((PASS + 1)); }
bad() { echo "  FAIL: $*" >&2; FAIL=$((FAIL + 1)); }

WORK="$(mktemp -d "${TMPDIR:-/tmp}/tesserafin-abi-controls.XXXXXX")"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

echo "ABI gate controls"
echo "  fixture tree: $WORK"
echo

# ── Fixture construction ──────────────────────────────────────────────────
# Two synthetic assemblies so the manifest has more than one entry and the
# "how many were actually compared" accounting is observable.

new_project() {
    local dir="$1" name="$2"
    mkdir -p "$dir"
    cat > "$dir/$name.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>$name</AssemblyName>
    <RootNamespace>$name</RootNamespace>
    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
  </PropertyGroup>
</Project>
EOF
}

build_variant() {
    local dir="$1" out="$2"; shift 2
    ( cd "$dir" && dotnet build -c Release -o "$out" --nologo -v q "$@" ) > "$WORK/build.log" 2>&1 || {
        echo "fixture build failed for $dir; see $WORK/build.log" >&2
        cat "$WORK/build.log" >&2
        exit 1
    }
}

new_project "$WORK/alpha" Alpha
new_project "$WORK/beta" Beta

cat > "$WORK/beta/Api.cs" <<'EOF'
namespace Beta;

public class Widget
{
    public int Size() => 1;
}
EOF
build_variant "$WORK/beta" "$WORK/out/beta"

alpha_source_baseline() {
    cat > "$WORK/alpha/Api.cs" <<'EOF'
namespace Alpha;

public class Thing
{
    public int Keep() => 1;
    public int Drop() => 2;
}
EOF
}

alpha_source_baseline
build_variant "$WORK/alpha" "$WORK/out/alpha-base"

# Additive: one extra public member and one extra public type.
cat > "$WORK/alpha/Api.cs" <<'EOF'
namespace Alpha;

public class Thing
{
    public int Keep() => 1;
    public int Drop() => 2;
    public int Added() => 3;
}

public class AddedThing
{
    public int Value() => 4;
}
EOF
build_variant "$WORK/alpha" "$WORK/out/alpha-additive"

# Removed public member.
cat > "$WORK/alpha/Api.cs" <<'EOF'
namespace Alpha;

public class Thing
{
    public int Keep() => 1;
}
EOF
build_variant "$WORK/alpha" "$WORK/out/alpha-removed"

# Incompatible public signature change.
cat > "$WORK/alpha/Api.cs" <<'EOF'
namespace Alpha;

public class Thing
{
    public string Keep() => "1";
    public int Drop() => 2;
}
EOF
build_variant "$WORK/alpha" "$WORK/out/alpha-signature"

alpha_source_baseline

# Version epoch: the baseline API at a LOWER assembly version (base is the SDK
# default 1.0.0.0), and the same downgrade combined with a real API break.
build_variant "$WORK/alpha" "$WORK/out/alpha-downgrade" -p:AssemblyVersion=0.3.0.0
cat > "$WORK/alpha/Api.cs" <<'EOF2'
namespace Alpha;

public class Thing
{
    public int Keep() => 1;
}
EOF2
build_variant "$WORK/alpha" "$WORK/out/alpha-downgrade-removed" -p:AssemblyVersion=0.3.0.0
alpha_source_baseline

# Directories holding only the two protected DLLs, as the workflow stages them.
stage_pair() {
    local dest="$1" alpha_src="$2" beta="$3"
    mkdir -p "$dest"
    [ "$alpha_src" = "-" ] || cp "$WORK/out/$alpha_src/Alpha.dll" "$dest/Alpha.dll"
    [ "$beta" = "-" ] || cp "$WORK/out/beta/Beta.dll" "$dest/Beta.dll"
}

stage_pair "$WORK/base"           alpha-base      yes
stage_pair "$WORK/head-identical" alpha-base      yes
stage_pair "$WORK/head-additive"  alpha-additive  yes
stage_pair "$WORK/head-removed"   alpha-removed   yes
stage_pair "$WORK/head-signature" alpha-signature yes
stage_pair "$WORK/head-partial"   alpha-base      -
stage_pair "$WORK/base-partial"   -               yes
stage_pair "$WORK/head-downgrade" alpha-downgrade yes
stage_pair "$WORK/head-downgrade-removed" alpha-downgrade-removed yes
mkdir -p "$WORK/empty-base"

MANIFEST="$WORK/manifest.txt"
printf 'Alpha.dll\nBeta.dll\n' > "$MANIFEST"

EMPTY_MANIFEST="$WORK/manifest-empty.txt"
printf '# nothing protected\n\n' > "$EMPTY_MANIFEST"

SHRUNK_MANIFEST="$WORK/manifest-shrunk.txt"
printf 'Alpha.dll\n' > "$SHRUNK_MANIFEST"

DUPLICATE_MANIFEST="$WORK/manifest-duplicate.txt"
printf 'Alpha.dll\nAlpha.dll\nBeta.dll\n' > "$DUPLICATE_MANIFEST"

UNSORTED_MANIFEST="$WORK/manifest-unsorted.txt"
printf 'Beta.dll\nAlpha.dll\n' > "$UNSORTED_MANIFEST"

MALFORMED_MANIFEST="$WORK/manifest-malformed.txt"
printf 'Alpha.dll\n../../etc/passwd\nBeta.dll\n' > "$MALFORMED_MANIFEST"

NEW_NONE="$WORK/new-none.txt"
printf '# normally empty\n' > "$NEW_NONE"

NEW_ALPHA="$WORK/new-alpha.txt"
printf 'Alpha.dll\n' > "$NEW_ALPHA"

EPOCH_NONE="$WORK/epoch-none.txt"   # deliberately never created
EPOCH_OK="$WORK/epoch-ok.txt"
printf '# reset\n1.0.0.0 0.3.0.0\n' > "$EPOCH_OK"
EPOCH_OTHER="$WORK/epoch-other.txt"
printf '1.0.0.0 0.2.0.0\n' > "$EPOCH_OTHER"
EPOCH_UP="$WORK/epoch-up.txt"
printf '0.3.0.0 1.0.0.0\n' > "$EPOCH_UP"
EPOCH_TWO="$WORK/epoch-two.txt"
printf '1.0.0.0 0.3.0.0\n2.0.0.0 0.3.0.0\n' > "$EPOCH_TWO"
EPOCH_SHORT="$WORK/epoch-short.txt"
printf '1.0.0 0.3.0\n' > "$EPOCH_SHORT"

NEW_BOTH="$WORK/new-both.txt"
printf 'Alpha.dll\nBeta.dll\n' > "$NEW_BOTH"

NEW_UNKNOWN="$WORK/new-unknown.txt"
printf 'Gamma.dll\n' > "$NEW_UNKNOWN"

UNRESOLVED_TOOL="$WORK/unresolved-tool.sh"
cat > "$UNRESOLVED_TOOL" <<'EOF'
#!/usr/bin/env bash
echo "Could not resolve reference 'System.Runtime.dll' directly or transitively referenced by x in any of the provided search directories."
exit 0
EOF
chmod +x "$UNRESOLVED_TOOL"

BROKEN_TOOL="$WORK/broken-tool.sh"
cat > "$BROKEN_TOOL" <<'EOF'
#!/usr/bin/env bash
echo "simulated ApiCompat infrastructure failure" >&2
exit 42
EOF
chmod +x "$BROKEN_TOOL"

# ── Harness ───────────────────────────────────────────────────────────────
# Every case runs the real script and asserts on its exit status, plus
# optionally on report text. `set -e` must never swallow the status.

LAST_OUTPUT=""
run_compat() {
    local rc=0
    LAST_OUTPUT="$(ABI_EXPECTED_ASSEMBLY_COUNT="${EXPECT_COUNT:-2}" \
                   ABI_NEW_ASSEMBLIES_MANIFEST="${NEW_MANIFEST:-$NEW_NONE}" \
                   ABI_VERSION_EPOCH_FILE="${EPOCH:-$EPOCH_NONE}" \
                   "$ABI_COMPAT" "$@" 2>&1)" || rc=$?
    return "$rc"
}

expect_green() {
    local label="$1"; shift
    local rc=0
    run_compat "$@" || rc=$?
    if [ "$rc" -eq 0 ]; then
        ok "GREEN $label"
    else
        bad "GREEN $label — expected exit 0, got $rc"
        printf '%s\n' "$LAST_OUTPUT" | tail -20 >&2
    fi
}

expect_red() {
    local label="$1" needle="$2"; shift 2
    local rc=0
    run_compat "$@" || rc=$?
    if [ "$rc" -eq 0 ]; then
        bad "RED $label — expected a non-zero exit, got 0"
        return
    fi
    if [ -n "$needle" ] && ! printf '%s' "$LAST_OUTPUT" | grep -qF -- "$needle"; then
        bad "RED $label — exited $rc but the report never mentioned '$needle'"
        printf '%s\n' "$LAST_OUTPUT" | tail -20 >&2
        return
    fi
    ok "RED $label (exit $rc)"
}

echo "-- expected green --"

expect_green "identical base and head" \
    "$WORK/base" "$WORK/head-identical" "$MANIFEST"

if printf '%s' "$LAST_OUTPUT" | grep -qF "2 of 2 protected assemblies reached an ApiCompat verdict"; then
    ok "GREEN report states 2 of 2 assemblies compared"
else
    bad "GREEN report did not state the compared count"
fi

expect_green "additive public member and type" \
    "$WORK/base" "$WORK/head-additive" "$MANIFEST"

NEW_MANIFEST="$NEW_ALPHA" \
    expect_green "assembly absent from base and declared newly introduced" \
    "$WORK/base-partial" "$WORK/head-identical" "$MANIFEST"

# The production invocation passes no --lref/--rref, so ApiCompat resolves
# references from the runtime and says nothing about them. If a future change
# ever passes an incomplete search directory, the verdict stays green while
# the analysis silently weakens — this asserts the report says so out loud.
ABI_APICOMPAT_CMD="$UNRESOLVED_TOOL" \
    expect_green "unresolved references do not fail the gate" \
    "$WORK/base" "$WORK/head-identical" "$MANIFEST"

if printf '%s' "$LAST_OUTPUT" | grep -qF "compatible verdicts were produced with unresolved references"; then
    ok "GREEN unresolved references are reported as a coverage limit"
else
    bad "GREEN unresolved references were swallowed"
fi

EPOCH="$EPOCH_OK" \
    expect_green "declared version epoch reset (CP0003 only, declared pair)" \
    "$WORK/base" "$WORK/head-downgrade" "$MANIFEST"
if printf '%s' "$LAST_OUTPUT" | grep -qF "declared version epoch reset 1.0.0.0 → 0.3.0.0"; then
    ok "GREEN epoch reset is named in the report"
else
    bad "GREEN epoch reset was accepted silently"
fi

echo
echo "-- expected red --"

expect_red "assembly version lowered without a declared epoch" "CP0003" \
    "$WORK/base" "$WORK/head-downgrade" "$MANIFEST"

EPOCH="$EPOCH_OK" \
    expect_red "declared epoch does not hide a real break" "CP0002" \
    "$WORK/base" "$WORK/head-downgrade-removed" "$MANIFEST"

EPOCH="$EPOCH_OTHER" \
    expect_red "epoch declared for a different pair" "CP0003" \
    "$WORK/base" "$WORK/head-downgrade" "$MANIFEST"

EPOCH="$EPOCH_UP" \
    expect_red "epoch that raises the version" "must lower the version" \
    "$WORK/base" "$WORK/head-downgrade" "$MANIFEST"

EPOCH="$EPOCH_TWO" \
    expect_red "epoch file with two declarations" "exactly one" \
    "$WORK/base" "$WORK/head-downgrade" "$MANIFEST"

EPOCH="$EPOCH_SHORT" \
    expect_red "epoch file with three-part versions" "four-part versions" \
    "$WORK/base" "$WORK/head-downgrade" "$MANIFEST"

expect_red "removed public member" "CP0002" \
    "$WORK/base" "$WORK/head-removed" "$MANIFEST"

expect_red "incompatible public signature change" "breaking change" \
    "$WORK/base" "$WORK/head-signature" "$MANIFEST"

ABI_ALLOW_BREAKING=1 expect_green "explicit policy accepts a classified breaking change" \
    "$WORK/base" "$WORK/head-removed" "$MANIFEST"
if printf '%s' "$LAST_OUTPUT" | grep -qF "no backward-compatibility claim"; then
    ok "accepted breaks are reported without claiming compatibility"
else
    bad "accepted break was reported as compatible"
fi
ABI_ALLOW_BREAKING=1 expect_red "breaking policy still refuses a missing head artifact" "missing from head" \
    "$WORK/base" "$WORK/head-partial" "$MANIFEST"
ABI_ALLOW_BREAKING=1 ABI_APICOMPAT_CMD="$BROKEN_TOOL" \
    expect_red "breaking policy still refuses a crashed tool" "ApiCompat failed" \
    "$WORK/base" "$WORK/head-identical" "$MANIFEST"
ABI_ALLOW_BREAKING=invalid expect_red "invalid breaking policy is rejected" "ABI_ALLOW_BREAKING must be 0 or 1" \
    "$WORK/base" "$WORK/head-identical" "$MANIFEST"

expect_red "protected assembly missing from head" "missing from head" \
    "$WORK/base" "$WORK/head-partial" "$MANIFEST"

# A missing file is fatal but was never judged. Counting it as compared would
# overstate what the run proved, which is exactly the kind of claim the old
# fail-open loop made.
if printf '%s' "$LAST_OUTPUT" | grep -qF "1 of 2 protected assemblies reached an ApiCompat verdict"; then
    ok "RED a missing assembly is not counted as compared"
else
    bad "RED report miscounted an uncompared assembly"
    printf '%s\n' "$LAST_OUTPUT" | grep -F "protected assemblies reached" >&2
fi

expect_red "protected assembly missing from base, not declared" "missing from base" \
    "$WORK/base-partial" "$WORK/head-identical" "$MANIFEST"

EXPECT_COUNT=2 expect_red "manifest entry removed (scope reduction)" "are expected" \
    "$WORK/base" "$WORK/head-identical" "$SHRUNK_MANIFEST"

expect_red "empty manifest" "zero assemblies" \
    "$WORK/base" "$WORK/head-identical" "$EMPTY_MANIFEST"

expect_red "duplicate manifest entry" "duplicate" \
    "$WORK/base" "$WORK/head-identical" "$DUPLICATE_MANIFEST"

expect_red "unsorted manifest" "not sorted" \
    "$WORK/base" "$WORK/head-identical" "$UNSORTED_MANIFEST"

expect_red "malformed manifest entry" "not a plain DLL file name" \
    "$WORK/base" "$WORK/head-identical" "$MALFORMED_MANIFEST"

expect_red "manifest file absent" "manifest not found" \
    "$WORK/base" "$WORK/head-identical" "$WORK/does-not-exist.txt"

expect_red "base directory absent" "base directory not found" \
    "$WORK/no-such-dir" "$WORK/head-identical" "$MANIFEST"

NEW_MANIFEST="$NEW_UNKNOWN" \
    expect_red "newly-introduced entry outside the protected manifest" "which is not in" \
    "$WORK/base" "$WORK/head-identical" "$MANIFEST"

NEW_MANIFEST="$NEW_BOTH" \
    expect_red "zero assemblies compared" "zero assemblies compared" \
    "$WORK/empty-base" "$WORK/head-identical" "$MANIFEST"

ABI_APICOMPAT_CMD="$BROKEN_TOOL" \
    expect_red "ApiCompat process failure" "ApiCompat failed" \
    "$WORK/base" "$WORK/head-identical" "$MANIFEST"

if printf '%s' "$LAST_OUTPUT" | grep -qF -- "— breaking change (exit"; then
    bad "tool failure was misreported as a breaking change"
else
    ok "RED tool failure is classified as indeterminate, not as a breaking change"
fi

ABI_APICOMPAT_CMD="dotnet tool run apicompat --not-a-real-option" \
    expect_red "invalid ApiCompat invocation" "ApiCompat failed" \
    "$WORK/base" "$WORK/head-identical" "$MANIFEST"

# Argument validation does not go through run_compat: it must fail before any
# manifest is read.
check_args() {
    local label="$1"; shift
    local rc=0
    "$ABI_COMPAT" "$@" > /dev/null 2>&1 || rc=$?
    if [ "$rc" -ne 0 ]; then
        ok "RED refuses $label"
    else
        bad "RED accepted $label"
    fi
}

check_args "no arguments"
check_args "a single argument" "$WORK/base"
check_args "four arguments" "$WORK/base" "$WORK/head-identical" "$MANIFEST" extra

echo
echo "-- staging (fail before upload) --"

stage_rc() {
    local rc=0
    LAST_OUTPUT="$(ABI_NEW_ASSEMBLIES_MANIFEST="${NEW_MANIFEST:-$NEW_NONE}" \
                   "$ABI_STAGE" "$@" 2>&1)" || rc=$?
    return "$rc"
}

if stage_rc "$WORK/head-identical" "$WORK/stage-ok" head "$MANIFEST" \
    && [ "$(find "$WORK/stage-ok" -name '*.dll' | wc -l)" -eq 2 ]; then
    ok "GREEN staging copies exactly the two protected assemblies"
else
    bad "GREEN staging of a complete head build"
fi

rc=0
stage_rc "$WORK/head-partial" "$WORK/stage-bad" head "$MANIFEST" || rc=$?
if [ "$rc" -ne 0 ] && printf '%s' "$LAST_OUTPUT" | grep -qF "refusing to upload an incomplete ABI artifact"; then
    ok "RED staging refuses an incomplete head build"
else
    bad "RED staging accepted an incomplete head build (exit $rc)"
fi

rc=0
stage_rc "$WORK/base-partial" "$WORK/stage-base-bad" base "$MANIFEST" || rc=$?
if [ "$rc" -ne 0 ]; then
    ok "RED staging refuses an undeclared assembly missing from base"
else
    bad "RED staging accepted an undeclared assembly missing from base"
fi

rc=0
NEW_MANIFEST="$NEW_ALPHA" stage_rc "$WORK/base-partial" "$WORK/stage-base-new" base "$MANIFEST" || rc=$?
if [ "$rc" -eq 0 ]; then
    ok "GREEN staging tolerates a declared newly introduced assembly on the base side"
else
    bad "GREEN staging of a declared newly introduced assembly (exit $rc)"
fi

rc=0
NEW_MANIFEST="$NEW_ALPHA" stage_rc "$WORK/head-partial" "$WORK/stage-head-new" head "$MANIFEST" || rc=$?
if [ "$rc" -ne 0 ]; then
    ok "RED the newly-introduced exemption never applies to the head side"
else
    bad "RED head staging honoured a newly-introduced exemption"
fi

echo
echo "-- repository manifest --"

# The production manifest must satisfy the same parser, and the count
# assertion in ci/abi-compat.sh must match it. This is what makes a silent
# scope reduction impossible: it needs an edit in both files.
rc=0
LIVE="$("$ABI_COMPAT" "$WORK/base" "$WORK/head-identical" 2>&1)" || rc=$?
if [ "$rc" -ne 0 ] && printf '%s' "$LIVE" | grep -qF "missing from head"; then
    ok "repository manifest parses and is enforced against a foreign build output"
else
    bad "repository manifest did not behave as expected (exit $rc)"
fi

# Pre-rename names legitimately appear in the manifest's header, which records
# the mapping. What must never come back is a pre-rename ENTRY, so compare
# against the parsed entries rather than the raw file.
if abi_manifest_read "$REPO_ROOT/ci/abi-assemblies.txt" | grep -qE 'MediaBrowser\.|Emby\.Naming'; then
    bad "the protected manifest still contains a pre-rename assembly name"
else
    ok "no pre-rename assembly name is protected"
fi

echo
echo "ABI gate controls: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
