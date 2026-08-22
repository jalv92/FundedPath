using System.Collections.Generic;

namespace FundedPath.Engine
{
    // The computed answer to "am I still passing, and what breaks first?".
    //
    // Everything the window draws comes from here and nowhere else -- the UI never recomputes a rule.
    // ChallengeEngine.Evaluate always fills Days and Warnings with non-null values, so the window can
    // bind straight to them without null checks on the paint tick.
    public sealed class ChallengeState
    {
        // Verdict precedence is fixed by spec 4.2. Breached is terminal: nothing overrides it.
        public Verdict Verdict { get; set; }

        // One line for the banner, already upper-cased, e.g. "ON TRACK - $390 TO PASS".
        public string Headline { get; set; }

        // Which rule has the least slack, in words and in dollars, e.g. "Floor - $2,250 of room".
        public string BindingConstraint { get; set; }

        // Realized only: StartBalance plus every closed dollar, including the day in progress. Built by
        // accumulation, NEVER from the broker's cash value plus realized P&L -- those two overlap and
        // adding them double-counts the session.
        public double Balance { get; set; }

        // Balance plus open-position P&L.
        public double Equity { get; set; }

        // The drawdown floor in force right now, for the day in progress.
        public double Floor { get; set; }

        // True once the floor has reached StartBalance + FloorLockOffset and stopped trailing forever.
        public bool FloorLocked { get; set; }

        // Dollars between the breach-basis value and the floor. Negative means already breached.
        // Uses the BreachBasis passed to Evaluate: Equity counts open positions, Balance does not.
        public double RoomToFloor { get; set; }

        // Dollars still needed to reach the phase's goal (profit target, or buffer + minimum payout).
        // Clamped at 0 once the goal is met; 0 also when the phase has no goal.
        public double ToTarget { get; set; }

        // 0..100 of the way from the opening balance to that goal, clamped at both ends.
        public double ProgressPct { get; set; }

        // P&L of the day in progress, on the same basis as RoomToFloor so the two can never disagree
        // about whether an open position counts.
        public double DayPnL { get; set; }

        // Largest single-day realized P&L of the challenge, including the day in progress. This is the
        // number a consistency rule measures.
        public double BestDayPnL { get; set; }

        // The largest a single day may be given the CURRENT total profit, i.e. profit * pct / 100.
        // It moves every time the account does. 0 when the rule is absent.
        public double ConsistencyCapNow { get; set; }

        // True when BestDayPnL is within ConsistencyCapNow, and true whenever the rule is absent.
        public bool ConsistencyOk { get; set; }

        // Completed days that actually traded, counted against MinDays / DaysToPayout.
        public int QualifyingDays { get; set; }

        // The completed days, oldest first, with ClosingBalance and FloorInForce filled in. Fresh rows:
        // never the caller's own objects. The day in progress is NOT in here -- the window draws its
        // live endpoint from Balance / Equity / Floor.
        public IReadOnlyList<TradingDay> Days { get; set; }

        // Unverified rules and engine assumptions, for the UI to show as caveats rather than facts.
        public string[] Warnings { get; set; }
    }
}
