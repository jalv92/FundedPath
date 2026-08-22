#!/usr/bin/env bash
# Full-tree compile gate for Funded Path, plus the read-only audit.
#
# `nt8c check <file>` is per-file Roslyn: it cannot see sibling files, so it reports cross-file
# references as errors that F5 compiles fine, and it misses name collisions with the ~240 @*.cs
# sources NinjaTrader ships under bin/Custom. The only check that simulates the NinjaScript
# Editor's F5 is a build of the FULL tree with our files overlaid, then filtering the errors down
# to the ones that name our files -- the stock sources spray errors this tool cannot resolve
# (missing refs, LangVersion) that the real Editor compiles without complaint.
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Per-machine. Override with NT8_CUSTOM=... if NinjaTrader lives somewhere else, or if you are on
# native Windows rather than WSL. The default is the WSL view of the standard Windows install.
CUSTOM="${NT8_CUSTOM:-/mnt/c/Users/$(whoami)/Documents/NinjaTrader 8/bin/Custom}"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# ---- gate 1: the account is read-only -------------------------------------------------------
#
# The README promises this add-on cannot place, modify or cancel an order. A promise nothing checks
# rots the first time somebody is in a hurry, so it is a gate: any order-submission API named in
# Engine/ or NinjaTrader/ fails the run. Line comments are stripped before matching, because these
# files describe the APIs they refuse to call and those sentences must not trip their own gate.
ORDER_API='Submit|OrderAction\.|CreateOrder|StartAtmStrategy|AtmStrategyCreate|\.Cancel\(|\.Change\('

echo "Auditing for order-submission APIs..."
HITS="$(cd "$REPO" && for f in Engine/*.cs NinjaTrader/*.cs; do
  sed 's://.*::' "$f" | grep -nE "$ORDER_API" | sed "s|^|$f:|"
done)"

if [ -n "$HITS" ]; then
  echo "FAIL -- this add-on is read-only on the account, but these lines name an order API:" >&2
  echo "$HITS" >&2
  echo "If one of them is a false positive, narrow ORDER_API in this script and say why." >&2
  exit 1
fi
echo "PASS -- no order-submission API in Engine/ or NinjaTrader/."
echo

# ---- gate 2: it compiles inside the real Custom tree -----------------------------------------

if [ ! -d "$CUSTOM" ]; then
  echo "NinjaTrader Custom tree not found at: $CUSTOM" >&2
  exit 1
fi

echo "Staging the real Custom tree (.cs only)..."
rsync -a --include='*/' --include='*.cs' --exclude='*' "$CUSTOM/" "$STAGE/Custom/"

echo "Overlaying Funded Path..."
mkdir -p "$STAGE/Custom/AddOns/FundedPath"
cp "$REPO"/Engine/*.cs "$REPO"/NinjaTrader/*.cs "$STAGE/Custom/AddOns/FundedPath/"

# Listed from inside the repo on purpose: the repo path contains a space, so `ls "$REPO"/Engine/*.cs`
# word-splits it and the filter picks up a bare "Code" alternative that matches any error line
# mentioning NinjaTrader.Code -- inventing failures that are not ours.
OURS="$(cd "$REPO" && ls Engine/*.cs NinjaTrader/*.cs | xargs -n1 basename | paste -sd'|')"
echo "Building the full tree; keeping only errors that name: $OURS"
echo

OUT="$STAGE/build.log"
nt8c build --custom-dir "$STAGE/Custom" --no-emit >"$OUT" 2>&1

# TextPosition CS1503 is a known nt8c false positive (Vendor.dll vs Custom.dll enum identity) that
# the real Editor compiles fine; it is filtered out here so it cannot mask a real failure.
MINE="$(grep -E "($OURS)" "$OUT" | grep -E ': error ' | grep -v 'CS1503.*TextPosition' || true)"

if [ -n "$MINE" ]; then
  echo "FAIL -- errors in our files:"
  echo "$MINE"
  exit 1
fi

echo "PASS -- no errors in Funded Path files."
grep -cE ': error ' "$OUT" | sed 's/^/(stock-tree errors ignored: /; s/$/)/'
