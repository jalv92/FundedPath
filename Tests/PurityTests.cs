using System.Collections.Generic;
using FundedPath.Engine;
using Xunit;

// Evaluate() runs on the window's 4 Hz paint tick. Two consequences are load-bearing:
//   * it must be a pure function of its arguments -- no static mutable state, no clock read, and no
//     write into the caller's rows -- or the floor would depend on how many times the window has
//     repainted since the account connected;
//   * it must not throw on bad data, because one unhandled exception on that tick kills the UI
//     thread for good and leaves a blank window over a healthy-looking log.
public class PurityTests
{
    [Fact]
    public void Evaluate_twice_with_the_same_inputs_returns_an_equal_state()
    {
        PropRules r = Fixtures.Eval50K();
        IReadOnlyList<TradingDay> days = Fixtures.DaysFromCloses(50000, 50820, 51960, 51530, 52240);

        ChallengeState a = ChallengeEngine.Evaluate(r, days, 250, -80, BreachBasis.Equity);
        ChallengeState b = ChallengeEngine.Evaluate(r, days, 250, -80, BreachBasis.Equity);

        Assert.Equal(a.Verdict, b.Verdict);
        Assert.Equal(a.Headline, b.Headline);
        Assert.Equal(a.BindingConstraint, b.BindingConstraint);
        Assert.Equal(a.Balance, b.Balance, 9);
        Assert.Equal(a.Equity, b.Equity, 9);
        Assert.Equal(a.Floor, b.Floor, 9);
        Assert.Equal(a.FloorLocked, b.FloorLocked);
        Assert.Equal(a.RoomToFloor, b.RoomToFloor, 9);
        Assert.Equal(a.ToTarget, b.ToTarget, 9);
        Assert.Equal(a.ProgressPct, b.ProgressPct, 9);
        Assert.Equal(a.DayPnL, b.DayPnL, 9);
        Assert.Equal(a.BestDayPnL, b.BestDayPnL, 9);
        Assert.Equal(a.ConsistencyCapNow, b.ConsistencyCapNow, 9);
        Assert.Equal(a.ConsistencyOk, b.ConsistencyOk);
        Assert.Equal(a.QualifyingDays, b.QualifyingDays);
        Assert.Equal(a.Warnings, b.Warnings);

        Assert.Equal(a.Days.Count, b.Days.Count);
        for (int i = 0; i < a.Days.Count; i++)
        {
            Assert.Equal(a.Days[i].Date, b.Days[i].Date);
            Assert.Equal(a.Days[i].RealizedPnL, b.Days[i].RealizedPnL, 9);
            Assert.Equal(a.Days[i].ClosingBalance, b.Days[i].ClosingBalance, 9);
            Assert.Equal(a.Days[i].FloorInForce, b.Days[i].FloorInForce, 9);
            Assert.Equal(a.Days[i].Fills, b.Days[i].Fills);
        }
    }

    [Fact]
    public void Evaluate_never_writes_into_the_callers_day_rows()
    {
        PropRules r = Fixtures.Eval50K();
        IReadOnlyList<TradingDay> days = Fixtures.DaysFromCloses(50000, 50820, 51960);

        ChallengeEngine.Evaluate(r, days, 0, 0, BreachBasis.Equity);

        // If Evaluate filled these in place, the second call above would be reading back its own
        // output -- and any caller keeping its own ledger would find the computed columns appearing
        // in rows it never asked to have computed.
        for (int i = 0; i < days.Count; i++)
        {
            Assert.Equal(0.0, days[i].ClosingBalance, 9);
            Assert.Equal(0.0, days[i].FloorInForce, 9);
        }

        // And the returned rows are fresh objects, not the caller's.
        ChallengeState s = ChallengeEngine.Evaluate(r, days, 0, 0, BreachBasis.Equity);
        for (int i = 0; i < days.Count; i++)
            Assert.NotSame(days[i], s.Days[i]);
    }

