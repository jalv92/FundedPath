using System;
using System.Collections.Generic;
using FundedPath.Engine;
using Xunit;

// Latching: what a firm remembers and a pure function does not.
//
// Every test here has the same shape as the bug the trader hit in Market Replay -- the condition
// fires, the account recovers, and the window goes back to saying ON TRACK. Each one therefore
// carries its own "before" half: the SAME inputs evaluated without a latch, showing the verdict
// reverting. Without that half these tests could pass for a reason that has nothing to do with the
// latch.
public class LatchTests
{
    // Same escape as the engine: this tree is edited on Windows and read by the NinjaScript compiler,
    // where a typed middle dot read back as Windows-1252 renders as mojibake.
    const string Dot = " \u00B7 ";

    // Two consecutive weekdays. The trading date is a calendar LABEL with no time component, which is
    // what SessionClock.TradingDate hands the caller.
    static readonly DateTime Aug19 = new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Unspecified);
    static readonly DateTime Aug20 = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Unspecified);

    // The catalog ships LucidPro's daily loss limit OFF, because that is a checkout option the trader
    // did not buy. VerdictTests covers resolving it through a real binding; here the rule just has to
    // be ON, so the clone is switched on directly and the test stays about the latch.
    static PropRules Eval50KWithDll()
    {
        PropRules r = Fixtures.Eval50K();
        r.DailyLossLimit = 1200;
        return r;
    }

    // ---- the daily lockout: latched for the DAY ----

    [Fact]
    public void A_day_that_hits_the_limit_and_then_recovers_is_still_locked_out()
    {
        PropRules r = Eval50KWithDll();

        // Down 1,300 on a 1,200 limit. Balance 48,700 is still 700 clear of the 48,000 floor, so this
        // is a lockout and not a breach.
        ChallengeState hit = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -1300, 0, BreachBasis.Equity, new LatchedState(), Aug19);
        Assert.Equal(Verdict.DailyLockout, hit.Verdict);
        Assert.Equal(Aug19, hit.Latched.DailyLockoutDate);
        Assert.Equal(-1300.0, hit.Latched.DailyLockoutAt, 6);

        // He wins 1,100 of it back. The firm does not care: he traded through his limit and the day is
        // over. Same trading date, so the latch holds.
        ChallengeState recovered = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -200, 0, BreachBasis.Equity, hit.Latched, Aug19);
        Assert.Equal(Verdict.DailyLockout, recovered.Verdict);
        Assert.Equal("DAILY LOCKOUT" + Dot + "DONE FOR TODAY", recovered.Headline);

        // And the cockpit says why, instead of quoting him the room he has back as if he could use it.
        Assert.Equal("Daily loss limit - hit today at $1,300 down. The day is locked; trading back above "
                     + "it does not reopen it.", recovered.BindingConstraint);

        // The "before": the exact same recovery with no latch reverts to ON TRACK. That is the bug.
        Assert.Equal(Verdict.InProgress,
            ChallengeEngine.Evaluate(r, Fixtures.NoDays(), -200, 0, BreachBasis.Equity).Verdict);
    }

    [Fact]
    public void A_new_trading_day_clears_the_lockout()
    {
        PropRules r = Eval50KWithDll();

        ChallengeState hit = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -1300, 0, BreachBasis.Equity, new LatchedState(), Aug19);
        Assert.Equal(Verdict.DailyLockout, hit.Verdict);

        // Next session: yesterday's 1,300 loss is a closed day in the ledger and the trader starts flat.
        // The lockout is scoped to its trading DAY, so a new date clears it -- and nothing else does.
        ChallengeState nextDay = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(-1300), 0, 0, BreachBasis.Equity, hit.Latched, Aug20);
        Assert.Equal(Verdict.InProgress, nextDay.Verdict);
        Assert.Equal(DateTime.MinValue, nextDay.Latched.DailyLockoutDate);
        Assert.Equal(0.0, nextDay.Latched.DailyLockoutAt, 6);

        // A soft limit locks the day, never the account: nothing terminal was latched on the way past.
        Assert.False(nextDay.Latched.ChallengeBreached);
    }

    // ---- the challenge breach: terminal ----

    [Fact]
    public void A_challenge_breach_survives_a_full_recovery_and_a_later_winning_day()
    {
        PropRules r = Fixtures.Eval50K();

        // 340 under the 48,000 floor. This is the moment the challenge ended.
        ChallengeState broke = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -2340, 0, BreachBasis.Equity, new LatchedState(), Aug19);
        Assert.Equal(Verdict.Breached, broke.Verdict);
        Assert.True(broke.Latched.ChallengeBreached);
        Assert.Equal(Aug19, broke.Latched.BreachedOn);
        Assert.Equal(47660.0, broke.Latched.BreachedAt, 6);
        Assert.Equal(48000.0, broke.Latched.BreachedFloor, 6);

        // He trades back to 48,210, above the floor. The firm is not handing the account back.
        ChallengeState recovered = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -1790, 0, BreachBasis.Equity, broke.Latched, Aug19);
        Assert.Equal(Verdict.Breached, recovered.Verdict);
        Assert.Equal(48210.0, recovered.Balance, 6);
        Assert.Equal(210.0, recovered.RoomToFloor, 6);

        // The "before": without the latch the same recovery reads as a live, healthy challenge.
        Assert.Equal(Verdict.InProgress,
            ChallengeEngine.Evaluate(r, Fixtures.NoDays(), -1790, 0, BreachBasis.Equity).Verdict);

        // A winning day after the breach is still a breach, on a later date, with the ledger grown.
        ChallengeState winningDay = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(-1790), 1500, 0, BreachBasis.Equity, recovered.Latched, Aug20);
        Assert.Equal(Verdict.Breached, winningDay.Verdict);
        Assert.Equal(49710.0, winningDay.Balance, 6);
        Assert.Equal(Aug19, winningDay.Latched.BreachedOn);   // the date of the breach, not of today
    }

    [Fact]
    public void The_wording_of_a_latched_breach_says_when_it_broke_and_where_he_stands_now()
    {
        // The trader reads this line after the fact, when he has forgotten which afternoon it was. A
        // bare "BREACHED" sends him hunting through the ledger for the day, and a bare "$210 of room"
        // is the sentence that made the cockpit lie in the first place.
        PropRules r = Fixtures.Eval50K();

        ChallengeState broke = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -2340, 0, BreachBasis.Equity, new LatchedState(), Aug19);
        // While he is actually under the floor the live depth is the whole story, unchanged.
        Assert.Equal("BREACHED" + Dot + "$340 BELOW THE FLOOR", broke.Headline);
        Assert.Equal("Floor - $340 below it", broke.BindingConstraint);

        ChallengeState recovered = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -1790, 0, BreachBasis.Equity, broke.Latched, Aug19);
        Assert.Equal("BREACHED Aug 19" + Dot + "$340 below the floor at the time. Now $210 above it, "
                     + "which does not matter.", recovered.Headline);
        Assert.Equal("Floor - breached Aug 19 at $47,660. The $210 of room now does not bring it back.",
                     recovered.BindingConstraint);
    }

    [Fact]
    public void The_latch_clears_only_through_an_explicit_reset()
    {
        PropRules r = Fixtures.Eval50K();

        ChallengeState broke = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -2340, 0, BreachBasis.Equity, new LatchedState(), Aug19);

        // Recovered, days later, on a different trading date, with a fat ledger. Still breached: unlike
        // the daily lockout, nothing about the calendar clears this one.
        ChallengeState later = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(-1790, 900, 900), 0, 0, BreachBasis.Equity, broke.Latched, Aug20);
        Assert.Equal(Verdict.Breached, later.Verdict);

        // The reset is handing back a fresh latch -- the trader saying, in the UI, that this is a new
        // challenge on this account. Only then does the same account read as live again.
        ChallengeState afterReset = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(-1790, 900, 900), 0, 0, BreachBasis.Equity, new LatchedState(), Aug20);
        Assert.Equal(Verdict.InProgress, afterReset.Verdict);
        Assert.False(afterReset.Latched.ChallengeBreached);

        // A reset does not paper over a live breach: an account still under the floor re-latches at once.
        ChallengeState stillUnder = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -2340, 0, BreachBasis.Equity, new LatchedState(), Aug20);
        Assert.Equal(Verdict.Breached, stillUnder.Verdict);
        Assert.Equal(Aug20, stillUnder.Latched.BreachedOn);
    }

    // ---- purity ----

    [Fact]
    public void Evaluate_does_not_mutate_the_LatchedState_it_was_handed()
    {
        // The caller owns this object and persists it. If Evaluate wrote into it, the caller could never
        // compare "what I had" against "what came back" -- and a breach latched on a paint tick would be
        // in the trader's file before anything decided it was real.
        PropRules r = Eval50KWithDll();
        LatchedState mine = new LatchedState();

        // Under the floor AND past the daily limit: both latches fire on this one call.
        ChallengeState s = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -2500, 0, BreachBasis.Equity, mine, Aug19);

        Assert.False(mine.ChallengeBreached);
        Assert.Equal(DateTime.MinValue, mine.BreachedOn);
        Assert.Equal(0.0, mine.BreachedAt, 9);
        Assert.Equal(0.0, mine.BreachedFloor, 9);
        Assert.Equal(DateTime.MinValue, mine.DailyLockoutDate);
        Assert.Equal(0.0, mine.DailyLockoutAt, 9);

        // What came back is a copy that DID latch.
        Assert.NotSame(mine, s.Latched);
        Assert.True(s.Latched.ChallengeBreached);

        // Evaluating twice with the same arguments still returns the same answer (the latch is an
        // argument like any other, so purity has to survive it).
        ChallengeState again = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -2500, 0, BreachBasis.Equity, mine, Aug19);
        Assert.Equal(s.Verdict, again.Verdict);
        Assert.Equal(s.Headline, again.Headline);
        Assert.Equal(s.Latched.BreachedAt, again.Latched.BreachedAt, 9);

        // An unbound account measures nothing, so it latches nothing -- but it must not silently DROP a
        // latch the caller is persisting either.
        LatchedState carried = new LatchedState();
        carried.ChallengeBreached = true;
        carried.BreachedOn = Aug19;
        ChallengeState untracked = ChallengeEngine.Evaluate(
            null, Fixtures.NoDays(), 0, 0, BreachBasis.Equity, carried, Aug20);
        Assert.Equal(Verdict.Untracked, untracked.Verdict);
        Assert.NotNull(untracked.Latched);
        Assert.NotSame(carried, untracked.Latched);
        Assert.True(untracked.Latched.ChallengeBreached);
        Assert.Equal(Aug19, untracked.Latched.BreachedOn);
    }

    // ---- what the chart needs to draw the daily-limit line ----

    [Fact]
    public void DailyLossLimitLevel_is_the_previous_close_minus_the_limit_and_zero_when_the_limit_is_off()
    {
        PropRules r = Eval50KWithDll();

        // Yesterday closed at 50,800 and the trader is down 300 so far today. The line sits at
        // 50,800 - 1,200 = 49,600: the day's OPENING balance is the previous day's CLOSE, and today's
        // fills must not move it. Reading it off today's first fill would slide the limit around all
        // session, which is the wrong number in both directions.
        ChallengeState s = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 50800), -300, 0, BreachBasis.Equity, new LatchedState(), Aug19);
        Assert.Equal(50800.0, s.DayOpenBalance, 6);
        Assert.Equal(49600.0, s.DailyLossLimitLevel, 6);

        // Same ledger, further into the day: the line has not moved.
        ChallengeState later = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 50800), -900, 0, BreachBasis.Equity, new LatchedState(), Aug19);
        Assert.Equal(49600.0, later.DailyLossLimitLevel, 6);

        // No limit bought: no line to draw. DayOpenBalance is still reported -- the chart needs it either way.
        ChallengeState off = ChallengeEngine.Evaluate(
            Fixtures.Eval50K(), Fixtures.DaysFromCloses(50000, 50800), -300, 0, BreachBasis.Equity);
        Assert.Equal(0.0, off.DailyLossLimitLevel, 6);
        Assert.Equal(50800.0, off.DayOpenBalance, 6);

        // Day one, nothing closed yet: the opening balance is the start balance, not zero.
        ChallengeState dayOne = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -300, 0, BreachBasis.Equity, new LatchedState(), Aug19);
        Assert.Equal(50000.0, dayOne.DayOpenBalance, 6);
        Assert.Equal(48800.0, dayOne.DailyLossLimitLevel, 6);

        // Above the Initial Trail Balance the firm replaces the fixed limit with LucidScale, which this
        // engine does not enforce. Nothing is watching the day, so there is no line to draw.
        PropRules funded = Fixtures.LiveSim50K();
        funded.DailyLossLimit = 1200;
        ChallengeState disarmed = ChallengeEngine.Evaluate(
            funded, Fixtures.DaysFromPnL(4300), -300, 0, BreachBasis.Equity, new LatchedState(), Aug19);
        Assert.Equal(54300.0, disarmed.DayOpenBalance, 6);
        Assert.Equal(0.0, disarmed.DailyLossLimitLevel, 6);
    }

    [Fact]
    public void A_latched_breach_still_reports_the_current_room_to_floor()
    {
        // The verdict is frozen; the RAIL is not. The trader still needs to see where the account
        // actually stands -- a frozen room-to-floor would tell him he is 340 under the floor on an
        // account that is 210 over it, and every number on that rail would be from a moment that has
        // passed.
        PropRules r = Fixtures.Eval50K();

        ChallengeState broke = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -2340, 0, BreachBasis.Equity, new LatchedState(), Aug19);
        Assert.Equal(-340.0, broke.RoomToFloor, 6);

        ChallengeState recovered = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), -1790, 0, BreachBasis.Equity, broke.Latched, Aug19);
        Assert.Equal(Verdict.Breached, recovered.Verdict);
        Assert.Equal(210.0, recovered.RoomToFloor, 6);      // current, not the -340 that was latched
        Assert.Equal(48210.0, recovered.Balance, 6);
        Assert.Equal(48000.0, recovered.Floor, 6);
        Assert.Equal(-1790.0, recovered.DayPnL, 6);

        // And it keeps moving with the account.
        ChallengeState nextDay = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(-1790), 1500, 0, BreachBasis.Equity, recovered.Latched, Aug20);
        Assert.Equal(1710.0, nextDay.RoomToFloor, 6);
        Assert.Equal(49710.0, nextDay.Balance, 6);
    }
}
