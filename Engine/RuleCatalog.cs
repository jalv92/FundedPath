using System;
using System.Collections.Generic;

namespace FundedPath.Engine
{
    // The compiled rulebook. Compiled, not loaded from a file: a rule change is a code change that
    // goes through review, so a corrupt or hand-edited config can never quietly move the floor.
    //
    // Phase 1 models LucidPro only (spec 1). The other three firms are present as single disabled
    // rows so the dropdown can show them greyed out instead of pretending they do not exist.
    //
    // Values are spec section 1 as written, EXCEPT where docs/rules-sources.md resolves them - that
    // file is the resolved rulebook and wins over the spec. Three of the seven open disagreements are
    // closed there (the 25K daily loss limit, what the LucidScale 60% is a percentage OF, and the
    // fixed limit below the initial trail); the remaining four still ship the spec value with the
    // conflict in that row's Notes, so it surfaces in the UI rather than dying in a document. Search
    // this file for "DISAGREEMENT".
    public static class RuleCatalog
    {
        const string LucidPlan = "LucidPro";

        // The floor lock is start + 100 on the EVALUATION and the FUNDED phase, identical on all four
        // sizes. Confirmed by the Initial Trail Balance the trader's dashboard reports: $52,100 on a
        // 50K = start + MaxLoss + 100, i.e. the trail stops once a day closes there, which puts the
        // floor at start + 100. (150K: 154,600 -> 150,100. Same rule.)
        //
        // The LIVE phase does NOT use this constant: that row ships FloorLockOffset = 2,000 from spec
        // 1.3, and its own note records that Lucid's live-structure article reads 2,000 as the
        // TRIGGER for the lock rather than the level. Live is unverified end to end; do not read the
        // evaluation/funded constant as covering it.
        const double LucidFloorLockOffset = 100.0;

        const string UrlEval    = "https://support.lucidtrading.com/en/articles/12890029-lucidpro-evaluation-account";
        const string UrlFunded  = "https://support.lucidtrading.com/en/articles/12890069-lucidpro-funded-account";
        const string UrlLive    = "https://support.lucidtrading.com/en/articles/13425130-new-live-structure";

        // Index-aligned per-size constants (spec 1.1). One column per array, one account size per
        // index, so a typo shows up as a misaligned row rather than as a plausible wrong number.
        static readonly int[]    Sizes      = { 25000, 50000, 100000, 150000 };
        static readonly double[] Targets    = { 1250.0, 3000.0, 6000.0, 9000.0 };
        static readonly double[] MaxLosses  = { 1000.0, 2000.0, 3000.0, 4500.0 };
        static readonly double[] DllWhenOn  = { 600.0, 1200.0, 1800.0, 2700.0 };
        static readonly int[]    MaxMinis   = { 2, 4, 6, 10 };

        static readonly List<PropRules> _all = new List<PropRules>();
        static readonly List<Firm> _firms = new List<Firm>();

        static RuleCatalog()
        {
            for (int i = 0; i < Sizes.Length; i++)
            {
                _all.Add(Evaluation(i));
                _all.Add(LiveSim(i));
                _all.Add(Live(i));
            }

            _all.Add(NotModelled(Firm.MyFundedFutures, "MyFundedFutures", "https://myfundedfutures.com/"));
            _all.Add(NotModelled(Firm.ApexTrader, "Apex Trader Funding", "https://apextraderfunding.com/"));
            _all.Add(NotModelled(Firm.TopstepTrader, "Topstep", "https://www.topstep.com/"));

            // Firms is derived from the rows rather than from Enum.GetValues so the dropdown can
            // never list a firm the catalog has no row for.
            for (int i = 0; i < _all.Count; i++)
                if (!_firms.Contains(_all[i].Firm))
                    _firms.Add(_all[i].Firm);
        }

        // The rows are shared, mutable objects. Read them freely; call Clone() before writing.
        public static IReadOnlyList<PropRules> All { get { return _all.AsReadOnly(); } }

