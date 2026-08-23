using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using FundedPath.Engine;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;

namespace FundedPath.NT
{
    // The window's whole body: selector bar, chart host, stat rail, and every NinjaTrader subscription.
    //
    // READ-ONLY ON THE ACCOUNT, by design (spec section 5). There is no Order, no OrderAction and no
    // Submit/Change/Cancel anywhere in this file or in this project. The cockpit measures; it never trades.
    //
    // Threading contract, and it is the whole architecture:
    //   - Account events (AccountItemUpdate / ExecutionUpdate / the static AccountStatusUpdate) arrive on
    //     a background connection thread. They write an immutable snapshot and return. They NEVER touch
    //     a WPF object.
    //   - A DispatcherTimer at 4 Hz on this window's UI thread reads that snapshot, decides whether
    //     anything actually changed, recomputes only then, and repaints.
    // Every NT8 window runs its own dispatcher thread, so a WPF mutation from a connection thread is not
    // "usually fine" - it is a deadlock or a cross-thread throw that kills this window's UI for good.

    // The cockpit's own state machine, which is NOT the engine's Phase: it adds Replay (a Playback
    // rehearsal of a bound challenge) and Untracked (the default for every account). Spec section 2.
    public enum CockpitPhase { Replay, Evaluation, LiveSim, Live, Untracked }

    // One closed trade of the day in progress, flattened to plain data for the Session view. Deliberately
    // free of NinjaTrader types so CurveChart never has to reach back into Cbi to draw a tooltip.
    public sealed class SessionFill
    {
        public DateTime TimeEt        { get; set; }   // exit time, Eastern, Unspecified kind
        public string   Instrument    { get; set; }
        public string   Side          { get; set; }   // "LONG" / "SHORT"
        public int      Quantity      { get; set; }
        public double   ProfitCurrency { get; set; }
        public double   Balance       { get; set; }   // running balance AFTER this trade closed
    }

    // Everything the chart needs, in one immutable-by-convention bundle. The tab builds it on the paint
    // tick; CurveChart renders it and asks no questions. Rebuilt, never mutated in place: the chart can
    // hold a reference across frames without ever seeing a half-updated series.
    public sealed class CockpitFrame
    {
        public ChallengeState State        { get; set; }   // never null
        public PropRules      Rules        { get; set; }   // null when the account is unbound
        public bool           Tracked      { get; set; }   // false => draw the Untracked state
        public bool           SessionView  { get; set; }   // false = Challenge (x = days), true = Session (x = fills)

        // Challenge view series: the completed days, oldest first, with ClosingBalance and FloorInForce
        // filled in by the engine. The day in progress is NOT a row here - it is the live endpoint below.
        public IReadOnlyList<TradingDay> Days { get; set; }

        // Session view series: today's closed trades, oldest first, with a running balance.
        public IReadOnlyList<SessionFill> Session { get; set; }

        public CockpitPhase   Phase        { get; set; }
        public Color          PhaseColor   { get; set; }
        public DateTime       TradingDate  { get; set; }   // the ET trading day in progress
        public double         LiveBalance  { get; set; }   // engine Balance: start + every realized dollar
        public double         LiveEquity   { get; set; }   // LiveBalance + open P&L
        // The binding's breach basis, carried on the frame rather than re-read from _binding while
        // rendering: the rail's "equity basis" caption, the chart's live endpoint and the engine's
        // RoomToFloor must all describe the SAME computation, and _binding can change between them.
        public bool           EquityBasis  { get; set; }

        // The binding revision this frame was computed from. Enforcement is the one decision here that
        // spends money, and it runs off a frame that was built earlier in the tick - so if Recompute
        // threw after the triggers advanced, the next tick could pair the OLD binding's frame with the
        // NEW _binding and act on a rule the trader had just changed. The frame carries the revision so
        // that pairing can be rejected instead of assumed, the same way EquityBasis is carried above.
        public int            BindingRev   { get; set; }
        public double         DayOpenBalance { get; set; } // balance at the start of the day in progress
        public int            FillsToday   { get; set; }
        public string[]       Warnings     { get; set; }   // engine warnings + this layer's own

        // The balance at which today's loss reaches the fixed daily loss limit, drawn on the chart as
        // the level the trader is actually trading against today. 0 = the rule is off, or the scaling
        // limit has taken over from it and nothing here is watching the day.
        public double         DailyLimitLevel { get; set; }

        // True when something upstream of the numbers is wrong: an unreadable store, a fallback time
        // zone, a non-USD account, or a non-finite value in the ledger. The panel still draws - it
        // just refuses to ACT on a frame carrying this, because a tool that closes positions on bad
        // data is worse than one that does nothing. DegradedReason says which, in the banner.
        public bool           Degraded       { get; set; }
        public string         DegradedReason { get; set; }
    }

    // One latched rule break, and what this add-on did about it. Persisted in the day ledger so it
    // survives a restart: that is what stops the panel treating an already-latched breach as a fresh
    // one and enforcing a second time on an account the trader has already been thrown out of.
    public sealed class EnforcementRecord
    {
        public DateTime AtUtc       { get; set; }   // when the latch tripped
        public DateTime TradingDate { get; set; }   // the ET trading day it tripped on
        public string   Kind        { get; set; }   // FundedPathTab.KindBreach / KindDayLock
        public double   Value       { get; set; }   // breach: room to floor (negative). lockout: day P&L
        public string   Headline    { get; set; }   // the engine's verdict line at the moment it tripped
        public string   Action      { get; set; }   // FundedPathTab.Act*
        public string   Detail      { get; set; }   // the enforcer's transcript, or why it held back
    }

    public class FundedPathTab : NTTabPage
    {
        // ---- palette (spec section 6, locked by the approved mockup) --------------------------------
        // Hardcoded, not pulled from NT8's skin dictionary. The mockup locks these, half of them are
        // semantic (gold = balance, red = floor) and TryFindResource returns null on a renamed key, which
        // paints nothing and looks exactly like a dead dispatcher. The only themed thing in this add-on is
        // the Control Center menu item's MainMenuItem style, over in FundedPathAddOn.
        static readonly Brush Ground   = Frozen(0x06, 0x0A, 0x16);
        static readonly Brush Panel    = Frozen(0x0C, 0x13, 0x22);
        static readonly Brush Card     = Frozen(0x0A, 0x10, 0x1D);
        static readonly Brush Line     = Frozen(0x1B, 0x25, 0x40);
        static readonly Brush LineSoft = Frozen(0x15, 0x1E, 0x33);
        static readonly Brush Gold     = Frozen(0xF2, 0xB3, 0x3D);
        static readonly Brush Green    = Frozen(0x27, 0xD6, 0x7B);
        static readonly Brush Red      = Frozen(0xFF, 0x54, 0x68);
        static readonly Brush Blue     = Frozen(0x4A, 0x7D, 0xFF);
        static readonly Brush TextCol  = Frozen(0xE9, 0xED, 0xF8);
        static readonly Brush Muted    = Frozen(0x7A, 0x87, 0xA2);
        static readonly Brush Dim      = Frozen(0x4E, 0x5A, 0x74);

        static readonly Color ReplayC  = Color.FromRgb(0xF2, 0xB3, 0x3D);
        static readonly Color EvalC    = Color.FromRgb(0x4A, 0x7D, 0xFF);
        static readonly Color LiveSimC = Color.FromRgb(0x27, 0xD6, 0x7B);
        static readonly Color LiveC    = Color.FromRgb(0x9B, 0x7B, 0xFF);
        static readonly Color UntrackC = Color.FromRgb(0x4E, 0x5A, 0x74);

        // The latched-state colours, as Colors rather than Brushes: the banner tints its own
        // background from them at low alpha, which needs the channels.
        static readonly Color BreachC = Color.FromRgb(0xFF, 0x54, 0x68);   // == Red
        static readonly Color LockC   = Color.FromRgb(0xF2, 0xB3, 0x3D);   // == Gold

        static readonly FontFamily Sans = new FontFamily("Segoe UI");
        static readonly FontFamily Mono = new FontFamily("Consolas");

        // Written as an escape, never typed: this tree is edited on Windows and read by the NinjaScript
        // compiler, and a UTF-8 middle dot read back as Windows-1252 renders as mojibake.
        const string Dot = " \u00B7 ";

        // ---- cross-thread handoff -------------------------------------------------------------------

        // Immutable. The connection thread swaps a NEW instance in; the UI thread takes one volatile read
        // and then owns its copy. Nothing is ever mutated in place, so there is no half-written snapshot
        // and the UI thread needs no lock to read it.
        sealed class AcctSnapshot
        {
            public readonly double Cash;         // AccountItem.CashValue, last value the adapter pushed
            public readonly double Unrealized;   // AccountItem.UnrealizedProfitLoss
            public readonly long   ExecSeq;      // bumped by every ExecutionUpdate, incl. amendments/removals

            public AcctSnapshot(double cash, double unrealized, long execSeq)
            {
                Cash = cash; Unrealized = unrealized; ExecSeq = execSeq;
            }
        }

        // Serializes the read-modify-write of _snap between the two account-thread handlers. They fire on
        // the same connection thread today, but that is not documented anywhere and a lost cash update
        // would persist until the adapter happened to push that item again.
        readonly object _snapLock = new object();
        volatile AcctSnapshot _snap = new AcctSnapshot(0, 0, 0);

        // ---- NinjaTrader state ----------------------------------------------------------------------
        Account _account;                       // written under _snapLock, read from the connection thread
        bool    _subscribed;
        bool    _dead;                          // set by Cleanup: this tab is torn down and must re-attach nothing
        SelectionChangedEventHandler _accountSelectionHandler;   // stored so PopulateAccounts can detach it
        string  _pendingRestoreAccount;         // a workspace-restored account that has not connected yet

        // ---- bindings + ledger ----------------------------------------------------------------------
        BindingStore   _store;
        AccountBinding _binding;
        // The resolved rulebook for _binding, or null when the account is unbound OR its firm/size is not
        // modelled. THIS is the single definition of "tracked" in this layer: every read of the account
        // beyond its identity is gated on it. Resolved once when the binding loads rather than per tick.
        PropRules      _rules;
        string         _bindingsPath;
        string         _ledgerPath;             // per-account day ledger, sibling of bindings.xml
        Dictionary<DateTime, TradingDay> _ledger;   // completed days keyed by ET trading date
        string         _storeError;

        // ---- UI -------------------------------------------------------------------------------------
        readonly Border    _stripe   = new Border { Height = 3 };
        readonly ComboBox  _accounts = new ComboBox { Width = 190 };   // the placeholder Grid below carries the margin
        readonly Button    _configure = new Button();
        readonly System.Windows.Shapes.Ellipse _phaseDot = new System.Windows.Shapes.Ellipse();
        readonly TextBlock _phaseText = new TextBlock();
        readonly TextBlock _phaseNote = new TextBlock();
        readonly TextBlock _title    = new TextBlock();
        readonly Button    _challengeBtn = new Button();
        readonly Button    _sessionBtn   = new Button();
        readonly Border    _verdictPill  = new Border();
        readonly TextBlock _verdictText  = new TextBlock();
        readonly TextBlock _warnText     = new TextBlock();
        // The engine's "what breaks first", under the view title. It was computed on every tick and
        // rendered nowhere - the README's headline promise, missing from the screen entirely.
        readonly TextBlock _constraint   = new TextBlock();
        // Painted OVER the account combo while nothing is selected: a WPF ComboBox has no placeholder,
        // and night one it renders as empty chrome next to a rail telling the trader to press Configure.
        readonly TextBlock _accountHint  = new TextBlock();
        string _warnFull = "";                  // last full warning text; the tooltip is rebuilt only on a change
        readonly StackPanel _rail        = new StackPanel();
        readonly Chip[]     _chips       = new Chip[5];
        readonly StatCard[] _cards       = new StatCard[5];
        readonly StatCard   _untrackedCard = MakeCard();
        readonly CurveChart _chart       = new CurveChart();

        // The latched-state banner. It spans the whole body, above the rail as well as the chart,
        // because it is the one thing on this screen that must not be missable.
        readonly Border    _banner      = new Border();
        readonly TextBlock _bannerText  = new TextBlock();
        readonly Button    _bannerReset = new Button();
        string _bannerFull = "";                // last banner tooltip text; rebuilt only on a change

        // ---- enforcement ----------------------------------------------------------------------------
        // Loaded from the day ledger when a binding attaches, so a latch survives a restart. That is
        // what stops a restart being read as a fresh rule break and enforcing all over again.
        // The engine's latch, which is CALLER-OWNED: Evaluate never mutates the instance it is handed,
        // it returns an updated copy on ChallengeState.Latched. Dropping it on the floor puts the
        // flash-and-vanish breach straight back, so it is persisted in the day ledger and handed back
        // on every call.
        LatchedState _latched = new LatchedState();
        List<EnforcementRecord> _enforcements = new List<EnforcementRecord>();
        EnforcementRecord _enforcingRec;        // the record the confirm loop is chasing, if any
        int      _enforceBusy;                  // Interlocked 0/1: exactly one enforcement run at a time
        DateTime _enforceRetryDeadline = DateTime.MinValue;   // platform clock, not the wall clock
        DateTime _enforceLastAttempt   = DateTime.MinValue;   // platform clock, not the wall clock

        // Replay-clock seconds between confirmation attempts. 2 is house-measured: long enough that a
        // normal fill lands before a second, over-closing flatten could go out, short enough to re-fire
        // quickly if the first attempt threw or was momentarily blocked.
        const int EnforceRetrySeconds   = 2;
        // How long to keep trying before telling the trader to finish it by hand. Long enough for a slow
        // broker, short enough that "being handled" never sits on the screen all evening.
        const int EnforceConfirmSeconds = 60;

        // The two things that latch, and the four things this add-on can have done about one.
        public const string KindBreach  = "Breached";
        public const string KindDayLock = "DailyLockout";
        public const string ActWarn     = "warn-only";
        public const string ActHeld     = "held-back";
        public const string ActRunning  = "enforcing";
        public const string ActDone     = "enforced";
        // Read back in ActRunning state, i.e. sent to the platform by a run that never reported: the
        // process was closed, or the account switched, while the flatten was in flight. See
        // RepairInterruptedEnforcements. It counts as HAVING REACHED the account, so it is never
        // superseded by a later edge - re-flattening on the strength of "we are not sure" is exactly
        // the wrong way to resolve the doubt.
        public const string ActUnknown  = "interrupted";

        DispatcherTimer _paintTimer;
        DateTime _lastPaintErr;                 // UI thread only - throttles the paint-tick catch log
        bool     _sessionView;

        // ---- recompute triggers ---------------------------------------------------------------------
        // The engine runs only when one of these actually moved. At 4 Hz a blind recompute would rebuild
        // SystemPerformance over every execution of the session four times a second for nothing.
        long     _lastSeq  = -1;
        double   _lastCash = double.NaN;
        double   _lastUnrealized = double.NaN;
        int      _bindingRev, _lastBindingRev = -1;
        DateTime _lastTradingDate = DateTime.MinValue;
        DateTime _lastClock = DateTime.MinValue;
        // volatile: OnSimulationAccountReset raises it on the connection thread and the paint tick reads
        // it on this window's own dispatcher thread.
        volatile bool _forceRecompute = true;
        bool     _reconciled;                   // one-shot commission reconcile, see ReconcileOnce
        volatile CockpitFrame _frame;           // the current answer; volatile because GetFrame() is public

