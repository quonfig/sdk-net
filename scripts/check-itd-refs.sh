#!/usr/bin/env bash
#
# Fail when the integration-test-data checkouts across this repo's workflows pin different refs
# (qfg-6vno.7). test.yaml (unit), release.yaml, chaos.yaml and failover-chaos.yaml must all run
# against one ITD tag, or chaos runs against an older contract than the code was written to.
#
# Usage: scripts/check-itd-refs.sh [workflows-dir]   (default: .github/workflows)

set -euo pipefail

dir="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/.github/workflows}"

# For each `repository: quonfig/integration-test-data` line, the next `ref:` line in the same
# file is that checkout's pin. Prints "file<TAB>ref".
pins="$(awk '
  FNR == 1 { want = 0 }
  /repository:[[:space:]]*quonfig\/integration-test-data/ { want = 1; next }
  want && /^[[:space:]]*ref:/ { sub(/^[[:space:]]*ref:[[:space:]]*/, ""); sub(/[[:space:]]*#.*$/, ""); print FILENAME "\t" $0; want = 0 }
  want && /^[[:space:]]*-[[:space:]]+(name|uses):/ { print FILENAME "\t(none)"; want = 0 }
' "$dir"/*.yml "$dir"/*.yaml 2>/dev/null || true)"

if [[ -z "$pins" ]]; then
  echo "::error::no integration-test-data checkouts found under $dir" >&2
  exit 1
fi

echo "integration-test-data refs:"
printf '%s\n' "$pins" | awk -F'\t' '{ printf "  %-50s %s\n", $1, $2 }'

distinct="$(printf '%s\n' "$pins" | cut -f2 | sort -u)"
if [[ "$(printf '%s\n' "$distinct" | wc -l | tr -d ' ')" != "1" || "$distinct" == "(none)" ]]; then
  echo "::error::integration-test-data refs differ across workflows (or a checkout has no ref); pin every checkout to the same tag" >&2
  exit 1
fi
echo "OK: every workflow pins integration-test-data at $distinct"
