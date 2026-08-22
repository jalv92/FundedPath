using System.Collections.Generic;
using FundedPath.Engine;
using Xunit;

// The rulebook itself, and the two ways a binding rewrites it.
//
// Everything here is a NUMBER A TRADER LOSES MONEY ON, so each expectation is written out in full
// rather than derived from the catalog's own arrays -- a test that recomputed the value the same way
// the code does would pass with the code wrong. Sources are named per assertion: the resolved
// rulebook is docs/rules-sources.md, which supersedes the design spec.
public class RuleCatalogTests
{
    static readonly int[]    Sizes = { 25000, 50000, 100000, 150000 };
    // rules-sources.md section 2: the SAME amounts in the evaluation and the funded phase. The
    // aggregator claiming none at 25K and 2,100 / 3,000 funded at 100K / 150K is rejected outright.
    static readonly double[] Dll   = { 600.0, 1200.0, 1800.0, 2700.0 };
    // rules-sources.md, "Initial Trail Balance": start + MaxLoss + 100. 52,100 on the 50K and 154,600 on the
    // 150K are the two the trader's dashboard states verbatim.
    static readonly double[] Buffers = { 26100.0, 52100.0, 103100.0, 154600.0 };

    static readonly Phase[] Phases = { Phase.Evaluation, Phase.LiveSim, Phase.Live };

    // ---- the catalog is complete -------------------------------------------------------------

    [Fact]
    public void Every_LucidPro_size_and_phase_resolves_to_a_modelled_row()
    {
        for (int i = 0; i < Sizes.Length; i++)
        {
            for (int p = 0; p < Phases.Length; p++)
            {
                PropRules r = RuleCatalog.Find(Firm.Lucid, "LucidPro", Sizes[i], Phases[p]);

                Assert.True(r != null, "no LucidPro row for " + Sizes[i] + " " + Phases[p]);
                Assert.True(r.Modelled, "LucidPro " + Sizes[i] + " " + Phases[p] + " is not modelled");
                Assert.Equal(Firm.Lucid, r.Firm);
                Assert.Equal("LucidPro", r.Plan);
                Assert.Equal(Sizes[i], r.Size);
                Assert.Equal(Phases[p], r.Phase);
                // Every modelled Lucid phase trails on session closes, never on an intraday high.
                Assert.Equal(HwmBasis.EodClose, r.HwmBasis);
            }
        }

        // A plan the catalog does not carry resolves to nothing rather than to a plausible row.
        Assert.Null(RuleCatalog.Find(Firm.Lucid, "LucidFlex", 50000, Phase.Evaluation));
    }

    // ---- the daily loss limit ------------------------------------------------------------------

    [Fact]
    public void The_fixed_daily_loss_limit_is_the_same_amount_in_both_phases()
    {
        for (int i = 0; i < Sizes.Length; i++)
        {
            PropRules eval  = RuleCatalog.Find(Firm.Lucid, "LucidPro", Sizes[i], Phase.Evaluation);
            PropRules funded = RuleCatalog.Find(Firm.Lucid, "LucidPro", Sizes[i], Phase.LiveSim);

            Assert.Equal(Dll[i], eval.DailyLossLimitWhenOn, 6);
            Assert.Equal(Dll[i], funded.DailyLossLimitWhenOn, 6);

            // Both rows SHIP the limit off: it is a checkout option, and the catalog must not
            // pre-arm a rule the trader may not have bought.
            Assert.Equal(0.0, eval.DailyLossLimit, 6);
            Assert.Equal(0.0, funded.DailyLossLimit, 6);
            Assert.False(eval.HasDailyLossLimit);
            Assert.False(funded.HasDailyLossLimit);

            // Soft on both: it locks the day, it never ends the account.
            Assert.True(eval.DailyLossSoft);
            Assert.True(funded.DailyLossSoft);
        }
    }

    [Fact]
    public void The_25K_daily_loss_limit_is_no_longer_carried_as_disputed()
    {
        // Confirmed by the dashboard card. It used to ship with a DISAGREEMENT note telling the
        // trader the 600 might not exist at all; D7 closes that, and a note that is no
        // longer true is worse than no note.
        PropRules r = RuleCatalog.Find(Firm.Lucid, "LucidPro", 25000, Phase.Evaluation);

        Assert.Equal(600.0, r.DailyLossLimitWhenOn, 6);
        for (int i = 0; i < r.Notes.Length; i++)
            Assert.DoesNotContain("DISAGREEMENT", r.Notes[i]);
    }

