using System.Collections.Generic;
using System.Reflection;
using FundedPath.Engine;
using Xunit;

// WHEN DOES A RUN END? Continuous says "when the trader resets it"; PerDay says "at every ET session
// close". That is one question with one answer, and the answer is a mode on the binding.
//
// The mode is dangerous in exactly one way: it changes what the SAME account's numbers MEAN, without
// changing a single rule. PerDay reads $50,700 where Continuous reads $51,400 on identical fills.
// So this suite pins down the two properties that keep it honest:
//   * PerDay is the engine's existing behaviour fed a different list of days -- ChallengeEngine
//     does not know this enum exists, and the assertions below are written so that a future edit
//     giving it opinions about run modes goes red here;
//   * nothing else about the binding moves with it.
// The disk half -- an old file loads Continuous, an unknown value loads Continuous, a round trip
// keeps PerDay -- lives in BindingStoreTests, next to the other fail-safe enum reads.
public class RunModeTests
{
    // LucidPro 50K evaluation, the numbers this whole repo is checked against:
    // start 50,000 - max loss 2,000 - floor lock at 50,100 - profit target 3,000 - DLL 1,200 if ON.
    const double Start = 50000.0, MaxLoss = 2000.0, Target = 3000.0;

    static AccountBinding Bound(RunMode mode)
    {
        AccountBinding b = new AccountBinding();
        b.AccountKey = BindingStore.KeyFor("Playback", "Sim101");
        b.Firm = Firm.Lucid;
        b.Plan = "LucidPro";
        b.Size = 50000;
        b.Phase = Phase.Evaluation;
        b.RunMode = mode;
        return b;
    }

    // ---- the one that matters: PerDay is the engine, unchanged, fed no completed days ----

    [Fact]
    public void Feeding_zero_completed_days_is_exactly_what_PerDay_means()
    {
        PropRules r = Fixtures.Eval50K();
        Assert.Equal(Start, r.StartBalance, 6);
        Assert.Equal(MaxLoss, r.MaxLoss, 6);

        // The trader's own sentence: "yesterday I got to $700 but I want to start the next day from
        // zero without counting those $700." Yesterday closed at 50,700 and today is +700 again. In a
        // run that spans days both days are his: the balance carries yesterday, and yesterday's close
        // has already ratcheted the floor up to 48,700.
        ChallengeState carried = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(Start, 50700), 700.0, 0, BreachBasis.Equity);
        Assert.Equal(51400.0, carried.Balance, 6);
        Assert.Equal(48700.0, carried.Floor, 6);

        // PerDay hands the engine no completed days at all, and both halves of the answer fall out of
        // that one fact: the balance is the start plus today, and the high-water mark is closes[-1],
        // i.e. the start balance, so the floor is start - max loss. There is no branch anywhere in
        // ChallengeEngine that produces this -- it is the day-zero case it has always had.
        ChallengeState fresh = ChallengeEngine.Evaluate(
            r, Fixtures.NoDays(), 700.0, 0, BreachBasis.Equity);

        Assert.Equal(50700.0, fresh.Balance, 6);        // start + today, and nothing else
        Assert.Equal(48000.0, fresh.Floor, 6);          // start - max loss, flat
        Assert.False(fresh.FloorLocked);
        Assert.Equal(700.0, fresh.DayPnL, 6);
        Assert.Equal(2700.0, fresh.RoomToFloor, 6);     // and 2,000 of it belongs to today alone
        Assert.Empty(fresh.Days);                       // nothing to plot as history, by construction
        Assert.Equal(0, fresh.QualifyingDays);

