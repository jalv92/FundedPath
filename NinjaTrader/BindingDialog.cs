using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FundedPath.Engine;
using NinjaTrader.Gui.Tools;

namespace FundedPath.NT
{
    // Maps ONE NinjaTrader account to ONE challenge, and writes it to bindings.xml.
    //
    // This is the only place in the add-on that decides whether an account is measured at all. An
    // account with no binding is Verdict.Untracked - measured by nothing, recorded nowhere - which
    // is what keeps the trader's own personal live account out of the cockpit by default (spec 2).
    // So the dialog is deliberately explicit: nothing is inferred from the account, everything is
    // chosen here.
    //
    // Read-only on the account, like the rest of the add-on: it reads no account object at all, only
    // the two strings the caller passes in.
    public class BindingDialog : NTWindow
    {
        // ---- palette (spec 6) -----------------------------------------------------------------
        // Hardcoded, never NinjaTrader's theme brushes: the design is locked by the approved mockup,
        // and a theme-derived colour would make it a function of the user's NT8 skin. Frozen because
        // every NT8 window runs on its own dispatcher thread - an unfrozen static brush belongs to
        // whichever thread built it and throws on the second window that touches it.
        private static readonly Brush Ground = Frozen(0x06, 0x0A, 0x16);
        private static readonly Brush Panel  = Frozen(0x0C, 0x13, 0x22);
        private static readonly Brush Card   = Frozen(0x0A, 0x10, 0x1D);
        private static readonly Brush Line   = Frozen(0x1B, 0x25, 0x40);
        private static readonly Brush Soft   = Frozen(0x15, 0x1E, 0x33);
        private static readonly Brush Gold   = Frozen(0xF2, 0xB3, 0x3D);
        private static readonly Brush Green  = Frozen(0x27, 0xD6, 0x7B);
        private static readonly Brush Red    = Frozen(0xFF, 0x54, 0x68);
        private static readonly Brush Text   = Frozen(0xE9, 0xED, 0xF8);
        private static readonly Brush Muted  = Frozen(0x7A, 0x87, 0xA2);
        private static readonly Brush Dim    = Frozen(0x4E, 0x5A, 0x74);

        // Labels Segoe UI, numbers Consolas - the house convention. FontFamily is immutable and
        // carries no dispatcher affinity, so unlike a brush it needs no Freeze().
        private static readonly FontFamily Sans = new FontFamily("Segoe UI");
        private static readonly FontFamily Mono = new FontFamily("Consolas");

        // ---- identity ---------------------------------------------------------------------------
        private readonly BindingStore _store;
        private readonly string       _path;
        private readonly string       _provider;
        private readonly string       _display;

        // The ledger key, Provider first (spec 2). Private: the dialog is modal and the tab re-reads
        // the shared store for the account IT is showing once ShowDialog returns, so nothing outside
        // this class ever needs the key or a change event.
        private readonly string _key;

        // ---- controls ---------------------------------------------------------------------------
        private ComboBox     _firm;
        private ComboBox     _plan;
        private ComboBox     _size;
        private ComboBox     _phase;
        private RadioButton  _runContinuous;
        private RadioButton  _runPerDay;
        private CheckBox     _dll;
        private RadioButton  _basisEquity;
        private RadioButton  _basisBalance;
        private RadioButton  _enforceWarn;
        private RadioButton  _enforceAct;
        private DatePicker   _start;
        private TextBox      _override;
        private TextBox      _peak;
        private TextBlock    _hint;
        private Button       _save;
        private Button       _remove;

        // What a NEW binding opens on. Not cosmetic: prefer=0 matches no size tag, SelectFirstEnabled
        // then picks index 0 = the 25K, and a Save made without noticing binds a $1,250 target and a
        // $24,000 floor onto a 50K account - plausible numbers, wrong rulebook.
        private const int DefaultSize = 50000;

        private Grid _form;
        private int  _row;

        // Repopulating a ComboBox raises SelectionChanged for every item removed and added. Without
        // this guard the firm handler re-enters through the plan handler and rebuilds the size list
        // from a half-built plan list.
        private bool _loading;

        public BindingDialog(string providerName, string accountDisplayName, BindingStore store, string storePath)
        {
            if (store == null)
                throw new ArgumentNullException("store");

            _store    = store;
            _path     = string.IsNullOrEmpty(storePath) ? BindingStore.DefaultPath : storePath;
            _provider = providerName == null ? "" : providerName;
            _display  = accountDisplayName == null ? "" : accountDisplayName;
            _key      = BindingStore.KeyFor(_provider, _display);

            // NTWindow paints its own chrome from Caption; Window.Title is ignored by it.
            Caption               = "Challenge binding";
            Width                 = 480;
            // Two money rows with their own explanation lines, the run-mode group, plus the
            // enforcement section - which is deliberately the tallest thing in the window, because a
            // control that can close positions has to be read, not skimmed. Tall enough to show all
            // of it at once on a 1080-row screen; the form scrolls if the machine has less, so this
            // number is a comfort setting now and no longer the only thing standing between the
            // enforcement section and the bottom edge.
            Height                = 960;
            ResizeMode            = ResizeMode.NoResize;
            ShowInTaskbar         = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background            = Ground;

            Content = BuildBody();
            LoadFromStore();
        }