    [Fact]
    public void Buying_the_limit_off_leaves_no_fixed_limit_in_either_phase()
    {
        // The dashboard-implied rule (D3): the checkout choice is made once and carries from
        // the evaluation into the funded account. One flag on the binding, both phases.
        AccountBinding off = Binding(50000, Phase.Evaluation);
        off.DailyLossLimitOn = false;
        Assert.Equal(0.0, off.ResolveRules().DailyLossLimit, 6);

        off.Phase = Phase.LiveSim;
        PropRules funded = off.ResolveRules();
        Assert.Equal(0.0, funded.DailyLossLimit, 6);
        Assert.False(funded.HasDailyLossLimit);

        // Switching the fixed limit off does NOT switch off LucidScale: above the Initial Trail
        // Balance the 60%-of-peak-EOD-profit rule still applies to a funded account.
        Assert.Equal(60.0, funded.ScaleDllPctOfPeakProfit, 6);

        AccountBinding on = Binding(50000, Phase.Evaluation);
        on.DailyLossLimitOn = true;
        Assert.Equal(1200.0, on.ResolveRules().DailyLossLimit, 6);
        on.Phase = Phase.LiveSim;
        Assert.Equal(1200.0, on.ResolveRules().DailyLossLimit, 6);
    }

    [Fact]
    public void LucidScale_is_carried_as_data_only_on_the_funded_phase()
    {
        for (int i = 0; i < Sizes.Length; i++)
        {
            Assert.Equal(60.0, RuleCatalog.Find(Firm.Lucid, "LucidPro", Sizes[i], Phase.LiveSim).ScaleDllPctOfPeakProfit, 6);
            // Evaluation and Live have no LucidScale rule at all.
            Assert.Equal(0.0, RuleCatalog.Find(Firm.Lucid, "LucidPro", Sizes[i], Phase.Evaluation).ScaleDllPctOfPeakProfit, 6);
            Assert.Equal(0.0, RuleCatalog.Find(Firm.Lucid, "LucidPro", Sizes[i], Phase.Live).ScaleDllPctOfPeakProfit, 6);
        }

        // Nothing is measured against it, and the funded row says so in words the UI will show.
        PropRules funded = RuleCatalog.Find(Firm.Lucid, "LucidPro", 50000, Phase.LiveSim);
        Assert.Contains(funded.Notes, n => n.Contains("DISPLAYED ONLY"));
        // The worked example from D2, so the "of PROFIT" reading cannot be quietly
        // reverted to "of balance" without this failing.
        Assert.Contains(funded.Notes, n => n.Contains("$3,000 peak EOD profit gives $1,800"));
    }

    // ---- the floor and the buffer ----------------------------------------------------------------

    [Fact]
    public void The_buffer_is_start_plus_max_loss_plus_100_on_every_size()
    {
        for (int i = 0; i < Sizes.Length; i++)
        {
            PropRules r = RuleCatalog.Find(Firm.Lucid, "LucidPro", Sizes[i], Phase.LiveSim);

            Assert.Equal(Buffers[i], r.Buffer, 6);
            // Not a coincidence, an identity: the buffer IS the close at which the trail stops.
            // Every later change has to keep these two the same number.
            Assert.Equal(r.TrailStopClose, r.Buffer, 6);
            // Lucid's own "minimum balance for a $500 payout" column.
            Assert.Equal(Buffers[i] + 500.0, r.PayoutBalance, 6);
        }
    }

