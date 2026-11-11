#!/usr/bin/env bash
# shellcheck disable=SC2015,SC2317
# SC2015: `<cond> && pass || fail` is intended — pass()/fail() both return 0.
# SC2317: cleanup() runs via the EXIT trap.
# Bounded compose smoke for the guided install (#89 / [A3]).
#
# Runs the canonical docker-compose.yml from an ISOLATED directory that contains
# ONLY the compose file + env example (no repo source), proving the zero-friction
# path: pull the immutable image, boot, REACH THE FIRST-RUN WIZARD IN A BROWSER
# and complete onboarding, survive a restart, keep state across container
# recreation, enforce read-only media, and keep named volumes on `down`.
# Requires NO source compilation.
#
# WHY THE BROWSER PART EXISTS (#115)
#
#   The first version of this script asserted only that `/System/Info/Public`
#   answered, and it passed against an image that ran with `--nowebclient` and
#   served the Swagger API documentation at `/` — no onboarding wizard existed at
#   all. API reachability is not browser-installability. The gates below fail on
#   any such image, and the real browser run is delegated to
#   docker/browser-onboarding.sh's Playwright suite so the two paths share one
#   definition of "onboarded".
#
# Usage: TESSERAFIN_IMAGE=<immutable-reference> docker/compose-smoke.sh [host-port]
set -euo pipefail

if [[ -z "${TESSERAFIN_IMAGE:-}" ]]; then
  echo "compose-smoke: set TESSERAFIN_IMAGE to an accessible immutable image" >&2
  exit 2
fi

PORT="${1:-8096}"
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
PROJECT="tf_a3_$$"
API="http://127.0.0.1:${PORT}/System/Info/Public"
ROOT="http://127.0.0.1:${PORT}/"

FAILED=0
pass() { echo "  PASS  $*"; }
fail() { echo "  FAIL  $*"; FAILED=1; }
dc() { docker compose -p "${PROJECT}" "$@"; }

cleanup() {
  ( cd "${WORK}" && dc down -v >/dev/null 2>&1 ) || true
  docker run --rm -v "${WORK}:/w" busybox chown -R "$(id -u):$(id -g)" /w >/dev/null 2>&1 || true
  rm -rf "${WORK}"
}
trap cleanup EXIT

# Readiness is the WEB CLIENT, not the API: `/System/Info/Public` is answered by
# the startup SetupServer while the application is still coming up, so an
# API-only probe races and `/` still returns 503 at that moment.
wait_api() { for _ in $(seq 1 120); do
  [[ "$(curl -s -o /dev/null -w '%{http_code}' "${ROOT}")" == "302" ]] \
    && curl -fsS "${API}" >/dev/null 2>&1 && return 0
  sleep 2
done; return 1; }

wizard_completed() {
  curl -fsS "${API}" | python3 -c '
import json,sys
d={k.lower():v for k,v in json.load(sys.stdin).items()}
print(str(d.get("startupwizardcompleted")).lower())'
}

echo "== isolated workdir (only compose + env + empty media; NO source) =="
cp "${SRC_DIR}/docker-compose.yml" "${WORK}/docker-compose.yml"
cp "${SRC_DIR}/.env.example" "${WORK}/.env.example"
mkdir -p "${WORK}/media"
echo "hello-tesserafin" > "${WORK}/media/probe.txt"
# Map the requested host port without editing the tracked compose file.
printf 'TESSERAFIN_MEDIA=%s/media\n' "${WORK}" > "${WORK}/.env"
# docker-compose.yml has no default image until the 1.0.0 launch; pass one through.
printf 'TESSERAFIN_IMAGE=%s\n' "${TESSERAFIN_IMAGE}" >> "${WORK}/.env"
[[ "${PORT}" != "8096" ]] && cat > "${WORK}/docker-compose.override.yml" <<YAML
services:
  tesserafin:
    ports:
      - "${PORT}:8096"
YAML
ls "${WORK}"
[[ ! -e "${WORK}/Dockerfile" && ! -e "${WORK}/src" ]] && pass "no source / Dockerfile in the install dir" || fail "source present in install dir"

cd "${WORK}"

echo "== docker compose config (renders + validates) =="
dc config >/dev/null && pass "compose config valid" || fail "compose config invalid"
IMG_REF="$(dc config | awk '/image:/{print $2; exit}')"
echo "  image: ${IMG_REF}"
# Immutability is a SHAPE, not a specific version: hard-coding `12.0.0-dev.` made
# this assertion silently version-locked and it would have had to be edited again
# at every epoch. A digest, or any `<core>-dev.<12-char commit>` tag, is
# immutable; `latest`, `preview`, `1`, `1.0` and bare `1.0.0` are not.
case "${IMG_REF}" in
  *@sha256:*)
    pass "compose pins the image by manifest digest (immutable)" ;;
  *:[0-9]*.[0-9]*.[0-9]*-dev.[0-9a-f]*)
    pass "compose uses an immutable development tag (${IMG_REF##*:})" ;;
  *)
    fail "compose image is not an immutable digest or -dev tag: ${IMG_REF}" ;;
esac

echo "== docker compose pull (from GHCR, no build) =="
dc pull >/dev/null 2>&1 && pass "image pulled via compose" || fail "compose pull failed"

