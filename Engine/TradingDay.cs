using System;

namespace FundedPath.Engine
{
    // One trading day of a challenge, as the engine and the persisted ledger both see it.
    //
    // Ownership of the fields is split, and getting it wrong is the classic double-count:
    //   the CALLER fills Date, RealizedPnL and Fills;
    //   ChallengeEngine.Evaluate fills ClosingBalance and FloorInForce.
    // Evaluate never writes back into the rows it was handed -- it returns fresh rows -- so a caller
    // that persists ChallengeState.Days gets the computed columns, and a caller that keeps its own
    // input list keeps it untouched.
    public sealed class TradingDay
    {
        // The ET trading-day date from SessionClock.TradingDate, i.e. a calendar label with no time
        // component and DateTimeKind.Unspecified. Never a raw fill timestamp.
        public DateTime Date { get; set; }

        // Closed-trade P&L for this day only, in account currency. Realized, never marks-to-market:
        // a position still open at the session close contributes nothing here.
        public double RealizedPnL { get; set; }

        // Account balance at this day's close = StartBalance + every RealizedPnL up to and including
        // this day. Written by Evaluate. This is the number the end-of-day drawdown trail reads.
        public double ClosingBalance { get; set; }

        // Number of fills that landed on this day. Used to decide whether the day counts toward a
        // minimum-trading-days or days-to-payout requirement. Written by the caller.
        public int Fills { get; set; }

        // The drawdown floor that applied DURING this day -- built from closes strictly BEFORE it, so
        // it is the floor the trader was actually trading against, not the one set by his own close.
        // Written by Evaluate.
        public double FloorInForce { get; set; }
    }
}
