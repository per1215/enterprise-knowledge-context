#!/usr/bin/env bash
# Guided EKC POC walkthrough (~10 minutes). Run scripts/demo-setup.sh once first.
#
#   scripts/demo.sh            press Enter between steps
#   scripts/demo.sh --auto     run everything without pauses (rehearsal / recording)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/demo/out"
export PATH="$HOME/.dotnet/tools:$PATH"
AUTO=false
[ "${1:-}" = "--auto" ] && AUTO=true

command -v ekc >/dev/null || { echo "ekc not installed: run scripts/demo-setup.sh" >&2; exit 1; }
[ -f "$OUT/eshop.graph.json" ] || { echo "maps missing: run scripts/demo-setup.sh" >&2; exit 1; }

F="$OUT/fabrikam.graph.json"
E="$OUT/eshop.graph.json"

scene() {
  printf '\n\033[1;35m━━━ %s ━━━\033[0m\n' "$1"
  printf '\033[2m%s\033[0m\n' "$2"
}

run() {
  printf '\n\033[1;36m$ %s\033[0m\n' "$*"
  if ! $AUTO; then read -r -p "  [Enter] " _; fi
  "$@"
}

cd "$ROOT"
clear 2>/dev/null || true
printf '\033[1mEKC: the open-source, self-hosted system map for AI agents\033[0m\n'
printf 'Everything below is computed from source code on this laptop: no build, no database, no GPU.\n'

scene "1. Map a real estate in seconds" \
  "Microsoft's eShop reference app: 19 projects, 474 files. Watch the time."
run ekc map samples/external/eshop --overlay samples/eshop/overlay.yaml -g "$E"
run ekc services -g "$E"

scene "2. Understand one service" \
  "Endpoints, who it calls, who calls it, events in/out, tables, rules. Every fact has file:line evidence."
run ekc describe ordering-api -g "$E"

scene "3. Trace a business transaction across services" \
  "The order saga: grace period → stock validation in catalog → payment → status fan-out. Reconstructed from code."
run ekc flow "Validate stock and take payment" -g "$E"

scene "4. What breaks if I change this?" \
  "A one-method change in the Order aggregate reaches three other services through events."
run ekc impact Order.SetPaidStatus -g "$E"

scene "5. Business rules and owners (Fabrikam Insurance sample)" \
  "Rules from code annotations and the overlay, owners from CODEOWNERS."
run ekc flow "Submit a claim" -g "$F"
run ekc impact ClaimValidator.EnsureWithinCoverage -g "$F"

scene "6. The pull-request blast radius" \
  "A developer rounds payment amounts. EKC comments this on the PR, and can block a High-risk merge."
run git -C "$OUT/pr-demo" log --oneline main..HEAD
run git -C "$OUT/pr-demo" --no-pager diff main...HEAD --stat
set +e
run ekc impact --diff main...HEAD --repo "$OUT/pr-demo" --pr-comment --fail-on high -g "$OUT/pr-demo.graph.json"
code=$?
set -e
printf '\n  exit code %s → the CI check would %s\n' "$code" "$([ "$code" = 3 ] && echo 'block the merge' || echo 'pass')"

scene "7. Architecture diagrams that never go stale" \
  "C4 container view generated from the code (also exported as Structurizr DSL)."
run ekc export c4 -g "$F"
printf '\n  Rendered diagrams: open demo/out/DEMO.md in VS Code → Markdown: Open Preview\n'

scene "8. Give any AI assistant this context (MCP)" \
  "Same answers inside Claude Code / Copilot / Cursor / Continue. In Claude Code, try:"
cat <<'EOF'
    > Using ekc-eshop, how does placing an order flow through the system,
      and what would break if I changed how OrderStatusChangedToPaid is published?
EOF

printf '\n\033[1mDone.\033[0m Questions to ask the audience: where would this have prevented your last incident?\n'