    [Fact]
    public void The_floor_locks_at_start_plus_100_in_the_evaluation_and_the_funded_phase()
    {
        for (int i = 0; i < Sizes.Length; i++)
        {
            PropRules eval   = RuleCatalog.Find(Firm.Lucid, "LucidPro", Sizes[i], Phase.Evaluation);
            PropRules funded = RuleCatalog.Find(Firm.Lucid, "LucidPro", Sizes[i], Phase.LiveSim);

            Assert.Equal(100.0, eval.FloorLockOffset, 6);
            Assert.Equal(100.0, funded.FloorLockOffset, 6);
            Assert.Equal(Sizes[i] + 100.0, eval.FloorLockLevel, 6);
            Assert.Equal(Sizes[i] + 100.0, funded.FloorLockLevel, 6);
        }

        // Live is the exception, and the catalog header now says so instead of claiming the lock is
        // a firm-wide constant on all three phases. The row is unverified and its note explains that
        // Lucid reads the 2,000 as the trigger for the lock, not as the level.
        PropRules live = RuleCatalog.Find(Firm.Lucid, "LucidPro", 50000, Phase.Live);
        Assert.Equal(2000.0, live.FloorLockOffset, 6);
        Assert.False(live.Verified);
        Assert.Contains(live.Notes, n => n.Contains("DISAGREEMENT on the floor lock"));
    }

    // ---- the firms that are only in the dropdown --------------------------------------------------

    [Fact]
    public void The_three_unmodelled_firms_are_listed_but_never_measure_anything()
    {
        Firm[] others = { Firm.MyFundedFutures, Firm.ApexTrader, Firm.TopstepTrader };

        for (int i = 0; i < others.Length; i++)
        {
            // Listed: the dropdown shows them greyed out rather than pretending they do not exist.
            Assert.Contains(others[i], RuleCatalog.Firms);
            Assert.False(RuleCatalog.IsModelled(others[i]));

            // Find hands back the placeholder so the dialog can resolve the entry it is showing --
            // but it is a row that measures nothing, and every caller checks Modelled.
            PropRules r = RuleCatalog.Find(others[i], "LucidPro", 50000, Phase.Evaluation);
            Assert.NotNull(r);
            Assert.False(r.Modelled);
            Assert.Equal(0, r.Size);

            // And the one call that matters refuses it: an account bound to one of these is
            // Untracked, measured by nothing.
            AccountBinding b = Binding(50000, Phase.Evaluation);
            b.Firm = others[i];
            Assert.Null(b.ResolveRules());
        }

        Assert.True(RuleCatalog.IsModelled(Firm.Lucid));
    }

    // ---- D2: an overridden start balance carries the buffer with it --------------------------------

    [Fact]
    public void A_start_balance_override_does_not_pay_out_on_zero_profit()
    {
        // The repro: a funded trader binds his 50K and types his CURRENT balance into "Start
        // balance". Three flat qualifying days, no P&L at all. Before the fix the buffer stayed at
        // the catalog's 52,100 while the start moved to 53,000, so PayoutBalance came out 52,600 --
        // under the balance -- and the cockpit told a trader who had made nothing to withdraw $900.
        AccountBinding b = Binding(50000, Phase.LiveSim);
        b.StartBalanceOverride = 53000.0;

        PropRules r = b.ResolveRules();

        Assert.Equal(53000.0, r.StartBalance, 6);
        Assert.Equal(55100.0, r.Buffer, 6);          // moved with the start
        Assert.Equal(r.TrailStopClose, r.Buffer, 6); // the identity the chart's buffer line reads
        Assert.Equal(55600.0, r.PayoutBalance, 6);
        Assert.Equal(53100.0, r.FloorLockLevel, 6);

        ChallengeState s = ChallengeEngine.Evaluate(r, Fixtures.DaysFromPnL(0.0, 0.0, 0.0), 0.0, 0.0, BreachBasis.Equity);

        Assert.NotEqual(Verdict.PayoutEligible, s.Verdict);
        Assert.Equal(Verdict.InProgress, s.Verdict);
        Assert.Equal(53000.0, s.Balance, 6);
        Assert.Equal(2600.0, s.ToTarget, 6);
        Assert.DoesNotContain("PAYOUT ELIGIBLE", s.Headline);

        // The distortion is stated rather than hidden: the trader is told the buffer moved and how
        // to undo it, because a payout level 3,000 above Lucid's is conservative, not correct.
        Assert.Contains(s.Warnings, w => w.Contains("Start balance overridden"));

        // The catalog row itself is untouched, or the next account on this plan inherits 55,100.
        Assert.Equal(52100.0, RuleCatalog.Find(Firm.Lucid, "LucidPro", 50000, Phase.LiveSim).Buffer, 6);
    }