        public static IReadOnlyList<Firm> Firms { get { return _firms.AsReadOnly(); } }

        public static bool IsModelled(Firm firm)
        {
            for (int i = 0; i < _all.Count; i++)
                if (_all[i].Firm == firm && _all[i].Modelled)
                    return true;
            return false;
        }

        // Returns the shared catalog row, or null when nothing matches (the caller then falls back
        // to Verdict.Untracked). A null or empty plan matches any plan, which is what a firm with a
        // single plan needs. Callers that intend to modify the result MUST Clone() it first.
        public static PropRules Find(Firm firm, string plan, int size, Phase phase)
        {
            // A firm with no modelled rows has exactly one placeholder row and no meaningful size
            // or phase, so match it on the firm alone - otherwise the dialog could not resolve the
            // greyed-out entry it is showing.
            bool modelled = IsModelled(firm);

            for (int i = 0; i < _all.Count; i++)
            {
                PropRules r = _all[i];
                if (r.Firm != firm) continue;
                if (!modelled) return r;
                if (r.Size != size || r.Phase != phase) continue;
                if (!string.IsNullOrEmpty(plan) && !string.Equals(r.Plan, plan, StringComparison.OrdinalIgnoreCase)) continue;
                return r;
            }
            return null;
        }

        // ---- LucidPro rows --------------------------------------------------------------------

        static PropRules Evaluation(int i)
        {
            List<string> notes = new List<string>();
            notes.Add("End-of-day trailing drawdown. The floor ratchets only on a session close, never on an intraday high, and freezes for good at start + $100.");
            notes.Add("Daily loss limit is chosen once at checkout and is SOFT: hitting it locks trading until the next session, it does not end the account. The trader's own 50K has it OFF.");
            notes.Add("The checkout choice is made once and covers BOTH phases: the same " + Money(DllWhenOn[i]) + " applies in the funded account, and buying the evaluation with the limit OFF means there is no fixed daily loss limit there either.");
            notes.Add("No consistency rule and no minimum trading days in the evaluation - a one-day pass is allowed. The 50% eval consistency quoted by some aggregators is LucidFlex's rule, not LucidPro's.");
            notes.Add("Inactivity, not modelled: an account with no trade producing at least $1 of net P&L in 30 calendar days is permanently deleted. Evaluations included.");

            return new PropRules
            {
                Firm            = Firm.Lucid,
                Plan            = LucidPlan,
                Phase           = Phase.Evaluation,
                Size            = Sizes[i],
                StartBalance    = Sizes[i],
                ProfitTarget    = Targets[i],
                MaxLoss         = MaxLosses[i],
                HwmBasis        = HwmBasis.EodClose,
                FloorLockOffset = LucidFloorLockOffset,
                // Ships OFF. The binding's DailyLossLimitOn flag copies DailyLossLimitWhenOn over
                // this on a Clone(); nothing mutates the catalog.
                DailyLossLimit       = 0.0,
                DailyLossLimitWhenOn = DllWhenOn[i],
                DailyLossSoft   = true,
                ConsistencyPct  = 0.0,
                ConsistencyBlocksPayoutOnly = false,
                Buffer          = 0.0,
                MinPayout       = 0.0,
                ScaleDllPctOfPeakProfit = 0.0,
                MaxContracts    = MaxMinis[i],
                MicroRatio      = 10,
                MinDays         = 0,
                DaysToPayout    = 0,
                Verified        = true,
                Notes           = notes.ToArray(),
                SourceUrl       = UrlEval,
                Modelled        = true
            };
        }