        // Same fills today, and yesterday's $700 is gone from both of the numbers it had moved --
        // the balance he is judged on and the floor that judges him. That is the whole request.
        Assert.Equal(700.0, carried.Balance - fresh.Balance, 6);
        Assert.Equal(700.0, carried.Floor - fresh.Floor, 6);
    }

    [Fact]
    public void The_floor_cannot_ratchet_in_PerDay_because_there_is_nothing_to_ratchet_from()
    {
        PropRules r = Fixtures.Eval50K();

        // A monster day in progress: +$2,000 does not move the floor, because the trail reads CLOSES
        // and today has not closed. Feed the engine no days on every one of them and the floor is the
        // same flat 48,000 all day, which is what "never ratchets" means in PerDay.
        double[] intraday = { -500.0, 0.0, 400.0, 2000.0 };
        for (int i = 0; i < intraday.Length; i++)
        {
            ChallengeState s = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), intraday[i], 0, BreachBasis.Equity);
            Assert.Equal(48000.0, s.Floor, 6);
            Assert.False(s.FloorLocked);
        }
    }

    [Fact]
    public void A_single_PerDay_day_answers_the_full_profit_target()
    {
        // The verdict a PerDay run asks for is "did THIS day pass?", and on LucidPro that question is
        // well posed as asked: no minimum trading days, so a one-day pass is allowed and the target
        // stays the plan's full 3,000 rather than some per-day slice this add-on invented.
        PropRules r = Fixtures.Eval50K();
        Assert.Equal(0, r.MinDays);
        Assert.Equal(Target, r.ProfitTarget, 6);

        ChallengeState shy = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 2999.0, 0, BreachBasis.Equity);
        Assert.Equal(Verdict.InProgress, shy.Verdict);
        Assert.Equal(1.0, shy.ToTarget, 6);

        ChallengeState made = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), Target, 0, BreachBasis.Equity);
        Assert.Equal(Verdict.Passed, made.Verdict);
        Assert.Equal(0.0, made.ToTarget, 6);

        // And the day can break on its own terms too: -2,000 is the floor, not a step toward one.
        ChallengeState broke = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), -2000.0, 0, BreachBasis.Equity);
        Assert.Equal(Verdict.Breached, broke.Verdict);
        Assert.Equal(48000.0, broke.Floor, 6);
    }

    // ---- the mode changes nothing else ----

    [Fact]
    public void RunMode_resolves_the_same_rules_the_same_floor_lock_and_the_same_daily_limit()
    {
        AccountBinding cont = Bound(RunMode.Continuous);
        AccountBinding perDay = Bound(RunMode.PerDay);
        cont.DailyLossLimitOn = perDay.DailyLossLimitOn = true;

        PropRules a = cont.ResolveRules();
        PropRules b = perDay.ResolveRules();

        // Field by field through reflection rather than by hand: the claim is that the mode is
        // independent of EVERY other field, including the next one somebody adds to PropRules.
        PropertyInfo[] props = typeof(PropRules).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(props);
        for (int i = 0; i < props.Length; i++)
            Assert.Equal(props[i].GetValue(a, null), props[i].GetValue(b, null));

        // Spelled out for the three the design names, so a reader does not have to trust the loop.
        Assert.Equal(50100.0, b.FloorLockLevel, 6);          // start + FloorLockOffset, untouched
        Assert.Equal(1200.0, b.DailyLossLimit, 6);           // the checkout option still applies
        Assert.Equal(53000.0, b.TargetBalance, 6);

        // ...and the same through the engine, where the daily limit becomes a balance the trader
        // reads off the chart. Both modes open the day at 50,000 here, so both draw it at 48,800.
        ChallengeState sa = ChallengeEngine.Evaluate(a, Fixtures.NoDays(), -300.0, 0, BreachBasis.Equity);
        ChallengeState sb = ChallengeEngine.Evaluate(b, Fixtures.NoDays(), -300.0, 0, BreachBasis.Equity);
        Assert.Equal(48800.0, sa.DailyLossLimitLevel, 6);
        Assert.Equal(sa.DailyLossLimitLevel, sb.DailyLossLimitLevel, 6);
        Assert.Equal(sa.Floor, sb.Floor, 6);
        Assert.Equal(sa.Verdict, sb.Verdict);
    }

    // ---- the one exception, and it is deliberate ----

    [Fact]
    public void The_high_water_seed_belongs_to_a_run_that_spans_days_so_PerDay_drops_it()
    {
        // PeakEodCloseSeed is the trader typing "my highest close so far was 51,800", because NT8 only
        // keeps about three days of executions and a challenge bound on day 12 cannot rebuild the rest.
        // It is a claim about a run that spans days. PerDay has no such run.
        AccountBinding cont = Bound(RunMode.Continuous);
        AccountBinding perDay = Bound(RunMode.PerDay);
        cont.PeakEodCloseSeed = perDay.PeakEodCloseSeed = 51800.0;

        PropRules a = cont.ResolveRules();
        PropRules b = perDay.ResolveRules();

        Assert.Equal(51800.0, a.SeededHwm, 6);
        Assert.Equal(49800.0, ChallengeEngine.Evaluate(a, Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Floor, 6);

        // Carried into PerDay it would put the floor at 49,800 on a day whose rule says 48,000 -- the
        // panel would report a breach the day never had, and an armed account would be flattened for
        // it. Dropped where the seed reaches the rules, so the engine, the rail and the dialog's
        // preview cannot disagree about which floor is in force.
        Assert.Equal(0.0, b.PeakEodCloseSeed, 6);
        Assert.Equal(Start, b.SeededHwm, 6);
        Assert.Equal(48000.0, ChallengeEngine.Evaluate(b, Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Floor, 6);
    }

    [Fact]
    public void A_new_binding_and_a_default_binding_are_both_Continuous()
    {
        // Not a formality: `new AccountBinding()` is what the dialog hands a first-time account, and
        // the enum's first member is the only thing standing between that trader and a challenge that
        // silently forgets every day he has already traded.
        Assert.Equal(RunMode.Continuous, new AccountBinding().RunMode);
        Assert.Equal(RunMode.Continuous, default(RunMode));
    }
}