        // ---- layout -----------------------------------------------------------------------------

        private UIElement BuildBody()
        {
            DockPanel root = new DockPanel { Margin = new Thickness(16), LastChildFill = true };

            UIElement header = BuildHeader();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            _hint = new TextBlock
            {
                FontFamily   = Sans,
                FontSize     = 11,
                Foreground   = Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 10, 0, 8),
                MinHeight    = 32
            };
            DockPanel.SetDock(_hint, Dock.Bottom);

            UIElement buttons = BuildButtons();
            DockPanel.SetDock(buttons, Dock.Bottom);

            // Bottom-docked in reverse visual order: buttons claim the bottom edge first, the hint
            // sits above them, and the form gets everything left over.
            root.Children.Add(buttons);
            root.Children.Add(_hint);
            // The form scrolls; the header, the hint and the buttons do not. This window is
            // NoResize with a fixed height, so before this every row added to the form pushed the
            // enforcement section further past the bottom edge - silently, because a Grid just clips
            // what does not fit. Now the overflow is reachable instead of gone.
            root.Children.Add(new ScrollViewer
            {
                Content                       = BuildForm(),
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background                    = Ground,
                BorderThickness               = new Thickness(0)
            });

            return root;
        }

        private UIElement BuildHeader()
        {
            StackPanel sp = new StackPanel();

            sp.Children.Add(new TextBlock
            {
                Text       = "ACCOUNT",
                FontFamily = Sans,
                FontSize   = 10,
                Foreground = Dim,
                Margin     = new Thickness(0, 0, 0, 2)
            });
            sp.Children.Add(new TextBlock
            {
                Text       = _display.Length == 0 ? "(no account)" : _display,
                FontFamily = Mono,
                FontSize   = 16,
                Foreground = Text
            });
            sp.Children.Add(new TextBlock
            {
                // The provider is half the ledger key, not decoration: a Playback rehearsal and the
                // real broker account share a display name and must never share a ledger.
                Text       = _provider.Length == 0 ? "unknown provider" : _provider,
                FontFamily = Sans,
                FontSize   = 11,
                Foreground = Muted,
                Margin     = new Thickness(0, 1, 0, 0)
            });

            Border b = new Border
            {
                Background      = Panel,
                BorderBrush     = Line,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(12, 8, 12, 10),
                Child           = sp
            };

            return b;
        }