        // ---- timezone -------------------------------------------------------------------------------
        TimeZoneInfo _etZone;                   // cached: a FindSystemTimeZoneById per fill would be absurd
        bool         _etResolved;
        string       _etWarning = "";

        public FundedPathTab()
        {
            // Bindings first: the account combo's selection handler reads _store the moment it fires.
            _bindingsPath = BindingStore.DefaultPath;
            _store = BindingStore.Load(_bindingsPath);
            _storeError = _store.LastLoadError;

            BuildUi();

            // Populate BEFORE attaching the selection handler's owner events, so the first selection runs
            // through the same OnAccountSelected path as every later one.
            _accountSelectionHandler = delegate { OnAccountSelected(); };
            _accounts.SelectionChanged += _accountSelectionHandler;
            Account.AccountStatusUpdate += OnAccountStatusUpdate;
            // The documented signal for a Playback rewind AND fast-forward, and for a manual sim-account
            // reset. All three wipe Account.Executions, so the whole series has to be rebuilt.
            Account.SimulationAccountReset += OnSimulationAccountReset;
            PopulateAccounts();

            // UI-thread paint clock, independent of data arrival. 4 Hz per spec section 5: this window has
            // no hot path, and a slower clock is a slower first breach warning.
            _paintTimer = new DispatcherTimer(DispatcherPriority.Render);
            _paintTimer.Interval = TimeSpan.FromMilliseconds(250);
            _paintTimer.Tick += OnPaintTick;
            _paintTimer.Start();
        }

        // ---- public surface for the chart -----------------------------------------------------------

        // CurveChart is a dumb renderer: it never reaches into an Account, never recomputes a rule, and
        // never keeps its own copy of the series. It draws whatever this returns. Null until the first
        // paint tick has run.
        public CockpitFrame GetFrame()
        {
            return _frame;
        }

        // ---- layout ---------------------------------------------------------------------------------

        void BuildUi()
        {
            DockPanel root = new DockPanel();
            root.Background = Ground;

            // 1. the 3px accent stripe carrying the phase colour.
            _stripe.Background = new SolidColorBrush(UntrackC);
            DockPanel.SetDock(_stripe, Dock.Top);
            root.Children.Add(_stripe);

            // 2. the selector bar.
            DockPanel bar = new DockPanel();
            bar.Background = Panel;
            bar.Margin = new Thickness(0);
            bar.LastChildFill = true;

            // Right block first: in a DockPanel the docked children take their slice in the order they are
            // added, and the last child fills what is left. Adding this after the chip panel would give the
            // chips the whole width and squeeze the phase chip to nothing.
            StackPanel right = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(10, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            StackPanel phaseRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _phaseDot.Width = 8; _phaseDot.Height = 8;
            _phaseDot.Margin = new Thickness(0, 0, 6, 0);
            _phaseDot.VerticalAlignment = VerticalAlignment.Center;
            _phaseDot.Fill = new SolidColorBrush(UntrackC);
            phaseRow.Children.Add(_phaseDot);
            _phaseText.FontFamily = Sans; _phaseText.FontSize = 12; _phaseText.FontWeight = FontWeights.Bold;
            _phaseText.Foreground = Dim; _phaseText.VerticalAlignment = VerticalAlignment.Center;
            _phaseText.Text = "UNTRACKED";
            phaseRow.Children.Add(_phaseText);
            right.Children.Add(phaseRow);
            _phaseNote.FontFamily = Sans; _phaseNote.FontSize = 10; _phaseNote.Foreground = Muted;
            _phaseNote.HorizontalAlignment = HorizontalAlignment.Right;
            _phaseNote.Margin = new Thickness(0, 2, 0, 0);
            _phaseNote.Text = NoteFor(CockpitPhase.Untracked);
            right.Children.Add(_phaseNote);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);

            // Left block: account combo, the read-only binding chips, Configure. A WrapPanel rather than a
            // StackPanel so a narrow window wraps the chips onto a second line instead of clipping the
            // Configure button off the edge (the fixed-height clip the radar hit, RadarTab.cs:257).
            WrapPanel left = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 6, 6) };
            _accounts.DisplayMemberPath = "DisplayName";   // the ledger key is keyed on DisplayName (spec 2), so show that, not Name
            _accounts.Background = Card;
            _accounts.Foreground = TextCol;
            _accounts.BorderBrush = Line;
            _accounts.VerticalAlignment = VerticalAlignment.Center;

            // The combo and its placeholder share one cell. IsHitTestVisible false so the click still
            // opens the drop-down: the hint is a label, never a control.
            System.Windows.Controls.Grid acctBox = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            acctBox.Children.Add(_accounts);
            _accountHint.Text = "Select an account";
            _accountHint.FontFamily = Sans; _accountHint.FontSize = 11; _accountHint.Foreground = Dim;
            _accountHint.Margin = new Thickness(9, 0, 26, 0);
            _accountHint.VerticalAlignment = VerticalAlignment.Center;
            _accountHint.IsHitTestVisible = false;
            acctBox.Children.Add(_accountHint);
            left.Children.Add(acctBox);

            string[] chipLabels = { "FIRM", "PLAN", "CHALLENGE", "DAILY LOSS LIMIT", "DRAWDOWN" };
            for (int i = 0; i < _chips.Length; i++)
            {
                _chips[i] = MakeChip(chipLabels[i]);
                left.Children.Add(_chips[i].Root);
            }

            StyleFlatButton(_configure, "Configure", Blue);
            _configure.Margin = new Thickness(4, 0, 0, 0);
            _configure.VerticalAlignment = VerticalAlignment.Center;
            _configure.Click += OnConfigureClick;
            left.Children.Add(_configure);

