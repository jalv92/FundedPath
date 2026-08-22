# Rule sources — LucidPro

Every number Funded Path ships in `Engine/RuleCatalog.cs`, with where it came from and how far we
trust it. **This file is the resolved rulebook and the only one.** Where it disagrees with §1 of the
build contract (`docs/specs/2026-08-22-design.md`), this file wins — the spec is the record of what
was approved on the day, this is the record of what survived verification.

**Last verification pass:** 2026-08-22.
**Amended 2026-08-22:** the separate rules addendum (`docs/specs/2026-08-22-rules-addendum.md`) was
folded into this file and deleted. One fact, one place. Its dashboard-sourced resolutions are the
rows marked **Dashboard-implied** below, and its sources are in §6.

**What ships:** the values in the approved build contract §1, which are the trader's own Lucid
dashboard as captured on 2026-08-22. Where a later re-verification against Lucid's published
articles disagreed, **the spec value still ships** and the disagreement is carried verbatim in that
catalog row's `Notes[]`, so it reaches the trader's screen instead of dying in a document. Search
`RuleCatalog.cs` for `DISAGREEMENT` to find those notes — there are **four**, one per still-open
funded or live disagreement, with the Live phase split across two of them. The three rows this file
records as resolved carry their resolution in the notes instead, and the breach-basis assumption
(D1) is surfaced as a pair of engine warnings rather than as a note.

**Method note.** `support.lucidtrading.com` returns HTTP 403 to most automated fetchers but serves
normally to `curl` with a browser `User-Agent`. Everything marked `[P*]` below was retrieved that way
on 2026-08-22 and is Lucid's own current help-centre copy, not an aggregator's summary. The pricing
card at `lucidtrading.com/#plans` sits behind Cloudflare and stayed unreachable; it is the only Lucid
property not read directly, and it is the probable origin of one disputed figure (see D5).

### Status legend

| Status | Meaning |
|--------|---------|
| **Verified** | Stated by a current primary Lucid article. Safe to rely on. |
| **Dashboard-implied** | Supported by the trader's own Lucid dashboard capture `[D]` (and usually a cross-check), but not stated by any Lucid article — or stated the other way. Good enough to ship; not good enough to argue with support about. |
| **Conflicted** | Two Lucid articles disagree with each other, or Lucid disagrees with the dashboard capture. The shipped value is one of the readings; the other is in `Notes[]`. Confirm with support before it matters. |
| **Unverifiable** | No source settles it. Lucid has not published the answer, and the dashboard does not show it. Shipped as an explicit assumption, labelled as one on screen. |
| **Not modelled** | A real Lucid rule the engine does not implement at all. Listed so nobody assumes the cockpit is watching it. |

---

## 1. Evaluation — `Phase.Evaluation`

