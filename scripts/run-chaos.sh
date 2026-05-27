#!/usr/bin/env bash
#
# Run the cross-SDK chaos harness against sdk-net (bead qfg-zp7i.14).
#
# 1. Builds + launches api-delivery in fixture mode on $CHAOS_API_DELIVERY_PORT.
# 2. Boots the shared toxiproxy launcher (../integration-test-data/chaos/start-chaos.sh)
#    pointed at that api-delivery via host.docker.internal.
# 3. Runs `dotnet test tests/Quonfig.Sdk.Chaos.Tests` with QUONFIG_CHAOS_RUN=1.
# 4. Tears everything down on exit (success or failure).
#
# Env knobs (override on the command line):
#   CHAOS_API_DELIVERY_PORT  port for the locally-spawned api-delivery (default 6550)
#   CHAOS_ONLY               comma list of scenarios, e.g. "02,05,07,09"
#   CHAOS_SKIP               comma list of scenarios to skip
#   CHAOS_POLL_MS            expectation poll interval (default 250)
#   CHAOS_WALL_CLOCK_CAP_S   cap each scenario's wall-clock seconds (default 0 = no cap)
#   CHAOS_FIXTURE_SDK_KEY    matches api-delivery fixture key file (default test-backend-key)
#   DOTNET                   dotnet binary (defaults to $HOME/.dotnet/dotnet then PATH)
#
# Examples:
#   ./scripts/run-chaos.sh                                # full suite
#   CHAOS_ONLY=02,05,07,09 ./scripts/run-chaos.sh         # the failures the bead targets
#   CHAOS_SKIP=11 CHAOS_WALL_CLOCK_CAP_S=30 ./scripts/run-chaos.sh   # fast red baseline

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SDK_NET_DIR="$(cd "$HERE/.." && pwd)"
REPO_ROOT="$(cd "$SDK_NET_DIR/.." && pwd)"
HARNESS_DIR="$REPO_ROOT/integration-test-data/chaos"

if [[ ! -d "$HARNESS_DIR" ]]; then
  echo "chaos harness not found at $HARNESS_DIR — is integration-test-data checked out as a sibling?" >&2
  exit 1
fi

# Identify ourselves to the shared chaos lock (qfg-47c2.32). Owner PID is THIS
# wrapper's pid so the lock survives the lifetime of the chaos run, not just
# the short-lived start-chaos.sh subprocess.
export QUONFIG_CHAOS_SESSION="${QUONFIG_CHAOS_SESSION:-sdk-net-$$-$(date +%s)}"
export QUONFIG_CHAOS_OWNER_PID=$$

API_PORT="${CHAOS_API_DELIVERY_PORT:-6550}"
FIXTURE_KEY="${CHAOS_FIXTURE_SDK_KEY:-test-backend-key}"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
if [[ ! -x "$DOTNET" ]]; then
  if command -v dotnet >/dev/null 2>&1; then
    DOTNET="$(command -v dotnet)"
  else
    echo "dotnet not found — set DOTNET=... or install .NET 8 SDK" >&2
    exit 1
  fi
fi

cleanup_done=0
cleanup() {
  if [[ "$cleanup_done" == "1" ]]; then return; fi
  cleanup_done=1
  echo "==> tearing down chaos harness"
  if [[ -n "${API_DELIVERY_PID:-}" ]]; then
    kill "$API_DELIVERY_PID" 2>/dev/null || true
    wait "$API_DELIVERY_PID" 2>/dev/null || true
  fi
  "$HARNESS_DIR/stop-chaos.sh" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

echo "==> building api-delivery binary"
API_BIN="$SDK_NET_DIR/.chaos-api-delivery"
( cd "$REPO_ROOT/api-delivery" && GOWORK=off go build -o "$API_BIN" ./cmd/server )

echo "==> starting api-delivery on :$API_PORT (FIXTURE_DIR=integration-test-data/data/integration-tests)"
# SSE_HEARTBEAT_INTERVAL=1s mirrors sdk-go's run-chaos.sh: the chaos run uses a shorter SSE
# read-deadline knob than production (so scenario 02 / 07 fire within a reasonable wall-clock),
# so the server must keepalive faster or the deadline trips before the first heartbeat lands.
PORT="$API_PORT" \
  FIXTURE_DIR="$REPO_ROOT/integration-test-data/data/integration-tests" \
  SDK_KEYS_FILE="$REPO_ROOT/api-delivery/testdata/fixture-sdk-keys.json" \
  QUONFIG_ENVIRONMENT=development \
  SSE_HEARTBEAT_INTERVAL=1s \
  "$API_BIN" &
API_DELIVERY_PID=$!

# Wait for api-delivery healthz.
for i in $(seq 1 30); do
  if curl -fsS "http://127.0.0.1:$API_PORT/healthz" >/dev/null 2>&1; then
    break
  fi
  sleep 0.5
  if [[ $i -eq 30 ]]; then
    echo "api-delivery did not come up on :$API_PORT within 15s" >&2
    exit 1
  fi
done

echo "==> booting toxiproxy via shared launcher (upstream :$API_PORT)"
CHAOS_UPSTREAM_HOST=host.docker.internal \
  CHAOS_UPSTREAM_SSE="$API_PORT" \
  CHAOS_UPSTREAM_HTTP="$API_PORT" \
  "$HARNESS_DIR/start-chaos.sh"

echo "==> running chaos scenarios via dotnet test"
cd "$SDK_NET_DIR"
QUONFIG_CHAOS_RUN=1 \
  CHAOS_API_DELIVERY_URL="http://127.0.0.1:$API_PORT" \
  CHAOS_FIXTURE_SDK_KEY="$FIXTURE_KEY" \
  "$DOTNET" test tests/Quonfig.Sdk.Chaos.Tests/Quonfig.Sdk.Chaos.Tests.csproj \
    --configuration Release \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=chaos-results.trx"
