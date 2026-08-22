using FundedPath.Engine;
using Xunit;

// The 40% consistency rule (spec 1.2). It is a MOVING cap -- a share of the profit made so far --
// not a fixed dollar figure, which is the part traders and re-implementations both get wrong.
//
//     cap = totalProfit * ConsistencyPct / 100     and     ok = bestDay <= cap
//
// It gates the payout only; VerdictTests covers the fact that failing it never fails the account.
public class ConsistencyTests
{
    [Fact]
    public void The_cap_scales_with_total_profit()
    {
        PropRules r = Fixtures.LiveSim50K();

        // 1,000 of profit caps a single day at 400. The same account 4,000 richer caps it at 2,000.
        // A cap read as a constant would give the same number for both.
        ChallengeState small = ChallengeEngine.Evaluate(r, Fixtures.DaysFromPnL(1000), 0, 0, BreachBasis.Equity);
        Assert.Equal(1000.0, small.Balance - r.StartBalance, 6);
        Assert.Equal(400.0, small.ConsistencyCapNow, 6);
        Assert.False(small.ConsistencyOk);   // the single 1,000 day is its own 100%

        ChallengeState bigger = ChallengeEngine.Evaluate(r, Fixtures.DaysFromPnL(1000, 4000), 0, 0, BreachBasis.Equity);
        Assert.Equal(5000.0, bigger.Balance - r.StartBalance, 6);
        Assert.Equal(2000.0, bigger.ConsistencyCapNow, 6);
        Assert.Equal(4000.0, bigger.BestDayPnL, 6);
        Assert.False(bigger.ConsistencyOk);  // 4,000 of 5,000 is 80%

        // Spread the same 5,000 across days that each stay inside 40% and the rule is satisfied.
        ChallengeState spread = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(2000, 1500, 1500), 0, 0, BreachBasis.Equity);
        Assert.Equal(2000.0, spread.ConsistencyCapNow, 6);
        Assert.Equal(2000.0, spread.BestDayPnL, 6);
        Assert.True(spread.ConsistencyOk);
    }

    [Fact]
    public void Best_day_exactly_at_the_cap_passes_and_a_cent_over_fails()
    {
        PropRules r = Fixtures.LiveSim50K();

        // 1,040 / 800 / 760 = 2,600 of profit, and 40% of 2,600 is exactly 1,040. The boundary is
        // inclusive: a trader who lands his best day precisely on the cap has satisfied the rule.
        ChallengeState atCap = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(1040, 800, 760), 0, 0, BreachBasis.Equity);
        Assert.Equal(1040.0, atCap.ConsistencyCapNow, 6);
        Assert.Equal(1040.0, atCap.BestDayPnL, 6);
        Assert.True(atCap.ConsistencyOk);

        // One cent more on the best day, one cent less on another: same total, same cap, and the
        // rule now fails. The engine's half-cent comparison tolerance must not swallow a whole cent.
        ChallengeState centOver = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(1040.01, 800, 759.99), 0, 0, BreachBasis.Equity);
        Assert.Equal(1040.0, centOver.ConsistencyCapNow, 2);
        Assert.Equal(1040.01, centOver.BestDayPnL, 6);
        Assert.False(centOver.ConsistencyOk);
    }

    [Fact]
    public void The_day_in_progress_is_a_day_like_any_other()
    {
        PropRules r = Fixtures.LiveSim50K();

        // 1,000 closed on an earlier day, 2,000 realized so far today. Today is the best day and it
        // counts against the cap now, not tomorrow: a rule that only looked at completed days would
        // tell the trader he is fine right up to the session close.
        ChallengeState s = ChallengeEngine.Evaluate(r, Fixtures.DaysFromPnL(1000), 2000, 0, BreachBasis.Equity);

        Assert.Equal(53000.0, s.Balance, 6);
        Assert.Equal(2000.0, s.BestDayPnL, 6);
        Assert.Equal(1200.0, s.ConsistencyCapNow, 6);   // 40% of 3,000
        Assert.False(s.ConsistencyOk);
    }

    [Fact]
    public void A_losing_account_gets_a_zero_cap_and_is_not_marked_inconsistent()
    {
        PropRules r = Fixtures.LiveSim50K();

        // Negative profit would give a negative dollar cap, which is not a number worth rendering
        // and would make every day "over the cap". Clamped to zero, and a best day that is itself a
        // loss stays inside it.
        ChallengeState s = ChallengeEngine.Evaluate(r, Fixtures.DaysFromPnL(-500), 0, 0, BreachBasis.Equity);
        Assert.Equal(0.0, s.ConsistencyCapNow, 6);
        Assert.True(s.ConsistencyOk);

        // Best day is 0, not -500: the day in progress is a day like any other and it is flat so
        // far, which makes it the best one on the books. Harmless here -- an account that is down
        // has a zero cap either way -- and it can never mask a real breach of the rule, because any
        // account with profit to measure has a positive day that outranks the flat one.
        Assert.Equal(0.0, s.BestDayPnL, 6);

        // The same losing day with the session already underway does surface as the best day.
        ChallengeState underway = ChallengeEngine.Evaluate(r, Fixtures.DaysFromPnL(-500), -200, 0, BreachBasis.Equity);
        Assert.Equal(-200.0, underway.BestDayPnL, 6);
        Assert.True(underway.ConsistencyOk);
    }

    [Fact]
    public void A_rule_that_cannot_be_evaluated_yet_does_not_steal_the_binding_constraint()
    {
        PropRules r = Fixtures.LiveSim50K();

        // +1,000 then -1,200. The account is DOWN 200, so there is no profit to withdraw and the
        // 40% rule has nothing to be a percentage OF: a payout cannot be requested at or below zero
        // profit, so the rule is not evaluable, let alone binding. Before the fix the cap clamped to
        // zero while the comparison kept running, which made the slack -1,000 -- the most negative
        // candidate on the board -- and the cockpit named consistency on an account that was $800
        // from a TERMINAL breach.
        ChallengeState s = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(1000, -1200), 0, 0, BreachBasis.Equity);

        Assert.Equal(49800.0, s.Balance, 6);
        Assert.Equal(49000.0, s.Floor, 6);        // ratcheted by the 51,000 close, then held
        Assert.Equal(800.0, s.RoomToFloor, 6);
        Assert.Equal(1000.0, s.BestDayPnL, 6);    // the day that would have "broken" the rule
        Assert.True(s.ConsistencyOk);
        Assert.Equal("Floor - $800 of room", s.BindingConstraint);

        // One dollar of profit and the rule is live again -- and now it really does bind, because
        // 40% of $1 against a $1,000 best day is a genuine payout blocker.
        ChallengeState barelyUp = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(1000, -999), 0, 0, BreachBasis.Equity);
        Assert.Equal(1.0, barelyUp.Balance - r.StartBalance, 6);
        Assert.False(barelyUp.ConsistencyOk);
        Assert.StartsWith("Consistency - ", barelyUp.BindingConstraint);
    }

    [Fact]
    public void The_evaluation_has_no_consistency_rule_at_all()
    {
        // Spec 1.1: none in the evaluation, and a one-day pass is allowed. The 50% figure quoted by
        // some aggregators is LucidFlex's rule, not LucidPro's.
        PropRules r = Fixtures.Eval50K();
        Assert.Equal(0.0, r.ConsistencyPct, 6);

        // The whole 3,000 target in a single day: passed, with no consistency objection.
        ChallengeState s = ChallengeEngine.Evaluate(r, Fixtures.DaysFromPnL(3000), 0, 0, BreachBasis.Equity);
        Assert.Equal(0.0, s.ConsistencyCapNow, 6);
        Assert.True(s.ConsistencyOk);
        Assert.Equal(Verdict.Passed, s.Verdict);
    }
}