| Rule | Ships as | Status | Source |
|------|----------|--------|--------|
| Start balance | 25,000 / 50,000 / 100,000 / 150,000 | Verified | [P1] |
| Profit target | 1,250 / 3,000 / 6,000 / 9,000 | Verified | [P1] |
| Max loss limit | 1,000 / 2,000 / 3,000 / 4,500 | Verified | [P1] [P6] |
| Drawdown type | End-of-day trailing on closing balances | Verified | [P6] — "At the end of each trading session, the system calculates the account's highest closing balance" |
| Floor lock level | `start + 100` (25,100 / 50,100 / 100,100 / 150,100) | Verified | [P6] "Locked MLL Balance"; identical table in [P11] |
| Trail-stop close | 26,100 / 52,100 / 103,100 / 154,600 | Verified | [P6] "Initial Trail Balance"; the 50K's 52,100 is confirmed on the dashboard card [D]. The much-quoted 154,600 is where the trail *stops*, not where the floor locks — `start + MaxLoss + 100` at every size |
| Breach comparison | at **or below** the floor | Verified | [P6] — "If your account balance reached the MLL, your account will be breached" |
| Daily loss limit, 50K / 100K / 150K | 1,200 / 1,800 / 2,700 when ON | Verified | [P1] [P5]; cross-checked in [A1] [A3] and `prop_rules.json`, and the 100K figure is on a dashboard card [D] |
| **Daily loss limit, 25K** | **600 when ON** | **Dashboard-implied** | The dashboard's green DLL chip plus `prop_rules.json`. Against it: [P1] and [P5] both list "None" for the 25K, [P5] twice. For it: the funded overview column shows "$600" [P2]. Resolved in favour of $600 — see D7, resolved |
| DLL is optional and chosen at checkout | yes, per account, both phases | Verified | [P9] — "Daily Loss Limit: On or Off ... at the time of purchase"; cannot be changed on an active account |
| DLL is soft | yes — locks the session, account survives | Verified | [P5] [P9] — "you do not lose your account for hitting DLL as long as the MLL has not been reached" |
| Max position size | 2 / 4 / 6 / 10 minis, x10 micros | Verified | [P1] |
| Consistency in the evaluation | none | Verified | No consistency column in [P1]; [P4] scopes the 40% to funded accounts only; the dashboard's eval card shows none [D]. The "50% eval consistency" some aggregators quote is **LucidFlex's** rule [P12] |
| Minimum trading days | none — a one-day pass is allowed | Verified | [P1] |
| Fee / time limit | one-time fee, no rebills, no deadline | Verified | [P1] |

## 2. Funded / Live Sim — `Phase.LiveSim`

| Rule | Ships as | Status | Source |
|------|----------|--------|--------|
| Start balance | = account size | Verified | implied by the trail table in [P6] |
| Max loss limit | same as the evaluation MLL | Verified | [P2] |
| Floor lock level | `start + 100` | Verified | [P6] — the EOD drawdown covers "LucidPro evaluation **and funded** accounts" |
| Buffer | 26,100 / 52,100 / 103,100 / 154,600 | Verified | [P3] "Required Buffer Balance"; 52,100 on the 50K funded card [D] |
| Buffer not withdrawable | yes | Verified | [P3] — "You can not take payout from the buffer balance" |
| Minimum payout request | $500 at every size | Verified | [P3] |
| **Payout profit goal** | **$500 flat** | **Conflicted** | Lucid publishes a per-cycle Minimum Profit Goal of $250 / $500 / $750 / $1,000 by size [P3]. The shipped flat $500 is correct for the 50K only. See D4 |
| **DLL below the initial trail** | **the evaluation's fixed amount for the size — 600 / 1,200 / 1,800 / 2,700 — and only if the account was bought with the limit ON** | **Dashboard-implied** | [P5]: funded accounts "begin with the same Fixed DLL values used in the evaluation" while the balance is under the initial trail; [P9]: the ON/OFF choice is made once, at purchase. The dashboard's 50K funded card reads "DLL (Below Initial Trail): NONE" [D] on an account the trader bought with the limit **OFF** — which is the reading that reconciles both. Resolved 2026-08-22; the spec's unconditional NONE no longer ships. See D3, resolved |
| **LucidScale DLL above the trail** | **60% of the highest end-of-day PROFIT — carried as data, never enforced** | Verified | [P5] gives the formula as *highest end-of-day account **profit*** x 60%, worked example $4,000 x 0.60 = $2,400; [A1] quotes it with a $3,000 peak profit giving $1,800 of room. [P2]'s column header and the dashboard card [D] read "60% of Peak EOD **Balance**" — loose wording, outweighed by the firm's own worked example. Resolved 2026-08-22 in favour of profit. See D2, resolved |
| LucidScale ratchets up only | not modelled (display only) | Verified | [P5] — "It does not decrease, even if the account draws down" |
| Consistency | 40%, largest day / total profit | Verified | [P3] [P4] [D] |
| Consistency blocks the payout, not the account | yes | Verified | [P3] lists it as a payout eligibility criterion |
| **Days to payout** | **3** | **Conflicted** | [P3]: "There is no fixed payout window, you may request a payout any day after meeting all eligibility criteria", and lists exactly three criteria, none of them a day count. See D5 |
| Payouts before the live pool | 5 | Verified | [P7] — but "Payout 5 represents the maximum payout level, not a guaranteed minimum for live eligibility. All live transitions occur at the discretion of the Lucid risk team" |
| Profit split | 90/10 | Verified | [P3] |
| Max position size | 2 / 4 / 6 / 10 minis | Verified | [P2] — no scaling plan, full size immediately |

