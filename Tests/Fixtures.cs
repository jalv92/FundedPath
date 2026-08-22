using System;
using System.Collections.Generic;
using FundedPath.Engine;

// Shared builders for the engine tests. Deliberately tiny: every test below states its own closes
// and its own expected floors, because the whole point of this suite is to catch a wrong floor, and
// a helper that computed the expected numbers would just re-implement the bug.
internal static class Fixtures
{
    // A fixed weekday so a TradingDay.Date never lands on a weekend by accident. Monday 2026-08-03.
    static readonly DateTime Day0 = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Unspecified);

    // Always Clone(): RuleCatalog hands back the SHARED row, and a test that mutated it would leak
    // into every later test in the run (xunit reuses the AppDomain across classes).
    public static PropRules Rules(int size, Phase phase)
    {
        PropRules r = RuleCatalog.Find(Firm.Lucid, "LucidPro", size, phase);
        Assert(r != null, "catalog is missing LucidPro " + size + " " + phase);
        return r.Clone();
    }

    public static PropRules Eval50K() { return Rules(50000, Phase.Evaluation); }

    public static PropRules LiveSim50K() { return Rules(50000, Phase.LiveSim); }

    // Build the day ledger from CLOSING BALANCES, because that is how the rulebook and the trader's
    // dashboard both express a challenge. The engine is fed RealizedPnL, so the deltas are derived
    // here once rather than being hand-computed (and mis-computed) in each test.
    public static IReadOnlyList<TradingDay> DaysFromCloses(double start, params double[] closes)
    {
        List<TradingDay> days = new List<TradingDay>(closes.Length);
        double prev = start;
        for (int i = 0; i < closes.Length; i++)
        {
            TradingDay d = new TradingDay();
            d.Date = Day0.AddDays(i);
            d.RealizedPnL = closes[i] - prev;
            d.Fills = 2;   // any non-zero count makes the day "qualifying"
            days.Add(d);
            prev = closes[i];
        }
        return days;
    }

    // Build the day ledger from per-day realized P&L, for the consistency tests where the day sizes
    // are the subject and the running balance is incidental.
    public static IReadOnlyList<TradingDay> DaysFromPnL(params double[] pnl)
    {
        List<TradingDay> days = new List<TradingDay>(pnl.Length);
        for (int i = 0; i < pnl.Length; i++)
        {
            TradingDay d = new TradingDay();
            d.Date = Day0.AddDays(i);
            d.RealizedPnL = pnl[i];
            d.Fills = 2;
            days.Add(d);
        }
        return days;
    }

    public static IReadOnlyList<TradingDay> NoDays() { return new List<TradingDay>(); }

    static void Assert(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
