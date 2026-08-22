using System;

namespace FundedPath.Engine
{
    // Engine layer: pure C#, LangVersion 7.3, ZERO NinjaTrader references. This file is compiled
    // twice - once by the Tests project (net8.0) and once by NinjaTrader itself, which pulls every
    // .cs under bin/Custom into one assembly. Anything NT-specific here would break the test build.

    public enum Firm { Lucid, MyFundedFutures, ApexTrader, TopstepTrader }

    public enum Phase { Evaluation, LiveSim, Live }

    // Which series the high-water mark ratchets on. LucidPro is EodClose on every phase: the floor
    // moves only when a session closes, never on an intraday high. IntradayEquity exists because
    // three of the four firms still to be modelled trail on the running equity peak.
    public enum HwmBasis { EodClose, IntradayEquity }

    // Which number is compared against the floor. See spec 1.4 - this is the one open assumption in
    // the whole model, so it is a setting the trader can flip, never a constant.
    public enum BreachBasis { Equity, Balance }

    public enum Verdict { InProgress, Passed, PayoutEligible, DailyLockout, Breached, Untracked }

    // One firm/plan/size/phase row of the rulebook. Plain data: no behaviour beyond the computed
    // helpers below, which exist only so the same arithmetic is not re-derived in the engine, the
    // rail cards and the chart.
    public sealed class PropRules
    {
        public Firm     Firm            { get; set; }
        public string   Plan            { get; set; }   // "LucidPro"
        public Phase    Phase           { get; set; }
        public int      Size            { get; set; }   // 50000 - the plan size, NOT the start balance
        public double   StartBalance    { get; set; }   // Live starts at 0 while Size stays 50000
        public double   ProfitTarget    { get; set; }   // 0 when the phase has none
        public double   MaxLoss         { get; set; }
        public HwmBasis HwmBasis        { get; set; }
        public double   FloorLockOffset { get; set; }   // floor stops at StartBalance + this
        public double   DailyLossLimit  { get; set; }   // 0 = off / none
        // The amount the DLL would be if the trader had bought it ON. Kept separate from
        // DailyLossLimit so the catalog can ship the rule OFF (which is how the trader's own
        // account is configured) while the UI toggle still has a number to switch back on.
        public double   DailyLossLimitWhenOn { get; set; }
        public bool     DailyLossSoft   { get; set; }
        public double   ConsistencyPct  { get; set; }   // 0 = none; 40 = 40%
        public bool     ConsistencyBlocksPayoutOnly { get; set; }
        public double   Buffer          { get; set; }   // 0 = n/a
        public double   MinPayout       { get; set; }

        // The LucidScale daily loss limit that replaces the fixed one once the balance is above the
        // Initial Trail Balance: a percentage of the highest END-OF-DAY PROFIT, never of the balance
        // (docs/rules-sources.md section 2, D2 resolved - "a $3,000 peak EOD profit gives you $1,800
        // of daily room"). Ratchets up only: a drawdown never lowers it. Soft, like the fixed limit.
        // 0 = n/a.
        //
        // NOT ENFORCED. Nothing in this engine compares a number against it - it is carried as data
        // so the UI can state the real rule instead of a sentence, and so enforcing it later is a
        // change in one place. Do not read a non-zero value here as "the cockpit is watching this".
        //
        // It DOES switch the fixed limit off above TrailStopClose, because that is where the firm
        // replaces one rule with the other; see ChallengeEngine's dllOn gate.
        public double   ScaleDllPctOfPeakProfit { get; set; }

        // The trader's own highest end-of-day close on this account, typed at binding time. 0 = none.
        // NT8's Account.Executions holds roughly the last three days, so a challenge bound on day 12
        // reconstructs a ledger that starts well after the real high-water mark. Without this the
        // floor restarts from StartBalance and reads LOWER than the firm's - room that does not
        // exist, in the one direction this add-on cannot afford to be wrong in.
        public double   PeakEodCloseSeed { get; set; }

        public int      MaxContracts    { get; set; }   // minis
        public int      MicroRatio      { get; set; }   // 10
        public int      MinDays         { get; set; }
        public int      DaysToPayout    { get; set; }
        public bool     Verified        { get; set; }
        public string[] Notes           { get; set; }
        public string   SourceUrl       { get; set; }
        public bool     Modelled        { get; set; }   // false for the three other firms

        // ---- computed read-only helpers -------------------------------------------------------
        // Each of these is used by at least two of {engine, rail, chart}; deriving them once here
        // is what keeps the floor cap and the target line from drifting apart.

        // The ceiling of the trailing floor (spec 4.1). Once the floor reaches this it is frozen.
        public double FloorLockLevel { get { return StartBalance + FloorLockOffset; } }

        // Day 0's floor of the RULE SET, seed-free: no close has happened yet, so the high-water mark
        // is the start balance. Kept seed-free on purpose - the engine sanity-checks the lock level
        // against it, and a seeded value above the lock level would fire that check as a rule bug.
        public double InitialFloor { get { return StartBalance - MaxLoss; } }

        // The high-water mark the floor starts from: the trader's known peak close when he seeded
        // one, otherwise the start balance. This is what ChallengeEngine.Evaluate starts its ratchet
        // at, so it is the floor of the whole cockpit, not just of the binding dialog's preview.
        //
        // Math.Max, not a bare assignment - a seed below the start balance is a typo, and letting it
        // through would LOWER the floor. A non-finite seed is dropped rather than maxed: Math.Max
        // with a NaN returns NaN, and a NaN high-water mark makes the floor NaN, which does not trip
        // the breach test but DISABLES it (every comparison against NaN is false).
        public double SeededHwm
        {
            get
            {
                double seed = PeakEodCloseSeed;
                if (double.IsNaN(seed) || double.IsInfinity(seed))
                    return StartBalance;
                return Math.Max(StartBalance, seed);
            }
        }

        // The floor in force before any close this add-on has seen, seed included and lock respected.
        // A seed far above the trail stop means the floor is already frozen, not sky-high.
        public double SeededFloor { get { return Math.Min(SeededHwm - MaxLoss, FloorLockLevel); } }

        // The balance that clears the phase's profit target (spec 4.2 rule 3).
        public double TargetBalance { get { return StartBalance + ProfitTarget; } }

        // The balance that clears the payout test (spec 4.2 rule 4). On LucidPro this reproduces
        // Lucid's own "Minimum Balance for $500 Payout" column exactly: 26,600 / 52,600 / 103,600 / 155,100.
        public double PayoutBalance { get { return Buffer + MinPayout; } }

        // The closing balance at which the trail stops moving, i.e. FloorLockLevel + MaxLoss.
        // On the funded phase this is the same number as Buffer, by construction, not by accident.
        public double TrailStopClose { get { return StartBalance + MaxLoss + FloorLockOffset; } }

        public bool HasDailyLossLimit { get { return DailyLossLimit > 0; } }

        public int MaxMicros { get { return MaxContracts * MicroRatio; } }

        // The UI applies the trader's own options (DLL on/off, start-balance override) on top of a
        // catalog row. Without a copy that write would edit the shared row and silently change the
        // rules for every other account bound to the same plan.
        public PropRules Clone()
        {
            PropRules copy = (PropRules)MemberwiseClone();
            // MemberwiseClone is shallow, so the clone would still point at the catalog's Notes
            // array. Copy it: the engine appends unverified-rule warnings per account.
            if (Notes != null)
                copy.Notes = (string[])Notes.Clone();
            return copy;
        }
    }
}