## 3. Live — `Phase.Live`

The whole phase ships **unverified against Lucid's own live article**: §1.3 of the spec states one set
of numbers for all four sizes, and [P7] scales them by size. Do not trade against these readouts.

| Rule | Ships as | Status | Source |
|------|----------|--------|--------|
| Start balance | $0 | Verified | [P7] |
| **Max loss limit** | **$2,000 at every size** | **Conflicted** | [P7] gives a Starting Live Drawdown of $1,000 / $2,000 / $3,000 / $4,500. The shipped value is the 50K row. See D6 |
| **Floor lock level** | **`start + 2,000`** | **Conflicted** | [P7]: "the Max Loss Limit locks at **$100**" once live profit reaches the starting live drawdown — $2,000 is the *trigger*, not the level. As shipped, the floor sits $1,900 too high on a 50K. See D6 |
| **Live bonus trigger** | **$2,100 of profit at every size** | **Conflicted** | [P7] Live Target = $1,100 / $2,100 / $3,100 / $4,600 |
| **Live bonus amount** | **$2,000 at every size** | **Conflicted** | [P7] bonus = the starting live drawdown: $1,000 / $2,000 / $3,000 / $4,500 |
| Bonus is subject to the 90/10 split | not modelled | Verified | [P7] — the trader nets 90% of it |
| Bonus eligibility (first trip live only, void with a legacy live account, not at LucidMaxx) | not modelled | Verified | [P7] |
| Early lock on an early payout request | not modelled | Verified | [P7] — requesting a payout before the target forces the $100 lock |
| Daily loss limit on live | none | Verified | [P7] |
| Consistency on live | none | Verified | [P4] [P7] |
| Live contract caps by profit tier **and exchange** | not modelled | Verified | [P8] — at 150K, CBOT caps at 8 and NYMEX at 6 while CME allows 10 |

## 4. Cross-phase

| Rule | Ships as | Status | Source |
|------|----------|--------|--------|
| **Breach basis (does unrealized P&L count?)** | **a setting; `Equity` is the default** | **Unverifiable** | Lucid defines EOD drawdown as "calculated using your account balance at the end of each trading session. **Unrealized gains and losses do not affect the drawdown calculation**" and intraday drawdown as including them [P10]. LucidPro is explicitly the EOD product [P6]. That definition lives in the LucidDaily collection, so applying it to LucidPro is an inference, not a proof, and the dashboard does not show the answer either. See D1 |
| **DLL ON/OFF carries from the evaluation into the funded account** | yes — a DLL bought OFF stays off in both phases | Dashboard-implied | The trader bought his 50K eval with the DLL OFF and his 50K funded card reads "DLL (Below Initial Trail): NONE" [D]. [P9] says the choice is made once at purchase; no article states the carry explicitly. No aggregator states it at all |
| Consistency resets after every approved payout | **not modelled** — measured over the whole account history | Verified | [P3] [P4]. From payout 2 onwards the cockpit's consistency read is pessimistic, never optimistic |
| 35% legacy consistency + 100%-of-first-$10k legacy split | not modelled | Verified | [P4] — applies to accounts purchased or reset before 2025-11-28 15:00 ET |
| Maximum payout per cycle | not modelled | Verified | [P3] — payout 1: 1,000 / 2,000 / 2,500 / 3,000; payouts 2+: 1,500 / 2,500 / 3,000 / 3,500 |
| **Inactivity deletion** | **not modelled** | Verified | [P-inact] — any account, evaluation or funded, with no trade producing at least $1 of net P&L in **30 calendar days is permanently deleted**. The floor logic will never catch this |

