<h1 align="center">Funded Path</h1>

<p align="center">
  <strong>A NinjaTrader 8 add-on that tells you, while you are still in the trade, whether you are passing your prop-firm challenge — and what breaks first.</strong>
</p>
<p align="center">
  Your platform shows you a P&amp;L. It does not show you the end-of-day trailing floor that actually ends your account, because that number lives in the firm's rulebook, not in NinjaTrader. This window computes it from your own fills and puts it on screen next to the balance.
</p>

<p align="center">
  <a href="#the-floor-is-the-part-people-get-wrong">The floor</a> ·
  <a href="#one-run-or-one-per-day">Run modes</a> ·
  <a href="#status">Status</a> ·
  <a href="#install">Install</a> ·
  <a href="#the-rulebook-it-models">Rulebook</a> ·
  <a href="#limitations">Limits</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-phase%201%20built-F2B33D?style=flat-square" alt="status: phase 1 built">
  <img src="https://img.shields.io/badge/platform-NinjaTrader%208-4A7DFF?style=flat-square" alt="platform: NinjaTrader 8">
  <img src="https://img.shields.io/badge/C%23-7.3-27D67B?style=flat-square" alt="C# 7.3">
  <img src="https://img.shields.io/badge/tests-84%20passing-27D67B?style=flat-square" alt="unit tests">
  <img src="https://img.shields.io/badge/account-read--only%20until%20armed-FF5468?style=flat-square" alt="read-only on the account until you arm enforcement">
  <img src="https://img.shields.io/badge/license-MIT-7A87A2?style=flat-square" alt="MIT license">
</p>

<p align="center">
  <img src="docs/images/funded-path-window.png" alt="The Funded Path window running in NinjaTrader on a Market Replay account: the selector row reads Lucid Trading / LucidPro / 50K PRO EVAL / daily loss limit Off / drawdown EOD trailing, the phase chip says REPLAY day 1, a gold session curve climbs from $50,000 to $53,013 above a dotted red floor at $48,000, the verdict chip reads PASSED - $3,013 OF PROFIT, and the right rail shows To target Reached, Room to floor $5,013, Account equity $53,013, Floor status Trailing, and the day's trade count" width="960">
</p>
<p align="center"><em>The window running in NinjaTrader 8, first Market Replay session: a 50K PRO EVAL rehearsed on a Playback account, passed on day one at <strong>+$3,013</strong>, with <strong>$5,013</strong> of room above a floor that has not ratcheted yet. The banner under the phase chip is the guarantee that makes rehearsing safe — a replay keeps its own ledger and cannot move a real challenge's high-water mark. Two things visible in this capture were fixed straight after it: the x-axis repeated the same minute stamp several times, and that last card headlined 35 <em>fills</em> over a 24/3 split of closed <em>trades</em> — two different denominators stacked as if they added up. It reads <code>TRADES TODAY</code> now. The design mockup this layout was built from is kept at <a href="docs/images/funded-path-design.png">docs/images/funded-path-design.png</a>.</em></p>

---

## The floor is the part people get wrong

A prop-firm challenge is arithmetic you are doing in your head while you trade. Most of it is easy:
the profit target is a fixed number, the daily loss limit is a fixed number. One rule is not, and it
is the one that ends accounts.

On a **50K LucidPro evaluation** your minimum balance is not $48,000. It is:

```
floor = max(closing balances so far) - 2,000
```

It **ratchets up every night you close green**, and it never comes back down. Close at $51,400 and
your floor is now $49,400 — a good day has quietly moved the line that kills you $1,400 closer.
That is the number nobody has on screen.

**But it stops.** Once a day closes at or above **$52,100**, the floor freezes at **$50,100** and
stops chasing you, permanently. From that moment you are trading against a locked $50,100 stop-out
and every dollar above it is yours to risk. Traders routinely do not know they have crossed that
line — or believe they have when they have not.

```
floor(day i) = min( max(SeededHwm, closes before day i) - MaxLoss , StartBalance + 100 )
```