        static PropRules LiveSim(int i)
        {
            List<string> notes = new List<string>();
            notes.Add("Buffer " + Money(Sizes[i] + MaxLosses[i] + LucidFloorLockOffset) + " is not withdrawable, and it is the same number Lucid calls the Initial Trail Balance. Profit split 90/10. Maximum 5 payouts before the live review pool, and the transition is at the risk team's discretion.");
            notes.Add("Daily loss limit below the Initial Trail Balance: the SAME fixed amount as the evaluation, " + Money(DllWhenOn[i]) + " on this size, and only if the account was bought with the limit ON. Resolved 2026-08-22 against the trader's dashboard plus two independent sources; the aggregator listing none at 25K and $2,100 / $3,000 at 100K / 150K is rejected, not carried as an open question.");
            notes.Add("DASHBOARD-IMPLIED, not published by the firm: the checkout ON/OFF choice carries from the evaluation into the funded account. No aggregator documents it. It is the only reading that fits the trader's own 50K PRO FUNDED card reading \"DLL (Below Initial Trail): NONE\" on an evaluation he bought with the limit OFF.");
            notes.Add("Above the Initial Trail Balance the fixed limit is replaced by the LucidScale DLL: 60% of the highest end-of-day PROFIT, not of the balance - a $3,000 peak EOD profit gives $1,800 of daily room. It ratchets up only, a drawdown never lowers it, and it is soft like the fixed limit. The dashboard's column header says \"60% of Peak EOD Balance\"; that wording is loose, and two independent sources plus the firm's own worked example say profit.");
            notes.Add("The LucidScale DLL is carried as data and DISPLAYED ONLY - no code path in this engine measures anything against it.");
            notes.Add("DISAGREEMENT on the payout target: recorded here as a flat $500. Lucid publishes a per-cycle Minimum Profit Goal of $250 / $500 / $750 / $1,000 by size, with $500 being the minimum payout REQUEST at every size. The spec's single value is right for the 50K only.");
            notes.Add("DISAGREEMENT on 'days to payout: 3': Lucid's payout article states there is no fixed payout window and lists three eligibility criteria, none of them a day count. The 3 traces to the pricing card. Gating payout eligibility on it withholds a payout Lucid would allow.");
            notes.Add("Consistency 40% resets after every approved payout; this engine measures it over the whole account history, so it reads pessimistically from payout 2 onwards.");
            notes.Add("The 40% applies to accounts purchased or reset on or after 2025-11-28 15:00 ET. Older accounts keep 35% and the legacy 100%-of-the-first-$10k split.");

            return new PropRules
            {
                Firm            = Firm.Lucid,
                Plan            = LucidPlan,
                Phase           = Phase.LiveSim,
                Size            = Sizes[i],
                StartBalance    = Sizes[i],
                ProfitTarget    = 0.0,          // the funded phase has no pass target, only a payout test
                MaxLoss         = MaxLosses[i], // same MLL as the evaluation
                HwmBasis        = HwmBasis.EodClose,
                FloorLockOffset = LucidFloorLockOffset,
                // Ships OFF like the evaluation, and carries the SAME amount: the funded phase keeps
                // the evaluation's fixed limit below the Initial Trail Balance (docs/rules-sources.md
                // section 2, D3 resolved; above that balance LucidScale replaces it). The
                // binding's one DailyLossLimitOn flag therefore switches the same rule in both
                // phases, which is exactly the carry-over the dashboard implies.
                DailyLossLimit       = 0.0,
                DailyLossLimitWhenOn = DllWhenOn[i],
                DailyLossSoft   = true,
                ConsistencyPct  = 40.0,
                ConsistencyBlocksPayoutOnly = true,
                Buffer          = Sizes[i] + MaxLosses[i] + LucidFloorLockOffset,
                MinPayout       = 500.0,
                // 60% of the peak END-OF-DAY PROFIT. Real modelled data, but nothing enforces it -
                // see PropRules.ScaleDllPctOfPeakProfit and the note above.
                ScaleDllPctOfPeakProfit = 60.0,
                MaxContracts    = MaxMinis[i],
                MicroRatio      = 10,
                MinDays         = 0,
                DaysToPayout    = 3,
                Verified        = false,        // two open items in this row, both in Notes
                Notes           = notes.ToArray(),
                SourceUrl       = UrlFunded,
                Modelled        = true
            };
        }