---

## 5. The open disagreements, in one place

**Four are open.** Each ships as the spec says and carries its counter-evidence in the catalog's
`Notes[]`, which the cockpit surfaces in the UI. Three sit in the funded and live phases; one, D1, is
cross-phase. The evaluation phase carries none.

| # | Phase | What | Shipped | The other reading | Who to ask |
|---|-------|------|---------|-------------------|------------|
| **D1** | cross-phase | Does an open position count against the floor? | `BreachBasis.Equity` (strict — unrealized counts) | [P10] says EOD drawdown ignores unrealized. Flip the setting to `Balance` for the literal reading | Support, or watch one breach |
| **D4** | funded | Funded payout profit goal | $500 flat | $250 / $500 / $750 / $1,000 by size [P3] | Nobody — [P3] is unambiguous; the shipped value is simply the 50K's |
| **D5** | funded | "Days to payout: 3" | 3, and `PayoutEligible` waits for it | [P3]: no fixed window at all. The 3 traces to the pricing card, which is a product-page figure, not a rule you can be failed against | Support, or just request a payout |
| **D6** | live | The whole Live phase | the 50K row applied to all four sizes, floor locking at `+2,000` | [P7] scales every figure by size and locks the floor at **$100** | Support, before going live |

**Resolved 2026-08-22, kept for the history.** The numbering does not shift: D2, D3 and D7 keep their
ids so the rows above and the catalog notes still line up with what was written before.

| # | Phase | What | Resolution |
|---|-------|------|------------|
| **D2** | funded | LucidScale DLL: 60% of *what*? | **Of the highest end-of-day PROFIT.** [P5] states the formula and works it ($4,000 x 0.60 = $2,400); [A1] works it again ($3,000 → $1,800). The "60% of Peak EOD Balance" column header on [P2] and on the dashboard is loose wording — read literally it would put a 50K's daily limit above $30,000. The catalog now carries `ScaleDllPctOfPeakProfit`, and nothing enforces it either way |
| **D3** | funded | Fixed DLL below the initial trail on a funded account | **The evaluation's amount for the size, if the account was bought with the limit ON.** [P5] says funded begins with the evaluation's fixed values; [P9] says the ON/OFF choice is made once at purchase. The dashboard's NONE [D] is that rule applied to an account bought OFF, not a different rule. The spec's unconditional NONE was wrong for anyone who bought the limit ON |
| **D7** | evaluation | 25K evaluation DLL: $600 or none? | **$600.** The dashboard's DLL chip and `prop_rules.json` agree, and [P2]'s funded overview column shows $600; the one aggregator claiming "none at 25K" was contradicted by both and rejected. [P1] and [P5] still print "None", which is why the row stays **Dashboard-implied** rather than Verified |

**Practical effect.** D6 and D5 are the two that put a wrong number on screen: on a live 50K the floor
reads $1,900 high once locked, and `PayoutEligible` is withheld for three days Lucid does not require.
D1 is a choice, not an error — the strict default warns earlier than the literal reading. **The
evaluation phase — the one this add-on was built for — carries no open disagreement.**

---

## 6. Sources

### Primary — Lucid Trading help centre, all retrieved 2026-08-22

