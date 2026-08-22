using System.Collections.Generic;
using FundedPath.Engine;
using Xunit;

// The caveat panel. Warnings are the only place an unverified rule, an assumption or a corrupt
// ledger ever reaches the trader, and the tab renders only the FIRST FOUR of them before collapsing
// the rest behind a "+N more" line. That makes both the wording and the ORDER load-bearing: a
// warning nobody reads is the same as a warning nobody wrote.
public class WarningTests
{
    [Fact]
    public void The_scaling_limit_warning_names_profit_and_never_balance()
    {
        // 60% of WHAT decides whether a 50K's scaling daily limit is $1,800 on a $3,000 peak profit
        // or, read as a share of the balance, something north of $30,000. The catalog was corrected
        // to peak end-of-day PROFIT; this warning is the only place that number reaches the screen,
        // and it used to print the rejected reading beside the correct one on the very same account.
        ChallengeState s = ChallengeEngine.Evaluate(
            Fixtures.LiveSim50K(), Fixtures.NoDays(), 0, 0, BreachBasis.Equity);

        string scale = null;
        foreach (string w in s.Warnings)
            if (w.StartsWith("The scaling daily loss limit (")) scale = w;

        Assert.NotNull(scale);
        Assert.Contains("60%", scale);
        Assert.Contains("profit", scale);
        Assert.DoesNotContain("balance", scale.ToLowerInvariant());

        // No LucidScale rule, no sentence about one.
        ChallengeState eval = ChallengeEngine.Evaluate(
            Fixtures.Eval50K(), Fixtures.NoDays(), 0, 0, BreachBasis.Equity);
        Assert.DoesNotContain(eval.Warnings, w => w.StartsWith("The scaling daily loss limit ("));
    }

    [Fact]
    public void An_engine_alarm_is_never_buried_behind_catalog_prose()
    {
        // The funded rows are unverified end to end, so every one of their nine catalog notes is
        // promoted to a warning. Emitted first, they filled all four rendered slots with prose and
        // pushed the corrupt-ledger alarm -- a wrong BALANCE, on the screen the trader is reading to
        // decide whether he can keep trading -- to #10, where nothing ever showed it.
        PropRules r = Fixtures.LiveSim50K();
        List<TradingDay> days = new List<TradingDay>(Fixtures.DaysFromPnL(900, 900, 800));
        days[1].RealizedPnL = double.NaN;

        ChallengeState s = ChallengeEngine.Evaluate(r, days, 0, 0, BreachBasis.Equity);

        // The premise: this row really is prose-heavy, or the ordering proves nothing.
        Assert.True(s.Warnings.Length > 4,
            "the funded row must still carry more warnings than the tab renders");
        Assert.Contains("non-finite", s.Warnings[0]);

        // Not an accident of this one alarm: every engine-computed warning sorts ahead of the first
        // catalog note, whatever the catalog happens to say today.
        int firstNote = -1;
        for (int i = 0; i < s.Warnings.Length && firstNote < 0; i++)
            for (int j = 0; j < r.Notes.Length; j++)
                if (s.Warnings[i] == r.Notes[j]) { firstNote = i; break; }

        Assert.True(firstNote >= 0, "the funded row's notes must still be promoted");
        Assert.Contains(s.Warnings, w => w.Contains("non-finite"));
        Assert.True(System.Array.IndexOf(s.Warnings, s.Warnings[0]) < firstNote);
        for (int i = firstNote; i < s.Warnings.Length; i++)
            Assert.DoesNotContain("non-finite", s.Warnings[i]);
    }