    [Fact]
    public void Evaluate_does_not_mutate_the_rule_set_it_was_handed()
    {
        // The rules object is shared with the rail, the chart and the dialog. A warning appended to
        // rules.Notes rather than to the state's own list would grow without bound at 4 Hz.
        PropRules r = Fixtures.LiveSim50K();
        int noteCount = r.Notes.Length;
        double maxLoss = r.MaxLoss;
        double start = r.StartBalance;

        ChallengeEngine.Evaluate(r, Fixtures.DaysFromPnL(900, 900, 800), 0, 0, BreachBasis.Equity);

        Assert.Equal(noteCount, r.Notes.Length);
        Assert.Equal(maxLoss, r.MaxLoss, 9);
        Assert.Equal(start, r.StartBalance, 9);
    }

    [Fact]
    public void A_NaN_from_an_account_mid_connect_cannot_hide_a_breach()
    {
        PropRules r = Fixtures.Eval50K();

        // NaN poisons comparisons in the DANGEROUS direction: NaN <= floor is false, so a real
        // breach would silently never be reported. It has to be scrubbed on the way in.
        ChallengeState nan = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), double.NaN, double.NaN, BreachBasis.Equity);
        Assert.False(double.IsNaN(nan.Balance));
        Assert.False(double.IsNaN(nan.Equity));
        Assert.False(double.IsNaN(nan.RoomToFloor));
        Assert.Equal(50000.0, nan.Balance, 9);
        Assert.Equal(Verdict.InProgress, nan.Verdict);

        ChallengeState inf = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 0, double.NegativeInfinity, BreachBasis.Equity);
        Assert.False(double.IsInfinity(inf.Equity));
        Assert.Equal(Verdict.InProgress, inf.Verdict);
    }

    [Fact]
    public void A_NaN_in_the_DAY_LEDGER_cannot_hide_a_breach_either()
    {
        PropRules r = Fixtures.Eval50K();

        // The live numbers were scrubbed; the ledger rows were not. A NaN day poisoned
        // balance -> balanceNow -> equityNow -> breachValue, and breachValue <= floor is false for
        // NaN forever, so the breach test was disabled PERMANENTLY rather than for one tick -- the
        // ledger round-trips through text and double.TryParse accepts the literal "NaN", so the bad
        // row came back on every launch.
        List<TradingDay> days = new List<TradingDay>(Fixtures.DaysFromCloses(50000, 51000));
        days[0].RealizedPnL = double.NaN;

        // $100k under water on an open position. There is no reading of the rulebook under which
        // this account is fine.
        ChallengeState s = ChallengeEngine.Evaluate(r, days, 0, -100000, BreachBasis.Equity);

        Assert.False(double.IsNaN(s.Balance));
        Assert.False(double.IsNaN(s.Equity));
        Assert.False(double.IsNaN(s.RoomToFloor));
        Assert.False(double.IsNaN(s.Floor));
        Assert.Equal(Verdict.Breached, s.Verdict);

        // Counted as zero, and the computed row must not carry the NaN back out to the ledger.
        Assert.Equal(50000.0, s.Balance, 9);
        Assert.Equal(48000.0, s.Floor, 9);
        Assert.Equal(0.0, s.Days[0].RealizedPnL, 9);
        Assert.Equal(50000.0, s.Days[0].ClosingBalance, 9);

        // Scrubbing silently would trade a hidden breach for a wrong balance. Say it out loud.
        Assert.Contains(s.Warnings, w => w.Contains("non-finite"));

        // A clean ledger stays quiet about it.
        Assert.DoesNotContain(
            ChallengeEngine.Evaluate(r, Fixtures.DaysFromCloses(50000, 51000), 0, 0, BreachBasis.Equity).Warnings,
            w => w.Contains("non-finite"));
    }

    [Fact]
    public void Bad_shapes_from_the_NT_layer_degrade_instead_of_throwing()
    {
        PropRules r = Fixtures.Eval50K();

        // A null day list is what an account produces before its execution history has loaded.
        ChallengeState nullDays = ChallengeEngine.Evaluate(r, null, 0, 0, BreachBasis.Equity);
        Assert.Empty(nullDays.Days);
        Assert.Equal(48000.0, nullDays.Floor, 9);

        // A null row inside the list is an NT-layer bug; skipping it beats an NRE on the paint tick.
        List<TradingDay> withNull = new List<TradingDay>(Fixtures.DaysFromCloses(50000, 51000));
        withNull.Insert(1, null);
        ChallengeState skipped = ChallengeEngine.Evaluate(r, withNull, 0, 0, BreachBasis.Equity);
        Assert.Single(skipped.Days);
        Assert.Equal(51000.0, skipped.Days[0].ClosingBalance, 9);

        // Every collection on the state is non-null so the window can bind straight to it.
        Assert.NotNull(nullDays.Days);
        Assert.NotNull(nullDays.Warnings);
    }

    [Fact]
    public void An_out_of_order_ledger_is_flagged_rather_than_silently_sorted()
    {
        PropRules r = Fixtures.Eval50K();

        // Sorting here would hide an NT-layer bucketing bug behind a plausible-looking floor, and a
        // wrong floor is the one number in this add-on nobody can afford to guess at.
        List<TradingDay> days = new List<TradingDay>(Fixtures.DaysFromCloses(50000, 51000, 52000));
        System.DateTime swap = days[0].Date;
        days[0].Date = days[1].Date;
        days[1].Date = swap;

        ChallengeState s = ChallengeEngine.Evaluate(r, days, 0, 0, BreachBasis.Equity);
        Assert.Contains(s.Warnings, w => w.Contains("ordered oldest-first"));
    }

    [Fact]
    public void A_disputed_rule_reaches_the_trader_instead_of_shipping_as_a_fact()
    {
        // Notes are rendered NOWHERE else in the codebase, so a note the engine does not promote to
        // a warning is written and never read. Before the fix a note was promoted only when the whole
        // row was unverified or the note flagged ITSELF with the word "unverified" -- so a VERIFIED
        // row carrying a DISAGREEMENT, the marker the rule catalog standardises on, shipped its
        // disputed number to the screen as a fact with the doubt sitting silently in the binary.
        //
        // The rule set is built here instead of being taken from the catalog on purpose: this is a
        // ChallengeEngine defect, and pinning it to whichever catalog row happens to carry a marker
        // today would make it fail on a rulebook edit that has nothing to do with the promotion rule.
        PropRules disputed = Fixtures.Eval50K();   // a Clone, safe to write
        disputed.Verified = true;
        disputed.Notes = new string[] {
            "Undisputed note about the trailing drawdown.",
            "DISAGREEMENT on the $600 DLL: two of the firm's own articles list 'None' for this size."
        };

        ChallengeState s = ChallengeEngine.Evaluate(disputed, Fixtures.NoDays(), 0, 0, BreachBasis.Equity);
        Assert.Contains(s.Warnings, w => w.Contains("DISAGREEMENT"));

        // A promotion rule, not a blanket one: the undisputed note stays out, or the caveat panel
        // becomes noise the trader learns to scroll past.
        Assert.DoesNotContain(s.Warnings, w => w.Contains("Undisputed note about"));

        // And neither original condition regressed.
        PropRules wholeRowUnverified = Fixtures.Eval50K();
        wholeRowUnverified.Verified = false;
        wholeRowUnverified.Notes = new string[] { "Undisputed note about the trailing drawdown." };
        Assert.Contains(
            ChallengeEngine.Evaluate(wholeRowUnverified, Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Warnings,
            w => w.Contains("Undisputed note about"));

        PropRules selfFlagged = Fixtures.Eval50K();
        selfFlagged.Verified = true;
        selfFlagged.Notes = new string[] { "This size is unverified against the firm's own table." };
        Assert.Contains(
            ChallengeEngine.Evaluate(selfFlagged, Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Warnings,
            w => w.Contains("unverified against"));
    }
}
