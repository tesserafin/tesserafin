#!/usr/bin/env bash
# Regenerate the committed OpenAPI contract — Docker, local, no hosted CI.
#
# Why this exists: hosted GitHub Actions quota has been exhausted since
# ~2026-07-06, so "OpenAPI Check" (.github/workflows/openapi-pull-request.yml)
# and "OpenAPI Generate" (.github/workflows/openapi-generate.yml) do not run on
# real PRs. ./ci/run.sh is the only live merge gate. This script is the local
# replacement for the generate half; the verify half runs inside ci/run.sh as
# OpenApiContractTests. See docs/openapi-contract.md.
#
# What it produces (both committed, both regenerated together):
#   openapi/openapi.json       canonical contract document
#   openapi/contract.lock.json {version, sha256} pin sidecar
#
# Determinism: the document is canonicalised before being written (object keys
# sorted, host-dependent `servers` dropped, LF, 2-space indent, trailing
# newline) — see tests/Tesserafin.Server.Integration.Tests/OpenApiContract.cs. Two
# runs of this script from a clean tree produce byte-identical files. Verify
# with:
#   ./ci/openapi-generate.sh && sha256sum openapi/openapi.json
#   ./ci/openapi-generate.sh && sha256sum openapi/openapi.json
#
# Usage:
#   ./ci/openapi-generate.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

IMAGE_TAG="tesserafin-ci"
NUGET_VOLUME="tesserafin-nuget"
TEST_PROJECT="tests/Tesserafin.Server.Integration.Tests/Tesserafin.Server.Integration.Tests.csproj"
SPEC_PATH="openapi/openapi.json"
LOCK_PATH="openapi/contract.lock.json"

banner() {
    echo ""
    echo "======================================================================"
    echo "== $*"
    echo "======================================================================"
}

banner "Building ${IMAGE_TAG} from Dockerfile.ci"
# Same empty-context trick as ci/run.sh: the repo is bind-mounted, never baked in.
BUILD_CTX="$(mktemp -d)"
trap 'rm -rf "$BUILD_CTX"' EXIT
docker build -f Dockerfile.ci -t "$IMAGE_TAG" "$BUILD_CTX"

banner "Generating ${SPEC_PATH} inside container"
set +e
docker run --rm \
    -v "$PWD":/repo \
    -v "${NUGET_VOLUME}":/nuget \
    -w /repo \
    -e TESSERAFIN_OPENAPI_WRITE=1 \
    "$IMAGE_TAG" \
    bash -c "
        set -euo pipefail
        dotnet test --project '${TEST_PROJECT}' --no-progress \
            --filter 'FullyQualifiedName~OpenApiContractTests.CommittedContract_MatchesRunningServer'
    "
STATUS=$?
set -e

# The container runs as root and the repo is bind-mounted read-write, so the
# files it just wrote are root-owned — which would make the host-side `git add`
# of those very files fail. Hand ownership back regardless of outcome, exactly
# as ci/run.sh does for bin/obj.
banner "Restoring working tree ownership to $(id -u):$(id -g)"
docker run --rm -v "$PWD":/repo "$IMAGE_TAG" chown -R "$(id -u):$(id -g)" /repo || true

banner "SUMMARY"
if [ "$STATUS" -ne 0 ]; then
    echo "RESULT: FAIL — generation did not complete, see the stage above" >&2
    exit "$STATUS"
fi

echo "RESULT: PASS — contract regenerated"
echo ""
echo "  ${SPEC_PATH}  sha256 $(sha256sum "${SPEC_PATH}" | cut -d' ' -f1)"
echo "  ${LOCK_PATH}"
echo ""
echo "Commit BOTH files in the same commit as the API change that caused them to move."