    [Fact]
    public void Warnings_read_as_sentences_to_a_trader_not_as_notes_to_a_developer()
    {
        // A caveat the trader cannot act on is noise, and noise is what teaches him to scroll past
        // the one that mattered. No ASCII "--" dashes, and no pointers to documents he does not have
        // open (a spec section, a source file, a rulebook page).
        //
        // The set below has to make every engine warning FIRE, or a string can be rewritten badly and
        // still pass: the corrupt-ledger and hard-rule warnings never appear on a clean modelled row.
        PropRules eval = Fixtures.Eval50K();

        List<TradingDay> nanRow = new List<TradingDay>(Fixtures.DaysFromCloses(50000, 51000));
        nanRow[0].RealizedPnL = double.NaN;

        List<TradingDay> shuffled = new List<TradingDay>(Fixtures.DaysFromCloses(50000, 51000, 52000));
        System.DateTime swap = shuffled[0].Date;
        shuffled[0].Date = shuffled[1].Date;
        shuffled[1].Date = swap;

        PropRules intraday = Fixtures.Eval50K();
        intraday.HwmBasis = HwmBasis.IntradayEquity;

        // No modelled row is like this. The warnings exist so a future firm cannot inherit LucidPro's
        // soft treatment silently, which means they are only ever read on a row built by hand.
        PropRules harsh = Fixtures.LiveSim50K();
        harsh.DailyLossLimit = 1200.0;
        harsh.DailyLossSoft = false;
        harsh.ConsistencyBlocksPayoutOnly = false;

        PropRules brokenRuleSet = Fixtures.Eval50K();
        brokenRuleSet.MaxLoss = 0.0;
        brokenRuleSet.FloorLockOffset = -1.0;

        string[][] all =
        {
            ChallengeEngine.Evaluate(eval, Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Warnings,
            ChallengeEngine.Evaluate(eval, Fixtures.NoDays(), 0, 0, BreachBasis.Balance).Warnings,
            ChallengeEngine.Evaluate(Fixtures.LiveSim50K(), Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Warnings,
            ChallengeEngine.Evaluate(Fixtures.Rules(50000, Phase.Live), Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Warnings,
            ChallengeEngine.Evaluate(eval, nanRow, 0, 0, BreachBasis.Equity).Warnings,
            ChallengeEngine.Evaluate(eval, shuffled, 0, 0, BreachBasis.Equity).Warnings,
            ChallengeEngine.Evaluate(intraday, Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Warnings,
            ChallengeEngine.Evaluate(harsh, Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Warnings,
            ChallengeEngine.Evaluate(harsh, Fixtures.DaysFromPnL(4300), -1300, 0, BreachBasis.Equity).Warnings,
            ChallengeEngine.Evaluate(brokenRuleSet, Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Warnings,
            ChallengeEngine.Evaluate(null, Fixtures.NoDays(), 0, 0, BreachBasis.Equity).Warnings
        };

        // Catalog notes travel through the same array, so only the engine's own sentences are judged.
        List<string> catalogNotes = new List<string>();
        foreach (PropRules r in RuleCatalog.All)
            if (r.Notes != null) catalogNotes.AddRange(r.Notes);

        List<string> engineWarnings = new List<string>();
        foreach (string[] set in all)
            foreach (string w in set)
            {
                if (catalogNotes.Contains(w) || engineWarnings.Contains(w)) continue;
                engineWarnings.Add(w);
                Assert.DoesNotContain("--", w);
                Assert.DoesNotContain("(spec ", w);
                Assert.DoesNotContain("phase 1", w);
                Assert.DoesNotContain("PropRules", w);
                Assert.DoesNotContain("rules-sources", w);
                Assert.DoesNotContain("addendum", w);
            }

        // The six rewritten sentences all have to be IN there, or this passes by not looking.
        Assert.Contains(engineWarnings, w => w.Contains("non-finite"));               // corrupt ledger row
        Assert.Contains(engineWarnings, w => w.StartsWith("Open positions are counted"));
        Assert.Contains(engineWarnings, w => w.StartsWith("Open positions are NOT counted"));
        Assert.Contains(engineWarnings, w => w.StartsWith("The scaling daily loss limit ("));
        Assert.Contains(engineWarnings, w => w.Contains("ends the account, but this screen"));
        Assert.Contains(engineWarnings, w => w.Contains("consistency rule does more than block a payout"));
    }
}
