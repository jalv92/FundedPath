using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FundedPath.Engine;
using Xunit;

// Bindings on disk. Two properties matter more than the XML itself:
//   * a round-trip must lose nothing -- a dropped field silently downgrades a tracked account;
//   * Load must never throw -- it runs on window open, and an exception there leaves a blank
//     cockpit over a healthy-looking log.
// And the ledger key must keep a Playback rehearsal away from the real account: merging those two
// is exactly what contaminated PropSim's high-water mark.
public class BindingStoreTests : IDisposable
{
    readonly string _dir;

    public BindingStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cockpit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* a leaked temp dir is not worth failing a run over */ }
    }

    string Path_(string name) { return Path.Combine(_dir, name); }

    // ---- round trip ----

    [Fact]
    public void Save_and_load_round_trip_preserves_every_field()
    {
        AccountBinding b = new AccountBinding();
        b.AccountKey           = BindingStore.KeyFor("Playback", "Sim101");
        b.AccountDisplayName   = "Sim101";
        b.Firm                 = Firm.Lucid;
        b.Plan                 = "LucidPro";
        b.Size                 = 150000;
        b.Phase                = Phase.LiveSim;
        b.DailyLossLimitOn     = true;
        b.BreachBasis          = BreachBasis.Balance;   // NOT the default, so a lost value shows up
        b.StartedUtc           = new DateTime(2026, 8, 22, 13, 45, 30, 123, DateTimeKind.Utc);
        b.StartBalanceOverride = 154600.5;              // a fraction, to catch a locale-formatted write
        b.PeakEodCloseSeed     = 158200.25;             // ditto, and above the override so a swap shows
        b.Enforcement          = EnforcementMode.Armed;   // NOT the default, so a lost value shows up
        b.RunMode              = RunMode.PerDay;          // NOT the default, so a lost value shows up
        b.Notes                = "second attempt, DLL on";

        BindingStore saved = new BindingStore();
        saved.Put(b);
        string path = Path_("bindings.xml");
        saved.Save(path);

        BindingStore loaded = BindingStore.Load(path);
        Assert.Equal("", loaded.LastLoadError);

        AccountBinding got = loaded.Find(b.AccountKey);
        Assert.NotNull(got);
        Assert.Equal(b.AccountKey, got.AccountKey);
        Assert.Equal(b.AccountDisplayName, got.AccountDisplayName);
        Assert.Equal(b.Firm, got.Firm);
        Assert.Equal(b.Plan, got.Plan);
        Assert.Equal(b.Size, got.Size);
        Assert.Equal(b.Phase, got.Phase);
        Assert.Equal(b.DailyLossLimitOn, got.DailyLossLimitOn);
        Assert.Equal(b.BreachBasis, got.BreachBasis);
        Assert.Equal(b.StartedUtc, got.StartedUtc);
        Assert.Equal(DateTimeKind.Utc, got.StartedUtc.Kind);   // "o" must keep the kind, not localise it
        Assert.Equal(b.StartBalanceOverride, got.StartBalanceOverride, 6);
        // A binding carried over from an account that had already trailed: losing this silently
        // hands the drawdown floor back the room the firm has taken away for good.
        Assert.Equal(b.PeakEodCloseSeed, got.PeakEodCloseSeed, 6);
        // Whether this add-on may close positions and stop strategies on this account. Round-tripping
        // it wrong in the safe direction merely disarms him; wrong in the other direction arms an
        // account he never armed.
        Assert.Equal(b.Enforcement, got.Enforcement);
        // Whether every day is its own challenge. Losing it in this direction quietly re-attaches the
        // days the trader asked to stop counting; losing it in the other throws his run away.
        Assert.Equal(b.RunMode, got.RunMode);
        Assert.Equal(b.Notes, got.Notes);
    }

    [Fact]
    public void Every_public_field_of_a_binding_is_covered_by_the_round_trip_test()
    {
        // A field added later that nobody wired into ToElement/FromElement would round-trip as its
        // default and the test above would still pass, because it only asserts fields it knows
        // about. This is the tripwire: adding a property to AccountBinding fails here until the
        // list -- and the assertions above -- are updated too.
        List<string> covered = new List<string>(new string[] {
            "AccountKey", "AccountDisplayName", "Firm", "Plan", "Size", "Phase",
            "DailyLossLimitOn", "BreachBasis", "StartedUtc", "StartBalanceOverride",
            "PeakEodCloseSeed", "Enforcement", "RunMode", "Notes"
        });

        PropertyInfo[] props = typeof(AccountBinding).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (PropertyInfo p in props)
            Assert.True(covered.Contains(p.Name), "AccountBinding." + p.Name + " is not covered by the round-trip test.");
        Assert.Equal(covered.Count, props.Length);
    }

    [Fact]
    public void Put_replaces_and_Remove_deletes_across_a_save()
    {
        string key = BindingStore.KeyFor("MyBroker", "APEX-1");

        BindingStore s = new BindingStore();
        AccountBinding first = new AccountBinding();
        first.AccountKey = key;
        first.Size = 25000;
        s.Put(first);

        AccountBinding second = new AccountBinding();
        second.AccountKey = key;
        second.Size = 100000;
        s.Put(second);   // same key: replaces, never duplicates

        string path = Path_("replace.xml");
        s.Save(path);
        Assert.Equal(100000, BindingStore.Load(path).Find(key).Size);

        s.Remove(key);
        s.Save(path);   // overwrite of an existing file goes through File.Replace
        Assert.Null(BindingStore.Load(path).Find(key));
    }

    // ---- load must never throw ----

    [Fact]
    public void A_corrupt_file_loads_as_an_empty_store_and_reports_instead_of_throwing()
    {
        string path = Path_("corrupt.xml");
        File.WriteAllText(path, "<FundedPathBindings><Binding><AccountKey>x");

        BindingStore store = BindingStore.Load(path);   // must not throw
        Assert.Null(store.Find("x"));
        Assert.NotEqual("", store.LastLoadError);
        Assert.Contains("corrupt.xml", store.LastLoadError);
    }

    [Fact]
    public void An_empty_file_and_a_missing_path_are_reported_without_throwing()
    {
        string empty = Path_("empty.xml");
        File.WriteAllText(empty, "   \r\n  ");
        BindingStore fromEmpty = BindingStore.Load(empty);
        Assert.NotEqual("", fromEmpty.LastLoadError);

        BindingStore fromNull = BindingStore.Load(null);
        Assert.NotEqual("", fromNull.LastLoadError);
        Assert.Null(fromNull.Find("anything"));
    }

    [Fact]
    public void A_missing_file_is_the_normal_first_run_and_is_not_an_error()
    {
        // Deliberate asymmetry with the cases above, and worth pinning down: on a machine that has
        // never opened the cockpit there is no bindings.xml, and reporting that as a load failure
        // would put a scary red line in front of a trader on his first launch. Every account is
        // simply Untracked, which is already the correct default.
        BindingStore store = BindingStore.Load(Path_("never-written.xml"));
        Assert.Equal("", store.LastLoadError);
        Assert.Null(store.Find(BindingStore.KeyFor("Playback", "Sim101")));
    }

    [Fact]
    public void One_unreadable_row_does_not_cost_the_trader_the_others()
    {
        // A binding with no AccountKey cannot be attached to an account, so it is dropped -- but the
        // good rows in the same file still load, and the count of what was lost is reported.
        string path = Path_("partial.xml");
        File.WriteAllText(path,
            "<FundedPathBindings version=\"1\">" +
            "  <Binding><AccountDisplayName>orphan</AccountDisplayName></Binding>" +
            "  <Binding><AccountKey>Playback|Sim101</AccountKey><Size>50000</Size><Phase>LiveSim</Phase></Binding>" +
            "</FundedPathBindings>");

        BindingStore store = BindingStore.Load(path);
        AccountBinding good = store.Find("Playback|Sim101");
        Assert.NotNull(good);
        Assert.Equal(50000, good.Size);
        Assert.Equal(Phase.LiveSim, good.Phase);
        Assert.Contains("1 binding(s)", store.LastLoadError);
    }

    [Fact]
    public void An_unknown_enum_value_falls_back_instead_of_taking_the_file_down()
    {
        string path = Path_("future.xml");
        File.WriteAllText(path,
            "<FundedPathBindings version=\"1\">" +
            "  <Binding><AccountKey>k</AccountKey><Phase>SomeFuturePhase</Phase>" +
            "  <BreachBasis>Nonsense</BreachBasis><Enforcement>SuperArmed</Enforcement>" +
            "  <RunMode>PerWeek</RunMode></Binding>" +
            "  <Binding><AccountKey>numeric</AccountKey><RunMode>7</RunMode></Binding>" +
            "  <Binding><AccountKey>legacy</AccountKey></Binding>" +
            "</FundedPathBindings>");

        BindingStore store = BindingStore.Load(path);
        AccountBinding b = store.Find("k");
        Assert.NotNull(b);
        Assert.Equal(Phase.Evaluation, b.Phase);
        // Equity is the STRICT default: an unreadable value must warn earlier, not later (spec 1.4).
        Assert.Equal(BreachBasis.Equity, b.BreachBasis);

        // The fallback that can cost money rather than accuracy. This add-on closes positions and
        // stops strategies when it is armed, so a value it cannot read must land on WarnOnly.
        Assert.Equal(EnforcementMode.WarnOnly, b.Enforcement);

        // The other fallback that changes what a challenge MEANS. PerDay feeds the engine zero
        // completed days, so a run mode read off a corrupt or future file must land on Continuous:
        // the mode the trader did not choose must never be the one that discards his history.
        Assert.Equal(RunMode.Continuous, b.RunMode);

        // A bare number is the trap in Enum.TryParse, which parses "7" happily and hands back a
        // member that does not exist. Without the Enum.IsDefined guard this binding would come back
        // in a run mode with no name and no behaviour attached to it.
        Assert.Equal(RunMode.Continuous, store.Find("numeric").RunMode);

        // And the case every trader hits exactly once: a bindings.xml written before enforcement
        // existed, carrying no such element at all. Nothing on disk may load as armed -- arming is a
        // decision a human takes in the dialog, per account. The same file predates run modes, so it
        // must come back Continuous: it is the record of a run that spans days.
        Assert.Equal(EnforcementMode.WarnOnly, store.Find("legacy").Enforcement);
        Assert.Equal(RunMode.Continuous, store.Find("legacy").RunMode);
    }

    // ---- the ledger key ----

    [Fact]
    public void KeyFor_keeps_a_playback_rehearsal_separate_from_the_live_account()
    {
        // NinjaTrader shows both of these as the same account name. Without the provider in the key
        // a Playback rehearsal of the evaluation would push the real challenge's high-water mark up
        // and take its floor with it -- permanently, since the floor never comes back down.
        string playback = BindingStore.KeyFor("Playback", "Sim101");
        string live     = BindingStore.KeyFor("NinjaTrader Brokerage", "Sim101");
        Assert.NotEqual(playback, live);
        Assert.Equal("Playback|Sim101", playback);

        // Both stored at once, both retrievable, neither shadowing the other.
        BindingStore s = new BindingStore();
        AccountBinding a = new AccountBinding(); a.AccountKey = playback; a.Phase = Phase.Evaluation;
        AccountBinding c = new AccountBinding(); c.AccountKey = live;     c.Phase = Phase.Live;
        s.Put(a); s.Put(c);
        string path = Path_("two.xml");
        s.Save(path);

        BindingStore back = BindingStore.Load(path);
        Assert.Equal(Phase.Evaluation, back.Find(playback).Phase);
        Assert.Equal(Phase.Live, back.Find(live).Phase);

        // A missing provider can never collapse two connections onto one key, and lookup ignores a
        // capitalisation difference rather than silently downgrading an account to Untracked.
        Assert.Equal("UnknownProvider|Sim101", BindingStore.KeyFor("", "Sim101"));
        Assert.Equal("UnknownProvider|Sim101", BindingStore.KeyFor(null, "Sim101"));
        Assert.Equal(playback, BindingStore.KeyFor("  Playback  ", " Sim101 "));
        Assert.NotNull(back.Find("playback|sim101"));
    }

    // ---- resolving a binding to rules ----

    [Fact]
    public void ResolveRules_applies_the_traders_options_to_a_copy_of_the_catalog_row()
    {
        AccountBinding b = new AccountBinding();
        b.AccountKey = BindingStore.KeyFor("MyBroker", "L-50K");
        b.Firm = Firm.Lucid;
        b.Plan = "LucidPro";
        b.Size = 50000;
        b.Phase = Phase.Evaluation;
        b.DailyLossLimitOn = true;
        b.StartBalanceOverride = 50250;

        PropRules r = b.ResolveRules();
        Assert.Equal(1200.0, r.DailyLossLimit, 6);
        Assert.Equal(50250.0, r.StartBalance, 6);
        Assert.Equal(50350.0, r.FloorLockLevel, 6);   // the override moves the lock with it

        // The shared catalog row must be untouched, or the next account bound to LucidPro 50K
        // inherits this trader's checkout options.
        PropRules catalog = RuleCatalog.Find(Firm.Lucid, "LucidPro", 50000, Phase.Evaluation);
        Assert.Equal(0.0, catalog.DailyLossLimit, 6);
        Assert.Equal(50000.0, catalog.StartBalance, 6);
        Assert.NotSame(catalog, r);
        Assert.NotSame(catalog.Notes, r.Notes);

        // A firm with no rulebook resolves to null, which the cockpit renders as Untracked.
        AccountBinding other = new AccountBinding();
        other.Firm = Firm.TopstepTrader;
        other.Size = 50000;
        other.Phase = Phase.Evaluation;
        Assert.Null(other.ResolveRules());
    }
}