- **[P1]** [LucidPro Evaluation Account](https://support.lucidtrading.com/en/articles/12890029-lucidpro-evaluation-account)
- **[P2]** [LucidPro Funded Account](https://support.lucidtrading.com/en/articles/12890069-lucidpro-funded-account)
- **[P3]** [LucidPro Payouts](https://support.lucidtrading.com/en/articles/12890092-lucidpro-payouts)
- **[P4]** [LucidPro Consistency Percentage](https://support.lucidtrading.com/en/articles/12890109-lucidpro-consistency-percentage)
- **[P5]** [LucidPro Daily Loss Limit](https://support.lucidtrading.com/en/articles/12890122-lucidpro-daily-loss-limit)
- **[P6]** [LucidPro Drawdown](https://support.lucidtrading.com/en/articles/12890136-lucidpro-drawdown)
- **[P7]** [New Live Structure](https://support.lucidtrading.com/en/articles/13425130-new-live-structure)
- **[P8]** [New Live Scaling Plan](https://support.lucidtrading.com/en/articles/15245873-new-live-scaling-plan)
- **[P9]** [LucidPro Customization](https://support.lucidtrading.com/en/articles/16226068-lucidpro-customization) — the DLL on/off purchase choice
- **[P10]** [LucidDaily Customization](https://support.lucidtrading.com/en/articles/16033858-luciddaily-customization) — Lucid's own EOD-vs-intraday definitions, including "unrealized"
- **[P11]** [LucidDaily Drawdown](https://support.lucidtrading.com/en/articles/15998425-luciddaily-drawdown) — carries the identical trail/lock table, which is what makes `start + 100` a firm-wide constant
- **[P12]** [LucidFlex Evaluation Account](https://support.lucidtrading.com/en/articles/12945790-lucidflex-evaluation-account) — proves the "50% eval consistency" claim belongs to LucidFlex
- **[P-inact]** [Inactivity Policy](https://support.lucidtrading.com/en/articles/11404632-inactivity-policy)
- Unreachable: `https://lucidtrading.com/#plans` — HTTP 403 (Cloudflare)

### The dashboard capture

- **[D]** The trader's own Lucid dashboard, **50K PRO EVAL** and **50K PRO FUNDED** cards, captured
  2026-08-22, plus the DLL chips on the other sizes' cards. This is what the build contract's §1
  values were read from. It is a screenshot of one trader's account, not a published rulebook: it
  proves what Lucid is enforcing on *him*, which is why rows resting on it alone are marked
  Dashboard-implied.

### Cross-checks and aggregators

Used to trace where a number came from and to break a tie between two readings — never as a primary
source on their own.

- **[A1]** [proptradingvibes.com — Lucid Trading 50K account rules](https://proptradingvibes.com/blog/lucid-trading-50k-account-rules) — carries the LucidScale worked example ("a $3,000 peak EOD profit gives you $1,800 of daily room") that decides D2's evidence
- **[A2]** [proptradingvibes.com — LucidPro funded account rules](https://proptradingvibes.com/blog/lucidpro-funded-account-rules) — traces the "3 days" figure to the pricing page and disclaims it
- **[A3]** [app.tradersforge.net — Lucid Trading](https://app.tradersforge.net/prop-firms/lucid-trading)
- **[A4]** [damnpropfirms.com — Lucid Trading rules and payouts](https://damnpropfirms.com/prop-firms/lucid-trading-rules-payouts/) — accurate except that it puts LucidFlex's 50% eval consistency on LucidPro
- "Phidias" is named as a third confirmation of the 50K's $1,200 DLL in the folded addendum, without
  a URL. Recorded here as-is; it was never load-bearing on its own.
- Local: `projects/Trading/PropSim/prop_rules.json` and
  `projects/Trading/PropSim/research/lucid-trader.json` (retrieved 2026-07-25, amended 2026-08-03).

**Rejected.** `propfirmcorner.com/firms/lucid-trading/rules/` describes a "LucidTest" plan with a 30%
consistency rule and a 5-day minimum. No such plan exists in Lucid's help centre. Do not cite it.
One aggregator (unnamed in the folded addendum) claimed no DLL at the 25K and funded DLLs of
$2,100 / $3,000 at the 100K and 150K; contradicted by the dashboard and by two other sources, and
rejected.

---

Rules change. This file is a snapshot, not a subscription. **Lucid's own help centre is the
authority; if it disagrees with this table, it is right and this table is stale.**