echo "== fresh named-volume install: up + API reachable + first-run state =="
dc up -d >/dev/null
wait_api || { fail "API never answered"; dc logs --tail 40; exit 1; }
pass "server reachable on :${PORT} after compose up"
dc exec -T tesserafin id -u | grep -qx 10000 && pass "runs as non-root uid 10000" || fail "not uid 10000"
# Media is read-only.
if dc exec -T tesserafin sh -c 'touch /media/.should-fail' 2>/dev/null; then fail "media is writable (should be ro)"; else pass "media mounted read-only (write rejected)"; fi
dc exec -T tesserafin cat /media/probe.txt 2>/dev/null | grep -qx hello-tesserafin && pass "read-only media is readable" || fail "media not readable"
# A DB was created on the data volume (first-run state).
dc exec -T tesserafin sh -c 'ls /data/data/*.db >/dev/null 2>&1' && pass "first-run state created (/data/*.db)" || fail "no DB created on first run"

echo "== the browser gets the web client, not API documentation (#115) =="
# `/` is a 302 to `/web/` — the server's own serving model. With `--nowebclient`
# the same middleware sends `/` to `/api-docs/swagger`, which is the defect this
# asserts against.
ROOT_REDIRECT="$(curl -s -o /dev/null -w '%{redirect_url}' "${ROOT}")"
ROOT_STATUS="$(curl -sL -o "${WORK}/root.html" -w '%{http_code}' "${ROOT}")"
ROOT_CT="$(curl -sL -o /dev/null -w '%{content_type}' "${ROOT}")"
echo "  / -> ${ROOT_REDIRECT} -> ${ROOT_STATUS} (${ROOT_CT})"
[[ "${ROOT_REDIRECT}" == *"/web/"* && "${ROOT_STATUS}" == "200" && "${ROOT_CT}" == text/html* ]] \
  && pass "/ serves the web client as HTML (302 -> /web/, 200)" \
  || fail "/ did not serve the web client (redirect='${ROOT_REDIRECT}' status=${ROOT_STATUS} type=${ROOT_CT})"
if grep -qiE 'swagger-ui|redoc' "${WORK}/root.html"; then
  fail "/ serves API documentation instead of the web client"
else
  pass "/ does not serve API documentation"
fi
[[ "$(wizard_completed)" == "false" ]] \
  && pass "pristine volumes present an incomplete first run (wizard pending)" \
  || fail "onboarding is already complete on pristine volumes — the browser gate would be vacuous"

echo "== real browser onboarding against THIS compose deployment (#115) =="
# Same Playwright suite the image-level gate uses, pointed at the compose stack.
if [[ -d "${SRC_DIR}/docker/browser-gate" ]]; then
  ( cd "${SRC_DIR}/docker/browser-gate" \
      && npm ci --no-audit --no-fund >/dev/null \
      && npx --no-install playwright install chromium >/dev/null 2>&1 \
      && TESSERAFIN_BASE_URL="http://127.0.0.1:${PORT}" npx --no-install playwright test ) \
    && pass "browser reached the wizard and completed onboarding via compose" \
    || fail "browser onboarding failed against the compose deployment"
  [[ "$(wizard_completed)" == "true" ]] \
    && pass "onboarding is recorded as complete after the browser run" \
    || fail "onboarding not recorded as complete after the browser run"
else
  fail "docker/browser-gate is missing — the A3 browser gate cannot run"
fi

echo "== survives an in-place restart =="
dc restart >/dev/null
wait_api && pass "web client reachable after restart" || fail "web client gone after restart"
[[ "$(wizard_completed)" == "true" ]] \
  && pass "completed onboarding survives a restart" \
  || fail "onboarding state lost across restart"

echo "== state persists across container RECREATION (down without -v, then up) =="
MARK="a3-persist-$$"
dc exec -T tesserafin sh -c "printf %s '${MARK}' > /config/.a3-marker"
dc down >/dev/null                      # removes container, KEEPS named volumes
# Assert volumes still exist after `down` (no -v).
docker volume ls --format '{{.Name}}' | grep -q "${PROJECT}_tesserafin_config" \
  && pass "named volumes retained after 'compose down'" || fail "named volumes destroyed by 'compose down'"
dc up -d >/dev/null
wait_api || { fail "API never answered after recreation"; dc logs --tail 40; exit 1; }
GOT="$(dc exec -T tesserafin cat /config/.a3-marker 2>/dev/null || true)"
[[ "${GOT}" == "${MARK}" ]] && pass "persistent state survived container recreation" || fail "state lost across recreation (got '${GOT}')"
[[ "$(wizard_completed)" == "true" ]] \
  && pass "completed onboarding survives container recreation" \
  || fail "onboarding state lost across container recreation"

echo "== down -v removes named volumes only when explicitly requested =="
dc down -v >/dev/null
docker volume ls --format '{{.Name}}' | grep -q "${PROJECT}_tesserafin_config" \
  && fail "named volumes survived 'down -v'" || pass "'compose down -v' removed named volumes"

echo
if [[ "${FAILED}" == 0 ]]; then echo "COMPOSE-SMOKE: all gates passed"; exit 0; else echo "COMPOSE-SMOKE: FAILED"; exit 1; fi
