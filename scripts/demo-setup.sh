#!/usr/bin/env bash
# One-time preparation of the EKC POC demo on this laptop (works with macOS bash 3.2).
#
#   scripts/demo-setup.sh               build, test, install `ekc`, fetch eShop, build maps and the diagram pack
#   scripts/demo-setup.sh --skip-tests  faster
#
# Everything the demo needs afterwards is local: it runs with Wi-Fi off (except the cloud AI assistant).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/demo/out"
ESHOP="$ROOT/samples/external/eshop"
ESHOP_COMMIT="913acea66708a59595351174cee9b29f37179d29"   # dotnet/eShop main, 2026-10-15 (tested)
VERSION="0.1.0"
SKIP_TESTS=false
[ "${1:-}" = "--skip-tests" ] && SKIP_TESTS=true

step() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
ok()   { printf '\033[32m ok\033[0m  %s\n' "$*"; }
die()  { printf '\033[31mfail\033[0m %s\n' "$*" >&2; exit 1; }

step "Checking this Mac"
command -v git >/dev/null || die "git is required"
command -v dotnet >/dev/null || die ".NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0"
dotnet --list-sdks | grep -q '^10\.' || die ".NET 10 SDK not found (dotnet --list-sdks)"
ok "macOS $(sw_vers -productVersion), $(( $(sysctl -n hw.memsize) / 1073741824 )) GB RAM, .NET $(dotnet --version)"

step "Building EKC"
cd "$ROOT"
dotnet build Ekc.slnx -c Release --nologo -v q
ok "build succeeded"
if ! $SKIP_TESTS; then
  dotnet test --solution Ekc.slnx -c Release --no-build 2>&1 | grep -E "total:|failed:|succeeded:" || true
fi

step "Installing the ekc command"
rm -rf "$OUT/nupkg"
dotnet pack src/Ekc.Cli -c Release -o "$OUT/nupkg" --nologo -v q
dotnet tool uninstall --global Ekc.Cli >/dev/null 2>&1 || true
dotnet tool install --global Ekc.Cli --add-source "$OUT/nupkg" --version "$VERSION" >/dev/null
export PATH="$HOME/.dotnet/tools:$PATH"
command -v ekc >/dev/null || die "ekc not on PATH; add: export PATH=\"\$HOME/.dotnet/tools:\$PATH\""
ok "ekc $VERSION installed at $(command -v ekc)"

step "Fetching dotnet/eShop (pinned commit)"
if [ ! -d "$ESHOP/src" ]; then
  mkdir -p "$ESHOP"
  git -C "$ESHOP" init -q
  git -C "$ESHOP" remote add origin https://github.com/dotnet/eShop.git
  git -C "$ESHOP" fetch -q --depth 1 origin "$ESHOP_COMMIT"
  git -C "$ESHOP" checkout -q FETCH_HEAD
fi
ok "eShop at $(git -C "$ESHOP" rev-parse --short HEAD 2>/dev/null || echo 'local copy')"

step "Building the system maps"
mkdir -p "$OUT"
ekc map samples/fabrikam -g "$OUT/fabrikam.graph.json"
ekc map "$ESHOP" --overlay samples/eshop/overlay.yaml -g "$OUT/eshop.graph.json"

step "Generating the diagram pack (open demo/out/DEMO.md with Markdown Preview)"
F="$OUT/fabrikam.graph.json"
E="$OUT/eshop.graph.json"
{
  echo "# EKC demo pack"
  echo
  echo "_Generated $(date '+%Y-%m-%d %H:%M') from source code by \`ekc\`. Open with **Markdown: Open Preview** (Mermaid extension)._"
  echo
  echo "## 1. Fabrikam Insurance: system map"
  echo '```mermaid'; ekc export c4 -g "$F"; echo '```'
  echo
  echo "## 2. Fabrikam: \"Submit a claim\""
  echo '```mermaid'; ekc flow "Submit a claim" --format mermaid -g "$F"; echo '```'
  ekc flow "Submit a claim" -g "$F"
  echo
  echo "## 3. Fabrikam: what breaks if the coverage rule changes?"
  ekc impact ClaimValidator.EnsureWithinCoverage -g "$F"
  echo
  echo "## 4. eShop: system map"
  echo '```mermaid'; ekc export c4 -g "$E"; echo '```'
  echo
  echo "## 5. eShop: \"Validate stock and take payment\" (order saga)"
  echo '```mermaid'; ekc flow "Validate stock and take payment" --format mermaid -g "$E"; echo '```'
  echo
  echo "## 6. eShop: what breaks if Order.SetPaidStatus changes?"
  ekc impact Order.SetPaidStatus -g "$E"
} > "$OUT/DEMO.md"
ekc export c4 --format structurizr --name "Fabrikam Insurance" -g "$F" > "$OUT/fabrikam.structurizr.dsl"
ekc export c4 --format structurizr --name "eShop" -g "$E" > "$OUT/eshop.structurizr.dsl"
ok "demo/out/DEMO.md, *.structurizr.dsl"

step "Preparing the pull-request demo repository"
PR="$OUT/pr-demo"
rm -rf "$PR"
cp -R samples/fabrikam "$PR"
git -C "$PR" init -q -b main
git -C "$PR" add -A
git -C "$PR" -c user.name=demo -c user.email=demo@example.com commit -qm "Fabrikam baseline"
git -C "$PR" checkout -q -b feature/round-payment-amounts
sed -i '' 's/Amount = message.Amount,/Amount = Math.Round(message.Amount, 2),/' "$PR/src/Payments.Api/ClaimApprovedConsumer.cs"
git -C "$PR" -c user.name=demo -c user.email=demo@example.com commit -qam "Round payment amounts to 2 decimals"
ekc map "$PR" -g "$OUT/pr-demo.graph.json" >/dev/null
ok "demo/out/pr-demo (branch feature/round-payment-amounts, 1 commit ahead of main)"

step "Smoke test"
ekc flow "claims-api: POST /api/claims" -g "$F" | head -3
ekc impact --diff main...HEAD --repo "$PR" -g "$OUT/pr-demo.graph.json" | head -1

cat <<EOF

Ready. Next:
  scripts/demo.sh              guided 10-minute walkthrough (press Enter between steps)
  code demo/out/DEMO.md        diagrams (Markdown: Open Preview)
  claude                       Claude Code picks up .mcp.json (servers ekc-eshop, ekc-fabrikam)
Runbook: docs/demo-runbook.md
EOF