        static PropRules Live(int i)
        {
            List<string> notes = new List<string>();
            notes.Add("UNVERIFIED PHASE. Spec 1.3 states one set of numbers for all four sizes; do not trade against these without confirming them.");
            notes.Add("ProfitTarget here is the LIVE BONUS trigger ($2,100 of profit pays a $2,000 bonus), not a pass target. Verdict.Passed is evaluation-only, so this can never read as 'challenge passed'.");
            notes.Add("DISAGREEMENT on size scaling: Lucid's live-structure article gives a starting live drawdown of $1,000 / $2,000 / $3,000 / $4,500 and a live target of $1,100 / $2,100 / $3,100 / $4,600 by size, with the bonus equal to the starting drawdown. The spec's $2,000 / $2,100 / $2,000 is the 50K row applied to every size.");
            notes.Add("DISAGREEMENT on the floor lock: FloorLockOffset encodes the LEVEL the floor freezes at, and $2,000 is recorded here per spec. Lucid says the max loss limit locks at $100 once live profit reaches the starting live drawdown - i.e. $2,000 is the TRIGGER, not the level. As specified the floor sits $1,900 too high on a 50K.");
            notes.Add("The live bonus is subject to the 90/10 split, is once per lifetime on a first trip live, is void for anyone who has held a legacy live account, and is unavailable at LucidMaxx. Requesting a payout before the target forces the lock early. None of this is modelled.");
            notes.Add("No daily loss limit and no consistency requirement on live accounts. Live contract caps also vary by profit tier and by exchange - not modelled.");

            return new PropRules
            {
                Firm            = Firm.Lucid,
                Plan            = LucidPlan,
                Phase           = Phase.Live,
                Size            = Sizes[i],     // the plan size still identifies the account
                StartBalance    = 0.0,          // live starts at zero, so the day 0 floor is -2,000
                ProfitTarget    = 2100.0,
                MaxLoss         = 2000.0,
                HwmBasis        = HwmBasis.EodClose,
                FloorLockOffset = 2000.0,
                DailyLossLimit       = 0.0,
                DailyLossLimitWhenOn = 0.0,
                DailyLossSoft   = false,        // there is no DLL on live, so softness is moot
                ConsistencyPct  = 0.0,
                ConsistencyBlocksPayoutOnly = false,
                Buffer          = 0.0,
                MinPayout       = 0.0,
                ScaleDllPctOfPeakProfit = 0.0,
                MaxContracts    = MaxMinis[i],
                MicroRatio      = 10,
                MinDays         = 0,
                DaysToPayout    = 0,
                Verified        = false,
                Notes           = notes.ToArray(),
                SourceUrl       = UrlLive,
                Modelled        = true
            };
        }

        // A firm that exists in the dropdown and nowhere else. Modelled = false is the only field
        // that matters: the UI disables the entry and the engine returns Verdict.Untracked.
        static PropRules NotModelled(Firm firm, string plan, string url)
        {
            return new PropRules
            {
                Firm      = firm,
                Plan      = plan,
                Phase     = Phase.Evaluation,
                Size      = 0,
                HwmBasis  = HwmBasis.IntradayEquity, // the three of them trail on running equity
                MicroRatio = 10,
                Verified  = false,
                Notes     = new string[] { "Not modelled yet. Phase 1 covers LucidPro only." },
                SourceUrl = url,
                Modelled  = false
            };
        }

        // Money formatting inside a note. Whole dollars only - every LucidPro figure is whole.
        // Invariant culture on purpose: these strings end up in the UI and in bindings.xml, and a
        // machine set to a comma-decimal locale would otherwise render "$1.200".
        static string Money(double v)
        {
            return "$" + v.ToString("#,##0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
