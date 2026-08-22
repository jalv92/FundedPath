using System;
using System.Collections.Generic;
using System.Globalization;

namespace FundedPath.Engine
{
    // The computation. Deterministic and side-effect free: no static mutable state, no clock read, no
    // I/O, and no write into the caller's TradingDay rows. Given the same arguments it returns the same
    // ChallengeState, which is what makes the floor algorithm testable at all.
    //
    // It is called from the window's 4 Hz paint tick, so it also never throws on bad data: a null rule
    // set, a null day list or a NaN from an account mid-connect all resolve to a state the UI can draw.
    // One unhandled throw on that tick kills the window's UI thread for good and leaves a blank window
    // over a healthy-looking log.
    public static class ChallengeEngine
    {
        // Money is compared with a half-cent tolerance so a target hit exactly to the dollar is not
        // missed by a float that landed a fraction of a cent short.
        private const double Cent = 0.005;

        // Separator for the headline, matching the mockup. Written as an escape rather than typed so the
        // source file stays pure ASCII: this tree is edited on Windows and read by the NinjaScript
        // compiler, and a UTF-8 middle dot read back as Windows-1252 renders as mojibake.
        private const string Dot = " \u00B7 ";

        // days must be ordered oldest-first and carry RealizedPnL; ClosingBalance and FloorInForce
        // are filled in by this call.
        public static ChallengeState Evaluate(
            PropRules rules,
            IReadOnlyList<TradingDay> days,   // completed days only, oldest first
            double openDayRealized,           // realized P&L of the day in progress
            double openUnrealized,            // unrealized P&L of open positions
            BreachBasis breachBasis)
        {
            List<string> warnings = new List<string>();

            // An unbound account is measured by nothing and recorded nowhere (spec section 2). The
            // trader's own live account must never be counted by accident, so "no rules" is a normal
            // outcome with a normal state, not an error.
            if (rules == null)
            {
                warnings.Add("No challenge is bound to this account. Nothing is being measured.");
                return UntrackedState(warnings);
            }

            if (!rules.Modelled)
            {
                warnings.Add((string.IsNullOrEmpty(rules.Plan) ? rules.Firm.ToString() : rules.Plan)
                             + " is not modelled yet. Nothing is being measured.");
                return UntrackedState(warnings);
            }

            // Account.Get on an account mid-connect can hand back garbage. A NaN would poison every
            // comparison below in the DANGEROUS direction -- NaN <= floor is false, so a real breach
            // would silently never be reported.
            openDayRealized = Finite(openDayRealized);
            openUnrealized = Finite(openUnrealized);

            // Catalog prose is appended at the very END of this method, never here. The tab renders
            // only the first four warnings; putting the rulebook's notes first buried the alarms
            // this engine computes behind them.

            double start = rules.StartBalance;
            double maxLoss = rules.MaxLoss;
            double lockLevel = rules.FloorLockLevel;   // StartBalance + FloorLockOffset

            if (maxLoss <= 0)
                warnings.Add("This rule set carries no max loss limit, so the floor sits at its lock level from day one.");

            // Day 0's floor is StartBalance - MaxLoss by contract (spec 4.1). That only holds while the
            // lock level sits above it. A catalog row with FloorLockOffset < -MaxLoss would clamp the
            // opening floor UPWARD and understate the risk, so say so rather than quietly repairing it.
            if (lockLevel < rules.InitialFloor)
                warnings.Add("Rule-set problem: the floor lock level sits below the opening floor. Check FloorLockOffset.");

            // ---- the floor algorithm (spec 4.1) --------------------------------------------------
            //
            //   floor(i) = min( max(StartBalance, closes[0..i-1]) - MaxLoss , StartBalance + FloorLockOffset )
            //
            // Two invariants carry the whole thing, and both are structural rather than checked:
            //   1. runningMax only ever goes UP (Math.Max below). It starts at SeededHwm, which is
            //      StartBalance unless the trader typed a higher peak close when he bound the
            //      account, so with no seed closes[-1] is the start balance and day 0's floor is
            //      StartBalance - MaxLoss.
            //   2. lockLevel is a constant.
            // min(monotonic-rising, constant) is itself monotonic rising and capped, so once the floor
            // touches the lock level it is frozen forever: a later lower close cannot lower it, because
            // a lower close never lowers runningMax in the first place.

            int n = days == null ? 0 : days.Count;
            List<TradingDay> series = new List<TradingDay>(n);

            // closes[-1]. StartBalance when nothing was seeded, and the trader's own highest
            // end-of-day close when he typed one: NT8 keeps roughly three days of executions, so a
            // challenge bound on day 12 rebuilds a ledger that starts long after the real high-water
            // mark and would trail from the start balance - reporting room the firm has already
            // taken away. SeededHwm clamps a seed below the start and drops a non-finite one, so
            // this can raise the floor and never lower it.
            double runningMax = rules.SeededHwm;
            double balance = start;
            double bestDay = openDayRealized;   // the day in progress is a day like any other
            int qualifying = 0;
            bool outOfOrder = false;
            bool scrubbedRow = false;
            DateTime prevDate = DateTime.MinValue;

            for (int i = 0; i < n; i++)
            {
                TradingDay src = days[i];
                if (src == null) continue;   // a null row is an NT-layer bug; skipping beats an NRE on the paint tick

                // Same hazard as openDayRealized / openUnrealized above, and worse, because a ledger
                // row persists: one NaN day poisons balance -> balanceNow -> equityNow -> breachValue,
                // and breachValue <= liveFloor is false for NaN forever, so the breach test is disabled
                // permanently rather than for one tick. The ledger round-trips through text and
                // double.TryParse accepts the literal "NaN", so it also survives every restart.
                double rowPnL = Finite(src.RealizedPnL);
                if (rowPnL != src.RealizedPnL) scrubbedRow = true;   // NaN != NaN, so this catches it too

                // Built from closes strictly BEFORE day i: the ratchet from day i's own close applies
                // from day i+1 onward, so this is the floor the trader was actually trading against.
                double floorForDay = Math.Min(runningMax - maxLoss, lockLevel);

                balance += rowPnL;

                // A fresh row, never a write into the caller's object: Evaluate has to be callable twice
                // on the same list without the second call reading back its own output.
                TradingDay d = new TradingDay();
                d.Date = src.Date;
                d.RealizedPnL = rowPnL;
                d.Fills = src.Fills;
                d.ClosingBalance = balance;
                d.FloorInForce = floorForDay;
                series.Add(d);

                if (rowPnL > bestDay) bestDay = rowPnL;

                // A day counts toward a minimum-days / days-to-payout requirement when it was actually
                // traded. Fills is the primary signal; RealizedPnL is the fallback for a ledger row
                // restored from disk that was written before fill counts were persisted.
                if (src.Fills > 0 || rowPnL != 0.0) qualifying++;

                // Ordering is the caller's contract. Flag a violation instead of sorting: sorting would
                // hide an NT-layer bug behind a plausible-looking floor.
                if (src.Date < prevDate) outOfOrder = true;
                prevDate = src.Date;

                // The ratchet. Math.Max, never a bare assignment -- a losing day must not pull the
                // high-water mark, and therefore the floor, back down.
                if (balance > runningMax) runningMax = balance;
            }

            if (outOfOrder)
                warnings.Add("The day ledger was not ordered oldest-first, so the drawdown floor may be wrong. That is a data bug, not a rule.");

            // Once for the whole ledger, not once per row: this runs at 4 Hz and a per-row warning
            // would grow the list without bound. Silent scrubbing is the thing to avoid -- a zeroed
            // day is still a wrong balance, it just fails safe instead of disabling the breach test.
            if (scrubbedRow)
                warnings.Add("A day in your ledger carried a non-finite P&L (NaN or infinity) and was counted as ZERO, so the balance and floor below are missing that day. Your ledger file is corrupt. Rebuild it before trusting these numbers.");

            // ---- live numbers ----------------------------------------------------------------------

            // Balance is StartBalance plus every realized dollar. NEVER the broker's cash value plus
            // realized P&L: those two overlap and adding them double-counts the whole session.
            double balanceNow = balance + openDayRealized;
            double equityNow = balanceNow + openUnrealized;

            // The live floor for the day in progress uses every completed close.
            double liveMax = runningMax;
            if (rules.HwmBasis == HwmBasis.IntradayEquity)
            {
                // ponytail: NOT implemented -- degraded to EodClose, and said out loud.
                //
                // A real intraday ratchet needs the equity PEAK, and this function is pure: it holds no
                // memory of earlier ticks and TradingDay records no intraday high, so the only peak it
                // could reach for is the equity of the current tick. Folding that in makes liveMax rise
                // and FALL with the tape, which breaks both invariants spec 4.1 rests on -- the floor
                // moves backwards (a +5,000 open trade prints a 50,100 floor, and the next tick at +50
                // prints 48,050) and the lock un-freezes. A floor that retreats is the one failure mode
                // this add-on exists to prevent, so the EodClose floor -- which is merely conservative
                // and never wrong in the dangerous direction -- ships instead.
                //
                // The upgrade needs a peak the CALLER owns across ticks and across restarts (an
                // equityPeak parameter fed from persisted state), not a parameter on this call.
                warnings.Add("Intraday-equity trailing drawdown is NOT IMPLEMENTED. The floor below is the end-of-day floor, which sits at or above the real one, so it can warn LATE. Do not trade a funded account on this number.");
            }

            double liveFloor = Math.Min(liveMax - maxLoss, lockLevel);
            // Exact equality would work for the Math.Min result, but runningMax - maxLoss can land a hair
            // under the lock level through float arithmetic and leave the card reading "still trailing"
            // forever. The tolerance is well below a cent.
            bool floorLocked = liveFloor >= lockLevel - 1e-9;

            // Breached is decided against the breach basis, and RoomToFloor uses the same one, so the two
            // numbers the trader reads side by side can never disagree.
            double breachValue = breachBasis == BreachBasis.Equity ? equityNow : balanceNow;
            double roomToFloor = breachValue - liveFloor;
            bool breached = breachValue <= liveFloor;

            if (breachBasis == BreachBasis.Equity)
                warnings.Add("Open positions are counted against the floor. Lucid says \"balance\" and never says \"unrealized\", so this is the strict reading: it warns you earlier than the firm might.");
            else
                warnings.Add("Open positions are NOT counted against the floor. That is the literal reading of Lucid's wording, and it warns you later than the strict one: an open loser can put you under the floor with this screen still green.");

            // Same basis again, so "Day P&L" and "Room to floor" agree about whether an open trade counts.
            double dayPnL = breachBasis == BreachBasis.Equity ? openDayRealized + openUnrealized : openDayRealized;

            // The fixed daily loss limit is a BELOW-the-Initial-Trail rule. From that balance up,
            // the firm replaces it with the scaling limit - 60% of the peak end-of-day profit, a
            // bigger allowance than the fixed one - and this engine does not enforce that. Keeping
            // the fixed limit armed up there reports a lockout on a session the firm would still
            // let the trader trade. See docs/rules-sources.md section 2.
            bool scaleReplacesFixedDll = rules.ScaleDllPctOfPeakProfit > 0 && balanceNow >= rules.TrailStopClose;
            bool dllOn = rules.HasDailyLossLimit && !scaleReplacesFixedDll;
            if (rules.HasDailyLossLimit && scaleReplacesFixedDll)
                warnings.Add("Your balance is at or above " + Money(rules.TrailStopClose)
                             + ", where the scaling daily loss limit takes over from the fixed one. Nothing here is watching your daily loss today.");
            // DailyLossLimit is a positive magnitude. Room is measured from the day's opening balance, so
            // being up on the day genuinely buys that much extra room before the limit bites.
            double dllRoom = dllOn ? rules.DailyLossLimit + dayPnL : 0.0;
            bool dailyLockout = dllOn && dayPnL <= -rules.DailyLossLimit + Cent;

            // ---- consistency -----------------------------------------------------------------------
            double totalProfit = balanceNow - start;
            // The profit test is part of the switch, not just of the cap. A payout cannot be requested
            // at or below zero profit, so with nothing to withdraw the rule is not evaluable -- and an
            // un-evaluable rule must not be able to win the binding-constraint pick below, where a zero
            // cap minus a positive best day is the most negative slack on the board and would name
            // "consistency" on an account whose real problem is that it is $800 from a terminal breach.
            bool consistencyOn = rules.ConsistencyPct > 0 && totalProfit > Cent;
            // The cap is a share of the profit made SO FAR, so it moves every time the account does:
            // $2,000 of total profit under a 40% rule caps any single day at $800. Clamped at zero
            // because a negative dollar cap is not a number worth rendering.
            double consistencyCap = consistencyOn
                ? Math.Max(0.0, totalProfit * rules.ConsistencyPct / 100.0)
                : 0.0;
            double consistencySlack = consistencyCap - bestDay;
            bool consistencyOk = !consistencyOn || bestDay <= consistencyCap + Cent;

            // ---- goal and days ---------------------------------------------------------------------
            double goal = 0.0;
            bool hasGoal = false;
            if (rules.Phase == Phase.LiveSim)
            {
                if (rules.Buffer > 0 || rules.MinPayout > 0)
                {
                    goal = rules.PayoutBalance;   // Buffer (not withdrawable) + the minimum payout
                    hasGoal = true;
                }
                if (rules.Buffer <= 0 && rules.MinPayout > 0)
                    warnings.Add("Rule-set problem: this funded row has a minimum payout but no buffer, so the payout level is being read as a bare dollar amount.");
            }
            else if (rules.ProfitTarget > 0)
            {
                goal = rules.TargetBalance;
                hasGoal = true;
            }

            double toTarget = hasGoal ? Math.Max(0.0, goal - balanceNow) : 0.0;

            // 0..100 of the way from the opening balance to the goal. Clamped at both ends: the rail
            // draws a bar, and a bar past 100% or below 0% is noise.
            double progressPct = 0.0;
            if (hasGoal && goal > start)
                progressPct = Clamp((balanceNow - start) / (goal - start) * 100.0, 0.0, 100.0);

            int daysRequired = rules.Phase == Phase.Evaluation ? rules.MinDays : rules.DaysToPayout;
            if (daysRequired < 0) daysRequired = 0;
            bool daysMet = qualifying >= daysRequired;
            int daysShort = daysMet ? 0 : daysRequired - qualifying;

            // ---- verdict, precedence exactly per spec 4.2 -------------------------------------------
            // The profit tests read the REALIZED balance, not equity: a firm settles a pass on closed
            // money, and an open winner does not pass you.
            Verdict verdict;
            if (breached)
                verdict = Verdict.Breached;
            else if (dailyLockout)
                verdict = Verdict.DailyLockout;
            else if (rules.Phase == Phase.Evaluation && hasGoal && balanceNow >= goal - Cent && daysMet)
                verdict = Verdict.Passed;
            else if (rules.Phase == Phase.LiveSim && hasGoal && balanceNow >= goal - Cent && consistencyOk && daysMet)
                // A failing consistency lands here and falls through to InProgress. It can never produce
                // Breached or any failed verdict: on LucidPro the rule blocks the payout, not the account.
                verdict = Verdict.PayoutEligible;
            else
                verdict = Verdict.InProgress;

            // ---- binding constraint: whichever rule has the least slack in dollars -------------------
            // Slack = dollars of room before the rule bites. A negative slack means the rule is already
            // broken, which is exactly why it wins the comparison without any special casing.
            const int PickFloor = 0, PickTarget = 1, PickDll = 2, PickConsistency = 3, PickDays = 4;

            double bestSlack = roomToFloor;
            int pick = PickFloor;

            // The goal only competes while there are dollars left to make. Once it is met it stops being
            // a constraint, and the calendar takes over below.
            if (hasGoal && toTarget > Cent && toTarget < bestSlack) { bestSlack = toTarget; pick = PickTarget; }
            if (dllOn && dllRoom < bestSlack) { bestSlack = dllRoom; pick = PickDll; }
            if (consistencyOn && consistencySlack < bestSlack) { bestSlack = consistencySlack; pick = PickConsistency; }

            // Breached is terminal (spec 4.2 rule 1), so nothing can break first: the floor already did.
            // Without this a soft rule with a deeper negative slack -- the DLL, which is not even one of
            // spec 4.2's {target, floor, consistency, days} candidates -- gets named as what breaks next
            // on an account that is already dead.
            if (breached) pick = PickFloor;

            // A days requirement has no dollar slack, so it can never win the comparison above. It becomes
            // the binding constraint exactly when the money side is already done: the target is met, the
            // consistency rule is satisfied, nothing has bitten, and the only thing left between the
            // trader and his verdict is the calendar. A floor with room left is a risk, not an unmet
            // requirement, so it does not outrank the missing days.
            if (!daysMet && !breached && !dailyLockout && consistencyOk && (!hasGoal || toTarget <= Cent))
                pick = PickDays;

            // What the phase's goal actually IS. The Live row's ProfitTarget is the LIVE BONUS
            // trigger, not a payout minimum: live accounts have no payout gate, so calling it one
            // tells a live trader he cannot withdraw until he clears it.
            string goalName = rules.Phase == Phase.Evaluation ? "Profit target"
                            : rules.Phase == Phase.Live ? "Live bonus" : "Payout minimum";
            string goalSuffix = rules.Phase == Phase.Evaluation ? " TO PASS"
                              : rules.Phase == Phase.Live ? " TO BONUS" : " TO PAYOUT";

            string binding;
            switch (pick)
            {
                case PickTarget:
                    binding = goalName + " - " + Money(toTarget) + " to go";
                    break;
                case PickDll:
                    binding = dllRoom >= 0
                        ? "Daily loss limit - " + Money(dllRoom) + " of room today"
                        : "Daily loss limit - reached, " + Money(-dllRoom) + " past it";
                    break;
                case PickConsistency:
                    binding = consistencySlack >= 0
                        ? "Consistency - " + Money(consistencySlack) + " of room under the " + Pct(rules.ConsistencyPct) + " cap"
                        : "Consistency - best day is " + Money(-consistencySlack) + " over the " + Pct(rules.ConsistencyPct) + " cap; payout blocked";
                    break;
                case PickDays:
                    binding = "Trading days - " + daysShort.ToString(CultureInfo.InvariantCulture)
                              + (daysShort == 1 ? " more day needed" : " more days needed");
                    break;
                default:
                    binding = roomToFloor >= 0
                        ? "Floor - " + Money(roomToFloor) + " of room"
                        : "Floor - " + Money(-roomToFloor) + " below it";
                    break;
            }

            // ---- headline ---------------------------------------------------------------------------
            string headline;
            switch (verdict)
            {
                case Verdict.Breached:
                    headline = "BREACHED" + Dot + Money(Math.Max(0.0, -roomToFloor)) + " BELOW THE FLOOR";
                    break;
                case Verdict.DailyLockout:
                    headline = "DAILY LOCKOUT" + Dot + "DONE FOR TODAY";
                    break;
                case Verdict.Passed:
                    headline = "PASSED" + Dot + Money(Math.Max(0.0, totalProfit)) + " OF PROFIT";
                    break;
                case Verdict.PayoutEligible:
                    // Only profit ABOVE the buffer can actually be withdrawn; the buffer itself never is.
                    headline = "PAYOUT ELIGIBLE" + Dot
                               + Money(Math.Max(0.0, balanceNow - (rules.Buffer > 0 ? rules.Buffer : start)))
                               + " ABOVE THE BUFFER";
                    break;
                default:
                    if (hasGoal && toTarget > Cent)
                        headline = (balanceNow >= start ? "ON TRACK" : "BELOW START") + Dot + Money(toTarget)
                                   + goalSuffix;
                    else if (consistencyOn && !consistencyOk)
                        headline = "PAYOUT BLOCKED" + Dot + "CONSISTENCY";
                    else if (!daysMet)
                        headline = "TARGET MET" + Dot + daysShort.ToString(CultureInfo.InvariantCulture)
                                   + (daysShort == 1 ? " MORE TRADING DAY" : " MORE TRADING DAYS");
                    else
                        headline = (totalProfit >= 0 ? "IN PROGRESS" + Dot + Money(totalProfit) + " OF PROFIT"
                                                     : "IN PROGRESS" + Dot + Money(-totalProfit) + " DOWN");
                    break;
            }

            // Catalog prose LAST, and everything above it is an engine-computed alarm. The tab
            // renders the first four warnings and hides the rest behind a "+N more" line, and the
            // funded rows alone carry nine notes: with prose first, the corrupt-ledger alarm landed
            // at #10 and never reached the screen. Any renderer that truncates now truncates prose.
            CollectRuleWarnings(rules, warnings);

            ChallengeState state = new ChallengeState();
            state.Verdict = verdict;
            state.Headline = headline;
            state.BindingConstraint = binding;
            state.Balance = balanceNow;
            state.Equity = equityNow;
            state.Floor = liveFloor;
            state.FloorLocked = floorLocked;
            state.RoomToFloor = roomToFloor;
            state.ToTarget = toTarget;
            state.ProgressPct = progressPct;
            state.DayPnL = dayPnL;
            state.BestDayPnL = bestDay;
            state.ConsistencyCapNow = consistencyCap;
            state.ConsistencyOk = consistencyOk;
            state.QualifyingDays = qualifying;
            state.Days = series;
            state.Warnings = warnings.ToArray();
            return state;
        }

