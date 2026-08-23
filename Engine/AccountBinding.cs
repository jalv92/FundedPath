using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace FundedPath.Engine
{
    // Engine layer: pure C#, no NinjaTrader references. The NT layer hands this file plain strings
    // (Account.Provider.ToString(), Account.Name) so the store can be tested without the platform.

    // What this add-on is allowed to DO to the account when a rule latches, per binding.
    //
    // WarnOnly is the default everywhere and is what an old bindings.xml loads as: the element did
    // not exist before this change, so every binding already on disk comes back with no value, and
    // the parser's fallback is this first member. An account must never become armed because a file
    // was written by an older build -- arming is a decision the trader takes once, per account, in
    // the dialog.
    public enum EnforcementMode
    {
        // Measure and say so. Nothing is ever written to the account. This is what the add-on was
        // from day one and it stays the default.
        WarnOnly,

        // Act like the firm does: on a latched breach or daily lockout, close the positions, cancel
        // the working orders and stop the strategies running on this account. One file does that
        // (NinjaTrader/Enforcer.cs) and nothing else in this codebase can.
        Armed
    }

    // One NT8 account mapped to one challenge. Absence of a binding is meaningful: an unbound
    // account is Verdict.Untracked, measured by nothing and recorded nowhere, which is what keeps
    // the trader's own personal live account out of the cockpit by default (spec 2).
    public sealed class AccountBinding
    {
        public string      AccountKey         { get; set; }   // BindingStore.KeyFor(...)
        public string      AccountDisplayName { get; set; }   // as NT8 shows it, for the UI
        public Firm        Firm               { get; set; }
        public string      Plan               { get; set; }
        public int         Size               { get; set; }
        public Phase       Phase              { get; set; }
        public bool        DailyLossLimitOn   { get; set; }   // the checkout option, per account
        public BreachBasis BreachBasis        { get; set; }
        public DateTime    StartedUtc         { get; set; }
        public double      StartBalanceOverride { get; set; } // 0 = use the catalog's StartBalance
        // The trader's own highest end-of-day close so far on this account. 0 = none, which means
        // "this challenge starts today". NT8's Account.Executions holds roughly the last three days,
        // so a challenge bound on day 12 rebuilds a ledger that begins long after the real
        // high-water mark: without this the floor restarts from the start balance and reads LOWER
        // than the firm's. Typed, because there is nowhere on the platform to read it from.
        public double      PeakEodCloseSeed   { get; set; }
        // What the add-on may DO when this account latches a breach or a daily lockout. Defaults to
        // WarnOnly for a new binding (the enum's first member) and, critically, for every binding
        // already in an older bindings.xml, which carries no such element at all: the parser falls
        // back to the same member. Nothing on disk can load as armed unless a human armed it.
        public EnforcementMode Enforcement    { get; set; }
        public string      Notes              { get; set; }   // free text, the trader's own

        // Resolve the catalog row for this binding and apply the trader's own options to a COPY.
        // Lives here rather than in the UI because the engine, the rail and the dialog all need the
        // same resolved rules, and three copies of this would drift. Returns null when the firm or
        // size is not modelled, which the caller reads as Untracked.
        public PropRules ResolveRules()
        {
            PropRules found = RuleCatalog.Find(Firm, Plan, Size, Phase);
            if (found == null || !found.Modelled)
                return null;

            PropRules r = found.Clone();

            if (DailyLossLimitOn)
                r.DailyLossLimit = r.DailyLossLimitWhenOn;

            // An override only makes sense above zero; the Live phase legitimately starts at 0 and
            // must not be "corrected" by an unset field.
            if (StartBalanceOverride > 0.0)
            {
                double planStart = r.StartBalance;
                r.StartBalance = StartBalanceOverride;

                // Buffer is NOT an independent number. On every funded row it IS TrailStopClose -
                // StartBalance + MaxLoss + FloorLockOffset, the $52,100 Lucid calls the Initial
                // Trail Balance - so moving the start and leaving the buffer at the catalog's
                // absolute value breaks the identity three things read: the chart's buffer line, the
                // level the floor locks at, and PayoutBalance (Buffer + MinPayout). Left alone, a
                // funded trader who types his CURRENT balance is told PAYOUT ELIGIBLE on zero profit
                // the moment that number clears the catalog's buffer. Carrying it keeps the model
                // self-consistent and errs toward "not yet", which is the only safe direction here.
                if (r.Buffer > 0.0)
                {
                    r.Buffer = r.TrailStopClose;

                    // Only when the number actually moved: an override typed as the plan's own start
                    // balance changes nothing, and a warning about a distortion that is not there
                    // trains the trader to ignore the warnings that are.
                    if (StartBalanceOverride != planStart)
                        r.Notes = AppendNote(r.Notes,
                            "Start balance overridden to " + Money(r.StartBalance) + ", against the plan's "
                            + Money(planStart) + ". The non-withdrawable buffer moved with it to " + Money(r.Buffer)
                            + ", so the payout level reads " + Money(r.PayoutBalance) + ". If you typed your CURRENT "
                            + "balance rather than the balance this funded account STARTED at, clear the override: "
                            + "the payout level and the floor lock are both measured from the start.");
                }
            }

            // Seeded on the rules rather than passed to the engine: the engine's high-water mark
            // starts at the start balance, and this is the one number that legitimately raises it.
            r.PeakEodCloseSeed = PeakEodCloseSeed;

            return r;
        }

        // The engine surfaces a row's Notes as warnings, so this is how a per-account adjustment
        // reaches the trader. Appends to the CLONE's array, never the catalog's.
        static string[] AppendNote(string[] notes, string note)
        {
            int n = notes == null ? 0 : notes.Length;
            string[] grown = new string[n + 1];
            for (int i = 0; i < n; i++)
                grown[i] = notes[i];
            grown[n] = note;
            return grown;
        }

        // Whole dollars, invariant culture: these strings are read on a machine set to a
        // comma-decimal locale, where "N0" would render 52100 as "52.100".
        static string Money(double v)
        {
            return "$" + v.ToString("#,##0", CultureInfo.InvariantCulture);
        }
    }

    // Bindings on disk. XML via System.Xml.Linq - no third-party JSON dependency is allowed inside
    // bin/Custom, where NinjaTrader controls the reference list.
    public sealed class BindingStore
    {
        const string RootName    = "FundedPathBindings";
        const string ElementName = "Binding";

        // Ordinal-ignore-case: NT8 account names are case-stable in practice, but a binding lost to
        // a capitalisation difference would silently downgrade a tracked account to Untracked, and
        // the trader would only notice when the cockpit stopped measuring.
        readonly Dictionary<string, AccountBinding> _byKey =
            new Dictionary<string, AccountBinding>(StringComparer.OrdinalIgnoreCase);

        // Empty when the last Load() was clean. Non-empty means the file was unreadable and this
        // store came up empty - the UI must say so, because saving over it would be data loss.
        public string LastLoadError { get; private set; }

        public BindingStore()
        {
            LastLoadError = "";
        }

        // MyDocuments is where NinjaTrader puts its user folder by default. A trader who relocated
        // "NinjaTrader 8" elsewhere passes an explicit path instead; this is only the default.
        public static string DefaultPath
        {
            get
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return Path.Combine(docs, "NinjaTrader 8", "FundedPath", "bindings.xml");
            }
        }

        // The ledger key. Provider FIRST and always present: a Playback rehearsal of the evaluation
        // and the real broker account share a display name, and merging their ledgers is precisely
        // the bug that contaminated PropSim's high-water mark (spec 2). An empty provider would let
        // two different connections collapse onto the same key, so it is never allowed through.
        public static string KeyFor(string providerName, string accountDisplayName)
        {
            string provider = providerName == null ? "" : providerName.Trim();
            string account  = accountDisplayName == null ? "" : accountDisplayName.Trim();
            if (provider.Length == 0) provider = "UnknownProvider";
            return provider + "|" + account;
        }

        // Never throws. A missing file is the normal first run and is NOT an error; an empty or
        // corrupt one yields an empty store with the reason on LastLoadError.
        public static BindingStore Load(string path)
        {
            BindingStore store = new BindingStore();

            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    store.LastLoadError = "No bindings path was given.";
                    return store;
                }

                if (!File.Exists(path))
                    return store;   // first run

                string text = File.ReadAllText(path);
                if (text == null || text.Trim().Length == 0)
                {
                    store.LastLoadError = "The bindings file is empty: " + path;
                    return store;
                }

                XDocument doc = XDocument.Parse(text);
                if (doc.Root == null)
                {
                    store.LastLoadError = "The bindings file has no root element: " + path;
                    return store;
                }

                // Parse into a local dictionary and only commit it once every element has been
                // walked, so a failure halfway through cannot leave a half-loaded store that then
                // gets written back over the good file.
                Dictionary<string, AccountBinding> parsed =
                    new Dictionary<string, AccountBinding>(StringComparer.OrdinalIgnoreCase);
                int skipped = 0;

                foreach (XElement e in doc.Root.Elements(ElementName))
                {
                    // Per-element guard. FromElement is written not to throw, but one future field
                    // parser that does would otherwise cost the trader every OTHER binding in the
                    // file. One bad row is skipped; the rest still load.
                    AccountBinding b;
                    try { b = FromElement(e); }
                    catch { b = null; }

                    if (b == null) { skipped++; continue; }
                    parsed[b.AccountKey] = b;
                }

                foreach (KeyValuePair<string, AccountBinding> kv in parsed)
                    store._byKey[kv.Key] = kv.Value;

                if (skipped > 0)
                    store.LastLoadError = skipped.ToString(CultureInfo.InvariantCulture) +
                                          " binding(s) in " + path + " were unreadable and were skipped.";
            }
            catch (Exception ex)
            {
                // Includes XmlException (corrupt), IOException (locked/unreadable) and
                // UnauthorizedAccessException. All of them mean the same thing to the cockpit:
                // come up empty, every account Untracked, and tell the trader why.
                store._byKey.Clear();
                store.LastLoadError = "Could not read " + path + ": " + ex.Message;
            }

            return store;
        }

        // Atomic: write a temp file, then swap it in. A crash or a power cut mid-write leaves either
        // the old file or the new one, never a truncated one. Unlike Load this is allowed to throw -
        // a failed save must reach the trader, not be swallowed into a silent no-op.
        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException("path");

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            XElement root = new XElement(RootName, new XAttribute("version", "1"));
            foreach (KeyValuePair<string, AccountBinding> kv in _byKey)
                root.Add(ToElement(kv.Value));

            XDocument doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

            // Same directory as the target: File.Replace requires both on one volume, and a temp
            // file in %TEMP% would fail on a machine whose Documents folder is a mapped drive.
            string temp   = path + ".tmp";
            string backup = path + ".bak";

            doc.Save(temp);

            if (File.Exists(path))
            {
                // ignoreMetadataErrors: true - a file created by a different NT8 install can carry
                // ACLs this process cannot copy, and losing the ACL is better than losing the save.
                File.Replace(temp, path, backup, true);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        // Null when the account is unmapped, which the cockpit renders as Untracked.
        public AccountBinding Find(string accountKey)
        {
            if (string.IsNullOrEmpty(accountKey))
                return null;

            AccountBinding b;
            return _byKey.TryGetValue(accountKey, out b) ? b : null;
        }

        public void Put(AccountBinding b)
        {
            if (b == null || string.IsNullOrEmpty(b.AccountKey))
                return;
            _byKey[b.AccountKey] = b;
        }

        public void Remove(string accountKey)
        {
            if (string.IsNullOrEmpty(accountKey))
                return;
            _byKey.Remove(accountKey);
        }

        // ---- XML mapping ----------------------------------------------------------------------
        // Every number and date goes through InvariantCulture on the way in AND out. On a machine
        // set to a comma-decimal locale (Javier's is es-ES) a plain ToString() would write
        // "52100,5", and the file would then be unreadable on any other machine.

        static XElement ToElement(AccountBinding b)
        {
            return new XElement(ElementName,
                new XElement("AccountKey", b.AccountKey ?? ""),
                new XElement("AccountDisplayName", b.AccountDisplayName ?? ""),
                new XElement("Firm", b.Firm.ToString()),
                new XElement("Plan", b.Plan ?? ""),
                new XElement("Size", b.Size.ToString(CultureInfo.InvariantCulture)),
                new XElement("Phase", b.Phase.ToString()),
                new XElement("DailyLossLimitOn", b.DailyLossLimitOn ? "true" : "false"),
                new XElement("BreachBasis", b.BreachBasis.ToString()),
                // Round-trip "o" keeps the UTC kind; a local-time stamp would shift the challenge
                // start date every time the machine changed timezone.
                new XElement("StartedUtc", b.StartedUtc.ToString("o", CultureInfo.InvariantCulture)),
                new XElement("StartBalanceOverride", b.StartBalanceOverride.ToString("R", CultureInfo.InvariantCulture)),
                new XElement("PeakEodCloseSeed", b.PeakEodCloseSeed.ToString("R", CultureInfo.InvariantCulture)),
                new XElement("Enforcement", b.Enforcement.ToString()),
                new XElement("Notes", b.Notes ?? ""));
        }

        // Returns null for an element that cannot produce a usable binding. The only field that
        // makes a binding usable is the key: without it the cockpit cannot attach it to an account.
        static AccountBinding FromElement(XElement e)
        {
            if (e == null)
                return null;

            string key = Text(e, "AccountKey");
            if (key.Length == 0)
                return null;

            AccountBinding b = new AccountBinding();
            b.AccountKey         = key;
            b.AccountDisplayName = Text(e, "AccountDisplayName");
            b.Firm               = ParseEnum(Text(e, "Firm"), Firm.Lucid);
            b.Plan               = Text(e, "Plan");
            b.Size               = ParseInt(Text(e, "Size"));
            b.Phase              = ParseEnum(Text(e, "Phase"), Phase.Evaluation);
            b.DailyLossLimitOn   = string.Equals(Text(e, "DailyLossLimitOn"), "true", StringComparison.OrdinalIgnoreCase);
            // Equity is the strict default (spec 1.4): an unreadable value must warn earlier, not later.
            b.BreachBasis        = ParseEnum(Text(e, "BreachBasis"), BreachBasis.Equity);
            b.StartedUtc         = ParseUtc(Text(e, "StartedUtc"));
            b.StartBalanceOverride = ParseDouble(Text(e, "StartBalanceOverride"));
            b.PeakEodCloseSeed     = ParseDouble(Text(e, "PeakEodCloseSeed"));
            // FAIL SAFE, not fail same: a missing element, an empty one, a hand-edited typo and a
            // value written by a future build all land on WarnOnly. This add-on can close positions
            // and stop strategies; the one direction that must never happen by accident is a file
            // loading as armed.
            b.Enforcement        = ParseEnum(Text(e, "Enforcement"), EnforcementMode.WarnOnly);
            b.Notes              = Text(e, "Notes");
            return b;
        }

        static string Text(XElement parent, string name)
        {
            XElement child = parent.Element(name);
            return child == null || child.Value == null ? "" : child.Value.Trim();
        }

        // A hand-edited or future enum value falls back instead of throwing: an unknown phase must
        // not take the whole bindings file down with it.
        static T ParseEnum<T>(string s, T fallback) where T : struct
        {
            T value;
            return Enum.TryParse(s, true, out value) && Enum.IsDefined(typeof(T), value) ? value : fallback;
        }

        static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static double ParseDouble(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0.0;
        }

        static DateTime ParseUtc(string s)
        {
            // RoundtripKind ONLY. Combining it with AdjustToUniversal is rejected at runtime with
            // an ArgumentException ("cannot be used with ... AdjustToUniversal"), which would take
            // the whole bindings file down on the first load. The conversion to UTC is done below.
            DateTime v;
            if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out v))
                return DateTime.MinValue;

            // A file written before the "o" format was used, or edited by hand, can come back
            // Unspecified. Treat it as UTC rather than letting ToLocalTime() shift it later.
            return v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : v.ToUniversalTime();
        }
    }
}
