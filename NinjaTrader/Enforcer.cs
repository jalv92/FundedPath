using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;

namespace FundedPath.NT
{
    // ============================================================================================
    // THE ONLY FILE IN FUNDED PATH THAT IS ALLOWED TO MUTATE AN ACCOUNT.
    //
    // Every other file in Engine/ and NinjaTrader/ is read-only on the account, and
    // tools/verify-compile.sh fails the build if an account-mutating API is named anywhere except
    // here. That gate is not ceremony. Until now this add-on could not reach the trader's money by
    // construction; it now closes his positions and aborts his running strategies, with a funded
    // account on the other side of the call. The only way to keep that reviewable is to keep the
    // surface small enough to read in one sitting: two platform calls in about a hundred lines --
    //
    //     Account.Flatten(ICollection<Instrument>)      cancels the working orders AND closes the
    //                                                   position, in one native call
    //     StrategyBase.SetState(State.Terminated)       the documented way to abort a running strategy
    //
    // -- and nothing else. No order is placed, changed or cancelled by hand anywhere in this add-on.
    //
    // If you are about to add a third mutating call here, that is the moment to ask whether it
    // belongs in this product at all.
    //
    // Design notes that are load-bearing, all measured or cited (see the API research in the build
    // log for this change):
    //
    //  * Flatten is FIRE AND FORGET. It returns void, and the Position.Close it delegates to is an
    //    async Task nobody awaits. The position is still open the instant it returns, so this file
    //    never reports "flat" on the strength of having called it -- it re-reads the account and
    //    says plainly when it could not confirm. The caller retries until IsQuiet is true.
    //  * ONE try/catch PER CALL, never one around a loop: a single throwing strategy must not
    //    abort the rest of the sweep.
    //  * Never hold lock (account.Positions) / lock (account.Strategies) across a platform call:
    //    snapshot inside the lock, act outside it.
    //  * Flatten runs BEFORE the strategies are stopped. Flatten is platform-native and cannot be
    //    overridden by user code; terminating first would let a third-party strategy's own
    //    OnStateChange(State.Terminated) place orders into an account that is still open.
    //  * CancelAllOrders is deliberately NOT called. Flatten's own shipped documentation says it
    //    cancels the orders and closes the position, and the house kill-switch has run exactly this
    //    call for months without orphaning an ATM leg. A second mutating API buys no new capability
    //    and doubles what a reviewer has to trust.
    //  * Account.IsAutoLiquidationEnabled / Account.DailyLossLimit are NOT touched. They are
    //    undocumented, the enforcement itself lives in a private stubbed method nobody can read,
    //    and IsAutoLiquidationEnabled is STATIC -- flipping it would reach every account in the
    //    platform, including ones the trader never bound. This add-on enforces on the account it
    //    was pointed at, or it does nothing.
    //
    // UNVERIFIED, and coded defensively for:
    //  * Whether Flatten throws or silently no-ops on a disconnected account. Every body in the
    //    shipped assembly is obfuscator-stubbed. Hence: try/catch, and a confirmation re-read.
    //  * Whether Flatten reaches ATM/OCO legs. House-measured over months of Market Replay, not
    //    documented. Any ATM found on the account is named in the transcript so the trader can
    //    check the SuperDOM himself.
    //  * SetState's thread affinity. Nothing documents one. What IS documented is that it runs the
    //    target strategy's OnStateChange(State.Terminated) SYNCHRONOUSLY on the calling thread --
    //    arbitrary third-party code, possibly with a Thread.Sleep in it. That is why the caller
    //    runs Enforce on a background thread and never on the window's dispatcher.
    // ============================================================================================
    public static class Enforcer
    {
        // Returns a human-readable transcript of everything it attempted and whether it worked. The
        // transcript is the product: it goes to the Output tab, into the banner, and into the day
        // ledger, so what happened to the account is legible tomorrow morning.
        //
        // Safe to call twice: it holds no state, an account with nothing open produces a transcript
        // saying so, and SetState on an already-terminated strategy is documented to be ignored.
        // Never throws to its caller -- a throw here would come out of a background thread with the
        // account half-enforced and no record of it.
        public static string Enforce(Account account, string reason)
        {
            StringBuilder t = new StringBuilder();

            try
            {
                // ---- 1. say what we are about to do, and to whom, BEFORE doing any of it --------
                // Written first and flushed to the Output tab first, so that if the platform dies
                // mid-flatten the log still names the account and the rule that tripped.
                string who = SafeName(account);
                Line(t, "FUNDED PATH ENFORCEMENT - " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                Line(t, "account: " + who);
                Line(t, "reason : " + (string.IsNullOrEmpty(reason) ? "(none given)" : reason));
                Log("[FundedPath] ENFORCING on " + who + " - " + reason);

                if (account == null)
                {
                    Line(t, "RESULT: nothing was done - no account was passed in.");
                    Log(t.ToString());
                    return t.ToString();
                }

                // ---- 2 + 3. the instruments to act on ------------------------------------------
                // Both sources, unioned. Positions alone is not enough: a resting order on an
                // instrument the account is FLAT in would survive the sweep and could open a brand
                // new position minutes after the tool told the trader he was done for the day.
                List<Instrument> targets = new List<Instrument>();
                List<string> seen = new List<string>();

                int openPositions = 0;
                try
                {
                    // Snapshot under the lock, act outside it. The documented example locks this
                    // collection and so does the house's kill-switch; it is mutated by the
                    // connection thread while we walk it.
                    lock (account.Positions)
                    {
                        foreach (Position p in account.Positions)
                        {
                            if (p == null || p.Instrument == null) continue;
                            if (p.MarketPosition == MarketPosition.Flat) continue;
                            openPositions++;
                            AddTarget(targets, seen, p.Instrument);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Line(t, "WARNING: the open positions could not be read (" + ex.Message + "). Check the account by hand.");
                }

                int workingOrders = 0;
                try
                {
                    lock (account.Orders)
                    {
                        foreach (Order o in account.Orders)
                        {
                            if (o == null || o.Instrument == null) continue;
                            // IsTerminalState is what NinjaTrader's own shipped source uses to ask
                            // "is this order still alive?". Naming the live states by hand would be
                            // a list to keep in sync with the platform forever.
                            if (Order.IsTerminalState(o.OrderState)) continue;
                            workingOrders++;
                            AddTarget(targets, seen, o.Instrument);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Line(t, "WARNING: the working orders could not be read (" + ex.Message + "). Check the account by hand.");
                }

                Line(t, "found  : " + openPositions + " open position(s), " + workingOrders +
                        " working order(s), across " + targets.Count + " instrument(s).");

                if (targets.Count == 0)
                {
                    Line(t, "orders + positions: nothing to do - the account is already flat with no working orders.");
                }
                else
                {
                    for (int i = 0; i < targets.Count; i++)
                    {
                        Instrument inst = targets[i];
                        string name = SafeInstrument(inst);
                        try
                        {
                            // ONE call does both steps: the shipped parameter documentation reads
                            // "orders to be cancelled / positions to be closed". An array satisfies
                            // ICollection<Instrument>, so no instrument lookup is needed.
                            account.Flatten(new Instrument[] { inst });
                            Line(t, "orders + positions: flatten issued for " + name +
                                    " (cancels its working orders and closes its position).");
                        }
                        catch (Exception ex)
                        {
                            Line(t, "orders + positions: FAILED for " + name + " - " + ex.Message +
                                    ". CLOSE IT BY HAND.");
                        }
                    }
                }

                // ---- 4. stop the strategies running on this account ----------------------------
                // Account.Strategies holds BOTH NinjaScript strategies and ATM strategies (an ATM
                // is a StrategyBase). Snapshot under the lock; call outside it.
                List<StrategyBase> strategies = new List<StrategyBase>();
                try
                {
                    lock (account.Strategies)
                    {
                        foreach (StrategyBase s in account.Strategies)
                            if (s != null) strategies.Add(s);
                    }
                }
                catch (Exception ex)
                {
                    Line(t, "strategies: the strategy list could not be read (" + ex.Message +
                            "). STOP YOUR STRATEGIES BY HAND in the Control Center.");
                }

                int stopped = 0, leftAlone = 0, failedStops = 0, atms = 0;
                for (int i = 0; i < strategies.Count; i++)
                {
                    StrategyBase s = strategies[i];
                    string label;
                    State st;
                    try
                    {
                        label = SafeStrategy(s, ref atms);
                        st = s.State;
                    }
                    catch (Exception ex)
                    {
                        failedStops++;
                        Line(t, "strategies: one strategy could not even be identified (" + ex.Message +
                                "). STOP IT BY HAND.");
                        continue;
                    }

                    if (st == State.Terminated)
                    {
                        leftAlone++;
                        Line(t, "strategies: " + label + " - already stopped.");
                        continue;
                    }

                    // SetState is documented to be legal only once the object has reached
                    // State.DataLoaded. The three states before it are named explicitly rather than
                    // compared ordinally: the enum's declared values are not part of any contract.
                    if (st == State.SetDefaults || st == State.Configure || st == State.Active)
                    {
                        leftAlone++;
                        Line(t, "strategies: " + label + " - not running yet (" + st + "), left alone.");
                        continue;
                    }

                    try
                    {
                        // The documented abort. NOT CloseStrategy: that one is virtual, so a
                        // third-party strategy can override it into a no-op, and it submits its own
                        // closing orders, which would race the flatten above.
                        //
                        // UNVERIFIED: SetState is itself an override on NinjaScriptBase rather than
                        // sealed, so in principle a strategy could intercept this too. Nothing
                        // shipped does. Per-call try/catch is the whole mitigation, and a failure is
                        // shouted rather than swallowed.
                        s.SetState(State.Terminated);
                        stopped++;
                        Line(t, "strategies: " + label + " - stop requested (was " + st + ").");
                    }
                    catch (Exception ex)
                    {
                        failedStops++;
                        Line(t, "strategies: " + label + " - FAILED to stop (" + ex.Message +
                                "). STOP IT BY HAND in the Control Center.");
                    }
                }

                if (strategies.Count == 0)
                    Line(t, "strategies: none are attached to this account.");
                else
                    Line(t, "strategies: " + stopped + " stopped, " + leftAlone + " needed nothing, " +
                            failedStops + " could not be stopped" +
                            (atms > 0 ? ", " + atms + " of them ATM (check the SuperDOM: an ATM's legs are cancelled by the flatten, but confirm it by eye)" : "") + ".");

                // ---- 5. did it actually take? --------------------------------------------------
                // Flatten returns before the fills land, so "not yet" here is the ordinary case for
                // a moment. The caller keeps checking and calls again until this reads confirmed.
                string detail;
                bool quiet = IsQuiet(account, out detail);
                Line(t, quiet
                    ? "RESULT: confirmed - no open position and no working order remain."
                    : "RESULT: NOT CONFIRMED YET - " + detail +
                      ". The platform closes positions asynchronously, so this is normal for a second or two; " +
                      "the panel keeps checking and acts again until it is confirmed.");

                if (failedStops > 0)
                    Line(t, "ACTION NEEDED: " + failedStops +
                            " strategy/strategies could not be stopped from here. Stop them by hand in the Control Center.");
            }
            catch (Exception ex)
            {
                // The outer net. Anything that got past a per-call catch still comes back as a
                // transcript, because a caller that gets an exception instead of a transcript has
                // no idea how far the enforcement got.
                Line(t, "UNEXPECTED FAILURE: " + ex.Message + " - CHECK THE ACCOUNT BY HAND.");
            }

            string text = t.ToString();
            try { Log(text); } catch { }
            return text;
        }

        // Read-only: no call in here mutates anything. Used both by Enforce's own confirmation line
        // and by the panel's retry loop, so there is ONE definition of "this account is quiet" and
        // the two can never disagree.
        //
        // Fails CLOSED: an account it cannot read is reported as not quiet, because "I could not
        // look" must never be rendered as "all clear".
        public static bool IsQuiet(Account account, out string detail)
        {
            detail = "";
            if (account == null) { detail = "no account"; return false; }

            // A disconnected account reads as flat: NinjaTrader empties Positions and Orders when the
            // connection drops, so "no open position and no working order" is exactly what a live,
            // unreachable position looks like from here. Confirming quiet off that read is the one
            // failure mode where this tool actively tells the trader to stop watching - and it is the
            // same read the retry loop trusts to stop retrying. Fail closed: no connection, no verdict.
            try
            {
                Connection cn = account.Connection;
                if (cn == null || cn.Status != ConnectionStatus.Connected)
                {
                    detail = "the account is not connected, so its positions and orders cannot be read. "
                           + "Nothing here can confirm the account is flat - check it in NinjaTrader yourself.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                detail = "could not read the connection state: " + ex.Message;
                return false;
            }

            try
            {
                List<string> open = new List<string>();
                lock (account.Positions)
                {
                    foreach (Position p in account.Positions)
                    {
                        if (p == null || p.Instrument == null) continue;
                        if (p.MarketPosition == MarketPosition.Flat) continue;
                        open.Add(SafeInstrument(p.Instrument) + " " + p.MarketPosition + " " +
                                 p.Quantity.ToString(CultureInfo.InvariantCulture));
                    }
                }

                int working = 0;
                lock (account.Orders)
                {
                    foreach (Order o in account.Orders)
                    {
                        if (o == null) continue;
                        if (!Order.IsTerminalState(o.OrderState)) working++;
                    }
                }

                if (open.Count == 0 && working == 0) return true;

                detail = (open.Count > 0 ? "still open: " + string.Join(", ", open.ToArray()) : "flat") +
                         (working > 0 ? "; " + working + " working order(s)" : "");
                return false;
            }
            catch (Exception ex)
            {
                detail = "the account could not be read (" + ex.Message + ")";
                return false;
            }
        }

        // ---- small helpers -------------------------------------------------------------------

        // Deduplicated by FullName rather than by reference: two Instrument objects for the same
        // contract would otherwise produce two flattens, and the second one goes out against an
        // account the first one may already have closed.
        static void AddTarget(List<Instrument> targets, List<string> seen, Instrument inst)
        {
            string key = SafeInstrument(inst);
            if (seen.Contains(key)) return;
            seen.Add(key);
            targets.Add(inst);
        }

        static string SafeInstrument(Instrument inst)
        {
            try { return inst == null ? "(null instrument)" : inst.FullName; }
            catch { return "(unreadable instrument)"; }
        }

        static string SafeName(Account account)
        {
            try { return account == null ? "(no account)" : account.DisplayName; }
            catch { return "(unreadable account)"; }
        }

        // An ATM is counted as it is named, so the summary line can tell the trader to eyeball the
        // SuperDOM without a second pass over the list.
        static string SafeStrategy(StrategyBase s, ref int atms)
        {
            string name;
            try { name = string.IsNullOrEmpty(s.Name) ? s.GetType().Name : s.Name; }
            catch { name = "(unnamed strategy)"; }

            bool isAtm = false;
            try { isAtm = s is AtmStrategy; }
            catch { }
            if (isAtm) { atms++; return "ATM " + name; }
            return name;
        }

        static void Line(StringBuilder t, string s)
        {
            t.Append(s);
            t.Append('\n');
        }

        static void Log(string s)
        {
            try { NinjaTrader.Code.Output.Process(s, NinjaTrader.NinjaScript.PrintTo.OutputTab1); }
            catch { }
        }
    }
}