        // Unverified rules travel with the state so the window can label a number as an assumption
        // rather than a fact (spec 1.2, 1.3 and section 7). A note is a warning when the whole rule set
        // is marked unverified, when the note flags itself in its own text, or when it carries the
        // catalog's DISAGREEMENT marker.
        //
        // Notes are rendered NOWHERE else in the codebase, so a note that fails all three tests is
        // written and never read. That is how a VERIFIED row carrying a DISAGREEMENT - the marker the
        // catalog standardises on - shipped its disputed number to the screen as a fact, with the
        // doubt sitting silently in the binary. No such row is left in the catalog today; the rule
        // exists so the next one cannot ship the same way. Ordinal and case-sensitive on purpose:
        // DISAGREEMENT is a marker, not an English word.
        private static void CollectRuleWarnings(PropRules rules, List<string> warnings)
        {
            if (rules.Notes != null)
            {
                for (int i = 0; i < rules.Notes.Length; i++)
                {
                    string note = rules.Notes[i];
                    if (string.IsNullOrEmpty(note)) continue;
                    if (!rules.Verified
                        || note.IndexOf("unverified", StringComparison.OrdinalIgnoreCase) >= 0
                        || note.IndexOf("DISAGREEMENT", StringComparison.Ordinal) >= 0)
                        warnings.Add(note);
                }
            }

            // Carried as data, never enforced. It is a share of the highest end-of-day PROFIT, and
            // the word matters: read as a share of the BALANCE it would put a 50K's daily limit above
            // $30,000. Resolved in docs/rules-sources.md, D2.
            if (rules.ScaleDllPctOfPeakProfit > 0)
                warnings.Add("The scaling daily loss limit (" + Pct(rules.ScaleDllPctOfPeakProfit)
                             + " of your highest end-of-day profit) is shown for reference only. Nothing here measures your day against it.");

            // Phase 1 has no terminal verdict for a hard daily loss limit, so it reports one as a
            // lockout. Lucid's is soft, so this never fires on the modelled catalog -- it exists so a
            // future firm cannot inherit the soft treatment silently.
            if (rules.HasDailyLossLimit && !rules.DailyLossSoft)
                warnings.Add("This firm's daily loss limit ends the account, but this screen reports it as a lockout for the day. Losing it will not be shown here as a breach.");

            // Same shape for consistency: the engine only ever uses it to gate payout eligibility.
            if (rules.ConsistencyPct > 0 && !rules.ConsistencyBlocksPayoutOnly)
                warnings.Add("This firm's consistency rule does more than block a payout, but this screen only uses it to hold back payout eligibility. It never marks the account failed.");
        }

        // An account measured by nothing. Every collection is non-null so the window can bind to it
        // directly, and ConsistencyOk is true so an untracked account does not paint a red chip.
        private static ChallengeState UntrackedState(List<string> warnings)
        {
            ChallengeState s = new ChallengeState();
            s.Verdict = Verdict.Untracked;
            s.Headline = "UNTRACKED" + Dot + "NO CHALLENGE BOUND";
            s.BindingConstraint = "Nothing is being measured on this account.";
            s.ConsistencyOk = true;
            // Balance and Equity stay 0: without a start balance there is no balance to report, and
            // echoing a raw account number here would look like a measurement that is not happening.
            s.Days = new List<TradingDay>();
            s.Warnings = warnings.ToArray();
            return s;
        }

        private static double Finite(double v)
        {
            return (double.IsNaN(v) || double.IsInfinity(v)) ? 0.0 : v;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        // InvariantCulture is load-bearing, not decoration: NinjaTrader runs under the machine's locale,
        // and on a Spanish install "N0" would render 2250 as "2.250" while the mockup and every rule
        // source read "2,250".
        private static string Money(double v)
        {
            return "$" + v.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string Pct(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture) + "%";
        }
    }
}