            bar.Children.Add(left);
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);

            Border barLine = new Border { Height = 1, Background = Line };
            DockPanel.SetDock(barLine, Dock.Top);
            root.Children.Add(barLine);

            // 3. the body: rail on the right, chart on the left.
            DockPanel body = new DockPanel();

            // The latched-breach banner, added to the body FIRST. A DockPanel gives its children their
            // slice in the order they were added, so adding it after the rail would leave it starting at
            // the chart's left edge instead of spanning the body.
            _bannerText.FontFamily = Sans; _bannerText.FontSize = 12; _bannerText.FontWeight = FontWeights.Bold;
            _bannerText.TextWrapping = TextWrapping.Wrap;
            _bannerText.VerticalAlignment = VerticalAlignment.Center;
            StyleFlatButton(_bannerReset, "RESET BREACH", TextCol);
            _bannerReset.Margin = new Thickness(12, 0, 0, 0);
            _bannerReset.VerticalAlignment = VerticalAlignment.Center;
            _bannerReset.Click += OnResetBreachClick;
            DockPanel bannerRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_bannerReset, Dock.Right);
            bannerRow.Children.Add(_bannerReset);      // docked child first, wrapping text fills the rest
            bannerRow.Children.Add(_bannerText);
            _banner.Child = bannerRow;
            _banner.BorderThickness = new Thickness(0, 0, 0, 1);
            _banner.Padding = new Thickness(14, 9, 14, 9);
            _banner.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(_banner, Dock.Top);
            body.Children.Add(_banner);

            // The rail lives in a ScrollViewer: the five cards are Auto-height and grow with their strings,
            // so a short window must scroll them, not clip the bottom one.
            ScrollViewer railScroll = new ScrollViewer
            {
                Width = 272,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Ground,
                BorderThickness = new Thickness(1, 0, 0, 0),
                BorderBrush = Line
            };
            _rail.Margin = new Thickness(10, 10, 10, 10);
            for (int i = 0; i < _cards.Length; i++)
            {
                _cards[i] = MakeCard();
                _rail.Children.Add(_cards[i].Root);
            }
            _rail.Children.Add(_untrackedCard.Root);
            _warnText.FontFamily = Sans; _warnText.FontSize = 10; _warnText.Foreground = Muted;
            _warnText.TextWrapping = TextWrapping.Wrap;
            _warnText.Margin = new Thickness(2, 8, 2, 0);
            _warnText.Visibility = Visibility.Collapsed;
            _rail.Children.Add(_warnText);
            railScroll.Content = _rail;
            DockPanel.SetDock(railScroll, Dock.Right);
            body.Children.Add(railScroll);

            // 4. the chart column: title + view toggle + verdict pill above the curve, and the
            //    binding constraint on its own line under them.
            DockPanel chartCol = new DockPanel();
            StackPanel headerCol = new StackPanel { Margin = new Thickness(14, 12, 14, 8) };
            DockPanel header = new DockPanel();

            _verdictText.FontFamily = Sans; _verdictText.FontSize = 12; _verdictText.FontWeight = FontWeights.Bold;
            _verdictText.Foreground = Gold;
            _verdictPill.Child = _verdictText;
            _verdictPill.CornerRadius = new CornerRadius(14);
            _verdictPill.Padding = new Thickness(14, 5, 14, 5);
            _verdictPill.BorderThickness = new Thickness(1);
            _verdictPill.BorderBrush = Line;
            _verdictPill.Background = Card;
            _verdictPill.VerticalAlignment = VerticalAlignment.Center;
            _verdictPill.HorizontalAlignment = HorizontalAlignment.Right;
            DockPanel.SetDock(_verdictPill, Dock.Right);
            header.Children.Add(_verdictPill);

            StackPanel titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            Border tick = new Border { Width = 3, Height = 18, Background = Blue, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(tick);
            _title.FontFamily = Sans; _title.FontSize = 15; _title.FontWeight = FontWeights.Bold;
            _title.Foreground = TextCol; _title.VerticalAlignment = VerticalAlignment.Center;
            _title.Text = "Challenge Curve";
            titleRow.Children.Add(_title);

            Border seg = new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = Card,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(14, 0, 0, 0),
                Padding = new Thickness(2),
                VerticalAlignment = VerticalAlignment.Center
            };
            StackPanel segInner = new StackPanel { Orientation = Orientation.Horizontal };
            StyleFlatButton(_challengeBtn, "CHALLENGE", Muted);
            StyleFlatButton(_sessionBtn, "SESSION", Muted);
            _challengeBtn.Click += delegate { SetView(false); };
            _sessionBtn.Click += delegate { SetView(true); };
            segInner.Children.Add(_challengeBtn);
            segInner.Children.Add(_sessionBtn);
            seg.Child = segInner;
            titleRow.Children.Add(seg);
            header.Children.Add(titleRow);

            headerCol.Children.Add(header);

            // Quieter than the verdict pill on purpose: the pill is the answer, this line is the reason
            // for it. Wraps rather than clips - the funded phase's line carries the consistency readout.
            _constraint.FontFamily = Sans; _constraint.FontSize = 11; _constraint.Foreground = Muted;
            _constraint.TextWrapping = TextWrapping.Wrap;
            _constraint.Margin = new Thickness(11, 7, 0, 0);
            headerCol.Children.Add(_constraint);

            DockPanel.SetDock(headerCol, Dock.Top);
            chartCol.Children.Add(headerCol);
            chartCol.Children.Add(_chart);          // LastChildFill: the curve takes everything left
            body.Children.Add(chartCol);

            root.Children.Add(body);
            Content = root;

            SetView(false);
            ApplyUntracked();                        // the default before any account has been selected
            RefreshAccountAffordances();             // ...and Configure is dead until there is an account
        }

        // ---- account plumbing -----------------------------------------------------------------------

        // Fires on every AccountStatusUpdate, connection blips included. Marshaled: this arrives on a
        // connection thread and every line of PopulateAccounts touches WPF.
        void OnAccountStatusUpdate(object sender, AccountStatusEventArgs e)
        {
            Dispatcher.InvokeAsync((Action)PopulateAccounts);
        }

        // Reassigning ItemsSource transiently resets SelectedItem to null, and the resulting
        // SelectionChanged would run a full account teardown on a mere connection blip - even though the
        // very next line restores the same account. Detach for the reassignment, then re-assert once.
        void PopulateAccounts()
        {
            // A PopulateAccounts queued by OnAccountStatusUpdate runs AFTER Cleanup() if the tab was closed
            // during a connection blip - the dispatcher does not cancel it. It would reach OnAccountSelected
            // with acct != null and _account == null, and Subscribe() would re-attach this dead tab's
            // handlers to the platform's long-lived Account, where nothing ever removes them again.
            if (_dead) return;

            List<Account> accts;
            lock (Account.All)                       // a plain Collection<T>, mutated by the connection thread
                accts = Account.All.ToList();

            Account keep = _accounts.SelectedItem as Account;

            // The workspace's account outranks BOTH of the others, and it has to. The constructor runs
            // PopulateAccounts before Restore, so as soon as the trader owns a real binding AND a
            // Playback one, FirstBound has already selected whichever the platform listed first: keep is
            // non-null by the time the restored account's connection comes up, and the workspace's own
            // choice loses silently, every session. It only wins when it is actually PRESENT - a name
            // that has not connected yet must not clear the selection.
            Account restored = _pendingRestoreAccount == null
                ? null
                : accts.FirstOrDefault(a => SameName(a, _pendingRestoreAccount));

            _accounts.SelectionChanged -= _accountSelectionHandler;
            try
            {
                _accounts.ItemsSource = accts;
                if (restored != null)
                    _accounts.SelectedItem = restored;
                else if (keep != null && accts.Contains(keep))
                    _accounts.SelectedItem = keep;
                else
                    _accounts.SelectedItem = FirstBound(accts);
            }
            finally
            {
                _accounts.SelectionChanged += _accountSelectionHandler;
            }

            // A workspace-restored account only exists once its connection is up, which is usually AFTER
            // this tab was built. Clear the pending name only once it has actually been honoured.
            if (_pendingRestoreAccount != null && SameName(_accounts.SelectedItem as Account, _pendingRestoreAccount))
                _pendingRestoreAccount = null;

            OnAccountSelected();
        }

        static bool SameName(Account a, string displayName)
        {
            return a != null && string.Equals(a.DisplayName, displayName, StringComparison.OrdinalIgnoreCase);
        }

        // Only an account that ALREADY carries a binding is selected on its own. Taking the first account
        // in the list points the cockpit at whatever the platform happens to list first - the trader's own
        // live account as often as not - and Untracked promises that account is not touched at all. The
        // combo still lists every account, so one can be picked by hand and bound with Configure; the only
        // thing read to answer this question is the account's identity (provider + display name), which is
        // what the combo itself displays.
        Account FirstBound(List<Account> accts)
        {
            for (int i = 0; i < accts.Count; i++)
            {
                Account a = accts[i];
                if (a != null && _store.Find(BindingStore.KeyFor(ProviderName(a), a.DisplayName)) != null)
                    return a;
            }
            return null;
        }

        void OnAccountSelected()
        {
            Account acct = _accounts.SelectedItem as Account;
            if (ReferenceEquals(acct, _account)) return;

            Unsubscribe();                            // detach from the OLD account before losing the reference

            // Both under the lock, and the handlers re-check the identity INSIDE that same lock. So a
            // handler for the outgoing account either lands before this (and its store is wiped by the
            // reset) or after (and is dropped by the re-check). Resetting outside the lock let the old
            // account's Cash/Unrealized be restored UNDER THE NEW ACCOUNT, and the next paint rendered the
            // previous account's open P&L.
            lock (_snapLock)
            {
                _account = acct;
                _snap = new AcctSnapshot(0, 0, 0);    // the previous account's cash/unrealized mean nothing here
            }
            _reconciled = false;
            _lastClock = DateTime.MinValue;           // a Playback account and a live one keep different clocks: a switch is not a rewind
            RefreshAccountAffordances();

            LoadBindingFor(acct);
            Subscribe();

            // Clearing the data is not clearing the screen: without this the previous challenge's curve
            // keeps painting until the first recompute of the new one lands.
            _chart.Clear();
            _frame = null;
            _forceRecompute = true;
            RefreshHeader();
        }

        void Subscribe()
        {
            if (_account == null || _subscribed) return;

            // UNTRACKED TOUCHES NOTHING (spec section 2). Not an event subscription and not the priming
            // Account.Get reads below: an unbound account may be the trader's own live one, and "not
            // measured" has to mean not read, not merely not written. OnConfigureClick re-asserts this the
            // moment a binding is created or removed.
            if (_rules == null) return;

            _account.AccountItemUpdate += OnAccountItemUpdate;
            _account.ExecutionUpdate   += OnExecutionUpdate;
            _subscribed = true;

            // Prime the snapshot. AccountItemUpdate only fires when an item MOVES, so an account that is
            // already connected and flat would otherwise sit at zero until the trader's next fill.
            // Account.Get is a cache read of the last value the adapter pushed - never a broker round trip -
            // and it throws on an account mid-connect, hence the try/catch (RadarChartTrader.cs:973).
            double cash = Get(_account, AccountItem.CashValue);
            double unreal = Get(_account, AccountItem.UnrealizedProfitLoss);
            lock (_snapLock) _snap = new AcctSnapshot(cash, unreal, _snap.ExecSeq + 1);
        }

        void Unsubscribe()
        {
            if (_account == null || !_subscribed) return;
            _account.AccountItemUpdate -= OnAccountItemUpdate;
            _account.ExecutionUpdate   -= OnExecutionUpdate;
            _subscribed = false;
        }

        // The currency argument is required by the signature and ignored by the implementation: the value
        // comes back in Account.Denomination whatever is passed. UsDollar is passed because that is what
        // the spec's dollar figures assume; the denomination itself is checked once, in Recompute.
        static double Get(Account acct, AccountItem item)
        {
            if (acct == null) return 0;
            try { return acct.Get(item, Currency.UsDollar); }
            catch { return 0; }   // an account mid-connect can throw - treat as zero, re-read next event
        }

        // ---- account-thread handlers ----------------------------------------------------------------
        // Both run on a background connection thread. They touch no WPF object, take no long lock and
        // allocate one small snapshot. Everything else waits for the paint tick.

        void OnAccountItemUpdate(object sender, AccountItemEventArgs e)
        {
            if (e == null || e.Account == null) return;

            // Only the two items the cockpit reads. Everything else the adapter pushes (margin, buying
            // power, per-item realized) would just churn the snapshot and force a pointless recompute.
            if (e.AccountItem != AccountItem.CashValue && e.AccountItem != AccountItem.UnrealizedProfitLoss)
                return;

            double v = e.Value;
            if (double.IsNaN(v) || double.IsInfinity(v)) return;   // a garbage push must not become a trigger that never settles

            lock (_snapLock)
            {
                // A late event from the account we just left. Checked INSIDE the lock: OnAccountSelected
                // swaps _account and resets the snapshot under this same lock, so a check outside it can
                // pass and then store the old account's cash on top of the reset.
                if (!ReferenceEquals(e.Account, _account)) return;

                AcctSnapshot s = _snap;
                _snap = e.AccountItem == AccountItem.CashValue
                    ? new AcctSnapshot(v, s.Unrealized, s.ExecSeq)
                    : new AcctSnapshot(s.Cash, v, s.ExecSeq);
            }
        }

        void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            if (e == null) return;

            // Just a sequence bump. The event also fires when the broker's back office AMENDS or REMOVES
            // an existing execution (e.Operation), so accumulating "the fill that just landed" here would
            // double-count an amendment. The paint tick rebuilds the whole day series from the snapshot
            // instead, which is correct for adds, amendments, removals and a Playback rewind alike.
            lock (_snapLock)
            {
                if (e.Execution != null && !ReferenceEquals(e.Execution.Account, _account)) return;

                AcctSnapshot s = _snap;
                _snap = new AcctSnapshot(s.Cash, s.Unrealized, s.ExecSeq + 1);
            }
        }

        // A Playback REWIND or FAST-FORWARD, and a manual sim-account reset, all raise this - it is the
        // documented event and it is the only one that catches a fast-forward at all. Background thread:
        // it raises the recompute flag and returns. The paint tick rebuilds the series (the reset wiped
        // Account.Executions) and MergeLedger drops the rows the abandoned run left dated in the future.
        void OnSimulationAccountReset(object sender, EventArgs e)
        {
            // Whether the reset Account arrives as the sender is not verifiable - the assembly is
            // obfuscated and nothing documents it. So a NULL sender means REBUILD, not ignore. Dropping
            // the event leaves only the clock heuristic in OnPaintTick, which sees a rewind and is blind
            // to a forward scrub - and a forward scrub wipes Account.Executions just the same. A
            // needless rebuild costs one recompute; a missed one shows a stale series all evening.
            Account reset = sender as Account;
            if (reset != null)
            {
                lock (_snapLock)
                {
                    if (!ReferenceEquals(reset, _account)) return;
                }
            }
            _forceRecompute = true;
        }

        // ---- the paint tick -------------------------------------------------------------------------

        void OnPaintTick(object sender, EventArgs e)
        {
            try
            {
                DateTime platformNow = PlatformNow(_account);
                DateTime today = SessionClock.TradingDate(ToEastern(platformNow));

                // Backstop only: Account.SimulationAccountReset is the documented signal and it covers a
                // rewind AND a fast-forward, which this heuristic cannot see. It stays because a clock that
                // jumps backwards without an event still means the same thing - the rewind WIPES
                // Account.Executions, so the whole series has to be rebuilt from scratch.
                if (_lastClock != DateTime.MinValue && platformNow < _lastClock.AddSeconds(-2))
                {
                    _forceRecompute = true;
                    NinjaTrader.Code.Output.Process("[FundedPath] replay clock moved backwards - rebuilding the day series.",
                        NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                }
                _lastClock = platformNow;

                AcctSnapshot s = _snap;              // one volatile read; the copy is ours for the rest of the tick
                bool changed = _forceRecompute
                    || s.ExecSeq != _lastSeq
                    || !s.Cash.Equals(_lastCash)
                    || !s.Unrealized.Equals(_lastUnrealized)
                    || _bindingRev != _lastBindingRev
                    || today != _lastTradingDate;

                if (changed)
                {
                    // Advance the triggers BEFORE computing. If Recompute throws on some data shape we have
                    // not seen, this tick fails once instead of retrying - and re-logging - four times a
                    // second forever.
                    _forceRecompute = false;
                    _lastSeq = s.ExecSeq;
                    _lastCash = s.Cash;
                    _lastUnrealized = s.Unrealized;
                    _lastBindingRev = _bindingRev;
                    _lastTradingDate = today;

                    CockpitFrame f = Recompute(s, today);
                    _frame = f;
                    Render(f);
                    PushToChart(f);                  // SetSeries/SetUntracked each call InvalidateVisual themselves
                }

                // OUTSIDE the change gate, deliberately. The latch EDGE is read off the current frame,
                // but the "did the flatten actually take" follow-up has to keep running on ticks where
                // nothing changed - a flatten that was blocked moves no number on this screen at all.
                CockpitFrame current = _frame;
                if (current != null) MaybeEnforce(current, platformNow);
            }
            catch (Exception ex)
            {
                // Each NT8 window runs its own dispatcher thread, so ONE unhandled throw here kills THIS
                // window's UI while the connection thread keeps feeding events - a permanently frozen
                // cockpit over a healthy-looking log. Catch, name it (throttled), keep the pump alive.
                if ((DateTime.UtcNow - _lastPaintErr).TotalSeconds >= 5)
                {
                    _lastPaintErr = DateTime.UtcNow;
                    NinjaTrader.Code.Output.Process("[FundedPath] PAINT TICK ERROR - " + ex,
                        NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                }
            }
        }

        // ---- the recompute --------------------------------------------------------------------------

        CockpitFrame Recompute(AcctSnapshot s, DateTime today)
        {
            Account acct = _account;
            AccountBinding binding = _binding;
            PropRules rules = _rules;                 // resolved once when the binding loaded, not four times a second
            BreachBasis basis = binding != null ? binding.BreachBasis : BreachBasis.Equity;

            List<string> extraWarnings = new List<string>();
            if (!string.IsNullOrEmpty(_storeError)) extraWarnings.Add(_storeError);
            if (!string.IsNullOrEmpty(_etWarning)) extraWarnings.Add(_etWarning);

            Derived d = new Derived();
            // Tracked separately from the warning text: this one gates ACTING on the account, and a
            // warning string is not a flag.
            bool denomMismatch = false;
            // The binding check comes FIRST. BuildFromExecutions scans Account.Executions, runs
            // SystemPerformance.Calculate over every one of them and prints the commission reconcile to the
            // Output tab; the denomination probe below is another read. None of that may touch an account
            // the trader never bound - Untracked is about reads as much as writes (spec section 2).
            if (acct != null && rules != null)
            {
                d = BuildFromExecutions(acct, today);

                // A dropped future-dated bucket moves the numbers in the OPTIMISTIC direction: its
                // realized P&L leaves the balance too, so dropping a LOSING day raises the balance and
                // raises room-to-floor. The NaN scrub next door warns loudly for exactly that reason;
                // this must too, or the two policies disagree about what silence means.
                if (d.FutureDays > 0)
                    extraWarnings.Add(d.FutureDays.ToString(CultureInfo.InvariantCulture) +
                        " trading day(s) of executions are dated AFTER today (latest " +
                        d.LatestFuture.ToString("MMM d yyyy", CultureInfo.InvariantCulture) +
                        ") and were dropped. Their realized P&L is NOT in the balance or the floor below, so this " +
                        "reads MORE room than you may have. That is a clock or broker-timestamp problem, not a rule.");

                // Account.Get and every AccountItem value come back in Account.Denomination, NOT in the
                // currency asked for - the argument is ignored by the platform. A non-USD account would
                // therefore silently compare euros against a dollar rulebook.
                try
                {
                    if (acct.Denomination != Currency.UsDollar)
                    {
                        denomMismatch = true;
                        extraWarnings.Add("This account is denominated in " + acct.Denomination +
                                          ", but the rulebook is in US dollars. The figures below are not comparable.");
                    }
                }
                catch { /* mid-connect: re-checked on the next recompute */ }
            }

            // An unbound account is measured by nothing and RECORDED NOWHERE (spec section 2). The ledger
            // write lives behind this guard so the trader's own live account can never be written to disk
            // by accident. Untracked is the default and needs no configuration to be correct.
            List<TradingDay> completed = d.Completed;
            if (rules != null && _ledger != null)
                completed = MergeLedger(d.Completed, today);

            // The latch and the trading date are the memory this pure function cannot hold. It hands
            // back the updated latch on the state; keeping it is what makes a breach survive the tick,
            // and persisting it is what makes it survive the restart.
            ChallengeState state = ChallengeEngine.Evaluate(rules, completed, d.OpenRealized, s.Unrealized,
                                                            basis, _latched, today);
            _latched = state.Latched;

            CockpitPhase phase = PhaseOf(acct, binding, rules);
            CockpitFrame f = new CockpitFrame();
            f.State = state;
            f.Rules = rules;
            f.Tracked = rules != null;
            f.SessionView = _sessionView;
            f.Days = state.Days;
            f.Session = d.Session;
            f.Phase = phase;
            f.PhaseColor = ColorFor(phase);
            f.TradingDate = today;
            f.LiveBalance = state.Balance;
            f.LiveEquity = state.Equity;
            f.EquityBasis = basis == BreachBasis.Equity;
            f.BindingRev  = _bindingRev;      // see CockpitFrame.BindingRev: enforcement refuses a stale pairing
            // Both off the engine now that it exposes them. They used to be recomputed here, and a
            // second definition of the daily-limit LEVEL is exactly the kind of duplicate that drifts
            // until the red line on the chart and the number on the card disagree.
            f.DayOpenBalance = state.DayOpenBalance;
            f.FillsToday = d.OpenFills;
            f.DailyLimitLevel = state.DailyLossLimitLevel;

            // THE GATE ON ACTING. Every branch below is a reason to distrust the numbers above, and the
            // enforcement path refuses to touch the account while one holds. Ordered by cause: a store
            // that failed to load is usually WHY the rest is wrong, so it is named first.
            if (!string.IsNullOrEmpty(_storeError))
                f.DegradedReason = "the bindings or the day ledger could not be read or written (" + _storeError + ")";
            else if (!string.IsNullOrEmpty(_etWarning))
                f.DegradedReason = "the Eastern time zone is a fallback, so trading days may be bucketed into the wrong session (" + _etWarning + ")";
            else if (denomMismatch)
                f.DegradedReason = "this account is not denominated in US dollars, so its figures are not comparable with the rulebook";
            else if (AnyNonFinite(state, completed, s.Unrealized))
                f.DegradedReason = "a non-finite value (NaN or infinity) is present in the day ledger or in the computed state";
            f.Degraded = f.DegradedReason != null;

            // Untracked: the engine's own two warnings ("No challenge is bound...", "<plan> is not
            // modelled yet") say in a bullet exactly what the untracked card says in a sentence, one
            // directly under the other. THIS layer's warnings survive - an unreadable bindings file is
            // usually WHY an account reads untracked, and that bullet is the only thing that says so.
            List<string> all = new List<string>();
            // FIRST, above everything, and only when it is actually true: an account the trader armed
            // and believes is being policed, that is in fact not being policed. The banner cannot say
            // this - it only appears once something has already latched, and by then it is too late to
            // go and fix a time zone.
            if (f.Degraded && binding != null && binding.Enforcement == EnforcementMode.Armed)
                all.Add("Enforcement is ARMED for this account but is BLOCKED right now: " + f.DegradedReason +
                        ". Nothing will be closed automatically until that is fixed.");
            all.AddRange(extraWarnings);
            if (rules != null && state.Warnings != null) all.AddRange(state.Warnings);
            f.Warnings = all.ToArray();
            return f;
        }

        sealed class Derived
        {
            public List<TradingDay>  Completed = new List<TradingDay>();
            public List<SessionFill> Session   = new List<SessionFill>();
            public double OpenRealized;
            public int    OpenFills;
            public int      FutureDays;     // buckets dated after today, dropped from the balance
            public DateTime LatestFuture;   // the furthest of them, named in the warning
        }

        // Rebuilds the whole day series from a snapshot of the account's executions. Never incremental:
        // a broker amendment and a Playback rewind both rewrite history, and an accumulator would either
        // double-count or keep the pre-rewind fills forever.
        Derived BuildFromExecutions(Account acct, DateTime today)
        {
            Derived d = new Derived();

            List<Execution> execs;
            lock (acct.Executions)                    // a plain Collection<T>, appended from the connection thread
                execs = acct.Executions.ToList();

            // Fills per trading day, counted from the executions themselves rather than from the closed
            // trades: two executions make one trade, and the card says "fills", not "trades".
            Dictionary<DateTime, int> fills = new Dictionary<DateTime, int>();
            for (int i = 0; i < execs.Count; i++)
            {
                Execution x = execs[i];
                if (x == null || x.IsSod) continue;   // IsSod is a synthetic start-of-day position marker, not a fill
                DateTime day = SessionClock.TradingDate(ToEastern(x.Time));
                int n;
                fills[day] = fills.TryGetValue(day, out n) ? n + 1 : 1;
            }

            // Closed trades, paired by the platform. This is the only supported way to get realized P&L per
            // trade; there is exactly one Calculate overload and it takes an ICollection<Execution>.
            Dictionary<DateTime, double> pnl = new Dictionary<DateTime, double>();
            double todaySum = 0;
            List<SessionFill> session = new List<SessionFill>();

            SystemPerformance perf = NinjaTrader.Cbi.SystemPerformance.Calculate(execs);
            if (perf != null && perf.AllTrades != null)
            {
                foreach (Trade t in perf.AllTrades)
                {
                    if (t == null || t.Exit == null) continue;   // an open trade has no exit and no realized dollars

                    // Bucketed by the EXIT, per spec section 5: a trade opened Monday evening and closed
                    // Tuesday morning is Tuesday's realized P&L, because that is when the money moved.
                    DateTime day = SessionClock.TradingDate(ToEastern(t.Exit.Time));
                    double p;
                    pnl[day] = pnl.TryGetValue(day, out p) ? p + t.ProfitCurrency : t.ProfitCurrency;

                    if (day == today)
                    {
                        todaySum += t.ProfitCurrency;
                        SessionFill sf = new SessionFill();
                        sf.TimeEt = ToEastern(t.Exit.Time);
                        sf.Instrument = InstrumentName(t);
                        sf.Side = t.Entry != null && t.Entry.MarketPosition == MarketPosition.Short ? "SHORT" : "LONG";
                        sf.Quantity = t.Quantity;
                        sf.ProfitCurrency = t.ProfitCurrency;
                        session.Add(sf);
                    }
                }
            }

            // The Session view's x axis is time, so the series must be sorted by it. AllTrades comes back in
            // trade-number order, which is entry order - a scale-out closed before an earlier trade's exit
            // would otherwise draw the curve backwards.
            session.Sort(delegate (SessionFill a, SessionFill b) { return a.TimeEt.CompareTo(b.TimeEt); });
            double run = 0;
            for (int i = 0; i < session.Count; i++)
            {
                run += session[i].ProfitCurrency;
                session[i].Balance = run;             // relative to the day's opening balance; the frame carries the offset
            }
            d.Session = session;

            // Split into completed days and the day in progress. A future-dated bucket (a clock skew, or a
            // stale broker timestamp) is dropped rather than treated as completed: it would ratchet the
            // high-water mark on a close that has not happened.
            List<DateTime> dates = new List<DateTime>(pnl.Keys);
            foreach (KeyValuePair<DateTime, int> kv in fills)
                if (!pnl.ContainsKey(kv.Key)) dates.Add(kv.Key);
            dates.Sort();

            for (int i = 0; i < dates.Count; i++)
            {
                DateTime day = dates[i];
                if (day > today)
                {
                    // Dropped, never counted: a day that has not closed cannot ratchet the high-water
                    // mark. COUNTED here so Recompute can say it was dropped - losing a losing day
                    // silently inflates both the balance and the room to the floor.
                    d.FutureDays++;
                    if (day > d.LatestFuture) d.LatestFuture = day;
                    continue;
                }
                double p; pnl.TryGetValue(day, out p);
                int n; fills.TryGetValue(day, out n);
                if (day == today) { d.OpenFills = n; continue; }

                TradingDay row = new TradingDay();
                row.Date = day;
                row.RealizedPnL = p;
                row.Fills = n;
                d.Completed.Add(row);                 // ClosingBalance / FloorInForce are the engine's to fill
            }
            d.OpenRealized = todaySum;

            ReconcileOnce(acct, todaySum, d.OpenFills);
            return d;
        }

        static string InstrumentName(Trade t)
        {
            try
            {
                Execution x = t.Entry ?? t.Exit;
                if (x == null || x.Instrument == null) return "";
                return x.Instrument.MasterInstrument != null ? x.Instrument.MasterInstrument.Name : x.Instrument.FullName;
            }
            catch { return ""; }
        }

        // Trade.ProfitCurrency's getter is obfuscated in the shipped assembly, so whether it is net or gross
        // of commission cannot be read off the platform - and TradesPerformance exposes GrossProfit,
        // NetProfit and TotalCommission as three separate members, which suggests it could be either. A
        // floor computed gross of commission is optimistic by exactly the commission, which is the wrong
        // direction. So MEASURE it once per session against the broker's own two figures and log the delta
        // rather than assuming (the same lesson PropSim's ledger already taught this workspace).
        void ReconcileOnce(Account acct, double sumProfitCurrency, int fillsToday)
        {
            if (_reconciled || fillsToday <= 0) return;
            _reconciled = true;
            double net = Get(acct, AccountItem.RealizedProfitLoss);
            double gross = Get(acct, AccountItem.GrossRealizedProfitLoss);
            NinjaTrader.Code.Output.Process(string.Format(CultureInfo.InvariantCulture,
                "[FundedPath] commission reconcile - sum(Trade.ProfitCurrency)={0:0.00} vs RealizedProfitLoss={1:0.00} (net) / GrossRealizedProfitLoss={2:0.00}. " +
                "Matching the net figure means the floor is computed net of commission.",
                sumProfitCurrency, net, gross), NinjaTrader.NinjaScript.PrintTo.OutputTab1);
        }

        // ---- the day ledger -------------------------------------------------------------------------
        //
        // Account.Executions is the CURRENT SESSION only (the platform keeps ~3 days loaded,
        // Account.LookbackDaysExecutions == 3), so a 20-day evaluation cannot be rebuilt from it after an
        // NT8 restart - and a missing winning day lowers the high-water mark, which lowers the floor, which
        // reports MORE room than the trader actually has. That is the dangerous direction, so completed
        // days are persisted next to bindings.xml and only the day in progress comes from executions.
        //
        // ponytail: a flat XML file per account key, rewritten whole. A challenge is tens of rows, not
        // thousands; if that ever changes, append-only is the upgrade.

        List<TradingDay> MergeLedger(List<TradingDay> computed, DateTime today)
        {
            bool dirty = false;

            // The challenge's start date, typed in the binding dialog. Days before it belong to whatever
            // this account was doing previously - an earlier evaluation, a funded account that reset, a
            // rehearsal - and counting them ratchets a high-water mark, and therefore a floor, from a
            // challenge that had not begun. DateTime.MinValue is the store's "not set" and compares
            // below every real date, so an unset date filters nothing.
            //
            // FILTERED, never deleted: the rows stay in the file. A mistyped start date must not destroy
            // history that cannot be rebuilt - Account.Executions reaches back three days, and a missing
            // winning day lowers the floor, which is the dangerous direction.
            DateTime start = _binding == null
                ? DateTime.MinValue
                : DateTime.SpecifyKind(_binding.StartedUtc.Date, DateTimeKind.Unspecified);

            // A Playback rewind (and a manual sim reset) moves "today" BACKWARDS, which leaves rows the
            // ABANDONED run persisted dated at or after the new today. The writer refuses to create such a
            // row, but nothing ever removed one that later became future-dated - and the engine consumes
            // them as completed days, showing the trader the floor a run he rewound away had ratcheted to.
            // Dropped here rather than on the rewind branch so the repair is unconditional: a file that
            // already carries future rows is cleaned on the first recompute, rewind detected or not.
            List<DateTime> future = new List<DateTime>();
            foreach (KeyValuePair<DateTime, TradingDay> kv in _ledger)
                if (kv.Key >= today) future.Add(kv.Key);
            for (int i = 0; i < future.Count; i++)
            {
                _ledger.Remove(future[i]);
                dirty = true;                         // and the file is rewritten below, so it matches what is shown
            }

            // The same repair for the latch records: a breach the trader rewound away must not keep the
            // banner up over a run that no longer exists. STRICTLY after today - a record dated today is
            // the day in progress, which is precisely what the banner is for.
            for (int i = _enforcements.Count - 1; i >= 0; i--)
                if (_enforcements[i] != null && _enforcements[i].TradingDate > today)
                {
                    _enforcements.RemoveAt(i);
                    dirty = true;
                }

            // ...and the ENGINE's latch with them. Purging only the records left the two halves out of
            // step: the latch still said Breached, the record that proved it had been rewound away, so the
            // edge test saw a fresh breach and, on an armed account, flattened a second time - on a run
            // that no longer exists. A Playback account can be armed (arming is gated on the rulebook,
            // not the provider), and Market Replay is exactly where this add-on gets rehearsed, so this
            // was live rather than theoretical. Same shape on a live account via a backwards clock step
            // across a day boundary.
            //
            // The latch is only ever cleared here for a breach the rewind actually undid: dated strictly
            // after today, on the same test the records use.
            if (_latched != null && _latched.ChallengeBreached && _latched.BreachedOn > today)
            {
                _latched.ChallengeBreached = false;
                _latched.BreachedOn        = DateTime.MinValue;
                _latched.BreachedAt        = 0.0;
                _latched.BreachedFloor     = 0.0;
                dirty = true;
                NinjaTrader.Code.Output.Process(
                    "[FundedPath] replay rewound past the breach - the challenge latch is cleared with it.",
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
            }
            if (_latched != null && _latched.DailyLockoutDate > today)
            {
                _latched.DailyLockoutDate = DateTime.MinValue;
                _latched.DailyLockoutAt   = 0.0;
                dirty = true;
            }

            // Computed rows win over stored ones for the days they cover: they are re-derived from the live
            // execution list, so they carry broker amendments and a Playback rewind's corrected history.
            // Days the executions no longer reach keep whatever the ledger holds.
            for (int i = 0; i < computed.Count; i++)
            {
                TradingDay c = computed[i];
                if (c.Date >= today) continue;        // never persist the day in progress: it is not closed yet
                if (c.Date < start) continue;         // before the challenge began: not part of it
                TradingDay old;
                if (_ledger.TryGetValue(c.Date, out old) && old.RealizedPnL.Equals(c.RealizedPnL) && old.Fills == c.Fills)
                    continue;
                TradingDay row = new TradingDay();
                row.Date = c.Date; row.RealizedPnL = c.RealizedPnL; row.Fills = c.Fills;
                _ledger[c.Date] = row;
                dirty = true;
            }

            // Saved BEFORE the series is projected: SaveLedger merges whatever a second tab has written
            // since, and the trader should see those days on this very frame rather than one tick later.
            if (dirty)
            {
                try { SaveLedger(today); }
                catch (Exception ex)
                {
                    // A failed ledger write is not fatal to the session - the in-memory series is still
                    // right - but it silently loses history at the next restart, so it must be said out loud.
                    _storeError = "Could not write the day ledger: " + ex.Message;
                    NinjaTrader.Code.Output.Process("[FundedPath] ledger save failed: " + ex, NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                }
            }

            // COMPLETED days only. The purge above is what makes that true of _ledger itself, so there is
            // one definition of "completed" here and not a filter that can drift from the one that writes.
            // The start date is the one filter applied HERE rather than by purging - see above.
            List<TradingDay> merged = new List<TradingDay>();
            foreach (KeyValuePair<DateTime, TradingDay> row in _ledger)
                if (row.Key >= start) merged.Add(row.Value);
            merged.Sort(delegate (TradingDay a, TradingDay b) { return a.Date.CompareTo(b.Date); });
            return merged;
        }

        void LoadBindingFor(Account acct)
        {
            _binding = null;
            _rules = null;
            _ledger = null;
            _ledgerPath = null;
            _bindingRev++;
            // The latch records belong to the account that is going away. Cleared before the new one's
            // are loaded, or the previous challenge's breach banner would sit over this account.
            _enforcements.Clear();
            _enforcingRec = null;
            _enforceRetryDeadline = DateTime.MinValue;
            _latched = new LatchedState();
            // Re-seed from the store rather than keeping the last message: a ledger error the trader has
            // since repaired must stop being reported, or the rail cries wolf for the rest of the session.
            _storeError = _store.LastLoadError;

            if (acct == null) return;

            string key = BindingStore.KeyFor(ProviderName(acct), acct.DisplayName);
            _binding = _store.Find(key);
            if (_binding == null) return;             // unbound: Untracked, and nothing is read or written

            // A binding whose firm/size the catalog does not model resolves to null and is Untracked too -
            // same gate, one field, so the display, the subscription and the disk writes cannot disagree.
            _rules = _binding.ResolveRules();
            if (_rules == null) return;

            string dir = Path.GetDirectoryName(_bindingsPath);
            _ledgerPath = Path.Combine(dir ?? "", "days-" + SafeFileName(key) + ".xml");
            bool failed;
            _ledger = LoadLedger(_ledgerPath, out failed, _enforcements);   // the failure is already on _storeError
            _latched = LoadLatch(_ledgerPath);
            RepairInterruptedEnforcements();
        }

        // A record is written as "enforcing" BEFORE anything is sent to the platform - deliberately, so a
        // crash between the two leaves evidence rather than silence. The cost is that any record still in
        // that state when it is READ BACK belongs to a run that never reported: NinjaTrader was closed
        // mid-flatten, the account was switched while the platform was working, or the enforcer's thread
        // died. Left alone it renders as "enforcing..." for ever, which reads as an action still in
        // flight and is the one thing a banner must never lie about.
        //
        // It is NOT downgraded to a failure: the flatten may well have gone through. It is marked
        // unfinished, which is the only thing that is actually known, and it points at the one place the
        // truth exists.
        void RepairInterruptedEnforcements()
        {
            for (int i = 0; i < _enforcements.Count; i++)
            {
                EnforcementRecord r = _enforcements[i];
                if (r == null || r.Action != ActRunning) continue;
                r.Action = ActUnknown;
                r.Detail = "INTERRUPTED. Enforcement was sent to the platform at " + EtStamp(r.AtUtc) +
                           " and this panel never saw it finish - NinjaTrader closed, or the account was " +
                           "switched, while it was working. It may well have gone through. Check the " +
                           "account and the Output tab, then close it by hand if anything is still open.";
            }
        }

        // failed means the file exists and could not be READ. That is NOT the same as "came up empty":
        // a missing file is the normal first day of a challenge. SaveLedger's read-modify-write turns on
        // exactly that difference - merging into a dictionary that only LOOKS empty replaces the file.
        // intoEnforcements is filled with the latch records in the same file when it is non-null. They
        // live in the day ledger rather than a file of their own because they are the same kind of fact
        // as a completed day - something that happened to this account, that must survive a restart, and
        // that only means anything next to the days around it.
        Dictionary<DateTime, TradingDay> LoadLedger(string path, out bool failed, List<EnforcementRecord> intoEnforcements)
        {
            failed = false;
            Dictionary<DateTime, TradingDay> rows = new Dictionary<DateTime, TradingDay>();
            try
            {
                if (!File.Exists(path)) return rows;   // first day of a challenge is not an error
                XDocument doc = XDocument.Load(path);
                if (doc.Root == null) return rows;
                foreach (XElement e in doc.Root.Elements("Day"))
                {
                    DateTime date;
                    double p; int n;
                    if (!DateTime.TryParseExact((string)e.Attribute("date") ?? "", "yyyy-MM-dd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                        continue;                      // one unreadable row is skipped; the rest still load
                    double.TryParse((string)e.Attribute("pnl") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out p);
                    int.TryParse((string)e.Attribute("fills") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                    TradingDay row = new TradingDay();
                    row.Date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
                    row.RealizedPnL = p;
                    row.Fills = n;
                    rows[row.Date] = row;
                }

                if (intoEnforcements != null)
                {
                    foreach (XElement e in doc.Root.Elements("Enforcement"))
                    {
                        EnforcementRecord rec = ReadEnforcement(e);
                        if (rec != null) intoEnforcements.Add(rec);
                    }
                }
            }
            catch (Exception ex)
            {
                // Come up empty and SAY SO. Silently starting from zero would rebuild the challenge with a
                // lower high-water mark and a floor that is too generous.
                failed = true;
                rows.Clear();
                _storeError = "Could not read the day ledger (" + path + "): " + ex.Message +
                              " - the multi-day series is incomplete until it is repaired.";
            }
            return rows;
        }

        void SaveLedger(DateTime today)
        {
            if (_ledgerPath == null || _ledger == null) return;
            string dir = Path.GetDirectoryName(_ledgerPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // READ-MODIFY-WRITE, not write. A second tab on the same account key holds its OWN _ledger and
            // rewrites this same file whole; whichever tab saved last would erase the other's completed
            // days. A missing winning day LOWERS the high-water mark, which lowers the floor, which reports
            // MORE room than the trader has - the dangerous direction. Our rows win on the days we cover:
            // they were just re-derived from the live execution list.
            bool failed;
            List<EnforcementRecord> mergedEnf = new List<EnforcementRecord>();
            Dictionary<DateTime, TradingDay> merged = LoadLedger(_ledgerPath, out failed, mergedEnf);

            // FAIL CLOSED, exactly as OnConfigureClick refuses to swap in a BindingStore that failed to
            // load. LoadLedger swallows every exception and hands back an empty dictionary, so without
            // this the merge below contributes nothing and the file is REPLACED by this tab's rows
            // alone - on one transient IOException, in a folder that is often OneDrive-synced here.
            // Losing a winning day lowers the high-water mark, lowers the floor, and reports more room
            // than the trader has. _ledger is left untouched, so the session itself stays correct.
            if (failed)
                throw new IOException("The existing day ledger could not be read, so it was not rewritten: " + _ledgerPath);

            foreach (KeyValuePair<DateTime, TradingDay> kv in _ledger)
                merged[kv.Key] = kv.Value;

            // ...and a future-dated row must not come back in through that merge: this is the one place
            // that writes the file, so it is the one place that can guarantee the file never holds a day
            // that has not closed.
            List<DateTime> future = new List<DateTime>();
            foreach (KeyValuePair<DateTime, TradingDay> kv in merged)
                if (kv.Key >= today) future.Add(kv.Key);
            for (int i = 0; i < future.Count; i++)
                merged.Remove(future[i]);

            _ledger = merged;

            // Same read-modify-write for the latch records, and for the same reason: a second tab on
            // this account key rewrites this file whole, and losing the record that a breach was already
            // enforced is what would let a restart enforce it a second time. Keyed on the instant plus
            // the kind, so our own copy of a record replaces the file's rather than duplicating it.
            List<EnforcementRecord> outEnf = new List<EnforcementRecord>();
            List<string> enfSeen = new List<string>();
            for (int i = 0; i < _enforcements.Count; i++)
            {
                EnforcementRecord rec = _enforcements[i];
                if (rec == null || rec.TradingDate > today) continue;
                string k = rec.AtUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + (rec.Kind ?? "");
                if (enfSeen.Contains(k)) continue;
                enfSeen.Add(k);
                outEnf.Add(rec);
            }
            for (int i = 0; i < mergedEnf.Count; i++)
            {
                EnforcementRecord rec = mergedEnf[i];
                if (rec == null || rec.TradingDate > today) continue;
                string k = rec.AtUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + (rec.Kind ?? "");
                if (enfSeen.Contains(k)) continue;
                enfSeen.Add(k);
                outEnf.Add(rec);
            }
            outEnf.Sort(delegate (EnforcementRecord a, EnforcementRecord b) { return a.AtUtc.CompareTo(b.AtUtc); });
            _enforcements = outEnf;

            XElement root = new XElement("FundedPathDays", new XAttribute("version", "1"));
            List<TradingDay> rows = new List<TradingDay>(_ledger.Values);
            rows.Sort(delegate (TradingDay a, TradingDay b) { return a.Date.CompareTo(b.Date); });
            for (int i = 0; i < rows.Count; i++)
            {
                // InvariantCulture on every number and date. On a comma-decimal machine (this one is es-ES)
                // a plain ToString would write "123,45" and the file would be unreadable everywhere else.
                root.Add(new XElement("Day",
                    new XAttribute("date", rows[i].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    new XAttribute("pnl", rows[i].RealizedPnL.ToString("R", CultureInfo.InvariantCulture)),
                    new XAttribute("fills", rows[i].Fills.ToString(CultureInfo.InvariantCulture))));
            }

            // LAST WRITER WINS, deliberately. A second tab on the same account key holds its own latch
            // and would need a merge, but a merge that adopts the file's breach would resurrect one the
            // trader has just reset. The enforcement RECORDS are merged (above), and those are what stop
            // an account being closed twice - so the worst a stale second tab costs is a headline.
            if (_latched != null)
                root.Add(new XElement("Latch",
                    new XAttribute("breached", _latched.ChallengeBreached ? "true" : "false"),
                    new XAttribute("on", Ymd(_latched.BreachedOn)),
                    new XAttribute("at", _latched.BreachedAt.ToString("R", CultureInfo.InvariantCulture)),
                    new XAttribute("floor", _latched.BreachedFloor.ToString("R", CultureInfo.InvariantCulture)),
                    new XAttribute("lockDate", Ymd(_latched.DailyLockoutDate)),
                    new XAttribute("lockAt", _latched.DailyLockoutAt.ToString("R", CultureInfo.InvariantCulture))));

            for (int i = 0; i < _enforcements.Count; i++)
            {
                EnforcementRecord rec = _enforcements[i];
                root.Add(new XElement("Enforcement",
                    // Round-trip "o" on a UTC instant: this stamp is compared against records written by
                    // another machine in another zone, and a local-time stamp would reorder them.
                    new XAttribute("at", rec.AtUtc.ToString("o", CultureInfo.InvariantCulture)),
                    new XAttribute("date", rec.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    new XAttribute("kind", rec.Kind ?? ""),
                    new XAttribute("value", rec.Value.ToString("R", CultureInfo.InvariantCulture)),
                    new XAttribute("action", rec.Action ?? ""),
                    new XAttribute("headline", rec.Headline ?? ""),
                    rec.Detail ?? ""));   // the enforcer's transcript, verbatim
            }

            // Temp-then-swap, same discipline as BindingStore.Save: a crash mid-write leaves the old file
            // or the new one, never a truncated one.
            string temp = _ledgerPath + ".tmp";
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(temp);
            if (File.Exists(_ledgerPath)) File.Replace(temp, _ledgerPath, _ledgerPath + ".bak", true);
            else File.Move(temp, _ledgerPath);
        }

        static string SafeFileName(string key)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            char[] chars = key.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(bad, chars[i]) >= 0 || chars[i] == '|') chars[i] = '_';
            return new string(chars);
        }

        static string ProviderName(Account acct)
        {
            // Provider.ToString() renders "Provider19", not "Rithmic" - the broker names live only in the
            // obfuscated TypeConverter. That is fine here: this string is a LEDGER KEY, never UI text, and
            // its only job is to keep a Playback rehearsal from sharing a ledger with the real account.
            try { return acct.Provider.ToString(); }
            catch { return "UnknownProvider"; }
        }

        static bool IsPlayback(Account acct)
        {
            if (acct == null) return false;
            try { return acct.Provider == Provider.Playback; }
            catch { return false; }   // fail closed: an unreadable Provider is not proof of a rehearsal
        }

        // ---- clock ----------------------------------------------------------------------------------

        // A Playback rehearsal must age against the REPLAY clock, never the wall clock: the replay can run
        // at 24x or be paused for an hour (shipped idiom, @BarTimer.cs:150). But that holds only for a
        // PLAYBACK ACCOUNT. Connection.PlaybackConnection is non-null as soon as the Playback connection
        // exists, whatever account this tab is bound to, so reading pb.Now unconditionally hands a Sim101 or
        // a live tab the replay's date: every real fill then buckets to the real today, no bucket matches
        // that date, and ROOM TO FLOOR, ACCOUNT EQUITY and the day count all go quietly wrong for as long as
        // Market Replay is connected. IsPlayback() answers false for a null account.
        static DateTime PlatformNow(Account acct)
        {
            Connection pb = Connection.PlaybackConnection;
            return (pb != null && IsPlayback(acct)) ? pb.Now : NinjaTrader.Core.Globals.Now;
        }

        // Every DateTime NinjaTrader hands out is already in the Tools > Options > General display zone, so
        // the conversion source is that zone and never the machine's local zone.
        DateTime ToEastern(DateTime platformTime)
        {
            try
            {
                TimeZoneInfo et = EasternZone();
                TimeZoneInfo src = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;

                // Kind is unverifiable on the platform's clock (the getter is obfuscated), and ConvertTime
                // THROWS when Kind is Utc/Local and disagrees with the source zone. Stamping Unspecified
                // makes the source zone argument authoritative, which is what we mean.
                DateTime t = DateTime.SpecifyKind(platformTime, DateTimeKind.Unspecified);

                if (et != null && src != null)
                    return TimeZoneInfo.ConvertTime(t, src, et);

                // No usable zone PAIR. EasternZone() already falls back to a hand-built -5/-4 Eastern, so
                // reaching here means the platform's own display zone could not be read and there is nothing
                // to convert FROM. Pass the time through and say so, rather than inventing an offset.
                if (_etWarning.Length == 0)
                    _etWarning = "NinjaTrader's display time zone could not be read. Trading days are bucketed on the platform clock as it comes and may be wrong.";
                return t;
            }
            catch (Exception ex)
            {
                if (_etWarning.Length == 0)
                    _etWarning = "Time zone conversion failed (" + ex.Message + "). Trading days may be bucketed wrong.";
                return platformTime;
            }
        }

        TimeZoneInfo EasternZone()
        {
            if (_etResolved) return _etZone;
            _etResolved = true;
            try
            {
                _etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            catch
            {
                try { _etZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
                catch
                {
                    // A hand-built zone rather than a flat -5. A fixed offset is an hour early from March to
                    // November, so the 18:00 session boundary lands at 17:00 EDT and every fill between the
                    // two is filed into the WRONG TRADING DAY for eight months of the year. The warning
                    // stays: a built-in rule is still not the machine's own zone data.
                    _etZone = CustomEastern();
                    _etWarning = "The Eastern time zone is not installed on this machine. " +
                                 "Trading days are bucketed with a built-in US Eastern rule (-5 in winter, -4 in summer) instead of the system's.";
                }
            }
            return _etZone;
        }

        // US Eastern from first principles: -5 standard, +1 hour from 02:00 on the second Sunday in March
        // to 02:00 on the first Sunday in November - the rule in force since 2007.
        static TimeZoneInfo CustomEastern()
        {
            try
            {
                TimeZoneInfo.TransitionTime dstStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
                TimeZoneInfo.TransitionTime dstEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
                TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), dstStart, dstEnd);
                return TimeZoneInfo.CreateCustomTimeZone("FundedPath Eastern", TimeSpan.FromHours(-5),
                    "FundedPath Eastern", "Eastern Standard Time", "Eastern Daylight Time",
                    new TimeZoneInfo.AdjustmentRule[] { rule });
            }
            catch { return null; }   // then ToEastern says so instead of pretending to have converted
        }

        // ---- rendering ------------------------------------------------------------------------------

        void Render(CockpitFrame f)
        {
            ChallengeState st = f.State;
            Color pc = f.PhaseColor;
            SolidColorBrush phaseBrush = new SolidColorBrush(pc);   // per-frame and unfrozen: cheap at 4 Hz, and it changes with the phase

            _stripe.Background = phaseBrush;
            _phaseDot.Fill = phaseBrush;
            _phaseText.Foreground = phaseBrush;
            _phaseText.Text = PhaseLabel(f.Phase) + (f.Tracked ? Dot + "day " + (f.Days.Count + 1) : "");
            _phaseNote.Text = NoteFor(f.Phase);

            _title.Text = (f.SessionView ? "Session Curve" : "Challenge Curve") + Dot +
                          f.TradingDate.ToString("MMM d", CultureInfo.InvariantCulture) +
                          (f.Tracked ? Dot + SourceLabel(f.Phase) : "");

            // A LATCH OUTRANKS THE LIVE VERDICT, and that is the whole point. He hit his daily limit,
            // the pill flashed, and it was back to ON TRACK before he looked up - the worst moment of
            // his day left no mark on the screen at all. A latched state now stays: until the day rolls
            // for a soft daily lockout, until he resets it for a challenge breach.
            EnforcementRecord latch = ActiveLatch(f.TradingDate);
            if (latch != null)
            {
                bool isBreach = latch.Kind == KindBreach;
                _verdictText.Text = isBreach
                    ? "BREACHED" + Dot + Money(Math.Max(0.0, -latch.Value)) + " BELOW THE FLOOR"
                    : "DAY LOCKED" + Dot + "DONE FOR TODAY";
                Brush lb = isBreach ? Red : Gold;
                _verdictText.Foreground = lb;
                _verdictPill.BorderBrush = lb;
            }
            else
            {
                _verdictText.Text = string.IsNullOrEmpty(st.Headline) ? "UNTRACKED" : st.Headline;
                Brush vb = VerdictBrush(st.Verdict);
                _verdictText.Foreground = vb;
                _verdictPill.BorderBrush = vb;
            }
            RenderBanner(f);

            // Untracked: the chart is greyed here, on the host, so the state is visibly correct even before
            // CurveChart draws its own "no challenge bound" message from frame.Tracked.
            _chart.Opacity = f.Tracked ? 1.0 : 0.35;

            if (!f.Tracked) { ApplyUntracked(); RenderWarnings(f); return; }

            PropRules r = f.Rules;
            SetChip(_chips[0], FirmLabel(r.Firm));
            SetChip(_chips[1], r.Plan);
            // The RULEBOOK's phase, never the cockpit's. On a Playback rehearsal the cockpit phase is
            // Replay, and "50K REPLAY" names no rule set at all - nothing on screen then said which
            // phase's rules were being measured. The connection state keeps the pill on the right,
            // which is where it belongs (and what the approved mockup shows).
            SetChip(_chips[2], SizeLabel(r.Size) + " " + RuleLabel(r.Phase));
            SetChip(_chips[3], r.HasDailyLossLimit ? Money(r.DailyLossLimit) + (r.DailyLossSoft ? " soft" : "") : "Off");
            SetChip(_chips[4], (r.HwmBasis == HwmBasis.EodClose ? "EOD" : "Intraday") + Dot + (st.FloorLocked ? "locked" : "trailing"));

            _untrackedCard.Root.Visibility = Visibility.Collapsed;
            for (int i = 0; i < _cards.Length; i++) _cards[i].Root.Visibility = Visibility.Visible;

            // Card 1 - To target. The goal is the profit target in the evaluation and the payout balance in
            // the funded phase; the engine has already picked whichever applies.
            SetCard(_cards[0], "TO TARGET",
                st.ToTarget > 0 ? Money(st.ToTarget) : "Reached",
                st.ProgressPct.ToString("0", CultureInfo.InvariantCulture) + "% done",
                st.ToTarget > 0 ? TextCol : Green);

            // Card 2 - Room to floor, on the trader's chosen breach basis. Negative means already breached.
            SetCard(_cards[1], "ROOM TO FLOOR",
                Money(st.RoomToFloor),
                "floor " + Money(st.Floor) + Dot + (f.EquityBasis ? "equity basis" : "balance basis"),
                st.RoomToFloor <= 0 ? Red : (st.RoomToFloor < Math.Max(250.0, r.MaxLoss * 0.2) ? Gold : TextCol));

            // Card 3 - Account equity. The broker's cash value is a CROSS-CHECK, never added to anything:
            // the broker's balance already includes the session's realized P&L, so adding the two would
            // double-count the day.
            //
            // Shown only where it means something. A Playback account's cash is whatever the Playback
            // window was configured with - $100,000 under a 50K challenge is the ordinary case - so
            // "broker $100,000" beside "$50,000" reads as a contradiction rather than as a check.
            bool crossCheck = _lastCash > 0 && f.Phase != CockpitPhase.Replay;
            SetCard(_cards[2], "ACCOUNT EQUITY",
                Money(st.Equity),
                Signed(st.DayPnL) + " session" + (crossCheck ? Dot + "broker cash " + Money(_lastCash) : ""),
                st.DayPnL < 0 ? Red : (st.DayPnL > 0 ? Green : TextCol));

            // Card 4 - Floor status. "Locked" is the good news: the drawdown has stopped trailing forever.
            SetCard(_cards[3], "FLOOR STATUS",
                st.FloorLocked ? "Locked" : "Trailing",
                st.FloorLocked ? "frozen at " + Money(st.Floor) : "ratchets on the session close",
                st.FloorLocked ? Green : Muted);

            // Card 5 - Trading days in the Challenge view; the mockup's Fills Today in the Session view,
            // because a day count says nothing about the session you are inside.
            if (f.SessionView)
            {
                // The headline counts the same things the subline partitions. It used to headline
                // FillsToday - raw executions - over a win/loss split of CLOSED TRADES, so a real
                // session read "35" above "24 winners - 3 losers": two different denominators stacked
                // as if they added up. A scale-in is several fills and one trade, so they never will.
                int win = 0, loss = 0, flat = 0;
                for (int i = 0; i < f.Session.Count; i++)
                {
                    double pl = f.Session[i].ProfitCurrency;
                    if (pl > 0) win++;
                    else if (pl < 0) loss++;
                    else flat++;
                }
                string split = win + " up" + Dot + loss + " down";
                if (flat > 0) split += Dot + flat + " flat";
                if (f.FillsToday > 0) split += Dot + f.FillsToday.ToString(CultureInfo.InvariantCulture) + " fills";
                SetCard(_cards[4], "TRADES TODAY",
                    f.Session.Count.ToString(CultureInfo.InvariantCulture), split, TextCol);
            }
            else
            {
                int need = r.Phase == Phase.LiveSim ? r.DaysToPayout : r.MinDays;
                SetCard(_cards[4], "TRADING DAYS", st.QualifyingDays.ToString(CultureInfo.InvariantCulture),
                    need > 0 ? need + " required" + Dot + (st.QualifyingDays >= need ? "met" : (need - st.QualifyingDays) + " to go")
                             : "no minimum",
                    need > 0 && st.QualifyingDays < need ? Gold : TextCol);
            }

            RenderConstraint(st, r);
            RenderWarnings(f);
        }

        // "What breaks first" - the README's headline promise, computed by the engine on every tick and
        // until now drawn nowhere. Quieter than the verdict pill: the pill is the answer, this is why.
        //
        // The funded phase's consistency readout rides on this line instead of taking the rail's fifth
        // card, because that card carries TRADING DAYS - days-to-payout, which is the OTHER gate on the
        // very same payout. Swapping one payout gate for the other buys nothing; this line fits both.
        void RenderConstraint(ChallengeState st, PropRules r)
        {
            string line = st.BindingConstraint == null ? "" : st.BindingConstraint;

            // BestDayPnL and ConsistencyCapNow are modelled in full and were rendered nowhere, so a
            // funded account got no consistency readout at all. ConsistencyPct > 0 is the rule's own
            // presence test; the cap itself is 0 until there is profit to take a share of.
            if (r != null && r.ConsistencyPct > 0)
                line += Dot + "best day " + Money(st.BestDayPnL) + " vs " +
                        (st.ConsistencyCapNow > 0
                            ? Money(st.ConsistencyCapNow) + " cap"
                            : "no cap yet (no profit to share)");

            _constraint.Text = line;
            // Gold, never red: on LucidPro a blown consistency rule blocks the PAYOUT, not the account.
            _constraint.Foreground = st.ConsistencyOk ? Muted : Gold;
            _constraint.Visibility = line.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        // The mockup's chip reads "50K PRO EVAL": LucidPro's own name for the phase whose rules are in
        // force. Deliberately not PhaseLabel, which names the COCKPIT state (and says REPLAY).
        static string RuleLabel(Phase p)
        {
            switch (p)
            {
                case Phase.LiveSim: return "PRO FUNDED";
                case Phase.Live:    return "LIVE";
                default:            return "PRO EVAL";
            }
        }

        // The tab owns the numbers; CurveChart owns the pixels. This is the whole seam: the frame is
        // projected into flat CurvePoints here, so the chart never sees a PropRules, an Account or a rule.
        void PushToChart(CockpitFrame f)
        {
            if (!f.Tracked) { _chart.SetUntracked(); return; }

            // BOTH views draw the target. The Session view briefly did not: a challenge target thousands
            // of dollars from an intraday range of a few hundred has to be forced into the y-range, and
            // that squeezed a quiet replayed day into a few percent of the plot. The trader overruled it
            // on 2026-08-22 - he wants the dashed green line where the target is, in every view, the way
            // his funding dashboard draws it, and a compressed quiet day is the price he is paying
            // knowingly. Do not "fix" this back without asking him.
            double target = GoalBalance(f.Rules);
            // The buffer line is a funded-phase idea only: it is the not-withdrawable floor of the payout
            // maths, and drawing it on an evaluation would invent a level that rule set does not have.
            double buffer = f.Rules.Phase == Phase.LiveSim ? f.Rules.Buffer : 0.0;

            List<CurvePoint> pts = new List<CurvePoint>();

            if (f.SessionView)
            {
                // A leading point at the day's opening balance, so the session curve starts where the day
                // started instead of at the first fill - and so a day with no fills yet still draws.
                CurvePoint open = new CurvePoint();
                open.Label = "open";
                open.Balance = f.DayOpenBalance;
                open.Floor = f.State.Floor;
                pts.Add(open);

                for (int i = 0; i < f.Session.Count; i++)
                {
                    SessionFill sf = f.Session[i];
                    CurvePoint p = new CurvePoint();
                    p.Label = sf.TimeEt.ToString("HH:mm", CultureInfo.InvariantCulture);
                    p.Balance = f.DayOpenBalance + sf.Balance;
                    // Flat across the session on purpose: an end-of-day trailing floor ratchets on the
                    // CLOSE, never intraday, so the red line the trader is trading against today is one
                    // level all day. A sloping intraday floor would be a lie about the rule.
                    p.Floor = f.State.Floor;
                    p.DayPnL = sf.ProfitCurrency;
                    p.Fills = 1;
                    p.Detail = sf.Instrument + Dot + sf.Side + " " + sf.Quantity.ToString(CultureInfo.InvariantCulture);
                    pts.Add(p);
                }

                // Session view gets the same live endpoint as Challenge view, for the same reason: with a
                // position open the last FILL is not "now", so hovering the rightmost point returned
                // realized room while the rail card showed live room - two numbers for one moment. Only
                // appended when it would actually differ, so a flat session is not given a duplicate point
                // sitting on top of its last fill.
                double liveNow = f.EquityBasis ? f.LiveEquity : f.LiveBalance;
                double lastPlotted = pts[pts.Count - 1].Balance;
                if (Math.Abs(liveNow - lastPlotted) > 0.005)
                {
                    CurvePoint now = new CurvePoint();
                    now.Label = "now";
                    now.Balance = liveNow;
                    now.Floor = f.State.Floor;
                    now.DayPnL = liveNow - lastPlotted;   // the open position, which is the whole gap
                    now.Fills = 0;
                    now.Detail = "open position";
                    pts.Add(now);
                }
            }
            else
            {
                for (int i = 0; i < f.Days.Count; i++)
                {
                    TradingDay d = f.Days[i];
                    CurvePoint p = new CurvePoint();
                    p.Label = d.Date.ToString("MMM d", CultureInfo.InvariantCulture);
                    p.Balance = d.ClosingBalance;
                    p.Floor = d.FloorInForce;      // the floor that applied DURING that day, not the one its own close set
                    p.DayPnL = d.RealizedPnL;
                    p.Fills = d.Fills;
                    pts.Add(p);
                }

                // The day in progress is not a ledger row - it has not closed - but it is the endpoint the
                // trader is actually looking at, so it is drawn from the live state.
                // Fed on the binding's BREACH BASIS, not on the realized balance. Two symptoms, one
                // cause: the gold endpoint froze while a position was open although ROOM TO FLOOR kept
                // moving, and the chart's hover computes room as Balance - Floor, so a $300 open loss
                // printed $2,000 in the tooltip and $1,700 on the card on the same screen. A completed
                // day's equity IS its closing balance, so every earlier point is unaffected.
                CurvePoint live = new CurvePoint();
                live.Label = f.TradingDate.ToString("MMM d", CultureInfo.InvariantCulture);
                live.Balance = f.EquityBasis ? f.LiveEquity : f.LiveBalance;
                live.Floor = f.State.Floor;
                live.DayPnL = f.State.DayPnL;
                live.Fills = f.FillsToday;
                pts.Add(live);
            }

            // The daily-limit LEVEL, not the limit itself, exactly like target and buffer are levels.
            // 0 means the rule is off or disarmed above the Initial Trail Balance, and the chart draws
            // no line for it.
            _chart.SetSeries(pts, target, buffer, target > 0 ? Money(target) : null, f.SessionView, f.DailyLimitLevel);
        }

        // MIRRORS ChallengeEngine's goal selection exactly (its "goal and days" block). Duplicated rather
        // than exposed on ChallengeState because the state carries the REMAINING dollars, not the level -
        // and a green line drawn at a different number than the To-target card would be the worst possible
        // bug in this window. If the engine's branch ever moves, this moves with it.
        static double GoalBalance(PropRules r)
        {
            if (r == null) return 0.0;
            if (r.Phase == Phase.LiveSim)
                return (r.Buffer > 0 || r.MinPayout > 0) ? r.PayoutBalance : 0.0;
            return r.ProfitTarget > 0 ? r.TargetBalance : 0.0;
        }

        void ApplyUntracked()
        {
            for (int i = 0; i < _chips.Length; i++) SetChip(_chips[i], "-");
            for (int i = 0; i < _cards.Length; i++) _cards[i].Root.Visibility = Visibility.Collapsed;
            _untrackedCard.Root.Visibility = Visibility.Visible;
            _constraint.Visibility = Visibility.Collapsed;   // "nothing is being measured", for a third time

            // Two different states needing two different sentences. With no account selected there is
            // nothing to bind and Configure is disabled, so telling the trader to press it points him at
            // a dead control - which is precisely what the first frame of the first evening looked like.
            if (_account == null)
                SetCard(_untrackedCard, "UNTRACKED", "No account",
                    "Pick an account in the box above, then press Configure.", Dim);
            else
                SetCard(_untrackedCard, "UNTRACKED", "Not measured",
                    "No challenge is bound to this account. Nothing is measured and nothing is written to disk. " +
                    "Press Configure to bind it.", Dim);
        }

        // Configure and the combo's placeholder answer one question - is there an account to configure
        // at all? _account changes in exactly one place, so this is called from exactly two: the initial
        // layout, and that place.
        void RefreshAccountAffordances()
        {
            bool has = _account != null;
            _configure.IsEnabled = has;
            _accountHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        }

        void RenderWarnings(CockpitFrame f)
        {
            if (f.Warnings == null || f.Warnings.Length == 0)
            {
                _warnText.Visibility = Visibility.Collapsed;
                return;
            }
            // Unverified rules are surfaced as caveats, never modelled silently (spec section 7). Still
            // capped - the rail scrolls, but the cards have to stay reachable - at six rather than four,
            // and the overflow is no longer a dead end: the tooltip carries every warning, in order.
            int n = Math.Min(f.Warnings.Length, 6);
            string[] shown = new string[n];
            for (int i = 0; i < n; i++) shown[i] = "- " + f.Warnings[i];
            _warnText.Text = string.Join("\n", shown) +
                             (f.Warnings.Length > n ? "\n- (+" + (f.Warnings.Length - n) + " more - hover for all)" : "");
            _warnText.Visibility = Visibility.Visible;

            // Rebuilt only when the text actually changes: Render runs on every recompute, and a fresh
            // TextBlock per tick for a tooltip nobody is hovering is pure garbage. No Foreground set on
            // purpose - it inherits from whatever ToolTip style NinjaTrader's skin is using.
            string full = string.Join("\n\n", f.Warnings);
            if (full != _warnFull)
            {
                _warnFull = full;
                _warnText.ToolTip = new TextBlock { Text = full, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
            }
        }

        // ---- enforcement ----------------------------------------------------------------------------
        //
        // Until this change the add-on could not reach the account BY CONSTRUCTION. It now acts the way
        // the firm does: when a rule LATCHES it closes the positions, cancels the working orders and
        // stops the strategies on that account. Everything that touches the platform lives in
        // Enforcer.cs; what lives here is the DECISION, which is the half with all the ways to be wrong:
        //
        //   * only on an account that carries a binding, and only when THAT binding is armed. Every
        //     binding is warn-only until a human arms it, including every binding already on disk.
        //   * only on the EDGE - the tick a latch goes from not-set to set. A latch already recorded,
        //     including one read back from the ledger after a restart, never fires again. A restart
        //     must not close a position the trader has since deliberately reopened.
        //   * never on a degraded frame. A store error, a fallback time zone, a non-USD account or a
        //     non-finite number in the ledger all mean the figures under the verdict cannot be trusted,
        //     and a tool that closes positions on bad data is worse than one that does nothing. It says
        //     so in the banner and in the log, and it does not act.
        //   * the record is written to the ledger BEFORE the platform is touched, so a crash between the
        //     two leaves a latch that is already recorded rather than one that fires again on the next
        //     start.
        //   * Enforcer.Enforce runs on a THREAD-POOL thread, never on this window's dispatcher:
        //     SetState(State.Terminated) runs the target strategy's OnStateChange synchronously on the
        //     calling thread, and that is third-party code that may block for as long as it likes.
        void MaybeEnforce(CockpitFrame f, DateTime platformNow)
        {
            ConfirmEnforcement(f, platformNow);

            string kind = f.State.Verdict == Verdict.Breached     ? KindBreach
                        : f.State.Verdict == Verdict.DailyLockout ? KindDayLock
                        : null;
            if (kind == null) return;
            EnforcementRecord prior = FindLatch(kind, f.TradingDate);
            if (!CanSupersede(prior)) return;   // already latched and already handled as well as it can be
            OnLatchEdge(f, kind, platformNow, prior);
        }

        // Whether a latch that is ALREADY recorded may be acted on again.
        //
        // The dedupe used to be "a record exists, therefore we are done", and that silently burned the
        // one chance an armed account gets. A challenge breach is terminal and fires ONCE: if it landed
        // during a single tick of degraded data, or while the binding was still warn-only, the held-back
        // record blocked every later attempt - that session and, because the record is persisted, every
        // session after it. The comment in the held-back message promises it "will act on the next rule
        // break"; for a once-only breach there is no next one.
        //
        // So: a record that actually reached the platform is final. One that did not is a placeholder
        // that a change in circumstances is allowed to supersede - and only a change that would produce
        // a DIFFERENT outcome, so warn-only does not re-fire itself four times a second.
        bool CanSupersede(EnforcementRecord prior)
        {
            if (prior == null) return true;                       // a fresh edge
            if (prior.Action == ActRunning || prior.Action == ActDone || prior.Action == ActUnknown)
                return false;   // it reached the platform, or may have; never send a second one
            CockpitFrame f = _frame;
            if (f == null) return false;
            if (!ArmedFor(f)) return false;                       // still warn-only: nothing has changed
            if (prior.Action == ActHeld && f.Degraded) return false;  // still cannot vouch for the data
            return true;                                          // armed now, and able to act
        }

        // One definition of "armed", used by the edge test and by the action that follows it, so the two
        // can never disagree about whether this account is allowed to be touched.
        bool ArmedFor(CockpitFrame f)
        {
            AccountBinding b = _binding;
            if (f == null || !f.Tracked || _account == null || b == null) return false;
            // A frame from a previous binding must never authorise an action under the current one. This
            // costs nothing in the normal case - Recompute rebuilds the frame in the same tick that
            // bumps the revision - and it closes the window where Recompute threw AFTER the triggers
            // advanced, leaving last binding's frame paired with this binding's account.
            if (f.BindingRev != _bindingRev) return false;
            return b.Enforcement == EnforcementMode.Armed;
        }

        // `prior` is a placeholder record this edge is superseding (warn-only, or held back on degraded
        // data) - see CanSupersede. It is UPDATED IN PLACE rather than joined by a second record, so the
        // ledger keeps one row per latch and the banner cannot show the same breach twice.
        void OnLatchEdge(CockpitFrame f, string kind, DateTime platformNow, EnforcementRecord prior)
        {
            EnforcementRecord rec = prior != null ? prior : new EnforcementRecord();
            rec.AtUtc       = DateTime.UtcNow;
            rec.TradingDate = f.TradingDate;
            rec.Kind        = kind;
            // The number the trader will want tomorrow: how far under the floor he went, or what the day
            // actually cost him. Taken at the instant it tripped, never recomputed afterwards.
            rec.Value       = kind == KindBreach ? f.State.RoomToFloor : f.State.DayPnL;
            rec.Headline    = f.State.Headline;

            AccountBinding b = _binding;
            Account acct = _account;
            bool armed = f.Tracked && acct != null && b != null && b.Enforcement == EnforcementMode.Armed;

            if (!armed)
            {
                rec.Action = ActWarn;
                rec.Detail = "Enforcement is set to WARN ONLY for this account, so nothing was sent to the platform. " +
                             "Close your position and stop your strategies yourself.";
            }
            else if (f.Degraded)
            {
                rec.Action = ActHeld;
                rec.Detail = "HELD BACK. Enforcement is armed for this account, but this panel is running on data it " +
                             "cannot vouch for: " + (f.DegradedReason ?? "unknown") + ". Nothing was sent to the platform. " +
                             "Close the account by hand, fix what is named here, and it will act on the next rule break.";
                NinjaTrader.Code.Output.Process("[FundedPath] ENFORCEMENT HELD BACK - " + rec.Detail,
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
            }
            else
            {
                rec.Action = ActRunning;
                rec.Detail = "Enforcement started at " + EtStamp(rec.AtUtc) + ". Waiting for the platform.";
            }

            if (prior == null) _enforcements.Add(rec);   // superseding one: it is already in the list
            PersistEnforcement(f.TradingDate);          // on disk BEFORE anything reaches the account

            if (rec.Action == ActRunning)
            {
                _enforcingRec = rec;
                _enforceRetryDeadline = platformNow.AddSeconds(EnforceConfirmSeconds);
                RunEnforcer(acct, rec, rec.Headline + " (" + kind + ")", platformNow);
            }

            RenderBanner(f);
        }

        // The "keep it flat" follow-up. Account.Flatten is fire and forget - it returns before the fills
        // land - and a flatten that was momentarily blocked changes nothing on screen, so the only honest
        // way to know it took is to re-read the account. This latches on CONFIRMED flat, never on
        // attempted: the house learned that the expensive way, when a blocked flatten burned a
        // once-per-day latch and the open position was never retried.
        void ConfirmEnforcement(CockpitFrame f, DateTime platformNow)
        {
            if (_enforceRetryDeadline == DateTime.MinValue) return;

            Account acct = _account;
            string detail;
            if (acct == null || Enforcer.IsQuiet(acct, out detail))
            {
                _enforceRetryDeadline = DateTime.MinValue;
                if (_enforcingRec != null)
                {
                    _enforcingRec.Detail += "\nCONFIRMED at " + EtStamp(DateTime.UtcNow) +
                                            ": no open position and no working order remain.";
                    PersistEnforcement(f.TradingDate);
                    RenderBanner(f);
                }
                return;
            }

            // Both clocks here are the PLATFORM clock, so a replay running at 24x retries at 24x too.
            // A rewind moves it backwards and stalls this window until the account reads quiet - which a
            // rewind makes true anyway, because it wipes the positions.
            if (platformNow >= _enforceRetryDeadline)
            {
                _enforceRetryDeadline = DateTime.MinValue;
                if (_enforcingRec != null)
                {
                    _enforcingRec.Detail += "\nGAVE UP CONFIRMING after " + EnforceConfirmSeconds + "s - " + detail +
                                            ". CLOSE IT BY HAND.";
                    PersistEnforcement(f.TradingDate);
                    RenderBanner(f);
                }
                NinjaTrader.Code.Output.Process(
                    "[FundedPath] enforcement could not be confirmed flat: " + detail + " - CLOSE IT BY HAND.",
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                return;
            }

            if ((platformNow - _enforceLastAttempt).TotalSeconds >= EnforceRetrySeconds)
                RunEnforcer(acct, _enforcingRec, "still not flat after the enforcement above", platformNow);
        }

        void RunEnforcer(Account acct, EnforcementRecord rec, string reason, DateTime platformNow)
        {
            if (acct == null || rec == null) return;

            // Exactly one run at a time. Two overlapping sweeps would fight over the same position, and
            // the second can go out AFTER the first one's fill - which is how a flat account ends up
            // short. Interlocked rather than a lock: this is read from the UI thread and released from a
            // thread-pool one.
            if (System.Threading.Interlocked.CompareExchange(ref _enforceBusy, 1, 0) != 0) return;
            _enforceLastAttempt = platformNow;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string transcript;
                try { transcript = Enforcer.Enforce(acct, reason); }
                catch (Exception ex)
                {
                    // Enforce is written never to throw. If it ever does, that is the single most
                    // important line in the log, not a swallowed exception on a background thread.
                    transcript = "THE ENFORCER ITSELF FAILED: " + ex.Message + " - CHECK THE ACCOUNT BY HAND.";
                }
                finally { System.Threading.Interlocked.Exchange(ref _enforceBusy, 0); }

                try
                {
                    Dispatcher.InvokeAsync((Action)delegate
                    {
                        if (_dead) return;             // the tab was closed while the platform was working
                        rec.Action = ActDone;
                        rec.Detail = transcript;
                        CockpitFrame cur = _frame;
                        PersistEnforcement(cur != null ? cur.TradingDate : _lastTradingDate);
                        if (cur != null) RenderBanner(cur);
                    });
                }
                catch { /* the window's dispatcher is gone; the transcript is already in the Output tab */ }
            });
        }

        // A challenge breach is latched for the whole challenge - it is what the firm recorded, and no
        // new day undoes it. A daily lockout belongs to ONE trading day and is gone when the day rolls,
        // which is what the rule itself does.
        EnforcementRecord FindLatch(string kind, DateTime today)
        {
            for (int i = _enforcements.Count - 1; i >= 0; i--)
            {
                EnforcementRecord r = _enforcements[i];
                if (r == null || r.Kind != kind) continue;
                if (kind == KindBreach || r.TradingDate == today) return r;
            }
            return null;
        }

        // What the pill and the banner show. A breach outranks a lockout: an account that is out is out,
        // whatever today's P&L happens to be doing.
        EnforcementRecord ActiveLatch(DateTime today)
        {
            EnforcementRecord breach = FindLatch(KindBreach, today);
            return breach != null ? breach : FindLatch(KindDayLock, today);
        }

        void PersistEnforcement(DateTime today)
        {
            if (_ledgerPath == null || _ledger == null) return;
            try { SaveLedger(today); }
            catch (Exception ex)
            {
                // Not fatal to the session - the latch is held in memory, so nothing enforces twice
                // before the next restart - but it must be said out loud, because after a restart it
                // could. It also degrades the frame, which blocks any further enforcement until fixed.
                _storeError = "Could not write the enforcement record to the day ledger: " + ex.Message;
                NinjaTrader.Code.Output.Process("[FundedPath] enforcement record not saved: " + ex,
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
            }
        }

        // The thing he said was missing. He hit his daily limit, the window flashed a message and went
        // back to ON TRACK, and the record of the worst moment of his day was gone before he looked up.
        // This carries what happened, when, at what value, and what this add-on did about it - and it
        // stays until the day rolls or he resets it.
        void RenderBanner(CockpitFrame f)
        {
            EnforcementRecord rec = f == null ? null : ActiveLatch(f.TradingDate);
            if (rec == null)
            {
                _banner.Visibility = Visibility.Collapsed;
                _bannerReset.Visibility = Visibility.Collapsed;
                _bannerFull = "";
                return;
            }

            bool breach = rec.Kind == KindBreach;
            Color c = breach ? BreachC : LockC;
            // Per-frame and unfrozen, like the phase brush: this repaints only when something latched or
            // the transcript arrived, not at 4 Hz.
            _banner.Background  = new SolidColorBrush(Color.FromArgb(30, c.R, c.G, c.B));
            _banner.BorderBrush = new SolidColorBrush(Color.FromArgb(140, c.R, c.G, c.B));
            _bannerText.Foreground = new SolidColorBrush(c);

            string what = breach
                ? "BREACHED" + Dot + Money(Math.Max(0.0, -rec.Value)) + " below the floor"
                : "DAY LOCKED" + Dot + Signed(rec.Value) + " on the day";

            _bannerText.Text = what + Dot + EtStamp(rec.AtUtc) + Dot + ActionLine(rec);
            _banner.Visibility = Visibility.Visible;
            // Only a challenge breach can be reset. A daily lockout clears itself when the day rolls,
            // exactly as the rule does, so a button for it would be a button that lies.
            _bannerReset.Visibility = breach ? Visibility.Visible : Visibility.Collapsed;

            string full = what + "\n" + EtStamp(rec.AtUtc) + " - " + (rec.Headline ?? "") + "\n\n" + (rec.Detail ?? "");
            if (full != _bannerFull)
            {
                _bannerFull = full;
                _banner.ToolTip = new TextBlock { Text = full, TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };
            }
        }

        // One line for the banner: what this add-on actually did, never a euphemism. The full transcript
        // is on the tooltip.
        static string ActionLine(EnforcementRecord rec)
        {
            if (rec.Action == ActWarn)    return "warn only - nothing was sent to your account.";
            if (rec.Action == ActHeld)    return "ENFORCEMENT HELD BACK - " + FirstLine(rec.Detail);
            if (rec.Action == ActRunning) return "enforcing...";
            if (rec.Action == ActUnknown) return "INTERRUPTED - " + FirstLine(rec.Detail);
            return Summarise(rec.Detail);
        }

        // The transcript's own verdict lines, in order of urgency: anything the trader still has to do
        // by hand beats the fact that the rest worked.
        static string Summarise(string transcript)
        {
            if (string.IsNullOrEmpty(transcript)) return "";
            string[] lines = transcript.Split('\n');
            string result = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                if (s.StartsWith("ACTION NEEDED:", StringComparison.Ordinal) ||
                    s.StartsWith("GAVE UP", StringComparison.Ordinal) ||
                    s.StartsWith("THE ENFORCER", StringComparison.Ordinal) ||
                    s.StartsWith("UNEXPECTED FAILURE:", StringComparison.Ordinal))
                    return s;
                // Last one wins: the confirmation is appended after the transcript was written.
                if (s.StartsWith("RESULT:", StringComparison.Ordinal) || s.StartsWith("CONFIRMED", StringComparison.Ordinal))
                    result = s;
            }
            return result.Length > 0 ? result : FirstLine(transcript);
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.IndexOf('\n');
            return i < 0 ? s : s.Substring(0, i);
        }

        // The trip time in Eastern, which is the zone every rule in this add-on is measured in. The
        // record stores UTC, so the file is unambiguous across machines and zones.
        string EtStamp(DateTime utc)
        {
            try
            {
                TimeZoneInfo et = EasternZone();
                if (et != null)
                    return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), et)
                           .ToString("MMM d HH:mm", CultureInfo.InvariantCulture) + " ET";
            }
            catch { /* fall through and say which zone it is, rather than mislabel it */ }
            return utc.ToString("MMM d HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }

        static bool NonFinite(double v) { return double.IsNaN(v) || double.IsInfinity(v); }

        // "Are these numbers sound enough to close a position on?" Anything here is a no. The engine
        // scrubs a NaN ledger row to zero and warns, so the state can read clean over a corrupt file -
        // which is why the raw rows are checked too, not just the computed state.
        static bool AnyNonFinite(ChallengeState st, List<TradingDay> days, double unrealized)
        {
            if (NonFinite(unrealized)) return true;
            if (st != null && (NonFinite(st.Balance) || NonFinite(st.Equity) || NonFinite(st.Floor) ||
                               NonFinite(st.RoomToFloor) || NonFinite(st.DayPnL) || NonFinite(st.BestDayPnL)))
                return true;
            if (days != null)
                for (int i = 0; i < days.Count; i++)
                    if (days[i] != null && NonFinite(days[i].RealizedPnL)) return true;
            return false;
        }

        // A fresh latch on ANY failure, including a missing file: this is the first day of a challenge
        // as often as it is a problem, and a latch invented out of an unreadable attribute would tell the
        // trader he is out when he is not.
        LatchedState LoadLatch(string path)
        {
            LatchedState latch = new LatchedState();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return latch;
                XDocument doc = XDocument.Load(path);
                if (doc.Root == null) return latch;
                XElement e = doc.Root.Element("Latch");
                if (e == null) return latch;

                latch.ChallengeBreached = string.Equals((string)e.Attribute("breached"), "true", StringComparison.OrdinalIgnoreCase);
                latch.BreachedOn       = ParseYmd((string)e.Attribute("on"));
                latch.BreachedAt       = ParseR((string)e.Attribute("at"));
                latch.BreachedFloor    = ParseR((string)e.Attribute("floor"));
                latch.DailyLockoutDate = ParseYmd((string)e.Attribute("lockDate"));
                latch.DailyLockoutAt   = ParseR((string)e.Attribute("lockAt"));
            }
            catch (Exception ex)
            {
                // Say so rather than starting clean in silence: a dropped breach latch is the panel
                // forgetting the single most important thing it knows about this account.
                latch = new LatchedState();
                _storeError = "Could not read the latched state (" + path + "): " + ex.Message +
                              " - a breach recorded earlier will not be shown until it is repaired.";
            }
            return latch;
        }

        // Empty rather than "0001-01-01" for MinValue, so an unset date reads as unset in the file too.
        static string Ymd(DateTime d)
        {
            return d == DateTime.MinValue ? "" : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        static DateTime ParseYmd(string s)
        {
            DateTime d;
            return DateTime.TryParseExact(s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                ? DateTime.SpecifyKind(d, DateTimeKind.Unspecified)
                : DateTime.MinValue;
        }

        static double ParseR(string s)
        {
            double v;
            return double.TryParse(s ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0.0;
        }

        static EnforcementRecord ReadEnforcement(XElement e)
        {
            try
            {
                DateTime at;
                // RoundtripKind alone, never combined with AdjustToUniversal: the combination throws at
                // runtime, and that would take the whole ledger down on load.
                if (!DateTime.TryParse((string)e.Attribute("at") ?? "", CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out at))
                    return null;
                DateTime date;
                if (!DateTime.TryParseExact((string)e.Attribute("date") ?? "", "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    return null;
                double v;
                double.TryParse((string)e.Attribute("value") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out v);

                EnforcementRecord rec = new EnforcementRecord();
                rec.AtUtc = at.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : at.ToUniversalTime();
                rec.TradingDate = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
                rec.Kind     = (string)e.Attribute("kind") ?? "";
                rec.Value    = v;
                rec.Action   = (string)e.Attribute("action") ?? "";
                rec.Headline = (string)e.Attribute("headline") ?? "";
                rec.Detail   = e.Value ?? "";
                return rec;
            }
            catch { return null; }   // one unreadable record is skipped; the rest of the ledger still loads
        }

        // ---- interaction ----------------------------------------------------------------------------

        void OnResetBreachClick(object sender, RoutedEventArgs e)
        {
            CockpitFrame f = _frame;
            if (f == null) return;
            if (FindLatch(KindBreach, f.TradingDate) == null) return;

            string name = _account != null ? _account.DisplayName : "this account";
            string question =
                "Clear the latched breach on " + name + "?\n\n" +
                "This clears what THIS PANEL shows and records. It does NOT undo anything your firm has " +
                "already recorded: if they logged the breach, the account is still breached with them, and " +
                "this button cannot give it back.\n\n" +
                "It does not reopen a position or restart a strategy either. And if the account is still " +
                "under its floor, the panel will latch again on the very next tick - and act again, if " +
                "enforcement is armed for this account.";

            MessageBoxResult answer;
            try
            {
                Window owner = Window.GetWindow(this);
                answer = owner != null
                    ? MessageBox.Show(owner, question, "Funded Path - reset the latched breach", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                    : MessageBox.Show(question, "Funded Path - reset the latched breach", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            }
            catch { return; }   // no dialog, no reset: this is not a decision to take on his behalf

            if (answer != MessageBoxResult.OK) return;

            for (int i = _enforcements.Count - 1; i >= 0; i--)
                if (_enforcements[i] != null && _enforcements[i].Kind == KindBreach) _enforcements.RemoveAt(i);

            // A fresh latch is how the engine's own contract says to reset one - there is no Clear() on
            // LatchedState on purpose, so that clearing a live breach is always an explicit act.
            _latched = new LatchedState();
            PersistEnforcement(f.TradingDate);
            NinjaTrader.Code.Output.Process("[FundedPath] the latched breach on " + name +
                " was cleared by hand. Nothing at the firm changed.", NinjaTrader.NinjaScript.PrintTo.OutputTab1);
            _forceRecompute = true;
            RenderBanner(f);
        }

        void SetView(bool session)
        {
            _sessionView = session;
            StyleSegment(_challengeBtn, !session);
            StyleSegment(_sessionBtn, session);
            _forceRecompute = true;   // the frame carries the view, so the chart needs a fresh one
        }

        void OnConfigureClick(object sender, RoutedEventArgs e)
        {
            Account acct = _account;
            if (acct == null) return;

            // Re-read the store from disk FIRST. Another tab of this window may have bound a DIFFERENT
            // account since this one loaded - the window's "+" makes a second tab on one click - and the
            // dialog rewrites the whole file from the instance it is handed, so a stale instance silently
            // drops that binding and reverts that account to Untracked. A store that FAILED to load is not
            // swapped in: writing an empty one over a file that is merely locked is the data loss this is
            // meant to prevent.
            BindingStore fresh = BindingStore.Load(_bindingsPath);
            if (string.IsNullOrEmpty(fresh.LastLoadError)) _store = fresh;

            // The dialog owns the write: it mutates the SHARED BindingStore and saves it, then closes. It
            // never sets DialogResult, so the return of ShowDialog says nothing - re-reading the store
            // afterwards is what tells us whether anything changed, and it is the same one line for saved,
            // removed and cancelled alike.
            BindingDialog dlg = new BindingDialog(ProviderName(acct), acct.DisplayName, _store, _bindingsPath);
            try { dlg.Owner = Window.GetWindow(this); }
            catch { /* a tab not yet inside a window still gets a usable ownerless dialog */ }
            dlg.ShowDialog();

            // Re-key off the CURRENT account rather than the dialog's AccountKey: modal or not, the account
            // this tab is bound to is the only thing this tab should reload.
            LoadBindingFor(_account);

            // The account may have just become tracked - or stopped being tracked - and an unbound account
            // carries no subscription at all. Unsubscribe is a no-op when there is nothing attached and
            // Subscribe returns early when there is no rulebook, so this is correct in all four directions.
            Unsubscribe();
            Subscribe();

            _chart.Clear();                            // the previous binding's curve is not this one's
            _forceRecompute = true;
        }

        // ---- labels ---------------------------------------------------------------------------------

        static CockpitPhase PhaseOf(Account acct, AccountBinding binding, PropRules rules)
        {
            // No binding, or a binding whose firm/size is not modelled yet, is Untracked. That is the
            // default for every account and the only state that needs no configuration.
            if (binding == null || rules == null) return CockpitPhase.Untracked;

            // A bound account on the Playback connection is a rehearsal, whatever phase the binding says.
            // Its ledger key already carries Provider, so it can never move the real challenge's high-water
            // mark; this only stops the DISPLAY from claiming the rehearsal is the real thing.
            if (IsPlayback(acct)) return CockpitPhase.Replay;

            switch (binding.Phase)
            {
                case Phase.LiveSim: return CockpitPhase.LiveSim;
                case Phase.Live:    return CockpitPhase.Live;
                default:            return CockpitPhase.Evaluation;
            }
        }

        static Color ColorFor(CockpitPhase p)
        {
            switch (p)
            {
                case CockpitPhase.Replay:     return ReplayC;
                case CockpitPhase.Evaluation: return EvalC;
                case CockpitPhase.LiveSim:    return LiveSimC;
                case CockpitPhase.Live:       return LiveC;
                default:                      return UntrackC;
            }
        }

        static string PhaseLabel(CockpitPhase p)
        {
            switch (p)
            {
                case CockpitPhase.Replay:     return "REPLAY";
                case CockpitPhase.Evaluation: return "EVALUATION";
                case CockpitPhase.LiveSim:    return "LIVE SIM";
                case CockpitPhase.Live:       return "LIVE";
                default:                      return "UNTRACKED";
            }
        }

        // The one-line explanation of what the state actually means, next to the chip. Without it the
        // difference between LIVE SIM and LIVE - which is the difference between a rehearsal and real
        // money - is one word in a corner.
        static string NoteFor(CockpitPhase p)
        {
            switch (p)
            {
                case CockpitPhase.Replay:
                    return "Market Replay rehearsal. Kept in its own ledger; it cannot move the real challenge.";
                case CockpitPhase.Evaluation:
                    return "Evaluation. Reach the target without closing a day below the floor.";
                case CockpitPhase.LiveSim:
                    return "Funded sim. What is measured is the payout test, not the pass test.";
                case CockpitPhase.Live:
                    return "Live account. Real money, and the floor is the only thing that matters.";
                default:
                    return "No challenge bound. Nothing is measured and nothing is recorded.";
            }
        }

        static string SourceLabel(CockpitPhase p)
        {
            return p == CockpitPhase.Replay ? "Playback" : PhaseLabel(p);
        }

        static string FirmLabel(Firm f)
        {
            switch (f)
            {
                case Firm.Lucid:            return "Lucid Trading";
                case Firm.MyFundedFutures:  return "MyFundedFutures";
                case Firm.ApexTrader:       return "Apex Trader";
                case Firm.TopstepTrader:    return "Topstep";
                default:                    return f.ToString();
            }
        }

        static string SizeLabel(int size)
        {
            return size >= 1000
                ? (size / 1000).ToString(CultureInfo.InvariantCulture) + "K"
                : size.ToString(CultureInfo.InvariantCulture);
        }

        static Brush VerdictBrush(Verdict v)
        {
            switch (v)
            {
                case Verdict.Breached:       return Red;
                case Verdict.DailyLockout:   return Gold;
                case Verdict.Passed:         return Green;
                case Verdict.PayoutEligible: return Green;
                case Verdict.Untracked:      return Dim;
                default:                     return Gold;
            }
        }

        // Whole dollars unless the figure genuinely carries cents. Money is what the trader compares against
        // his dashboard, so "$390.00" where the dashboard says "$390" reads as a different number.
        static string Money(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "n/a";
            double a = Math.Abs(v);
            string fmt = Math.Abs(a - Math.Round(a)) < 0.005 ? "N0" : "N2";
            return (v < 0 ? "-$" : "$") + a.ToString(fmt, CultureInfo.InvariantCulture);
        }

        static string Signed(double v)
        {
            return (v > 0 ? "+" : "") + Money(v);
        }

        // ---- small WPF builders ---------------------------------------------------------------------

        static Brush Frozen(byte r, byte g, byte b)
        {
            SolidColorBrush br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();   // a static unfrozen brush belongs to whichever window created it and throws in the second window's render pass
            return br;
        }

        sealed class Chip
        {
            public Border Root;
            public TextBlock Value;
        }

        static Chip MakeChip(string label)
        {
            Chip c = new Chip();
            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };
            TextBlock l = new TextBlock
            {
                Text = label, FontFamily = Sans, FontSize = 10, Foreground = Muted,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            c.Value = new TextBlock
            {
                FontFamily = Sans, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = TextCol,
                VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(l);
            sp.Children.Add(c.Value);
            c.Root = new Border
            {
                Child = sp,
                Background = Card,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            return c;
        }

        static void SetChip(Chip c, string value)
        {
            c.Value.Text = string.IsNullOrEmpty(value) ? "-" : value;
        }

        sealed class StatCard
        {
            public Border Root;
            public TextBlock Title;
            public TextBlock Value;
            public TextBlock Sub;
        }

        static StatCard MakeCard()
        {
            StatCard c = new StatCard();
            StackPanel sp = new StackPanel();
            c.Title = new TextBlock { FontFamily = Sans, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Muted };
            c.Value = new TextBlock { FontFamily = Mono, FontSize = 21, Foreground = TextCol, Margin = new Thickness(0, 3, 0, 0) };
            c.Sub   = new TextBlock { FontFamily = Sans, FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            sp.Children.Add(c.Title);
            sp.Children.Add(c.Value);
            sp.Children.Add(c.Sub);
            // Height is left to content on purpose: the sub-line wraps, and a fixed height clips it the
            // moment a rule set has a longer label (RadarTab.cs:257 learned this the expensive way).
            c.Root = new Border
            {
                Child = sp,
                Background = Card,
                BorderBrush = LineSoft,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 0, 10)
            };
            return c;
        }

        static void SetCard(StatCard c, string title, string value, string sub, Brush valueBrush)
        {
            c.Title.Text = title;
            c.Value.Text = value;
            c.Value.Foreground = valueBrush;
            c.Sub.Text = sub;
        }

        // NT8's skin leaves a default Button as grey 3D chrome, which reads as broken against this palette.
        // A flat template with a hover/disabled trigger is the smallest thing that fixes it.
        static void StyleFlatButton(Button b, string text, Brush fg)
        {
            b.Content = text;
            b.FontFamily = Sans;
            b.FontSize = 11;
            b.FontWeight = FontWeights.Bold;
            b.Foreground = fg;
            b.Background = Card;
            b.BorderBrush = Line;
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(12, 5, 12, 5);
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.SnapsToDevicePixels = true;
            b.Template = PillTemplate(6);
        }

        static void StyleSegment(Button b, bool active)
        {
            b.Background = active ? Blue : Brushes.Transparent;
            b.Foreground = active ? Brushes.White : Muted;
            b.BorderThickness = new Thickness(0);
            b.Template = PillTemplate(5);
            b.Padding = new Thickness(12, 4, 12, 4);
            b.Margin = new Thickness(0);
        }

        static System.Windows.Controls.ControlTemplate PillTemplate(double radius)
        {
            System.Windows.Controls.ControlTemplate t = new System.Windows.Controls.ControlTemplate(typeof(Button));
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            FrameworkElementFactory cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;
            Trigger dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            t.Triggers.Add(dis);
            return t;
        }

        // ---- NTTabPage members ----------------------------------------------------------------------

        // Access modifiers are not interchangeable here: Cleanup is public, the other three protected.
        public override void Cleanup()
        {
            // FIRST statement, before anything can yield: a PopulateAccounts already queued on this
            // dispatcher runs after this method returns, and without the flag it re-subscribes this dead
            // tab to the platform's long-lived Account for the rest of the NinjaTrader session.
            _dead = true;

            // Unsubscribe FIRST, then drop the account, so a handler already queued on the connection
            // thread hits its own null/identity guard and drops instead of touching a torn-down tab.
            Unsubscribe();
            // The STATIC ones: these are the handlers that outlive the window on a recompile.
            Account.AccountStatusUpdate    -= OnAccountStatusUpdate;
            Account.SimulationAccountReset -= OnSimulationAccountReset;
            lock (_snapLock) _account = null;
            if (_accountSelectionHandler != null)
            {
                _accounts.SelectionChanged -= _accountSelectionHandler;
                _accountSelectionHandler = null;
            }
            _configure.Click -= OnConfigureClick;
            _bannerReset.Click -= OnResetBreachClick;
            if (_paintTimer != null)
            {
                // Stop AND null: a stopped-but-alive timer still holds its Tick delegate, which holds this
                // tab, which holds every visual in it.
                _paintTimer.Tick -= OnPaintTick;
                _paintTimer.Stop();
                _paintTimer = null;
            }
            _frame = null;
            base.Cleanup();   // always last
        }

        protected override string GetHeaderPart(string variable)
        {
            return _account != null ? _account.DisplayName : "Funded Path";
        }

        protected override void Save(XElement element)
        {
            if (element == null) return;
            element.Add(new XElement("CockpitAccount", _account != null ? _account.DisplayName : ""));
            element.Add(new XElement("CockpitView", _sessionView ? "Session" : "Challenge"));
        }

        protected override void Restore(XElement element)
        {
            if (element == null) return;
            try
            {
                // View first, account second: selecting the account triggers a recompute, and the frame it
                // builds carries the view flag.
                XElement v = element.Element("CockpitView");
                if (v != null) SetView(string.Equals(v.Value, "Session", StringComparison.OrdinalIgnoreCase));

                XElement a = element.Element("CockpitAccount");
                if (a != null && !string.IsNullOrEmpty(a.Value))
                {
                    // The account usually does not exist yet - its connection comes up after the workspace
                    // is restored - so remember the name and let PopulateAccounts honour it when it appears.
                    _pendingRestoreAccount = a.Value;
                    Account found = null;
                    IEnumerable<Account> items = _accounts.ItemsSource as IEnumerable<Account>;
                    if (items != null) found = items.FirstOrDefault(x => SameName(x, a.Value));
                    if (found != null) { _accounts.SelectedItem = found; _pendingRestoreAccount = null; }
                }
            }
            catch (Exception ex)
            {
                // A malformed or hand-edited attribute must not abort the whole Restore - fall back to the
                // defaults, exactly as if the element had been missing.
                NinjaTrader.Code.Output.Process("[FundedPath] restore failed: " + ex.Message,
                    NinjaTrader.NinjaScript.PrintTo.OutputTab1);
            }
        }
    }
}