    [Fact]
    public void An_override_on_a_phase_with_no_buffer_still_only_moves_the_start()
    {
        // The evaluation has no buffer to carry, so the override behaves exactly as before and no
        // note is appended.
        AccountBinding b = Binding(50000, Phase.Evaluation);
        b.StartBalanceOverride = 50250.0;

        PropRules r = b.ResolveRules();

        Assert.Equal(50250.0, r.StartBalance, 6);
        Assert.Equal(0.0, r.Buffer, 6);
        Assert.Equal(50350.0, r.FloorLockLevel, 6);
        Assert.Equal(RuleCatalog.Find(Firm.Lucid, "LucidPro", 50000, Phase.Evaluation).Notes.Length, r.Notes.Length);
    }

    // ---- D3: the peak-close seed ------------------------------------------------------------------

    [Fact]
    public void A_seeded_peak_close_raises_the_floor_a_mid_challenge_binding_would_understate()
    {
        // Day 12 of a 50K evaluation whose best close was 52,000. NT8 keeps roughly three days of
        // executions, so the rebuilt ledger starts at 50,000 and the floor reads 48,000: 2,000 of
        // room that does not exist.
        AccountBinding b = Binding(50000, Phase.Evaluation);
        Assert.Equal(50000.0, b.ResolveRules().SeededHwm, 6);
        Assert.Equal(48000.0, b.ResolveRules().SeededFloor, 6);
        Assert.Equal(48000.0,
            ChallengeEngine.Evaluate(b.ResolveRules(), Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Floor, 6);

        b.PeakEodCloseSeed = 52000.0;
        PropRules r = b.ResolveRules();

        Assert.Equal(52000.0, r.PeakEodCloseSeed, 6);
        Assert.Equal(52000.0, r.SeededHwm, 6);
        Assert.Equal(50000.0, r.SeededFloor, 6);   // the firm's floor, 2,000 above the naive one
        Assert.True(r.SeededFloor > r.InitialFloor);

        // The assertion this test was NAMED for. SeededHwm and SeededFloor are what the binding
        // dialog previews; the number that ends challenges is the one ChallengeEngine computes, and
        // for as long as it ignored the seed the dialog promised "Floor $50,000" while the rail drew
        // 48,000 and the cockpit reported 2,000 of room the firm had already taken away.
        ChallengeState s = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 0, 0, BreachBasis.Equity);
        Assert.Equal(50000.0, s.Floor, 6);
        Assert.Equal(r.SeededFloor, s.Floor, 6);

        // InitialFloor stays seed-free on purpose: the engine sanity-checks the lock level against
        // it, and a seeded value above that level would fire the check as a rule-set bug.
        Assert.Equal(48000.0, r.InitialFloor, 6);
    }

    [Fact]
    public void The_seed_is_capped_by_the_lock_and_can_never_lower_the_floor()
    {
        AccountBinding b = Binding(50000, Phase.LiveSim);

        // Well past the trail stop: the floor is frozen at start + 100, not sky-high.
        b.PeakEodCloseSeed = 60000.0;
        Assert.Equal(50100.0, b.ResolveRules().SeededFloor, 6);
        Assert.Equal(b.ResolveRules().FloorLockLevel, b.ResolveRules().SeededFloor, 6);

        // Below the start balance -- a typo. The high-water mark starts AT the start balance and
        // only ratchets up, so it is clamped rather than allowed to drop the floor.
        b.PeakEodCloseSeed = 40000.0;
        Assert.Equal(50000.0, b.ResolveRules().SeededHwm, 6);
        Assert.Equal(48000.0, b.ResolveRules().SeededFloor, 6);
    }

    // ---- helper ------------------------------------------------------------------------------------

    static AccountBinding Binding(int size, Phase phase)
    {
        AccountBinding b = new AccountBinding();
        b.AccountKey = BindingStore.KeyFor("MyBroker", "L-" + size);
        b.AccountDisplayName = "L-" + size;
        b.Firm = Firm.Lucid;
        b.Plan = "LucidPro";
        b.Size = size;
        b.Phase = phase;
        b.BreachBasis = BreachBasis.Equity;
        return b;
    }
}
