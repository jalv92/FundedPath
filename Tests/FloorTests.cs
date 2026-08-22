using System.Collections.Generic;
using FundedPath.Engine;
using Xunit;

// The floor is the number that ends challenges, so it gets the hardest tests in this suite.
//
// Spec 4.1, verbatim:
//     floor(day i) = min( max(closes[0..i-1]) - MaxLoss , StartBalance + FloorLockOffset )
// with closes[-1] == StartBalance. Two consequences drive every case below:
//   * the floor shown for day i is built from closes STRICTLY BEFORE day i, so a winning day pays
//     out on the ratchet only from the next day onward;
//   * min(monotonic-rising, constant) is capped forever, so the lock is permanent.
public class FloorTests
{
    // ---- the trivial end of the algorithm, which is also the one a refactor breaks first ----

    [Fact]
    public void Day_zero_floor_is_start_minus_maxloss()
    {
        PropRules r = Fixtures.Eval50K();
        Assert.Equal(50000.0, r.StartBalance, 6);
        Assert.Equal(2000.0, r.MaxLoss, 6);

        // No completed day at all: the high-water mark is closes[-1], i.e. the start balance.
        ChallengeState s = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 0, 0, BreachBasis.Equity);
        Assert.Equal(48000.0, s.Floor, 6);
        Assert.False(s.FloorLocked);

