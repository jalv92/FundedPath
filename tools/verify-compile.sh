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
# Two tiers, because this add-on is no longer purely read-only.
#
# NEVER_ANYWHERE: order entry. Nothing in this repo submits, amends or cancels an order, in any file,
# including the Enforcer. Flatten is documented to cancel the working orders as part of closing the
# position, so there is no reason to ever reach for these.
NEVER_ANYWHERE='Submit|OrderAction\.|CreateOrder|StartAtmStrategy|AtmStrategyCreate|\.Cancel\(|\.Change\('

# ENFORCER_ONLY: the calls that mutate an account. These are the whole reason enforcement exists, and
# they are allowed in exactly one file. The point of the split is that a reviewer never has to read
# twelve files to know what can touch the trader's money - the gate proves it is one.
# FlattenEverything is account-wide and undocumented: banned outright, in the Enforcer too.
ENFORCER_ONLY='\.Flatten\(|CancelAllOrders|SetState\(State\.|IsAutoLiquidationEnabled'
ENFORCER_FILE='NinjaTrader/Enforcer.cs'

echo "Auditing for order-submission APIs..."
HITS="$(cd "$REPO" && for f in Engine/*.cs NinjaTrader/*.cs; do
  # Comments are stripped first: the files explain what they deliberately do NOT call, and that prose
  # must not trip the gate that enforces it.
  STRIPPED="$(sed 's://.*::' "$f")"
  echo "$STRIPPED" | grep -nE "$NEVER_ANYWHERE" | sed "s|^|$f:|"
  echo "$STRIPPED" | grep -nE 'FlattenEverything' | sed "s|^|$f: (account-wide, undocumented) |"
  if [ "$f" != "$ENFORCER_FILE" ]; then
    echo "$STRIPPED" | grep -nE "$ENFORCER_ONLY" | sed "s|^|$f: (only $ENFORCER_FILE may touch the account) |"
  fi
done)"

if [ -n "$HITS" ]; then
  echo "FAIL -- an account-mutating call escaped NinjaTrader/Enforcer.cs, or an order API appeared:" >&2
  echo "$HITS" >&2
  echo "If one is a false positive, narrow the pattern in this script and say why in the same commit." >&2
  exit 1
fi
echo "PASS -- no order entry anywhere, and every account-mutating call is inside $ENFORCER_FILE."
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

# The verdict must NOT rest on grepping a log. This gate used to decide purely by whether any
# `: error ` line named one of our basenames, with nt8c's exit code thrown away -- so every way
# nt8c can fail WITHOUT producing such a line printed PASS. Reproduced: `nt8c build --custom-dir
# <missing>` exits 3 printing only `error: custom dir not found: ...`, and an empty tree exits 3
# printing `error: no .cs files found under ...`. Neither names our files, so both were a green
# run over a build that never happened.
#
# So: capture the exit code, ask for structured output, and refuse a vacuous pass.
OUT="$STAGE/build.json"
nt8c build --custom-dir "$STAGE/Custom" --no-emit --agent >"$OUT" 2>&1
RC=$?
OURS_COUNT="$(cd "$REPO" && ls Engine/*.cs NinjaTrader/*.cs | wc -l)"

python3 - "$OUT" "$RC" "$OURS" "$OURS_COUNT" <<'PY'
import json, re, sys
raw = open(sys.argv[1]).read()
rc, ours, ours_count = int(sys.argv[2]), sys.argv[3], int(sys.argv[4])

try:
    d = json.loads(raw)
except Exception:
    # A hard nt8c failure prints a bare `error: ...` line instead of JSON. Reading that as
    # "no errors named our files" is exactly the old bug.
    print(raw.strip()[:2000])
    print("FAIL -- nt8c did not produce JSON (exit %d). The build did not run." % rc)
    sys.exit(1)

errs = d.get("results", {}).get("errors", []) or []
n = d.get("meta", {}).get("files_compiled", 0) or 0

# A build that compiled fewer files than we staged did not compile the tree, whatever it says
# about errors. This floor is what makes a green mean something.
if n < ours_count:
    print("FAIL -- nt8c compiled %s files, fewer than the %d Funded Path sources staged. "
          "The build did not reach our code." % (n, ours_count))
    sys.exit(1)

pat = re.compile("(" + ours + ")")
mine = [e for e in errs if pat.search(e.get("file", "") or "")]

# TextPosition CS1503 is a known nt8c false positive (Vendor.dll vs Custom.dll enum identity)
# that the real Editor compiles fine. Suppressed, but COUNTED and printed: silently swallowing
# a whole error code is how a genuine CS1503 on a TextPosition argument would slip through.
def is_known_fp(e):
    return e.get("code") == "CS1503" and "TextPosition" in (e.get("message", "") or "")

fp = [e for e in mine if is_known_fp(e)]
mine = [e for e in mine if not is_known_fp(e)]

print("files compiled: %s   errors in tree: %d   in Funded Path files: %d"
      % (n, len(errs), len(mine)))
if fp:
    print("(suppressed %d known nt8c TextPosition CS1503 false positive(s) in our files)" % len(fp))

if mine:
    print("FAIL -- errors in our files:")
    for e in mine[:40]:
        print("  %s(%s,%s): %s %s" % (e.get("file", "?").split("/")[-1], e.get("line"),
                                      e.get("col"), e.get("code"), e.get("message")))
    sys.exit(1)

print("PASS -- no errors in Funded Path files.")
print("(stock-tree errors ignored: %d)" % (len(errs) - len(mine) - len(fp)))
PY