        private UIElement BuildForm()
        {
            _form = new Grid { Margin = new Thickness(0, 14, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
            _form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            _firm  = MakeCombo();
            _plan  = MakeCombo();
            _size  = MakeCombo();
            _phase = MakeCombo();

            _firm.SelectionChanged  += OnFirmChanged;
            _plan.SelectionChanged  += OnPlanChanged;
            _size.SelectionChanged  += OnSizeChanged;
            _phase.SelectionChanged += OnLeafChanged;

            AddRow("Firm", _firm);
            AddRow("Plan", _plan);
            AddRow("Account size", _size);
            AddRow("Phase", _phase);

            // ---- what a RUN is, which is what makes every number below mean something ----------
            // Continuous is the default here and in the store, for every binding old and new: it is
            // what this add-on has always done, and it is how a real evaluation is actually traded.
            // The other option is not a lesser mode - it is a different question ("did THIS day
            // pass?"), which is the only question worth asking when a strategy is being judged one
            // replay day at a time. So both options say what they DO, not which one is normal.
            _runContinuous = MakeRadio("RunMode", "One running challenge - the way a real evaluation runs", false);
            _runPerDay     = MakeRadio("RunMode", "Each day is its own challenge", false);
            _runContinuous.IsChecked = true;
            _runContinuous.Checked += OnLeafChanged;
            _runPerDay.Checked     += OnLeafChanged;

            StackPanel run = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            run.Children.Add(_runContinuous);
            run.Children.Add(_runPerDay);
            // Only the second option carries a paragraph, the same way the enforcement group only
            // explains the option that changes what happens. The last two sentences are the ones the
            // trader needs before he dares click it: this is a lens over the ledger, not a broom.
            run.Children.Add(Explain(
                "Every trading day starts fresh at the plan's starting balance: yesterday's profit does "
              + "not carry into today and the floor never ratchets, so the verdict answers \"did THIS day "
              + "pass?\" instead of \"is the run still alive?\". For judging an automated strategy day by "
              + "day in Market Replay. It discards nothing on disk - every day already recorded stays in "
              + "the ledger and keeps its own result. It changes what counts, not what is kept."));
            AddRow("How days count", run);

            _dll = new CheckBox
            {
                FontFamily          = Sans,
                FontSize            = 12,
                Foreground          = Text,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(0, 6, 0, 6)
            };
            // Both directions to the same handler as every other control: without them the caption
            // and the hint only caught up after Save, which is the one moment feedback is useless.
            _dll.Checked   += OnLeafChanged;
            _dll.Unchecked += OnLeafChanged;
            AddRow("Daily loss limit", _dll);

            _basisEquity  = MakeRadio("BreachBasis", "Equity - counts open positions (strict)", false);
            _basisBalance = MakeRadio("BreachBasis", "Balance - realized only", false);
            _basisEquity.IsChecked = true;
            StackPanel basis = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            basis.Children.Add(_basisEquity);
            basis.Children.Add(_basisBalance);
            AddRow("Breach basis", basis);

            // Blank is a real answer, not an unfilled field: it means "count every day this account
            // has", which is the right answer whenever the account has only ever run this challenge.
            _start = new DatePicker
            {
                FontFamily  = Sans,
                FontSize    = 12,
                Background  = Card,
                Foreground  = Text,
                BorderBrush = Line,
                Margin      = new Thickness(0, 3, 0, 3)
            };

            StackPanel started = new StackPanel();
            started.Children.Add(_start);
            started.Children.Add(Explain(
                "Trading days that closed BEFORE this date are left out of the challenge - set it when "
              + "the account ran an earlier evaluation or a reset first. They are filtered, never "
              + "deleted, so a wrong date here loses no history. Leave it empty to count every day in "
              + "the ledger."));
            AddRow("First day counted", started);

            _override = MakeMoneyBox();
            _override.TextChanged += OnLeafChanged;
            AddRow("Start balance", _override);

            // The one field that cannot be derived from the platform. NT8's Account.Executions holds
            // roughly the last three days, so a challenge bound on day 12 rebuilds a ledger that
            // starts long after the real high-water mark, and the floor comes out BELOW the firm's -
            // room the trader does not have, on the exact number this add-on exists to get right.
            _peak = MakeMoneyBox();
            _peak.TextChanged += OnLeafChanged;

            StackPanel peak = new StackPanel();
            peak.Children.Add(_peak);
            peak.Children.Add(Explain(
                "Your highest END-OF-DAY balance on this account so far - it seeds the high-water mark "
              + "the drawdown floor trails from, and the floor below moves as you type. Leave it empty "
              + "if this challenge starts today. Without it the floor restarts from the start balance "
              + "and reads LOWER than the firm's. Ignored when each day is its own challenge: there is "
              + "no run for a peak close to be a claim about."));
            AddRow("Peak day close", peak);

            // ---- the only control in this add-on that can act on the account --------------------
            // Everything above decides what the cockpit MEASURES. This decides what it DOES: on a
            // broken rule it can flatten the account itself. So it is a labelled section rather than
            // one more checkbox in a row of settings, the safe option is selected here as the section
            // is built - so EVERY new binding opens on "Warn me only", whatever was chosen on the last
            // account - and the dangerous option carries its own bordered card, because a control that
            // spends real money must not look like the breach-basis radios above it.
            _enforceWarn = MakeRadio("Enforcement", "Warn me only", false);
            _enforceAct  = MakeRadio("Enforcement", "Do what the firm does - flatten and stop the strategy", true);
            _enforceWarn.IsChecked = true;
            _enforceWarn.Checked += OnLeafChanged;
            _enforceAct.Checked  += OnLeafChanged;

            StackPanel armed = new StackPanel();
            armed.Children.Add(_enforceAct);
            // In the trader's words, not the API's. Every verb here is something he will watch happen
            // to his own account, so none of them is softened and none of them is left out.
            armed.Children.Add(Explain(
                "The moment a rule breaks, this closes every open position on this account at market, "
              + "cancels the orders working on it, and stops the automated strategies running on it. It "
              + "cannot be undone - the positions are gone at whatever price the market gave. A bug in "
              + "this add-on would do exactly the same to an account that was never in trouble.", Red));

            StackPanel enforcement = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            enforcement.Children.Add(_enforceWarn);
            enforcement.Children.Add(new Border
            {
                Background      = Card,
                BorderBrush     = Red,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(8, 6, 8, 6),
                Margin          = new Thickness(0, 5, 0, 0),
                Child           = armed
            });
            AddRow("When a rule breaks", enforcement);

            PopulateFirms();
            return _form;
        }

        private UIElement BuildButtons()
        {
            _remove = MakeButton("Remove binding", Red);
            _remove.HorizontalAlignment = HorizontalAlignment.Left;
            _remove.Click += OnRemove;

            Button cancel = MakeButton("Cancel", Muted);
            cancel.Click += delegate { Close(); };

            _save = MakeButton("Save", Green);
            _save.Click += OnSave;

            StackPanel right = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            right.Children.Add(cancel);
            right.Children.Add(_save);

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            Grid.SetColumn(_remove, 0); g.Children.Add(_remove);
            Grid.SetColumn(right, 1);   g.Children.Add(right);
            return g;
        }

        private void AddRow(string label, UIElement control)
        {
            _form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock t = new TextBlock
            {
                Text              = label,
                FontFamily        = Sans,
                FontSize          = 12,
                Foreground        = Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 10, 0)
            };
            Grid.SetRow(t, _row);       Grid.SetColumn(t, 0);       _form.Children.Add(t);
            Grid.SetRow(control, _row); Grid.SetColumn(control, 1); _form.Children.Add(control);
            _row++;
        }

        // ---- population -------------------------------------------------------------------------

        private void PopulateFirms()
        {
            _loading = true;
            try
            {
                _firm.Items.Clear();
                IReadOnlyList<Firm> firms = RuleCatalog.Firms;
                for (int i = 0; i < firms.Count; i++)
                {
                    Firm f = firms[i];
                    bool modelled = RuleCatalog.IsModelled(f);
                    // Shown and disabled, never omitted: a firm silently missing from the list reads
                    // as "the cockpit does not know about it", which is a different and wrong answer.
                    _firm.Items.Add(MakeItem(f, FirmLabel(f), modelled, "not modelled yet"));
                }
            }
            finally { _loading = false; }
        }

        private void PopulatePlans(Firm firm, string prefer)
        {
            _loading = true;
            try
            {
                _plan.Items.Clear();
                List<string> plans = new List<string>();
                IReadOnlyList<PropRules> all = RuleCatalog.All;
                for (int i = 0; i < all.Count; i++)
                {
                    PropRules r = all[i];
                    if (r.Firm != firm || string.IsNullOrEmpty(r.Plan) || plans.Contains(r.Plan)) continue;
                    plans.Add(r.Plan);
                    _plan.Items.Add(MakeItem(r.Plan, r.Plan, r.Modelled, "not modelled yet"));
                }
                if (!SelectByTag(_plan, prefer))
                    SelectFirstEnabled(_plan);
            }
            finally { _loading = false; }
        }

        private void PopulateSizes(Firm firm, string plan, int prefer)
        {
            _loading = true;
            try
            {
                _size.Items.Clear();
                List<int> sizes = new List<int>();
                IReadOnlyList<PropRules> all = RuleCatalog.All;
                for (int i = 0; i < all.Count; i++)
                {
                    PropRules r = all[i];
                    // Size 0 is the not-modelled placeholder row: it identifies no account and must
                    // not appear as a choosable size.
                    if (r.Firm != firm || r.Size <= 0) continue;
                    if (!SamePlan(r.Plan, plan) || sizes.Contains(r.Size)) continue;
                    sizes.Add(r.Size);
                    _size.Items.Add(MakeItem(r.Size, Money(r.Size), r.Modelled, "not modelled yet"));
                }
                if (!SelectByTag(_size, prefer))
                    SelectFirstEnabled(_size);
            }
            finally { _loading = false; }
        }

        private void PopulatePhases(Firm firm, string plan, int size, Phase prefer)
        {
            _loading = true;
            try
            {
                _phase.Items.Clear();
                List<Phase> phases = new List<Phase>();
                IReadOnlyList<PropRules> all = RuleCatalog.All;
                for (int i = 0; i < all.Count; i++)
                {
                    PropRules r = all[i];
                    if (r.Firm != firm || r.Size != size || !SamePlan(r.Plan, plan)) continue;
                    if (phases.Contains(r.Phase)) continue;
                    phases.Add(r.Phase);
                    _phase.Items.Add(MakeItem(r.Phase, PhaseLabel(r.Phase), r.Modelled, "not modelled yet"));
                }
                if (!SelectByTag(_phase, prefer))
                    SelectFirstEnabled(_phase);
            }
            finally { _loading = false; }
        }

        // ---- events -----------------------------------------------------------------------------

        private void OnFirmChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            Firm firm;
            if (!TryFirm(out firm)) { Refresh(); return; }
            PopulatePlans(firm, PlanTag());
            PopulateSizes(firm, PlanTag(), SizeTag());
            PopulatePhases(firm, PlanTag(), SizeTag(), PhaseTag());
            Refresh();
        }