        // And the first row of a real ledger carries the same number as its FloorInForce.
        ChallengeState s2 = ChallengeEngine.Evaluate(r, Fixtures.DaysFromCloses(50000, 50820), 0, 0, BreachBasis.Equity);
        Assert.Equal(48000.0, s2.Days[0].FloorInForce, 6);
    }

    [Fact]
    public void Floor_for_day_i_uses_only_closes_strictly_before_it()
    {
        PropRules r = Fixtures.Eval50K();
        IReadOnlyList<TradingDay> days = Fixtures.DaysFromCloses(50000, 50820, 51960);

        ChallengeState s = ChallengeEngine.Evaluate(r, days, 0, 0, BreachBasis.Equity);

        Assert.Equal(2, s.Days.Count);
        Assert.Equal(48000.0, s.Days[0].FloorInForce, 6);   // no close yet
        Assert.Equal(48820.0, s.Days[1].FloorInForce, 6);   // 50,820 only, NOT 51,960
        Assert.Equal(49960.0, s.Floor, 6);                  // live floor sees both closes

        // The closing balances are the engine's own accumulation, not the caller's.
        Assert.Equal(50820.0, s.Days[0].ClosingBalance, 6);
        Assert.Equal(51960.0, s.Days[1].ClosingBalance, 6);
    }

    [Fact]
    public void A_winning_day_raises_the_floor_the_next_day_not_the_same_day()
    {
        PropRules r = Fixtures.Eval50K();

        // One +1,000 day. If the ratchet were applied to the day's own row, day 0 would read 49,000
        // -- a floor the trader was never actually trading against. That off-by-one is the single
        // most plausible way to get this algorithm wrong, so it is asserted on its own.
        ChallengeState s = ChallengeEngine.Evaluate(r, Fixtures.DaysFromCloses(50000, 51000), 0, 0, BreachBasis.Equity);

        Assert.Equal(48000.0, s.Days[0].FloorInForce, 6);
        Assert.Equal(49000.0, s.Floor, 6);   // in force from the NEXT day
    }

    [Fact]
    public void A_losing_day_never_lowers_the_floor()
    {
        PropRules r = Fixtures.Eval50K();

        // Up to 51,000, then a 1,500 loss back to 49,500. The high-water mark is a Math.Max, so the
        // floor stays where the good day left it. A bare assignment would drop it to 47,500 and
        // silently hand the trader 1,500 of room the firm has already taken away.
        ChallengeState s = ChallengeEngine.Evaluate(r, Fixtures.DaysFromCloses(50000, 51000, 49500), 0, 0, BreachBasis.Equity);

        Assert.Equal(48000.0, s.Days[0].FloorInForce, 6);
        Assert.Equal(49000.0, s.Days[1].FloorInForce, 6);
        Assert.Equal(49000.0, s.Floor, 6);
    }

    // ---- the lock ----

    [Fact]
    public void Floor_freezes_at_start_plus_lock_offset_and_never_moves_again()
    {
        PropRules r = Fixtures.Eval50K();
        Assert.Equal(100.0, r.FloorLockOffset, 6);
        Assert.Equal(50100.0, r.FloorLockLevel, 6);

        // 53,000 would trail the floor to 51,000 without the cap; the cap holds it at 50,100.
        // Then a catastrophic close (45,000, far BELOW the floor) and a new all-time high (60,000).
        // Neither may move it: the drawdown cannot lower a Math.Max high-water mark, and the new
        // high is already past the cap.
        ChallengeState s = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 53000, 45000, 60000), 0, 0, BreachBasis.Equity);

        Assert.Equal(48000.0, s.Days[0].FloorInForce, 6);
        Assert.Equal(50100.0, s.Days[1].FloorInForce, 6);   // capped, not 51,000
        Assert.Equal(50100.0, s.Days[2].FloorInForce, 6);   // the crash did not lower it
        Assert.Equal(50100.0, s.Floor, 6);                  // the new high did not raise it
        Assert.True(s.FloorLocked);
    }

    [Fact]
    public void Lock_is_reported_only_once_the_floor_has_actually_reached_the_cap()
    {
        PropRules r = Fixtures.Eval50K();

        // One dollar short of the trail-stop close: floor 50,099.99, still trailing.
        ChallengeState under = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 52099.99), 0, 0, BreachBasis.Equity);
        Assert.Equal(50099.99, under.Floor, 6);
        Assert.False(under.FloorLocked);

        // Exactly at it: locked. TrailStopClose is StartBalance + MaxLoss + FloorLockOffset.
        Assert.Equal(52100.0, r.TrailStopClose, 6);
        ChallengeState at = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 52100), 0, 0, BreachBasis.Equity);
        Assert.Equal(50100.0, at.Floor, 6);
        Assert.True(at.FloorLocked);
    }

    // ---- the basis the floor ratchets on ----

    [Fact]
    public void An_intraday_equity_basis_never_moves_the_floor_backwards()
    {
        // No modelled Lucid row uses IntradayEquity, so it has to be forced on to be tested at all.
        // Mutating is safe: Fixtures hands back a Clone().
        PropRules r = Fixtures.Eval50K();
        r.HwmBasis = HwmBasis.IntradayEquity;

        // Two consecutive paint ticks of the SAME open trade, nothing closed in between. Evaluate is
        // pure and remembers no earlier tick, so folding the current equity into the high-water mark
        // made the mark rise and FALL with the tape: tick 1 printed a 50,100 LOCKED floor and tick 2
        // -- 4,950 of open profit later -- printed 48,050 and un-locked it. Spec 4.1 forbids both,
        // and a floor that retreats is the single failure this add-on exists to prevent.
        ChallengeState peak = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 0, 5000, BreachBasis.Equity);
        ChallengeState later = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 0, 50, BreachBasis.Equity);

        Assert.Equal(48000.0, peak.Floor, 6);
        Assert.Equal(48000.0, later.Floor, 6);
        Assert.False(peak.FloorLocked);
        Assert.False(later.FloorLocked);

        // Degraded to the end-of-day floor, which is conservative rather than wrong -- and it says
        // so, instead of printing an intraday floor it has no way to compute.
        Assert.Contains(peak.Warnings, w => w.Contains("NOT IMPLEMENTED"));

        // An EodClose row is unaffected and stays silent.
        ChallengeState eod = ChallengeEngine.Evaluate(Fixtures.Eval50K(), Fixtures.NoDays(), 0, 5000, BreachBasis.Equity);
        Assert.Equal(48000.0, eod.Floor, 6);
        Assert.DoesNotContain(eod.Warnings, w => w.Contains("NOT IMPLEMENTED"));
    }

    // ---- the seeded high-water mark ----

    [Fact]
    public void A_seeded_peak_close_is_the_mark_the_engine_actually_trails_from()
    {
        // Day 12 of a 50K evaluation whose best close was 52,000, bound today. NT8 keeps roughly
        // three days of executions, so the rebuilt ledger starts at 50,000. Trailing from the start
        // balance prints a 48,000 floor and hands the trader 2,000 of room the firm has already taken
        // away -- the one direction this add-on cannot be wrong in. Mutating is safe: a Clone().
        PropRules r = Fixtures.Eval50K();
        r.PeakEodCloseSeed = 52000.0;

        ChallengeState fresh = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 0, 0, BreachBasis.Equity);
        Assert.Equal(50000.0, fresh.Floor, 6);
        Assert.Equal(r.SeededFloor, fresh.Floor, 6);   // the dialog's preview and the rail agree
        Assert.False(fresh.FloorLocked);

        // It is a high-water MARK, not a one-off offset: a losing day after the binding cannot pull
        // it back down, and the first ledger row carries the seeded floor too.
        ChallengeState later = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 49500), 0, 0, BreachBasis.Equity);
        Assert.Equal(50000.0, later.Days[0].FloorInForce, 6);
        Assert.Equal(50000.0, later.Floor, 6);

        // The lock still caps it: a seed past the trail stop freezes the floor at start + 100 rather
        // than putting it sky-high.
        r.PeakEodCloseSeed = 60000.0;
        ChallengeState capped = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 0, 0, BreachBasis.Equity);
        Assert.Equal(50100.0, capped.Floor, 6);
        Assert.True(capped.FloorLocked);

        // A typo below the start balance cannot LOWER the floor, and a NaN restored from the bindings
        // file must not reach the high-water mark: a NaN floor fails no comparison, so it would not
        // trip the breach test, it would disable it.
        r.PeakEodCloseSeed = 40000.0;
        Assert.Equal(48000.0, ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Floor, 6);
        r.PeakEodCloseSeed = double.NaN;
        ChallengeState nan = ChallengeEngine.Evaluate(r, Fixtures.NoDays(), 0, -3000, BreachBasis.Equity);
        Assert.Equal(48000.0, nan.Floor, 6);
        Assert.False(double.IsNaN(nan.RoomToFloor));
        Assert.Equal(Verdict.Breached, nan.Verdict);

        // And with no seed at all, nothing moved.
        Assert.Equal(48000.0,
            ChallengeEngine.Evaluate(Fixtures.Eval50K(), Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Floor, 6);
    }

    // ---- the two worked examples the rulebook is quoted by ----

    [Fact]
    public void Spec_50K_worked_example_floors()
    {
        PropRules r = Fixtures.Eval50K();

        // Five closes from the build brief, so all FIVE days the brief describes are asserted: the
        // fixture used to stop at four and the fifth number in the sequence was never checked.
        //
        // In-force floors, days 0..4: 48,000 / 48,820 / 49,960 / 49,960 / 50,100. That is the ONE-day
        // lag spec 4.1 states ("the ratchet from day i's own close applies from day i+1") and the same
        // lag as the winning-day test above. An earlier brief listed this sequence with an extra day
        // of lag; the code and the spec agree with each other, so the spec's sequence is what is
        // asserted here and the engine is NOT to be bent to match that brief.
        ChallengeState s = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(50000, 50820, 51960, 51530, 52240, 52610), 0, 0, BreachBasis.Equity);

        Assert.Equal(5, s.Days.Count);
        Assert.Equal(48000.0, s.Days[0].FloorInForce, 6);   // closes[-1] = 50,000
        Assert.Equal(48820.0, s.Days[1].FloorInForce, 6);   // 50,820 - 2,000
        Assert.Equal(49960.0, s.Days[2].FloorInForce, 6);   // 51,960 - 2,000
        Assert.Equal(49960.0, s.Days[3].FloorInForce, 6);   // 51,530 is a losing day: no ratchet
        Assert.Equal(50100.0, s.Days[4].FloorInForce, 6);   // 52,240 - 2,000 = 50,240, capped
        Assert.Equal(50100.0, s.Floor, 6);                  // 52,610 - 2,000 = 50,610, capped too
        Assert.True(s.FloorLocked);

        // The closes themselves must round-trip through the RealizedPnL the engine is actually fed.
        Assert.Equal(50820.0, s.Days[0].ClosingBalance, 6);
        Assert.Equal(51960.0, s.Days[1].ClosingBalance, 6);
        Assert.Equal(51530.0, s.Days[2].ClosingBalance, 6);
        Assert.Equal(52240.0, s.Days[3].ClosingBalance, 6);
        Assert.Equal(52610.0, s.Days[4].ClosingBalance, 6);
    }

    [Fact]
    public void Spec_150K_trail_stops_at_a_close_of_154600()
    {
        PropRules r = Fixtures.Rules(150000, Phase.Evaluation);
        Assert.Equal(4500.0, r.MaxLoss, 6);
        Assert.Equal(150100.0, r.FloorLockLevel, 6);
        Assert.Equal(154600.0, r.TrailStopClose, 6);

        // One dollar short of the trail stop: still trailing, floor a dollar under the cap.
        ChallengeState under = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(150000, 154599), 0, 0, BreachBasis.Equity);
        Assert.Equal(150099.0, under.Floor, 6);
        Assert.False(under.FloorLocked);

        // At 154,600 the trail stops for good: 154,600 - 4,500 == 150,100 == the lock level.
        ChallengeState at = ChallengeEngine.Evaluate(
            r, Fixtures.DaysFromCloses(150000, 154600), 0, 0, BreachBasis.Equity);
        Assert.Equal(150100.0, at.Floor, 6);
        Assert.True(at.FloorLocked);
    }

    // ---- catalog invariant behind both worked examples ----

    [Fact]
    public void Every_lucid_row_locks_its_floor_at_start_plus_100()
    {
        // "154,600" is where the trail STOPS, not where the floor locks; the lock offset is a
        // firm-wide 100 on all four sizes and all three phases. Live is the deliberate exception:
        // it starts at 0 and the spec records a 2,000 lock offset (flagged in that row's Notes).
        int checkedRows = 0;
        foreach (PropRules r in RuleCatalog.All)
        {
            if (r.Firm != Firm.Lucid || !r.Modelled) continue;
            checkedRows++;
            if (r.Phase == Phase.Live) { Assert.Equal(2000.0, r.FloorLockOffset, 6); continue; }
            Assert.Equal(100.0, r.FloorLockOffset, 6);
            Assert.Equal(r.StartBalance + 100.0, r.FloorLockLevel, 6);
        }
        Assert.Equal(12, checkedRows);   // 4 sizes x 3 phases
    }
}