`SeededHwm` is your start balance, or the peak end-of-day close you type in when you bind an account
mid-challenge — see [First run](#first-run). Bind on day one and it is just the start balance, and
the expression is the plain one.

That is the floor of **one running challenge**, which is the default and the way a real evaluation is
actually traded. A binding can instead treat every day as its own challenge — for judging an
automated strategy one Market Replay day at a time — and then there are no earlier closes to trail
from, so the whole expression collapses to a flat `start − max loss` that never moves.
[Both modes, and when to pick which.](#one-run-or-one-per-day)

That one expression is the whole product. Everything else on the window exists to put it in context:
how much room you have to it right now, how much is left to the target, and which of the two you hit
first. It is computed from **end-of-day closes only** — never from an intraday high — because that is
how Lucid computes it, and being wrong in either direction is expensive.

**And the window names which one.** Under the verdict there is one quiet line: whichever of
{profit target, floor, daily loss limit, consistency, trading days, payout minimum} has the least
slack right now, in dollars wherever a dollar figure exists —

```
Floor - $800 of room
Consistency - best day is $460 over the 40% cap; payout blocked
Trading days - 2 more days needed
```

The verdict pill is the answer; this line is why. On the funded phase it also carries the
consistency readout — `best day $1,240 vs $980 cap` — because the rail's fifth card is already spent
on the *other* payout gate, the day count.

---

## Status

| Layer | State |
|-------|-------|
| **Engine** (`Engine/`) | Complete. Pure C# 7.3, deterministic, zero NinjaTrader references. **84 unit tests passing.** |
| **NT8 add-on** (`NinjaTrader/`) | **Built and running.** Compiles clean through the full-tree gate (`tools/verify-compile.sh`) and passed its first Market Replay rehearsal on 2026-08-22 — that session is the hero image above. Not yet run against a real funded challenge. |
| **Firms modelled** | **One: Lucid Trading, LucidPro** — all four sizes, all three phases. MyFundedFutures, Apex and Topstep appear in the dropdown greyed out and labelled "not modelled yet". They are placeholders, not rulebooks. |
| **Rule fidelity** | The evaluation phase carries **no open disagreement**: every value is verified against Lucid's own help centre, except the 25K daily loss limit, which is settled from the trader's own dashboard. **Four are still open** — three in the funded and live phases, one cross-phase (whether unrealized P&L counts against the floor). All four ship as specified and all are surfaced in the window's warning block, which prints the first six warnings and carries the whole list, in order, on hover; three more were closed on 2026-08-22. See [`docs/rules-sources.md`](docs/rules-sources.md). |

---

## Four states, and one safe default

NinjaTrader cannot tell an evaluation account from a funded account from your own personal live
money. All three arrive over a broker connection and are identical to `Account.Provider`. So the
cockpit does not guess.

**Every account starts `Untracked`.** It is measured by nothing and recorded nowhere until you
explicitly bind it to a challenge.

| State | How it is decided | Colour |
|-------|-------------------|--------|
| `Untracked` | **no binding — the default for every account** | `#4E5A74` |
| `Replay` | the account is a Playback account *and* it has a binding | `#F2B33D` |
| `Evaluation` | the binding says Evaluation | `#4A7DFF` |
| `LiveSim` | the binding says LiveSim (funded) | `#27D67B` |
| `Live` | the binding says Live | `#9B7BFF` |

**Your own live account can never be counted by accident.** There is no auto-detection to misfire,
and Untracked means untouched: an unbound account gets no event subscription, no balance read, no
scan of its executions and no file on disk. It appears in the account dropdown so that you can bind
it, and that is the whole of the cockpit's contact with it.

Ledgers never mix either: the ledger key is `Provider + "|" + AccountDisplayName`, so a Playback
rehearsal of your evaluation cannot move the real challenge's high-water mark. That exact
contamination is what corrupted an earlier tool's ledger; it is designed out here.

---

## One run, or one per day

A prop evaluation is **one run that spans days**: yesterday's profit is in today's balance, and every
green close drags the floor up behind you. That is what this window measures by default, and it is
what your firm's dashboard shows you.

There is a second question with the same rulebook, and it is the one you ask in Market Replay:
**did *this day* pass?** Run a day, read the verdict, jump to the next day. A running challenge
answers it wrongly twice over — yesterday's +$700 is sitting in today's balance, and yesterday's
close has already moved today's floor, so day 3 passes partly because day 2 was green.

So each binding chooses **how days count**, in the binding dialog, and everything else follows:

| | **One running challenge** *(default)* | **Each day is its own challenge** |
|---|---|---|
| Balance | start balance + every completed day + today | start balance + today, and nothing else |
| Floor | `max(closes so far) − max loss` — ratchets on every green close, freezes at `start + 100` | `start − max loss`. Flat all day, the same every day, never ratchets |
| Peak day close | seeds the high-water mark | ignored — it is a claim about a run, and there is no run |
| Profit target | the plan's target, reached over the run | the plan's **full** target, in one day. LucidPro allows a one-day pass, so the question is fair as posed |
| A breach | latches until you reset it | latches for that day; the next trading day starts clean |
| Enforcement | as armed | as armed — the day broke, so the strategy stops |
| The day ledger | the run's history: summed into the balance, feeds the floor | a **scorecard**: one row per day, that day's P&L and that day's own verdict, never summed |
| The Challenge chart | one compounding curve | one point per day at `start + that day's own P&L`, under a flat floor line |
| Fifth rail card | `TRADING DAYS` | `DAYS PASSED` — *3 of 10*, each day judged on its own |

**Neither choice deletes anything.** Both modes write the same ledger file, day by day, and each day
keeps its own row on disk — its P&L, its fills, and whatever the panel recorded about a rule break. The mode changes what counts, not what is kept — switch back and the
run reads exactly as it did. And every binding is a running challenge unless you say otherwise: a
`bindings.xml` written before this option existed carries no run mode and loads as one, so nothing
starts counting differently because you updated the add-on.

### Judging an automated strategy in Market Replay

1. **Bind the Playback account** to the plan you are testing — same firm, plan, size and phase you
   would actually buy. A replay account keeps its own ledger and can never move a real challenge's
   high-water mark, so this is safe to do against the plan you are really trading.
2. **Set "How days count" to "Each day is its own challenge."** Leave the peak day close empty —
   it is not used in this mode.
3. **Decide whether to arm enforcement.** Armed, a strategy that breaks a rule gets flattened and
   stopped mid-day, which is what the firm would do to it — the day ends where it would really have
   ended. On "Warn me only" the day runs to the close and you read the damage afterwards.
4. **Run the replay day.** The verdict answers *this* day: `PASSED` at the plan's full target,
   `BREACHED` at the flat floor, `DAY LOCKED` at the daily limit if you bought one, `IN PROGRESS`
   until one of them happens.
5. **Jump to the next replay day.** It opens at the plan's start balance, with a fresh floor and a
   clean latch. Yesterday's result is on the scorecard, not in today's arithmetic.
6. **Read the scorecard.** The Challenge view is a row of independent outcomes, one point per day,
   and the fifth rail card reads `DAYS PASSED — 3 of 10`. Ten replay days, ten verdicts, in one
   session.

**Starting over, in either mode.** **NEW RUN**, in the toolbar, makes today the run's first day:
every day already recorded stops counting toward the run, a latched breach is cleared, and the record
of what the panel did about a rule break is dropped. Your history stays in the ledger file — it
simply stops counting. The latched-breach banner offers the same action as **START A NEW RUN**,
because starting again is what you actually want after a breach.

It asks before it does any of it, and the question says the part that matters out loud: **it changes
only what this panel remembers.** If your firm logged a breach, the account is still breached with
them and nothing in this repository can give it back — and if the account is still under its floor,
the panel latches again on the very next tick.

---

## What it reads, and what it never does

**Reads, per bound account:**

- `Account.Executions` → closed trades via `SystemPerformance.Calculate`, bucketed into ET trading
  days by each trade's **exit** time, to build the closing-balance series the floor trails on.
- `AccountItem.CashValue` for realized balance, `AccountItem.UnrealizedProfitLoss` for open P&L.
- The Playback clock (`Connection.PlaybackConnection.Now`) **when the bound account is itself a
  Playback account**, so a replay rehearsal ages against the replay's calendar — which can run at
  24x or sit paused — and not against your wall clock. Every other account ages against
  `Core.Globals.Now` even while Market Replay is connected, so a live account's trading day is never
  dated by somebody else's replay.

**Writes, in total: two XML files**, both under `Documents/NinjaTrader 8/FundedPath/`, both
written temp-then-swap — so each leaves a `.tmp` for the length of the write and keeps the previous
version as `.bak`.

- `bindings.xml` — your account bindings. One file, every account.
- `days-<provider>_<account>.xml` — **one per bound account**: the ledger of completed trading days.
  It is not a cache. `Account.Executions` holds the current session only, roughly three days, so
  after an NT8 restart a 20-day evaluation cannot be rebuilt from the platform at all. In a running
  challenge a green day that goes missing lowers the high-water mark, which lowers the floor, which
  reports *more* room than you have — the dangerous direction. Only the day in progress comes from
  executions; everything before it comes from this file. Deleting it is not a harmless reset.
  Both run modes write it; a per-day binding reads it as a scorecard of finished days rather than as
  the history of a run, so there a missing day is a hole in your record, not a wrong floor.

**Never, under any circumstance:** places, modifies or cancels an order. There is no order-entry
surface anywhere in this repository.

**It can, if you arm it, close your positions.** That is the one thing it does to an account, it is
off by default in every new binding, and you turn it on per account in the binding dialog, under a
control that spells out what it will do before you can choose it. Armed, a latched rule break makes
it flatten every open position on that account at market and stop the NinjaScript strategies running
on it — what the firm does to you, done a few seconds earlier and on your own terms. It holds back
and warns instead whenever this panel is running on data it cannot vouch for: a bindings file that
failed to load, a missing Eastern time zone, an account in another currency, a non-finite number in
the day ledger. A tool that flattens on bad data is worse than one that does nothing.

<p align="center">
  <img src="docs/images/funded-path-enforcement.png" alt="The Funded Path window on a Playback account after a rule break: a red banner reads BREACHED - $265.50 below the floor - Aug 22 20:58 ET - CONFIRMED at Aug 22 20:58 ET: no open position and no working order remain. The session curve climbs to $52,000, rolls over and ends inside the shaded red band under the floor, and the right rail reads To target $4,874 at 0% done, Room to floor -$305.50 against a floor of $48,431.50 on an equity basis, Account equity $48,126 at -$2,305.50 for the session, Floor status Trailing, and 14 trades today" width="960">
</p>

<p align="center"><em>The same window a moment after a break, on a replay account. What the banner reports is not
<em>&ldquo;a flatten was sent&rdquo;</em> — it is <strong>&ldquo;CONFIRMED &hellip; no open position and no working order
remain&rdquo;</strong>, read back from the account after the fact. Sending an instruction and having none of your
risk left are different claims, and only the second one is worth putting on a banner. The verdict is
<strong>latched</strong>: the curve is drawn against the floor it broke and stays broken there, because the firm
does not un-fail you when price comes back. The button in this capture says <code>RESET BREACH</code>; it is
now <code>START A NEW RUN</code>, and it does more than clear the latch — it moves the run's first counted day to
today, so the days already recorded stop counting, while nothing is deleted from the ledger on disk.</em></p>

**Every call that touches an account lives in one file**, `NinjaTrader/Enforcer.cs`, and
`tools/verify-compile.sh` proves it rather than asking you to trust it. Two tiers: order entry
(`Submit`, `OrderAction.`, `CreateOrder`, `StartAtmStrategy`, `AtmStrategyCreate`, `.Cancel(`,
`.Change(`) is banned in **every** file including the Enforcer; the account-mutating calls
(`.Flatten(`, `CancelAllOrders`, `SetState(State.`, plus the account-wide `FlattenEverything`) are
banned in all eleven other files and allowed only in the Enforcer. Line comments are stripped before
the grep, so the sentences describing the APIs it refuses to call cannot satisfy their own gate, and
the audit runs *before* the compile, so tripping it fails the run without building anything. The
guarantee is not that this code cannot touch your account — it is that you can read everything that
can, in one file, in one sitting.

---

## The rulebook it models

LucidPro, all four sizes. Every value was read from the trader's own Lucid dashboard on 2026-08-22
and then re-checked line by line against Lucid's help centre. Where the two disagreed, the dashboard
value ships and the disagreement travels with it, onto the window. Every row's source and status is
in **[`docs/rules-sources.md`](docs/rules-sources.md)**, which is the authority — this table is the
summary.

### Evaluation — verified

| Size | Start | Profit target | Max loss | Floor locks at | Trail stops on a close of | Daily loss limit | Max size |
|------|-------|---------------|----------|----------------|---------------------------|------------------|----------|
| 25K  | 25,000  | 1,250 | 1,000 | 25,100  | 26,100  | 600 *(dashboard)* or OFF | 2 mini / 20 micro |
| 50K  | 50,000  | 3,000 | 2,000 | 50,100  | 52,100  | 1,200 or OFF | 4 mini / 40 micro |
| 100K | 100,000 | 6,000 | 3,000 | 100,100 | 103,100 | 1,800 or OFF | 6 mini / 60 micro |
| 150K | 150,000 | 9,000 | 4,500 | 150,100 | 154,600 | 2,700 or OFF | 10 mini / 100 micro |

No consistency rule and no minimum trading days — a one-day pass is allowed. The daily loss limit is
bought ON or OFF at checkout and is **soft**: hitting it locks trading until the next session, it
does not end the account.

The 25K's $600 is the one evaluation figure Lucid's own articles do not print — two of them say
"None" and only the funded overview shows $600. It is settled from the dashboard, and the catalog
still shows you the counter-evidence before you switch that toggle on.

### Funded and Live — partially unverified

| Field | Ships as | Confidence |
|-------|----------|------------|
| Funded max loss, floor lock, buffer | eval MLL, `start + 100`, `start + MLL + 100` | verified |
| Funded consistency | 40%, largest day vs total profit, blocks the **payout** not the account | verified |
| Profit split | 90/10 | verified |
| **Funded payout profit goal** | flat $500 | **conflicted** — Lucid publishes $250 / $500 / $750 / $1,000 by size |
| **Days to payout** | 3 | **conflicted** — Lucid's payout article says there is no fixed window at all |
| **LucidScale DLL** | 60% of peak EOD **profit** | verified against Lucid's own worked example — but **displayed only, never enforced.** The "peak EOD balance" wording on the dashboard is loose |
| **Fixed DLL below the initial trail** | the evaluation's amount for the size, and only if you bought the limit ON | **dashboard-implied** — Lucid publishes the amounts and the one-time checkout choice, but never states that the choice carries into the funded account |
| **The entire Live phase** | one set of numbers for all four sizes | **unverified.** Lucid scales every live figure by size and locks the floor at $100, not $2,000. Do not trade against these readouts |
| **Does unrealized P&L count against the floor?** | a setting, defaulting to **yes (strict)** | **assumption.** Lucid's wording says balance; the strict default warns you earlier. Flip it in the binding dialog |

Anything marked conflicted or unverified is labelled as such **on the window**, not only here — the
engine attaches the catalog's own note to the state and the window prints it. The warning block
shows the first six and appends "(+N more - hover for all)", whose tooltip carries every warning in
order, and the engine's computed alarms are ordered ahead of the rulebook's prose, so a live alarm is
never pushed off the bottom by a footnote. Part of the
cockpit's job is telling you what it does not know.

---

## Install

Funded Path ships as source, like every NinjaScript add-on.

1. Copy **both** `Engine/*.cs` and `NinjaTrader/*.cs` **flat** into:

   ```
   Documents/NinjaTrader 8/bin/Custom/AddOns/FundedPath/
   ```

   Both folders' files go into that one folder, with no sub-folders. NinjaTrader compiles every `.cs`
   under `bin/Custom` into a single assembly, so the directory split in this repository is for
   humans — the engine is kept NinjaTrader-free so the test project can compile it, and that
   separation has no meaning once the files are deployed.

2. Open the **NinjaScript Editor** in NinjaTrader and press **F5** to compile.

3. **Control Center → New → Funded Path.**

Requires NinjaTrader 8. No NuGet packages, no DLLs, no external dependencies — persistence uses
`System.Xml.Linq`, which ships with the platform.

## First run

1. **Bind an account.** Pick your NT8 account and tell the cockpit what it is: firm, plan, size,
   phase. Nothing is measured until you do, and nothing but the accounts you bind is ever touched.
2. **Pick the challenge.** Size and phase set the whole rulebook — target, max loss, floor lock,
   buffer. Set the daily-loss-limit toggle to match what you actually bought at checkout.
3. **Choose how days count.** Leave it on *One running challenge* for a real evaluation: that is a
   run spanning days, scored the way the firm scores it. Switch it to *Each day is its own
   challenge* when you are judging a strategy one Market Replay day at a time and the only thing you
   want to know is whether each day passed. [The two modes, side by side.](#one-run-or-one-per-day)
4. **In a running challenge that did not start today, type in your highest end-of-day close.**
   The day series is rebuilt from `Account.Executions`, and NinjaTrader keeps only the current session there —
   roughly three days. Bind a 50K evaluation on day 12 and the platform can tell the cockpit nothing
   about days 1 to 9: the high-water mark would restart from $50,000 and the floor would read
   **$2,000 lower than the real one**, which is the direction that gets accounts closed. So the
   binding dialog asks for the peak close you have already made, previews the floor it produces while
   you type, and stores it with the binding: the engine starts its high-water mark there. Leave it
   empty when day one is today — and in per-day mode, where there is no run for a peak close to be a
   claim about and the box is ignored.

5. **If this account traded something else before the challenge, set the first day counted.**
   Ledger days that closed before that date are left out — an earlier evaluation, a reset, a
   rehearsal — so their closes cannot ratchet a high-water mark for a challenge that had not begun,
   and so they stay off a per-day scorecard. They are filtered, not deleted: a wrong date here loses
   no history. Leave it empty to count every day in the ledger.

6. **Done.** From here the window computes everything from your own fills, and each completed day is
   written to the ledger so the next restart still knows about it.

## Development

```bash
dotnet test                      # the engine: pure C#, no NinjaTrader, runs anywhere
tools/verify-compile.sh          # the add-on: account-write audit, then a full-tree compile in the real Custom tree
```

`verify-compile.sh` runs two gates: the account-write audit described above, then the compile. The
compile half exists because `nt8c check <file>` is per-file Roslyn — it cannot see sibling
files, so it reports cross-file references as errors the NinjaScript Editor compiles fine, and it
misses name collisions with the ~240 sources NinjaTrader ships under `bin/Custom`. The script stages
the real Custom tree, overlays our files, builds the whole thing, and keeps only the errors that name
our files. It is the closest thing to F5 that runs outside the platform.

Engine files must never reference a NinjaTrader type, directly or transitively. That is not a style
preference — it is what lets the floor algorithm be tested without launching a trading platform.

---

## Limitations

Read these before you rely on a number.

- **The live phase is unverified.** Its figures are the 50K row applied to all four sizes, and the
  floor lock level is encoded as $2,000 where Lucid says $100. On a live 50K the floor reads roughly
  $1,900 too high once locked. Detail in [`docs/rules-sources.md`](docs/rules-sources.md).
- **Consistency is measured over the whole account history**, not per payout cycle. Lucid resets the
  40% after every approved payout; the engine has no concept of a payout event, so from your second
  payout onward the consistency read is pessimistic. It will never call you compliant when you are
  not — only the reverse.
- **The LucidScale DLL is displayed, never enforced.** The catalog carries it as 60% of the highest
  end-of-day **profit** and the window says so, but no code path measures your day against it. What
  the engine *does* model is the boundary: at or above the Initial Trail Balance (`start + max loss
  + 100`) the fixed daily limit is **disarmed**, because that is where the firm hands over to the
  scaling one — so from there up, nothing on this window is watching your daily loss, and it says
  that out loud.
- **Inactivity is not tracked.** Lucid permanently deletes any account, evaluation or funded, with no
  trade producing at least $1 of net P&L in 30 calendar days. No floor logic catches that.
- **Payout maximums per cycle, and live contract caps by profit tier and exchange, are not modelled.**
- **NinjaTrader only remembers about three days of executions.** Everything older is whatever the
  day ledger recorded while the window was open — it cannot recover a day it never saw. In a running
  challenge, binding an account mid-challenge without seeding the peak close leaves the floor too
  low, and too low reads as *more* room than you have.
- **The day series comes from NinjaTrader's execution history and that ledger, and from nothing
  else.** A reset, a payout or a manual adjustment made in the firm's dashboard is invisible to the
  platform and therefore invisible here.
- **One firm.** The other three dropdown entries are labels, not rulebooks.
- **The floor is computed, not authoritative.** The firm's own dashboard holds the number that ends
  your account. This tool exists so that number does not surprise you — not to replace it.
- **A seeded peak close is only as good as what you type.** The engine starts its high-water mark at
  `max(start balance, peak close)` and the binding dialog previews the resulting floor as you type,
  but nothing can check the number against the firm. Type it wrong low and the floor reads low. A
  per-day binding drops the seed entirely — every day starts at the plan's start balance.
- **A per-day scorecard can only score a day it watched.** A day's verdict comes from what actually
  latched while the window was open. A day whose P&L reached the ledger but whose intraday path the
  panel never saw is scored on its close alone, so it reads *did not pass* rather than *breached* —
  it will never invent a rule break it did not see.
- **Per-day mode is built for evaluation days, not for payout planning.** On a funded binding it
  measures the 40% consistency rule inside a single day, where your best day *is* your total profit,
  so it will report the payout blocked every time. Judge funded consistency in a running challenge.

---

## Disclaimer

Funded Path is an **independent, unofficial** tool with **no affiliation, endorsement or
relationship** with Lucid Trading or any other proprietary trading firm. Firm names and rule values
appear here solely to describe what the software models.

Prop-firm rules change without notice, and this repository is a snapshot rather than a subscription.
**You remain solely responsible for your own compliance with your firm's rules**, and for verifying
any figure this software displays against your firm's dashboard before acting on it. Nothing here is
financial advice. Trading futures involves substantial risk of loss.

## License

[MIT](LICENSE) — Copyright (c) 2026 Javier Lora.