        private void OnPlanChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            Firm firm;
            if (!TryFirm(out firm)) { Refresh(); return; }
            PopulateSizes(firm, PlanTag(), SizeTag());
            PopulatePhases(firm, PlanTag(), SizeTag(), PhaseTag());
            Refresh();
        }

        private void OnSizeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            Firm firm;
            if (!TryFirm(out firm)) { Refresh(); return; }
            PopulatePhases(firm, PlanTag(), SizeTag(), PhaseTag());
            Refresh();
        }

        private void OnLeafChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            Refresh();
        }

        // Recomputes everything that depends on the four selectors: the DLL toggle, the hint line and
        // whether Save is allowed at all. One method so the toggle and the guard can never disagree
        // about which rules row is selected.
        private void Refresh()
        {
            PropRules rules = SelectedRules();
            bool ok = rules != null && rules.Modelled;

            _plan.IsEnabled     = _plan.Items.Count  > 0;
            _size.IsEnabled     = _size.Items.Count  > 0;
            _phase.IsEnabled    = _phase.Items.Count > 0;
            _start.IsEnabled    = ok;
            _override.IsEnabled = ok;
            _peak.IsEnabled     = ok;
            _basisEquity.IsEnabled  = ok;
            _basisBalance.IsEnabled = ok;
            _runContinuous.IsEnabled = ok;
            _runPerDay.IsEnabled     = ok;
            // Same gate as the rest: a combination the engine cannot measure has no rule to break, so
            // it must not be armable either.
            _enforceWarn.IsEnabled  = ok;
            _enforceAct.IsEnabled   = ok;
            _save.IsEnabled     = ok;

            // The toggle switches a rule that exists; a plan with no purchasable DLL has nothing to
            // switch on, so the box is disabled AND cleared rather than left checked on a zero.
            double amount = rules == null ? 0.0 : rules.DailyLossLimitWhenOn;
            if (amount > 0.0)
            {
                _dll.IsEnabled = true;
                // The caption states the STATE, not the option on offer. An unchecked box captioned
                // "ON - $1,200" reads at a glance as the exact opposite of what it does.
                _dll.Content = _dll.IsChecked == true
                    ? "ON - " + Money(amount) + " counted (soft: locks the day, not the account)"
                    : "OFF - not counted (would be " + Money(amount) + " if you bought it ON)";
                _dll.Foreground = Text;
            }
            else
            {
                _dll.IsEnabled  = false;
                // Under the guard: Unchecked now routes back into Refresh, and a method re-entering
                // itself is a loop waiting for its second cause.
                _loading = true;
                _dll.IsChecked = false;
                _loading = false;
                _dll.Content    = ok ? "not offered on this plan and phase" : "-";
                _dll.Foreground = Dim;
            }

            _remove.IsEnabled = _store.Find(_key) != null;

            ShowHint(DefaultHint(rules), rules != null && !rules.Modelled ? Gold : Muted);
        }

        private string DefaultHint(PropRules rules)
        {
            if (_store.LastLoadError.Length > 0)
                return "bindings.xml: " + _store.LastLoadError + " Saving now will overwrite it.";

            if (rules == null)
                return "No rules for this combination. The account stays Untracked.";

            if (!rules.Modelled)
                return FirmLabel(rules.Firm) + " is not modelled yet - phase 1 covers LucidPro only. "
                     + "Binding to it would leave the account Untracked.";

            // A COPY, carrying what is typed right now: the shared catalog row must never be written
            // to, and the trader needs to watch the floor move as he fills the two money boxes.
            PropRules preview = rules.Clone();
            double over = TypedMoney(_override);
            if (over > 0.0)
                preview.StartBalance = over;
            preview.PeakEodCloseSeed = TypedMoney(_peak);

            // Same number the rail's ROOM TO FLOOR and FLOOR STATUS cards will read once this is
            // saved: the engine starts its high-water mark at PropRules.SeededHwm, so a seeded peak
            // close moves the floor the moment it is written, not on the next session close.
            //
            // Which is why the mode comes FIRST. Per-day feeds the engine no completed days at all,
            // so there is no high-water mark to trail and none of the three trailing branches below
            // is true - the floor is the flat start-minus-max-loss line, every day, and this is the
            // only place the trader is told that before he saves.
            string floor;
            if (_runPerDay.IsChecked == true)
            {
                floor = "Each day starts fresh at " + Money(preview.StartBalance) + ". Floor "
                      + Money(preview.InitialFloor) + " - the start balance minus the max loss - flat all "
                      + "day and the same every day: with no earlier close to trail, it never ratchets, and "
                      + "neither yesterday's profit nor the peak close above moves it.";
                if (preview.ProfitTarget > 0.0)
                    floor += " A day passes at " + Money(preview.TargetBalance) + ", the plan's full target.";
            }
            else if (preview.SeededFloor >= preview.FloorLockLevel)
                floor = "Floor " + Money(preview.SeededFloor) + ", already locked - a close at or above "
                      + Money(preview.TrailStopClose) + " freezes the floor there for good, and your peak "
                      + "close is past it.";
            else if (preview.PeakEodCloseSeed > preview.StartBalance)
                floor = "Floor " + Money(preview.SeededFloor) + " as soon as you save, trailing your peak "
                      + "close; it ratchets on the session close only, and freezes for good at "
                      + Money(preview.FloorLockLevel) + ".";
            else
                floor = "Floor " + Money(preview.SeededFloor) + " on day one; it ratchets on the session "
                      + "close only, and freezes for good at " + Money(preview.FloorLockLevel) + ".";

            // The daily-limit toggle and the enforcement radios are one idea, so the hint says what the
            // toggle will DRAW and what the radios will DO when the price reaches it. Naming the dash is
            // the point: on the chart, DASHED red is the day and DOTTED red is the account.
            string daily = "";
            if (_dll.IsEnabled && _dll.IsChecked == true && rules.DailyLossLimitWhenOn > 0.0)
            {
                daily = " Daily limit ON: the chart draws a red DASHED line at the day's opening balance "
                      + "minus " + Money(rules.DailyLossLimitWhenOn) + ", in both views. The DOTTED red line is "
                      + "the account floor - a different rule.";
                if (_enforceAct.IsChecked == true)
                    daily += " Armed: touching it flattens this account and stops its strategies.";
            }

            // The unverified phases are exactly the ones a trader binds mid-challenge, so they get
            // the warning AND the floor - returning early here left the funded phase with no
            // feedback at all while he typed the peak close.
            if (!rules.Verified)
                return "Unverified rules on this phase: the cockpit shows them as warnings, not facts. " + floor + daily;

            return floor + daily;
        }

        private void ShowHint(string text, Brush colour)
        {
            _hint.Text       = text;
            _hint.Foreground = colour;
        }

        // ---- load / save ------------------------------------------------------------------------

        private void LoadFromStore()
        {
            AccountBinding b = _store.Find(_key);

            _loading = true;
            try
            {
                if (b == null)
                {
                    // A fresh binding defaults to the only modelled firm and to the strict breach
                    // basis (spec 1.4): warn earlier, never later.
                    SelectByTag(_firm, Firm.Lucid);
                    _basisEquity.IsChecked = true;
                    // Warn-only, always, for every new binding. Never carried over from another
                    // account: arming is a decision made once per account, out loud.
                    _enforceWarn.IsChecked = true;
                    _runContinuous.IsChecked = true;
                    _start.SelectedDate    = null;
                    _override.Text         = "";
                    _peak.Text             = "";
                }
                else
                {
                    SelectByTag(_firm, b.Firm);
                    _dll.IsChecked          = b.DailyLossLimitOn;
                    _basisEquity.IsChecked  = b.BreachBasis == BreachBasis.Equity;
                    _basisBalance.IsChecked = b.BreachBasis == BreachBasis.Balance;
                    // Both directions explicitly: the two radios share a group, but this dialog is a
                    // fresh window each time and an unset pair would render as neither chosen.
                    _enforceAct.IsChecked   = b.Enforcement == EnforcementMode.Armed;
                    _enforceWarn.IsChecked  = b.Enforcement != EnforcementMode.Armed;
                    // Both directions again, and biased the same way: a bindings.xml written before
                    // this option existed carries no run mode, the store falls back to Continuous,
                    // and the account keeps behaving exactly as it did yesterday.
                    _runPerDay.IsChecked     = b.RunMode == RunMode.PerDay;
                    _runContinuous.IsChecked = b.RunMode != RunMode.PerDay;
                    // MinValue is the store's "not set", which here means "count every day in the
                    // ledger" - a blank picker, not a date the trader never chose.
                    _start.SelectedDate = b.StartedUtc == DateTime.MinValue
                        ? (DateTime?)null
                        : (DateTime?)b.StartedUtc.Date;
                    _override.Text = b.StartBalanceOverride > 0.0
                        ? b.StartBalanceOverride.ToString("0.##", CultureInfo.InvariantCulture)
                        : "";
                    _peak.Text = b.PeakEodCloseSeed > 0.0
                        ? b.PeakEodCloseSeed.ToString("0.##", CultureInfo.InvariantCulture)
                        : "";
                }
            }
            finally { _loading = false; }

            // Outside the guard: the cascade has to run for real now that the firm is chosen.
            Firm firm;
            if (TryFirm(out firm))
            {
                PopulatePlans(firm, b == null ? null : b.Plan);
                PopulateSizes(firm, PlanTag(), b == null ? DefaultSize : b.Size);
                PopulatePhases(firm, PlanTag(), SizeTag(), b == null ? Phase.Evaluation : b.Phase);
            }
            Refresh();
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            AccountBinding b = BuildBinding();
            if (b == null)
                return;                     // BuildBinding has already put the reason on the hint

            AccountBinding previous = _store.Find(_key);
            _store.Put(b);
            try
            {
                _store.Save(_path);
            }
            catch (Exception ex)
            {
                // BindingStore.Save is deliberately allowed to throw - a failed save has to reach the
                // trader. Roll the in-memory store back first: leaving it holding a binding that is
                // not on disk would show a measured account that silently reverts on the next restart.
                if (previous != null) _store.Put(previous); else _store.Remove(_key);
                ShowHint("Could not save " + _path + ": " + ex.Message, Red);
                NinjaTrader.Code.Output.Process("[FundedPath] binding save failed: " + ex,
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                return;
            }

            Close();
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            AccountBinding previous = _store.Find(_key);
            if (previous == null)
                return;

            _store.Remove(_key);
            try
            {
                _store.Save(_path);
            }
            catch (Exception ex)
            {
                _store.Put(previous);
                ShowHint("Could not save " + _path + ": " + ex.Message, Red);
                NinjaTrader.Code.Output.Process("[FundedPath] binding remove failed: " + ex,
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                return;
            }

            Close();
        }

        // Never null on success. Returns null with the reason on the hint when the selection cannot
        // produce a binding the engine could measure.
        private AccountBinding BuildBinding()
        {
            if (_key.Length == 0 || _display.Length == 0)
            {
                ShowHint("No account to bind.", Red);
                return null;
            }

            PropRules rules = SelectedRules();
            if (rules == null || !rules.Modelled)
            {
                ShowHint("Pick a modelled firm, plan, size and phase before saving.", Red);
                return null;
            }

            double over = 0.0;
            string typed = _override.Text == null ? "" : _override.Text.Trim();
            if (typed.Length > 0)
            {
                // Invariant on purpose: this value is written to bindings.xml, which must read the
                // same on a machine set to a comma-decimal locale.
                if (!TryMoney(typed, out over))
                {
                    ShowHint("Start balance must be a plain positive number - not NaN, not infinity - "
                           + "or blank to use the plan's " + Money(rules.StartBalance) + ".", Red);
                    return null;
                }
            }

            double peak = 0.0;
            string typedPeak = _peak.Text == null ? "" : _peak.Text.Trim();
            if (typedPeak.Length > 0)
            {
                if (!TryMoney(typedPeak, out peak))
                {
                    ShowHint("Peak day close must be a plain positive number - not NaN, not infinity - "
                           + "or blank if this challenge starts today.", Red);
                    return null;
                }

                // A peak below the start balance is a typo, not a challenge in drawdown: the
                // high-water mark starts AT the start balance and only ever ratchets up. Accepting
                // it would silently do nothing, which is the worst of the three outcomes.
                double start = over > 0.0 ? over : rules.StartBalance;
                if (peak > 0.0 && peak < start)
                {
                    ShowHint("Peak day close " + Money(peak) + " is below the start balance " + Money(start)
                           + ". It is your highest end-of-day balance so far, which can never be lower than "
                           + "where the account started.", Red);
                    return null;
                }
            }

            AccountBinding b = new AccountBinding();
            b.AccountKey           = _key;
            b.AccountDisplayName   = _display;
            b.Firm                 = rules.Firm;
            b.Plan                 = rules.Plan;
            b.Size                 = rules.Size;
            b.Phase                = rules.Phase;
            b.DailyLossLimitOn     = _dll.IsEnabled && _dll.IsChecked == true;
            // IsEnabled-guarded like the toggle above, and for a harder reason: a binding the engine
            // cannot measure must never be written armed, and a radio the trader could not click is
            // not consent to touch his account.
            b.Enforcement          = _enforceAct.IsEnabled && _enforceAct.IsChecked == true
                                     ? EnforcementMode.Armed
                                     : EnforcementMode.WarnOnly;
            b.BreachBasis          = _basisBalance.IsChecked == true ? BreachBasis.Balance : BreachBasis.Equity;
            // IsEnabled-guarded like the two above: a combination the engine cannot measure is
            // written Continuous, which is the default for every binding, old and new.
            b.RunMode              = _runPerDay.IsEnabled && _runPerDay.IsChecked == true
                                     ? RunMode.PerDay
                                     : RunMode.Continuous;
            b.StartBalanceOverride = over;
            b.PeakEodCloseSeed     = peak;
            b.Notes                = "";

            // The picker yields a calendar DATE, not an instant. Stamped Utc so the store's "o"
            // round-trip cannot shift it a day when the machine's timezone changes; the tab compares
            // it as a date, never as a moment.
            b.StartedUtc = _start.SelectedDate.HasValue
                ? DateTime.SpecifyKind(_start.SelectedDate.Value.Date, DateTimeKind.Utc)
                : DateTime.MinValue;

            return b;
        }

        // ---- selection helpers ------------------------------------------------------------------

        // The selected catalog row, or null. Note a DISABLED ComboBoxItem can still be the selected
        // one - the user cannot click it, but LoadFromStore can select it programmatically when the
        // file names a firm phase 1 does not model. Every caller must check Modelled, not IsEnabled.
        private PropRules SelectedRules()
        {
            Firm firm;
            if (!TryFirm(out firm))
                return null;
            return RuleCatalog.Find(firm, PlanTag(), SizeTag(), PhaseTag());
        }

        private bool TryFirm(out Firm firm)
        {
            firm = Firm.Lucid;
            ComboBoxItem it = _firm.SelectedItem as ComboBoxItem;
            if (it == null || !(it.Tag is Firm))
                return false;
            firm = (Firm)it.Tag;
            return true;
        }

        private string PlanTag()
        {
            ComboBoxItem it = _plan.SelectedItem as ComboBoxItem;
            return it == null ? null : it.Tag as string;
        }

        private int SizeTag()
        {
            ComboBoxItem it = _size.SelectedItem as ComboBoxItem;
            return it != null && it.Tag is int ? (int)it.Tag : 0;
        }

        private Phase PhaseTag()
        {
            ComboBoxItem it = _phase.SelectedItem as ComboBoxItem;
            return it != null && it.Tag is Phase ? (Phase)it.Tag : Phase.Evaluation;
        }

        // The ONE money parse in this dialog, used by both the live preview and the strict save.
        // Invariant culture on purpose: these values are written to bindings.xml, which must read the
        // same on a machine set to a comma-decimal locale.
        //
        // NaN and Infinity are rejected HERE rather than at each call site, because NumberStyles.Float
        // parses both literals and neither is caught by a `< 0` guard: "NaN" typed into either money
        // box reached PropRules.SeededFloor and printed "Floor $NaN", and once the engine consumes the
        // seed it would reach the floor the trader trades against.
        private static bool TryMoney(string text, out double value)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
            {
                value = 0.0;
                return false;
            }
            return true;
        }

        // The lenient read, for the hint that refreshes on every keystroke: blank, half-typed,
        // negative and non-finite all mean "nothing yet", never an error. BuildBinding calls the same
        // parse and turns a false into a message - the two are different jobs on one set of rules.
        private static double TypedMoney(TextBox box)
        {
            string t = box == null || box.Text == null ? "" : box.Text.Trim();
            double v;
            return t.Length > 0 && TryMoney(t, out v) ? v : 0.0;
        }

        private static bool SelectByTag(ComboBox c, object tag)
        {
            if (tag == null)
                return false;
            for (int i = 0; i < c.Items.Count; i++)
            {
                ComboBoxItem it = c.Items[i] as ComboBoxItem;
                if (it != null && tag.Equals(it.Tag)) { c.SelectedIndex = i; return true; }
            }
            return false;
        }

        private static void SelectFirstEnabled(ComboBox c)
        {
            for (int i = 0; i < c.Items.Count; i++)
            {
                ComboBoxItem it = c.Items[i] as ComboBoxItem;
                if (it != null && it.IsEnabled) { c.SelectedIndex = i; return; }
            }
            // All disabled (a not-modelled firm): select the first anyway so the trader can SEE what
            // he picked greyed out, instead of an empty box that looks like a bug.
            c.SelectedIndex = c.Items.Count > 0 ? 0 : -1;
        }

        // ---- control factories ------------------------------------------------------------------

        private static Brush Frozen(byte r, byte g, byte b)
        {
            SolidColorBrush s = new SolidColorBrush(Color.FromRgb(r, g, b));
            s.Freeze();
            return s;
        }

        // Money entry: Consolas, because these sit under the Consolas figures in the header and a
        // proportional font would make two dollar amounts of the same width look different.
        private static TextBox MakeMoneyBox()
        {
            return new TextBox
            {
                FontFamily               = Mono,
                FontSize                 = 12,
                Height                   = 26,
                Background               = Card,
                Foreground               = Text,
                BorderBrush              = Line,
                BorderThickness          = new Thickness(1),
                CaretBrush               = Text,
                Padding                  = new Thickness(6, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin                   = new Thickness(0, 3, 0, 3)
            };
        }

        // The small grey line under a control. Three of them now, and they must not drift apart.
        // colour is null for the two explanatory ones; the enforcement section passes Red, because
        // that paragraph is a warning and grey is what the eye skips.
        private static TextBlock Explain(string text, Brush colour = null)
        {
            return new TextBlock
            {
                Text         = text,
                FontFamily   = Sans,
                FontSize     = 10,
                Foreground   = colour ?? Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 4)
            };
        }

        private static ComboBox MakeCombo()
        {
            return new ComboBox
            {
                Height                   = 26,
                Margin                   = new Thickness(0, 3, 0, 3),
                FontFamily               = Sans,
                FontSize                 = 12,
                Background               = Card,
                Foreground               = Text,
                BorderBrush              = Line,
                BorderThickness          = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding                  = new Thickness(6, 0, 6, 0)
            };
        }

        // Tag carries the value; Content carries the copy. Never parse the label back into a value.
        private static ComboBoxItem MakeItem(object tag, string label, bool enabled, string disabledHint)
        {
            return new ComboBoxItem
            {
                Tag        = tag,
                Content    = enabled ? label : label + "  -  " + disabledHint,
                IsEnabled  = enabled,
                FontFamily = Sans,
                FontSize   = 12,
                Background = Card,
                Foreground = enabled ? Text : Dim
            };
        }

        // One factory for both radio groups. The enforcement group needs its dangerous option to
        // carry more weight than its safe one - semibold and red against plain text - and that is a
        // flag rather than a second factory, so the two groups cannot drift apart on font or spacing.
        private static RadioButton MakeRadio(string group, string label, bool danger)
        {
            return new RadioButton
            {
                Content    = label,
                GroupName  = group,
                FontFamily = Sans,
                FontSize   = 12,
                FontWeight = danger ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = danger ? Red : Text,
                Margin     = new Thickness(0, 2, 0, 2)
            };
        }

        private static Button MakeButton(string label, Brush accent)
        {
            return new Button
            {
                Content         = label,
                MinWidth        = 92,
                Height          = 28,
                Margin          = new Thickness(8, 0, 0, 0),
                FontFamily      = Sans,
                FontSize        = 12,
                Foreground      = accent,
                Background      = Soft,
                BorderBrush     = Line,
                BorderThickness = new Thickness(1)
            };
        }

        // ---- copy -------------------------------------------------------------------------------

        private static string FirmLabel(Firm f)
        {
            switch (f)
            {
                case Firm.Lucid:           return "Lucid Trading";
                case Firm.MyFundedFutures: return "MyFundedFutures";
                case Firm.ApexTrader:      return "Apex Trader Funding";
                case Firm.TopstepTrader:   return "Topstep";
            }
            return f.ToString();
        }

        private static string PhaseLabel(Phase p)
        {
            switch (p)
            {
                case Phase.Evaluation: return "Evaluation";
                case Phase.LiveSim:    return "Live Sim (funded)";
                case Phase.Live:       return "Live";
            }
            return p.ToString();
        }

        // Whole dollars, invariant culture - the strings sit next to Consolas figures on a dark
        // surface and a locale-formatted "1.200" would read as a decimal.
        private static string Money(double v)
        {
            return "$" + v.ToString("#,##0", CultureInfo.InvariantCulture);
        }

        private static bool SamePlan(string a, string b)
        {
            if (string.IsNullOrEmpty(b))
                return true;                // a null filter matches any plan, same as RuleCatalog.Find
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
