using FundedPath.Engine;
using Xunit;

// Verdict precedence, spec 4.2:
//   1 Breached (terminal) > 2 DailyLockout (soft) > 3 Passed (Evaluation) >
//   4 PayoutEligible (LiveSim) > 5 InProgress.
// The two things worth breaking a build over are that Breached outranks a passing balance, and that
// nothing in the LiveSim path can ever turn a consistency failure into a failed account.
public class VerdictTests
{
    // The engine writes the headline separator as an escape so the source file stays pure ASCII
    // (this tree is edited on Windows and read by the NinjaScript compiler, where a UTF-8 middle dot
    // read back as Windows-1252 renders as mojibake). The tests spell it the same way.
    const string Dot = " \u00B7 ";

    // ---- 1. Breached is terminal ----

    [Fact]
    public void Breached_beats_a_balance_above_the_profit_target()
    {
        PropRules r = Fixtures.Eval50K();

        // Realized 53,500 -- past the 53,000 target, with the day count already met (MinDays is 0
        // in the evaluation). An open 6,000 loser drags equity to 47,500, under the 50,100 floor.
        ChallengeState s = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 53500), 0, -6000, BreachBasis.Equity);

        Assert.Equal(50100.0, s.Floor, 6);
        Assert.Equal(53500.0, s.Balance, 6);
        Assert.Equal(47500.0, s.Equity, 6);
        Assert.Equal(Verdict.Breached, s.Verdict);
        Assert.StartsWith("BREACHED", s.Headline);

        // Proof the inputs really would have passed: the same numbers on the balance basis do.
        // Without this the test above could be passing for the wrong reason (a floor bug rather
        // than precedence).
        ChallengeState onBalance = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 53500), 0, -6000, BreachBasis.Balance);
        Assert.Equal(Verdict.Passed, onBalance.Verdict);
    }

    // ---- the one open assumption in the whole model (spec 1.4) ----

    [Fact]
    public void Breach_is_decided_on_equity_or_on_balance_according_to_the_basis()
    {
        PropRules r = Fixtures.Eval50K();

        // Nothing closed, so balance is still 50,000 and the floor is 50,000 - 2,000. An open
        // 2,100 loser puts EQUITY 100 under the floor and leaves BALANCE 2,000 above it. This is
        // the case the two readings of Lucid's rulebook actually disagree about.
        ChallengeState equity = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 0, -2100, BreachBasis.Equity);
        Assert.Equal(48000.0, equity.Floor, 6);
        Assert.Equal(Verdict.Breached, equity.Verdict);
        Assert.Equal(-100.0, equity.RoomToFloor, 6);
        Assert.Equal(-2100.0, equity.DayPnL, 6);   // same basis as RoomToFloor, by contract

        ChallengeState balance = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 0, -2100, BreachBasis.Balance);
        Assert.Equal(Verdict.InProgress, balance.Verdict);
        Assert.Equal(2000.0, balance.RoomToFloor, 6);
        Assert.Equal(0.0, balance.DayPnL, 6);      // an open position does not count on this basis
    }

    // ---- 2. the soft daily loss limit ----

    [Fact]
    public void A_soft_daily_lockout_does_not_breach_the_account()
    {
        // Resolved through a real binding, because the DLL only exists on an account whose owner
        // bought it at checkout: the catalog row ships it OFF and the binding switches it on.
        AccountBinding b = new AccountBinding();
        b.AccountKey = BindingStore.KeyFor("Playback", "Sim101");
        b.Firm = Firm.Lucid;
        b.Plan = "LucidPro";
        b.Size = 50000;
        b.Phase = Phase.Evaluation;
        b.DailyLossLimitOn = true;

        PropRules r = b.ResolveRules();
        Assert.Equal(1200.0, r.DailyLossLimit, 6);
        Assert.True(r.DailyLossSoft);
        Assert.Equal(0.0, RuleCatalog.Find(Firm.Lucid, "LucidPro", 50000, Phase.Evaluation).DailyLossLimit, 6);

        // Down 1,300 on the day: past the 1,200 limit, but balance 48,700 is still 700 clear of the
        // 48,000 floor. The account survives -- a lockout is not a breach.
        ChallengeState hit = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), -1300, 0, BreachBasis.Equity);
        Assert.Equal(Verdict.DailyLockout, hit.Verdict);
        Assert.NotEqual(Verdict.Breached, hit.Verdict);
        Assert.Equal(48700.0, hit.Balance, 6);
        Assert.True(hit.RoomToFloor > 0);
        Assert.Equal("DAILY LOCKOUT" + Dot + "DONE FOR TODAY", hit.Headline);

        // Exactly at the limit locks out; a dollar short does not.
        Assert.Equal(Verdict.DailyLockout,
            ChallengeEngine.Evaluate(r, Fixtures.NoDays(), -1200, 0, BreachBasis.Equity).Verdict);
        Assert.Equal(Verdict.InProgress,
            ChallengeEngine.Evaluate(r, Fixtures.NoDays(), -1199, 0, BreachBasis.Equity).Verdict);
    }

    [Fact]
    public void The_binding_constraint_never_contradicts_a_terminal_breach()
    {
        // Same resolved binding as the lockout test: the DLL only exists on an account whose owner
        // bought it at checkout.
        AccountBinding b = new AccountBinding();
        b.AccountKey = BindingStore.KeyFor("Playback", "Sim101");
        b.Firm = Firm.Lucid;
        b.Plan = "LucidPro";
        b.Size = 50000;
        b.Phase = Phase.Evaluation;
        b.DailyLossLimitOn = true;
        PropRules r = b.ResolveRules();

        // Down 2,500 realized on the day. Balance 47,500 is 500 UNDER the 48,000 floor, and the
        // 1,200 daily limit was blown 1,300 ago. The account is dead. Before the fix the DLL's
        // -1,300 slack outbid the floor's -500 and the cockpit named a SOFT rule as what breaks
        // first on an account that is already terminal -- and the DLL is not even one of spec 4.2's
        // {target, floor, consistency, days} candidates.
        ChallengeState s = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), -2500, 0, BreachBasis.Equity);

        Assert.Equal(Verdict.Breached, s.Verdict);
        Assert.Equal(48000.0, s.Floor, 6);
        Assert.Equal(-500.0, s.RoomToFloor, 6);
        Assert.Equal("Floor - $500 below it", s.BindingConstraint);
        Assert.Equal("BREACHED" + Dot + "$500 BELOW THE FLOOR", s.Headline);

        // The DLL still binds while the account is ALIVE -- the fix silences it only once the floor
        // has already ended the challenge.
        ChallengeState alive = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), -1300, 0, BreachBasis.Equity);
        Assert.Equal(Verdict.DailyLockout, alive.Verdict);
        Assert.Equal("Daily loss limit - reached, $100 past it", alive.BindingConstraint);
    }

    [Fact]
    public void The_fixed_daily_loss_limit_stops_at_the_initial_trail_balance()
    {
        // A funded 50K bought with the limit ON. The fixed $1,200 is a BELOW-the-Initial-Trail rule:
        // from $52,100 up, Lucid replaces it with LucidScale (60% of the peak end-of-day profit, so a
        // $3,000 peak profit is $1,800 of room, not $1,200) and this engine does not enforce that.
        AccountBinding b = new AccountBinding();
        b.AccountKey = BindingStore.KeyFor("Playback", "Sim101");
        b.Firm = Firm.Lucid;
        b.Plan = "LucidPro";
        b.Size = 50000;
        b.Phase = Phase.LiveSim;
        b.DailyLossLimitOn = true;
        PropRules r = b.ResolveRules();
        Assert.Equal(1200.0, r.DailyLossLimit, 6);
        Assert.Equal(52100.0, r.TrailStopClose, 6);
        Assert.Equal(60.0, r.ScaleDllPctOfPeakProfit, 6);

        // BELOW the trail: 51,000 closed, down 1,300 today -> 49,700. The fixed limit is in force and
        // the engine is right to lock the session.
        ChallengeState below = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(1000), -1300, 0, BreachBasis.Equity);
        Assert.Equal(49700.0, below.Balance, 6);
        Assert.Equal(Verdict.DailyLockout, below.Verdict);

        // ABOVE it: 54,300 closed, down 1,300 today -> 53,000, past 52,100. LucidScale owns the day
        // now and would allow $500 more. Reporting DailyLockout here stops a trader the firm is still
        // letting trade, so the engine must stand down -- and say that nothing is watching his day.
        ChallengeState above = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(4300), -1300, 0, BreachBasis.Equity);
        Assert.Equal(53000.0, above.Balance, 6);
        Assert.NotEqual(Verdict.DailyLockout, above.Verdict);
        Assert.DoesNotContain("Daily loss limit", above.BindingConstraint);
        Assert.Contains(above.Warnings, w => w.Contains("scaling daily loss limit takes over"));

        // The stand-down is scoped to rows that HAVE a scaling limit. The evaluation has none, so the
        // fixed limit stays armed at any balance -- there is nothing there to replace it.
        AccountBinding e = new AccountBinding();
        e.AccountKey = BindingStore.KeyFor("Playback", "Sim101");
        e.Firm = Firm.Lucid;
        e.Plan = "LucidPro";
        e.Size = 50000;
        e.Phase = Phase.Evaluation;
        e.DailyLossLimitOn = true;
        PropRules eval = e.ResolveRules();
        Assert.Equal(0.0, eval.ScaleDllPctOfPeakProfit, 6);
        ChallengeState evalHigh = ChallengeEngine.Evaluate(
            eval, Fixtures.DaysFromPnL(4300), -1300, 0, BreachBasis.Equity);
        Assert.Equal(53000.0, evalHigh.Balance, 6);
        Assert.Equal(Verdict.DailyLockout, evalHigh.Verdict);
    }

    // ---- 3. Passed ----

    [Fact]
    public void Passed_requires_the_target_and_the_minimum_days()
    {
        // LucidPro's evaluation has no minimum-days rule (a one-day pass is allowed), so the gate
        // is forced on here to test it at all. Mutating is safe: Fixtures hands back a Clone().
        PropRules r = Fixtures.Eval50K();
        r.MinDays = 3;
        Assert.Equal(53000.0, r.TargetBalance, 6);

        // Target met on day one. The money is done; the calendar is not.
        ChallengeState early = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 53000), 0, 0, BreachBasis.Equity);
        Assert.Equal(Verdict.InProgress, early.Verdict);
        Assert.Equal(1, early.QualifyingDays);
        Assert.Equal("TARGET MET" + Dot + "2 MORE TRADING DAYS", early.Headline);
        Assert.Equal("Trading days - 2 more days needed", early.BindingConstraint);

        // Three days, target met: passed.
        ChallengeState passed = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 51000, 52000, 53000), 0, 0, BreachBasis.Equity);
        Assert.Equal(Verdict.Passed, passed.Verdict);
        Assert.Equal(3, passed.QualifyingDays);
        Assert.Equal("PASSED" + Dot + "$3,000 OF PROFIT", passed.Headline);

        // Three days, a dollar short of the target: not passed.
        ChallengeState shortOfTarget = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 51000, 52000, 52999), 0, 0, BreachBasis.Equity);
        Assert.Equal(Verdict.InProgress, shortOfTarget.Verdict);
        Assert.Equal(1.0, shortOfTarget.ToTarget, 6);
    }

    [Fact]
    public void A_day_with_no_fills_and_no_pnl_does_not_count_toward_the_day_requirement()
    {
        PropRules r = Fixtures.Eval50K();
        r.MinDays = 3;

        // The middle row is a session the trader sat out. It is a real calendar day and it belongs
        // in the ledger and on the chart, but a firm counts TRADING days, not elapsed ones.
        System.Collections.Generic.List<TradingDay> days =
            new System.Collections.Generic.List<TradingDay>(Fixtures.DaysFromCloses(50000, 51500, 51500, 53000));
        days[1].Fills = 0;
        days[1].RealizedPnL = 0;

        ChallengeState s = ChallengeEngine.Evaluate(r, days, 0, 0, BreachBasis.Equity);
        Assert.Equal(2, s.QualifyingDays);
        Assert.Equal(Verdict.InProgress, s.Verdict);
    }

    // ---- 4. PayoutEligible ----

    [Fact]
    public void PayoutEligible_requires_the_buffer_plus_minimum_the_consistency_rule_and_the_days()
    {
        PropRules r = Fixtures.LiveSim50K();
        Assert.Equal(52100.0, r.Buffer, 6);        // not withdrawable
        Assert.Equal(500.0, r.MinPayout, 6);
        Assert.Equal(52600.0, r.PayoutBalance, 6); // Lucid's own "minimum balance for a $500 payout"
        Assert.Equal(3, r.DaysToPayout);
        Assert.Equal(40.0, r.ConsistencyPct, 6);

        // 900 / 900 / 800 -- three qualifying days, 2,600 of profit, best day 900 against a
        // 40% x 2,600 = 1,040 cap. Everything satisfied.
        ChallengeState ok = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(900, 900, 800), 0, 0, BreachBasis.Equity);
        Assert.Equal(52600.0, ok.Balance, 6);
        Assert.Equal(Verdict.PayoutEligible, ok.Verdict);
        Assert.True(ok.ConsistencyOk);
        Assert.Equal(3, ok.QualifyingDays);
        Assert.Equal("PAYOUT ELIGIBLE" + Dot + "$500 ABOVE THE BUFFER", ok.Headline);

        // A dollar under the payout balance: not eligible, and the money is what is binding.
        ChallengeState shortOfBuffer = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(900, 900, 799), 0, 0, BreachBasis.Equity);
        Assert.Equal(Verdict.InProgress, shortOfBuffer.Verdict);
        Assert.Equal(1.0, shortOfBuffer.ToTarget, 6);
        Assert.Equal("Payout minimum - $1 to go", shortOfBuffer.BindingConstraint);

        // Same money, same consistency, but the day requirement raised to 5: the calendar binds.
        // (A 40% rule cannot be satisfied in fewer than three days -- max/total >= 1/n -- so the
        // day gate has to be moved rather than the day count reduced to isolate it.)
        PropRules slower = Fixtures.LiveSim50K();
        slower.DaysToPayout = 5;
        ChallengeState shortOfDays = ChallengeEngine.Evaluate(
            slower, Fixtures.DaysFromPnL(900, 900, 800), 0, 0, BreachBasis.Equity);
        Assert.Equal(Verdict.InProgress, shortOfDays.Verdict);
        Assert.Equal("Trading days - 2 more days needed", shortOfDays.BindingConstraint);
    }

    [Fact]
    public void A_failing_consistency_rule_downgrades_to_InProgress_and_never_fails_the_account()
    {
        PropRules r = Fixtures.LiveSim50K();

        // Same 2,600 of profit and the same balance as the eligible case, but taken as
        // 1,500 / 600 / 500. Best day 1,500 against the 1,040 cap: the payout is blocked.
        ChallengeState s = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromPnL(1500, 600, 500), 0, 0, BreachBasis.Equity);

        Assert.Equal(52600.0, s.Balance, 6);
        Assert.False(s.ConsistencyOk);

        // The whole point: on LucidPro consistency gates the PAYOUT, not the account. It must not
        // produce Breached, and it must not produce any terminal verdict at all.
        Assert.Equal(Verdict.InProgress, s.Verdict);
        Assert.NotEqual(Verdict.Breached, s.Verdict);
        Assert.NotEqual(Verdict.DailyLockout, s.Verdict);
        Assert.Equal("PAYOUT BLOCKED" + Dot + "CONSISTENCY", s.Headline);
        Assert.Equal("Consistency - best day is $460 over the 40% cap; payout blocked", s.BindingConstraint);
    }

    // ---- the Live phase's goal is a bonus, not a payout ----

    [Fact]
    public void The_live_phase_calls_its_bonus_a_bonus_and_never_a_payout()
    {
        // The Live row's ProfitTarget is the LIVE BONUS trigger -- $2,100 of profit pays a $2,000
        // bonus. Live accounts have no payout minimum at all, so "TO PAYOUT" told a live trader he
        // could not withdraw until he cleared a number that gates a bonus, not his money.
        PropRules r = Fixtures.Rules(50000, Phase.Live);
        Assert.Equal(0.0, r.StartBalance, 6);
        Assert.Equal(2100.0, r.ProfitTarget, 6);
        Assert.Equal(0.0, r.MinPayout, 6);

        ChallengeState s = ChallengeEngine.Evaluate(r, Fixtures.DaysFromPnL(500), 0, 0, BreachBasis.Equity);

        Assert.Equal(500.0, s.Balance, 6);
        Assert.Equal(1600.0, s.ToTarget, 6);
        Assert.Equal("ON TRACK" + Dot + "$1,600 TO BONUS", s.Headline);
        Assert.Equal("Live bonus - $1,600 to go", s.BindingConstraint);
        Assert.DoesNotContain("PAYOUT", s.Headline);
        Assert.DoesNotContain("Payout", s.BindingConstraint);

        // Scoped to Live: the other two phases keep their own words.
        Assert.Contains("TO PASS",
            ChallengeEngine.Evaluate(Fixtures.Eval50K(), Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Headline);
        // A dollar short of the funded payout level, so the goal really is what binds there.
        ChallengeState funded = ChallengeEngine.Evaluate(
            Fixtures.LiveSim50K(), Fixtures.DaysFromPnL(900, 900, 799), 0, 0, BreachBasis.Equity);
        Assert.Contains("TO PAYOUT", funded.Headline);
        Assert.StartsWith("Payout minimum - ", funded.BindingConstraint);
    }

    // ---- 5. nothing bound ----

    [Fact]
    public void An_unbound_or_unmodelled_account_is_measured_by_nothing()
    {
        // No rules at all: the default for every NT8 account (spec 2). The trader's own personal
        // live account must land here, never in a challenge.
        ChallengeState none = ChallengeEngine.Evaluate(null, Fixtures.DaysFromPnL(500), 100, 50, BreachBasis.Equity);
        Assert.Equal(Verdict.Untracked, none.Verdict);
        Assert.Equal(0.0, none.Balance, 6);      // echoing a real number would look like a measurement
        Assert.Equal(0.0, none.Equity, 6);
        Assert.Empty(none.Days);
        Assert.NotEmpty(none.Warnings);
        Assert.True(none.ConsistencyOk);         // an untracked account must not paint a red chip

        // A firm that is in the dropdown but has no rulebook behind it yet.
        PropRules notModelled = RuleCatalog.Find(Firm.ApexTrader, null, 0, Phase.Evaluation);
        Assert.False(notModelled.Modelled);
        Assert.Equal(Verdict.Untracked,
            ChallengeEngine.Evaluate(notModelled, Fixtures.DaysFromPnL(500), 0, 0, BreachBasis.Equity).Verdict);
    }
}
