#!/usr/bin/env bash
#
# Run the failover + canonical-ordering chaos rigs against sdk-net (bead qfg-7h5d.1.11).
#
# Unlike run-chaos.sh (single upstream), these two rigs spawn their OWN api-delivery fixture
# upstream(s) from inside the .NET test process:
#   - FailoverChaosTests.Failover drives scenarios-failover/ against ONE upstream behind the
#     primary ('http') + 'secondary' proxies; faults hit the primary leg.
#   - FailoverChaosTests.Ordering drives scenarios-ordering/ against TWO upstreams pinned to
#     divergent Meta.generations (one pair per scenario).
#
# So this wrapper only:
#   1. builds the api-delivery binary (the test spawns it per scenario, generation-pinned),
#   2. boots toxiproxy + the seeded sse/http/secondary proxies (the test repoints them),
#   3. runs `dotnet test` (gated on QUONFIG_FAILOVER_CHAOS_RUN=1) with the binary + fixture paths,
#   4. tears everything down on exit.
#
# Env knobs (override on the command line):
#   TEST_FILTER     dotnet test --filter (default 'Category=FailoverChaos' — both rigs)
#   CHAOS_SKIP      comma scenario filter, matched against the file base name
#                   (default 'o01-secondary-newer' — needs cross-leg max-wins, qfg-7h5d.1.14)
#   CHAOS_ONLY      comma scenario filter (allow-list)
#   CHAOS_POLL_MS   expectation poll interval (default 200)
#   CHAOS_FIXTURE_SDK_KEY  backend SDK key matching the fixture (default test-backend-key)
#   DOTNET          dotnet binary (defaults to $HOME/.dotnet/dotnet then PATH)
#
# Examples:
#   ./scripts/run-failover-chaos.sh
#   TEST_FILTER='FullyQualifiedName~FailoverChaosTests.Failover' ./scripts/run-failover-chaos.sh
#   CHAOS_ONLY=f02 ./scripts/run-failover-chaos.sh   # just the hang-failover scenario

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SDK_NET_DIR="$(cd "$HERE/.." && pwd)"
REPO_ROOT="$(cd "$SDK_NET_DIR/.." && pwd)"
HARNESS_DIR="$REPO_ROOT/integration-test-data/chaos"

if [[ ! -d "$HARNESS_DIR" ]]; then
  echo "chaos harness not found at $HARNESS_DIR — is integration-test-data checked out as a sibling?" >&2
  exit 1
fi

# Identify ourselves to the shared chaos lock (qfg-47c2.32). Owner PID is THIS wrapper's pid so
# the lock survives the whole run, not just the short-lived start-chaos.sh subprocess.
export QUONFIG_CHAOS_SESSION="${QUONFIG_CHAOS_SESSION:-sdk-net-failover-$$-$(date +%s)}"
export QUONFIG_CHAOS_OWNER_PID=$$

TEST_FILTER="${TEST_FILTER:-Category=FailoverChaos}"
export CHAOS_SKIP="${CHAOS_SKIP:-o01-secondary-newer}"
export CHAOS_ONLY="${CHAOS_ONLY:-}"
export CHAOS_POLL_MS="${CHAOS_POLL_MS:-200}"
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
  "$HARNESS_DIR/stop-chaos.sh" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

echo "==> building api-delivery binary (the test spawns it per scenario)"
API_BIN="$SDK_NET_DIR/.chaos-api-delivery"
( cd "$REPO_ROOT/api-delivery" && GOWORK=off go build -o "$API_BIN" ./cmd/server )

echo "==> booting toxiproxy via shared launcher (no upstream — the test spawns its own)"
"$HARNESS_DIR/start-chaos.sh"

echo "==> running failover + ordering scenarios (filter=$TEST_FILTER skip=${CHAOS_SKIP:-<none>})"
cd "$SDK_NET_DIR"
QUONFIG_FAILOVER_CHAOS_RUN=1 \
  CHAOS_API_DELIVERY_BIN="$API_BIN" \
  CHAOS_FIXTURE_DIR="$REPO_ROOT/integration-test-data/data/integration-tests" \
  CHAOS_SDK_KEYS_FILE="$REPO_ROOT/api-delivery/testdata/fixture-sdk-keys.json" \
  CHAOS_UPSTREAM_HOST="${CHAOS_UPSTREAM_HOST:-host.docker.internal}" \
  CHAOS_FIXTURE_SDK_KEY="$FIXTURE_KEY" \
  "$DOTNET" test tests/Quonfig.Sdk.Chaos.Tests/Quonfig.Sdk.Chaos.Tests.csproj \
    --configuration Release \
    --filter "$TEST_FILTER" \
    --logger "console;verbosity=normal" \
    --logger "trx;LogFileName=failover-chaos-results.trx"
