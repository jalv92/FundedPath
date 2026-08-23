using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FundedPath.NT
{
    // One plotted point of the growth curve. Deliberately plain numbers: FundedPathTab flattens the
    // engine's TradingDay list (Challenge view) or today's fills (Session view) into these, so the
    // chart has zero knowledge of NinjaTrader account types or of FundedPath.Engine.
    public sealed class CurvePoint
    {
        public string Label   { get; set; }   // x-axis tick + readout header: "Aug 21" or "13:40"
        public double Balance { get; set; }   // the gold curve's value at this point
        public double Floor   { get; set; }   // the minimum balance in force AT this point (stepped)
        public double DayPnL  { get; set; }   // day P&L (Challenge view) / trade P&L (Session view)
        public int    Fills   { get; set; }
        public string Detail  { get; set; }   // Session view only: "MNQ 09-26 . SELL 2". null otherwise
    }

    // Growth curve, drawn entirely in OnRender (spec section 6). No Shape children, no ToolTip,
    // no Popup: the hover readout is painted by this element so it can never outlive the tab.
    public class CurveChart : FrameworkElement
    {
        // ---- palette, locked by docs/images/funded-path-design.png (spec section 6) ----
        // Hardcoded on purpose. These are semantic (gold = balance, red = floor); an NT8 theme brush
        // cannot carry that meaning, arrives unfrozen and owned by another dispatcher, and returns
        // null on a renamed key - which would paint nothing at all with no exception.
        static readonly Color GoldC   = Color.FromRgb(0xF2, 0xB3, 0x3D);
        static readonly Color RedC    = Color.FromRgb(0xFF, 0x54, 0x68);
        static readonly Color GreenC  = Color.FromRgb(0x27, 0xD6, 0x7B);
        static readonly Color BlueC   = Color.FromRgb(0x4A, 0x7D, 0xFF);

        static readonly Brush PanelBg = FrozenBrush(Color.FromRgb(0x0C, 0x13, 0x22));
        static readonly Brush CardBg  = FrozenBrush(Color.FromArgb(242, 0x0A, 0x10, 0x1D));
        static readonly Pen   CardLn  = FrozenPen(Color.FromRgb(0x1B, 0x25, 0x40), 1);
        static readonly Brush Divider = FrozenBrush(Color.FromRgb(0x1B, 0x25, 0x40));
        static readonly Brush TextBr  = FrozenBrush(Color.FromRgb(0xE9, 0xED, 0xF8));
        static readonly Brush MutedBr = FrozenBrush(Color.FromRgb(0x7A, 0x87, 0xA2));
        static readonly Brush DimBr   = FrozenBrush(Color.FromRgb(0x4E, 0x5A, 0x74));
        static readonly Brush GoldBr  = FrozenBrush(GoldC);
        static readonly Brush GoldTxt = FrozenBrush(Color.FromRgb(0xFF, 0xD3, 0x7A));
        static readonly Brush RedBr   = FrozenBrush(RedC);
        static readonly Brush GreenBr = FrozenBrush(GreenC);
        static readonly Brush EndDot  = FrozenBrush(Color.FromRgb(0xFF, 0xD3, 0x7A));   // endpoint token

        // Dash/space are multiples of the pen thickness, so the numbers below are tuned against the
        // mockup at the thickness each pen actually uses - changing a thickness changes the rhythm.
        //
        // The two SERIES pens are round-capped; the straight reference lines are not. On day one the
        // series is a single point, so its figure is one short stub (see BuildSpline) - and a flat cap
        // on a stub that short renders nothing at all. Day one showing no red floor line is the whole
        // product missing from the first frame the trader ever sees.
        static readonly Pen CurvePen  = FrozenPen(GoldC, 2.6, PenLineCap.Round);        // spec: 2.6px gold
        static readonly Pen TargetPen = FrozenDash(GreenC, 1.6, 9, 7);                  // dashed green
        static readonly Pen BufferPen = FrozenDash(BlueC, 1.0, 0.5, 2.5);               // thin dotted blue
        static readonly Pen FloorPen  = FrozenDash(RedC, 1.6, 0.5, 2.0, PenLineCap.Round);   // dotted red
        // The daily loss limit: DASHED red, deliberately the target's own rhythm and thickness in the
        // other colour, because the two of them are the same KIND of thing - one straight level, fixed
        // for the whole session, that the day is traded against.
        //
        // WHY THE TWO REDS DIFFER: dotted is the account floor, dashed is the daily limit. They are two
        // different rules with two different costs - the floor ends your ACCOUNT, this one ends your DAY
        // - so they must never read as the same line drawn twice. The floor also arrives as a smoothed
        // series with a red wash under it, which is a second, coarser cue at a glance; this one is one
        // flat rule, so it is drawn flat.
        static readonly Pen DailyPen  = FrozenDash(RedC, 1.6, 9, 7);                    // dashed red
        static readonly Pen GridPen   = FrozenDash(Color.FromArgb(38, 0xE9, 0xED, 0xF8), 1, 1, 3);
        static readonly Pen CrossPen  = FrozenDash(Color.FromArgb(120, 0x7A, 0x87, 0xA2), 1, 3, 3);

        // Washes and the endpoint halo need a ctor, so they are built and frozen once here.
        static readonly Brush GoldWash;
        static readonly Brush RedWash;
        static readonly Brush EndHalo;

        static readonly Typeface Sans = new Typeface("Segoe UI");
        // Mono for every number (house convention: labels Sans, figures Consolas) - the mockup's
        // gutter and time ticks are tabular, so a changing digit never nudges the label sideways.
        static readonly Typeface Mono = new Typeface(new FontFamily("Consolas"),
                                             FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        static CurveChart()
        {
            // Vertical gradient, default MappingMode.RelativeToBoundingBox: WPF maps it to the FILLED
            // geometry's bounds, so the 34% end always lands on the curve itself and the 0% end on the
            // plot floor - exactly what the mockup measures (alpha 0.33 just under the line).
            var gw = new LinearGradientBrush(Color.FromArgb(87, GoldC.R, GoldC.G, GoldC.B),
                                             Color.FromArgb(0,  GoldC.R, GoldC.G, GoldC.B), 90.0);
            gw.Freeze();
            GoldWash = gw;

            // Red starts a touch above the gold's 87. Equal alpha on two hues does not read as equal
            // brightness: coral over this ink reads dimmer than gold at the same alpha (Radar 2026-07-20).
            var rw = new LinearGradientBrush(Color.FromArgb(92, RedC.R, RedC.G, RedC.B),
                                             Color.FromArgb(0,  RedC.R, RedC.G, RedC.B), 90.0);
            rw.Freeze();
            RedWash = rw;

            var halo = new RadialGradientBrush();
            halo.GradientStops.Add(new GradientStop(Color.FromArgb(120, 0xFF, 0xD3, 0x7A), 0.30));
            halo.GradientStops.Add(new GradientStop(Color.FromArgb(46,  GoldC.R, GoldC.G, GoldC.B), 0.62));
            halo.GradientStops.Add(new GradientStop(Color.FromArgb(0,   GoldC.R, GoldC.G, GoldC.B), 1.0));
            halo.Freeze();
            EndHalo = halo;
        }

        // ---- layout constants (everything else is derived from ActualWidth/ActualHeight) ----
        private const double PadTop      = 14;
        private const double PadRight    = 16;
        private const double PadBottom   = 6;
        private const double GutterGap   = 12;   // gutter text right edge -> plot left edge
        private const double LabelRowGap = 9;    // plot bottom -> x-label top
        private const double AxisSize    = 11;   // gutter + x-tick font size
        private const int    MaxTicks    = 3;   // spec section 6: "two or three round-number labels"
        private const double SoloHalf    = 3;   // half-width of the stub a single-point series draws

        public CurveChart() { ClipToBounds = true; }

        // ---- data (UI-thread confined: only SetSeries/Clear/SetUntracked and OnRender touch it) ----
        private IReadOnlyList<CurvePoint> _pts;
        private double _target, _buffer, _dailyLimit;
        private string _targetLabel;
        private bool   _sessionView;
        private bool   _untracked;
        private int    _ver;          // bumped by every data change; part of the layout cache key

        /// <summary>
        /// Push a new series. The caller hands over a list it will not mutate afterwards (FundedPathTab
        /// rebuilds a fresh one on each 4 Hz paint tick), so this keeps the reference rather than copying.
        /// target/buffer/dailyLimit of 0 mean "this phase has none" and their lines are not drawn.
        /// dailyLimit is the LEVEL the daily loss limit sits at - the day's opening balance minus the
        /// limit - not the limit itself, exactly like target and buffer are levels and not distances.
        /// </summary>
        // ponytail: dailyLimit is last and defaults to 0 (= not drawn) purely so this file and
        // FundedPathTab can land in either order without a red tree. The tab always passes it.
        public void SetSeries(IReadOnlyList<CurvePoint> points, double target, double buffer,
                              string targetLabel, bool sessionView, double dailyLimit = 0.0)
        {
            _pts = points; _target = target; _buffer = buffer; _dailyLimit = dailyLimit;
            _targetLabel = targetLabel; _sessionView = sessionView;
            _untracked = false;
            _ver++;
            InvalidateVisual();
        }

        // Radar bug-audit B1: dropping the data is not clearing the screen. Called from the account
        // binding switch, or the old challenge's curve keeps painting under the new account's name.
        public void Clear()
        {
            _pts = null; _target = 0; _buffer = 0; _dailyLimit = 0; _targetLabel = null;
            _untracked = false; _hoverIdx = -1;
            _ver++;
            InvalidateVisual();
        }

        // The default state for every NT8 account (spec section 2): measured by nothing, plotted as nothing.
        public void SetUntracked()
        {
            Clear();
            _untracked = true;
            InvalidateVisual();
        }

        // ---- cached layout: rebuilt only when the data, the element size or the DPI changes ----
        private int    _layVer = -1;
        private double _layW = -1, _layH = -1, _layDpi = -1;
        private bool   _layOk;
        private double _plotL, _plotR, _plotT, _plotB;
        private double _stepX;
        private double[] _px, _pyBal, _pyFloor;
        private double _targetY, _bufferY, _dailyY;
        private bool   _hasTarget, _hasBuffer, _hasDaily;
        private Geometry _balFill, _balLine, _floorFill, _floorLine;
        private FormattedText[] _tickFt;
        private double[]        _tickY;
        private FormattedText   _targetFt, _dailyFt;
        private FormattedText[] _xFt;
        private double[]        _xFtX;

        // Cached hover readout, keyed on (point index, data version, dpi) so a 4 Hz repaint while the
        // cursor sits still does not rebuild eight FormattedText objects per frame.
        private int    _roIdx = -1, _roVer = -1;
        private double _roDpi = -1;
        private FormattedText   _roHead, _roDetail;
        private FormattedText[] _roL, _roV;

        // ---- hover ----
        private int    _hoverIdx = -1;
        private double _hoverY;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point m = e.GetPosition(this);
            int idx = -1;
            if (_layOk && _pts != null && _pts.Count > 0 && m.X >= _plotL - _stepX * 0.5 && m.X <= _plotR + _stepX * 0.5)
            {
                // x is uniform by index in BOTH views (the mockup's 09:30..13:40 ticks are evenly
                // spaced although the fills are not), so nearest-point snapping is arithmetic.
                idx = _stepX > 0 ? (int)Math.Round((m.X - _plotL) / _stepX) : 0;
                if (idx < 0) idx = 0;
                if (idx > _pts.Count - 1) idx = _pts.Count - 1;
            }
            // Repaint only when something visible changed. The readout card tracks the cursor's y, so
            // a vertical wiggle inside one slot still has to invalidate; a horizontal one does not,
            // and with no hover at all neither axis matters.
            if (idx == _hoverIdx && (idx < 0 || Math.Abs(m.Y - _hoverY) < 1.0)) return;
            _hoverIdx = idx; _hoverY = m.Y;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIdx < 0) return;
            _hoverIdx = -1;
            InvalidateVisual();
        }

        // A throw inside a WPF render pass kills THIS window's dispatcher permanently - a frozen
        // cockpit over a healthy-looking Output. Catch, name the culprit, throttle to one line per 5s.
        private DateTime _lastRenderErr;
        protected override void OnRender(DrawingContext dc)
        {
            try { RenderCore(dc); }
            catch (Exception ex)
            {
                if ((DateTime.UtcNow - _lastRenderErr).TotalSeconds >= 5)
                {
                    _lastRenderErr = DateTime.UtcNow;
                    NinjaTrader.Code.Output.Process("[FundedPath] CURVE RENDER ERROR - " + ex,
                        NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                }
            }
        }

        private void RenderCore(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // Full-bounds first: this is also what makes a bare FrameworkElement hit-testable at all
            // (it has no Background property), so the whole surface reports MouseMove.
            dc.DrawRectangle(PanelBg, null, new Rect(0, 0, w, h));

            if (_untracked) { DrawUntracked(dc, w, h, dpi); return; }
            if (!EnsureLayout(w, h, dpi)) return;

            // 1. round-number gridlines (faint, dotted)
            for (int i = 0; i < _tickY.Length; i++)
                dc.DrawLine(GridPen, new Point(_plotL, Snap(_tickY[i])), new Point(_plotR, Snap(_tickY[i])));

            // 2. washes, bottom-up: gold from the balance curve down, then the red floor band over it.
            if (_balFill   != null) dc.DrawGeometry(GoldWash, null, _balFill);
            if (_floorFill != null) dc.DrawGeometry(RedWash,  null, _floorFill);

            // 3. straight reference lines. This order is also the readout's row order.
            if (_hasTarget) dc.DrawLine(TargetPen, new Point(_plotL, Snap(_targetY)), new Point(_plotR, Snap(_targetY)));
            if (_hasDaily)  dc.DrawLine(DailyPen,  new Point(_plotL, Snap(_dailyY)),  new Point(_plotR, Snap(_dailyY)));
            if (_hasBuffer) dc.DrawLine(BufferPen, new Point(_plotL, Snap(_bufferY)), new Point(_plotR, Snap(_bufferY)));

            // 4. the two splines. The floor is smoothed exactly like the balance curve because the
            //    trader asked for it (spec section 6), even though the underlying floor only steps at
            //    the session close. That smoothing is DRAWING ONLY: every floor number printed in the
            //    readout comes from _pts[i].Floor, the point's exact stepped value, never from the
            //    interpolated path between two points.
            if (_floorLine != null) dc.DrawGeometry(null, FloorPen, _floorLine);
            if (_balLine   != null) dc.DrawGeometry(null, CurvePen, _balLine);

            // 5. endpoint dot with its soft halo
            int last = _pts.Count - 1;
            Point lastPt = new Point(_px[last], _pyBal[last]);
            dc.DrawEllipse(EndHalo, null, lastPt, 11, 11);
            dc.DrawEllipse(EndDot,  null, lastPt, 4.2, 4.2);

            // 6. axes
            for (int i = 0; i < _tickFt.Length; i++)
                dc.DrawText(_tickFt[i], new Point(_plotL - GutterGap - _tickFt[i].Width, _tickY[i] - _tickFt[i].Height / 2.0));
            if (_targetFt != null)
                dc.DrawText(_targetFt, new Point(_plotL - GutterGap - _targetFt.Width, _targetY - _targetFt.Height / 2.0));
            if (_dailyFt != null)
                dc.DrawText(_dailyFt, new Point(_plotL - GutterGap - _dailyFt.Width, _dailyY - _dailyFt.Height / 2.0));
            for (int i = 0; i < _xFt.Length; i++)
                dc.DrawText(_xFt[i], new Point(_xFtX[i], _plotB + LabelRowGap));

            // 7. hover overlay, last so it sits over everything
            if (_hoverIdx >= 0 && _hoverIdx < _pts.Count) DrawHover(dc, w, h, dpi);
        }

        // ---- layout ------------------------------------------------------------------------------

        private bool EnsureLayout(double w, double h, double dpi)
        {
            if (_layVer == _ver && _layW == w && _layH == h && _layDpi == dpi) return _layOk;
            _layVer = _ver; _layW = w; _layH = h; _layDpi = dpi;
            _layOk = false;

            if (_pts == null || _pts.Count == 0) return false;
            int n = _pts.Count;

            _hasTarget = _target > 0;
            _hasBuffer = _buffer > 0;
            // Above zero is the switch: the limit is a checkout option, and an account bought without
            // it hands over a level of 0 and gets no line at all.
            _hasDaily  = _dailyLimit > 0;

            // --- value range: every drawn datum must fit, target and floor included. That is the whole
            //     point of the panel - you read where you are BETWEEN the two lines that end the run.
            double vMin = double.MaxValue, vMax = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                CurvePoint p = _pts[i];
                if (p.Balance < vMin) vMin = p.Balance;
                if (p.Balance > vMax) vMax = p.Balance;
                if (p.Floor   < vMin) vMin = p.Floor;
                if (p.Floor   > vMax) vMax = p.Floor;
            }
            if (_hasTarget) { if (_target < vMin) vMin = _target; if (_target > vMax) vMax = _target; }
            if (_hasBuffer) { if (_buffer < vMin) vMin = _buffer; if (_buffer > vMax) vMax = _buffer; }
            // Same reason as the floor: a level today is traded against that fell off the bottom of the
            // panel would be a line the trader cannot see himself approaching.
            if (_hasDaily)  { if (_dailyLimit < vMin) vMin = _dailyLimit; if (_dailyLimit > vMax) vMax = _dailyLimit; }

            double span = vMax - vMin;
            // A flat series (day one, or a single point) has zero span: invent one so nothing divides
            // by zero and the line lands mid-panel instead of on an edge.
            if (span <= 1e-9) { span = Math.Max(1.0, Math.Abs(vMax) * 0.002); vMin -= span / 2.0; vMax += span / 2.0; }
            double vTop = vMax + span * 0.04;      // mockup ratios: tight over the target...
            double vBot = vMin - span * 0.09;      // ...roomier under the floor
            double vSpan = vTop - vBot;

            // --- gutter labels first: the gutter's width is whatever the widest of them measures.
            double step = NiceStep(vSpan / 3.0);
            var tickVals = new List<double>();
            double t0 = Math.Ceiling(vBot / step) * step;
            for (double v = t0; v <= vTop + 1e-6 && tickVals.Count < MaxTicks; v += step) tickVals.Add(v);

            double gutterW = 0;
            _targetFt = null;
            _dailyFt  = null;
            if (_hasTarget && !string.IsNullOrEmpty(_targetLabel))
            {
                _targetFt = FT(_targetLabel, AxisSize, GreenBr, dpi, Mono);
                gutterW = _targetFt.Width;
            }
            // Formatted here rather than passed in: Cash() writes the same "#,##0" the tab's Money()
            // writes for the target label, so one caller-side format string cannot drift from the other.
            if (_hasDaily)
            {
                _dailyFt = FT(Cash(_dailyLimit), AxisSize, RedBr, dpi, Mono);
                if (_dailyFt.Width > gutterW) gutterW = _dailyFt.Width;
            }

            // Plot geometry needs the gutter width, and the collision test below needs the plot
            // geometry - so measure the candidates, lay out, then decide which survive.
            var ftCand = new FormattedText[tickVals.Count];
            for (int i = 0; i < tickVals.Count; i++)
            {
                ftCand[i] = FT(Compact(tickVals[i]), AxisSize, MutedBr, dpi, Mono);
                if (ftCand[i].Width > gutterW) gutterW = ftCand[i].Width;
            }

            double xLabelH = FT("0", AxisSize, MutedBr, dpi, Mono).Height;
            _plotL = gutterW + GutterGap;
            _plotR = w - PadRight;
            _plotT = PadTop;
            _plotB = h - PadBottom - xLabelH - LabelRowGap;

            // A window dragged narrow/short cannot hold a plot: drop it cleanly rather than draw
            // inverted rects over the panel edge (house rule: height-guard, never overflow).
            if (_plotR - _plotL < 8 || _plotB - _plotT < 12) return false;

            double ph = _plotB - _plotT;
            double pw = _plotR - _plotL;

            // --- map value -> y, index -> x
            _stepX = n > 1 ? pw / (n - 1) : 0;
            _px = new double[n]; _pyBal = new double[n]; _pyFloor = new double[n];
            var balPts   = new Point[n];
            var floorPts = new Point[n];
            for (int i = 0; i < n; i++)
            {
                double x = n > 1 ? _plotL + i * _stepX : _plotL + pw / 2.0;
                _px[i]      = x;
                _pyBal[i]   = _plotB - (_pts[i].Balance - vBot) / vSpan * ph;
                _pyFloor[i] = _plotB - (_pts[i].Floor   - vBot) / vSpan * ph;
                balPts[i]   = new Point(x, _pyBal[i]);
                floorPts[i] = new Point(x, _pyFloor[i]);
            }
            _targetY = _plotB - (_target - vBot) / vSpan * ph;
            _bufferY = _plotB - (_buffer - vBot) / vSpan * ph;
            _dailyY  = _plotB - (_dailyLimit - vBot) / vSpan * ph;

            _balLine   = BuildSpline(balPts,   false, 0);
            _floorLine = BuildSpline(floorPts, false, 0);
            // No wash under a single point: the fill would be a 6px-wide sliver dropping to the plot
            // floor - a rendering artefact, not a gradient. Day one draws its two marks and nothing else.
            _balFill   = n > 1 ? BuildSpline(balPts,   true, _plotB) : null;
            _floorFill = n > 1 ? BuildSpline(floorPts, true, _plotB) : null;

            // --- gutter ticks that survive: inside the plot band, and not sitting on the target label.
            var keepFt = new List<FormattedText>();
            var keepY  = new List<double>();
            for (int i = 0; i < tickVals.Count; i++)
            {
                double y = _plotB - (tickVals[i] - vBot) / vSpan * ph;
                if (y < _plotT + 2 || y > _plotB - 2) continue;
                if (_targetFt != null && Math.Abs(y - _targetY) < 13) continue;
                if (_dailyFt  != null && Math.Abs(y - _dailyY)  < 13) continue;
                keepFt.Add(ftCand[i]); keepY.Add(y);
            }
            _tickFt = keepFt.ToArray();
            _tickY  = keepY.ToArray();

            // --- x labels: stride so they never touch, walking BACK from the last point so the last
            //     one is always drawn (it is the one the trader reads) and is the highlighted one.
            var xf = new List<FormattedText>();
            var xx = new List<double>();
            double widest = 0;
            for (int i = 0; i < n; i++)
            {
                string s = _pts[i].Label;
                if (string.IsNullOrEmpty(s)) continue;
                double cw = FT(s, AxisSize, MutedBr, dpi, Mono).Width;
                if (cw > widest) widest = cw;
            }
            int stride = 1;
            if (_stepX > 0 && widest > 0) stride = Math.Max(1, (int)Math.Ceiling((widest + 16) / _stepX));
            // Consecutive duplicates are skipped, not drawn. Stride only guarantees the labels do not
            // TOUCH; it says nothing about them being different. A replay session puts several fills
            // inside the same minute, so a session axis came out reading "12:41 12:41 12:41 12:41" -
            // four identical stamps that look like a rendering fault and tell the trader nothing. A gap
            // where the stamp would repeat is the honest answer: nothing new to say at this tick.
            string placed = null;
            for (int i = n - 1; i >= 0; i -= stride)
            {
                string s = _pts[i].Label;
                if (string.IsNullOrEmpty(s)) continue;
                if (s == placed) continue;
                placed = s;
                FormattedText ft = FT(s, AxisSize, i == n - 1 ? TextBr : MutedBr, dpi, Mono);
                double x = _px[i] - ft.Width / 2.0;
                if (x < 0) x = 0;
                if (x + ft.Width > w) x = w - ft.Width;
                xf.Add(ft); xx.Add(x);
            }
            _xFt  = xf.ToArray();
            _xFtX = xx.ToArray();

            _layOk = true;
            return true;
        }

        // Smooth cubic through the points, optionally closed down to a baseline for the gradient wash.
        // Uniform Catmull-Rom converted to Bezier control points; the terminal points are duplicated so
        // the curve ends ON the first/last datum instead of short of it.
        // ponytail: no monotone clamp on the control points. Uniform Catmull-Rom can overshoot a local
        // extreme by a pixel or two, which on a near-breach day could read as the gold line dipping
        // under the red floor when it never did. The mockup's rounded peaks ARE that overshoot, so it
        // stays. If it ever misleads, clamp c1.Y/c2.Y into [min(p1.Y,p2.Y), max(p1.Y,p2.Y)] - two lines,
        // and the cubic's convex hull then guarantees the drawn curve never leaves the data's range.
        private static Geometry BuildSpline(Point[] p, bool closeToBaseline, double baselineY)
        {
            var geo = new StreamGeometry();
            if (p == null || p.Length == 0) { geo.Freeze(); return geo; }

            // A one-day challenge (or a session before its first fill) has no segment at all, and a
            // ZERO-LENGTH figure draws nothing: a flat cap has no width, and a dash pattern laid along
            // zero length yields no dash whatever the cap is - which is why day one used to show a gold
            // endpoint dot, a green target and NO red floor. So a single point becomes a short stub
            // centred on it, long enough for the dotted floor pen to lay a dash on.
            bool solo = p.Length == 1;
            Point head = solo ? new Point(p[0].X - SoloHalf, p[0].Y) : p[0];
            Point tail = solo ? new Point(p[0].X + SoloHalf, p[0].Y) : p[p.Length - 1];

            using (StreamGeometryContext ctx = geo.Open())   // Dispose() calls Close(); never call both
            {
                // isClosed stays false even for the wash: WPF closes an open figure implicitly when it
                // fills, whereas isClosed:true would also STROKE the closing leg back up to p[0].
                ctx.BeginFigure(head, closeToBaseline, false);

                if (solo)
                {
                    ctx.LineTo(tail, true, false);
                }
                else
                {
                    for (int i = 0; i < p.Length - 1; i++)
                    {
                        Point p0 = p[i == 0 ? 0 : i - 1];
                        Point p1 = p[i];
                        Point p2 = p[i + 1];
                        Point p3 = p[i + 2 >= p.Length ? p.Length - 1 : i + 2];

                        Point c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                        Point c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                        ctx.BezierTo(c1, c2, p2, true, true);
                    }
                }

                if (closeToBaseline)
                {
                    // isStroked:false - these two legs close the fill area without drawing an outline.
                    ctx.LineTo(new Point(tail.X, baselineY), false, false);
                    ctx.LineTo(new Point(head.X, baselineY), false, false);
                }
            }

            geo.Freeze();   // same reason as the brushes: no change tracking, no thread affinity
            return geo;
        }

        // ---- hover readout -----------------------------------------------------------------------

        private void DrawHover(DrawingContext dc, double w, double h, double dpi)
        {
            int i = _hoverIdx;
            CurvePoint p = _pts[i];

            // Crosshair stem. A 1px pen centred on an integer coordinate straddles two device pixel
            // columns and smears; centring it on x.5 lands it on exactly one.
            double cx = Math.Floor(_px[i]) + 0.5;
            dc.DrawLine(CrossPen, new Point(cx, _plotT), new Point(cx, _plotB));

            // A dot on each line that has a row in the card below, in the same order. All of them sit on
            // a data point, where the spline passes exactly through the value - so the dot never lies
            // about where the number came from.
            if (_hasTarget && _targetY >= _plotT && _targetY <= _plotB)
                dc.DrawEllipse(GreenBr, null, new Point(_px[i], _targetY), 3, 3);
            if (_hasDaily && _dailyY >= _plotT && _dailyY <= _plotB)
                dc.DrawEllipse(RedBr, null, new Point(_px[i], _dailyY), 3, 3);
            dc.DrawEllipse(RedBr,  null, new Point(_px[i], _pyFloor[i]), 3, 3);
            dc.DrawEllipse(GoldBr, null, new Point(_px[i], _pyBal[i]),   3.6, 3.6);

            BuildReadout(i, p, dpi);

            const double padX = 11, padY = 9, gap = 22, rowGap = 3;
            double bodyW = 0, bodyH = 0;
            for (int r = 0; r < _roL.Length; r++)
            {
                double lw = _roL[r].Width + gap + _roV[r].Width;
                if (lw > bodyW) bodyW = lw;
                bodyH += Math.Max(_roL[r].Height, _roV[r].Height) + rowGap;
            }
            double headW = _roHead.Width;
            if (_roDetail != null && _roDetail.Width > headW) headW = _roDetail.Width;
            double headH = _roHead.Height + (_roDetail != null ? _roDetail.Height + 1 : 0) + 7;

            double cardW = Math.Max(bodyW, headW) + 2 * padX;
            double cardH = headH + bodyH + 2 * padY;

            // Flip to the other side of the cursor rather than run off the panel, then clamp - on the
            // last point (the one the trader hovers most) the card would otherwise be half off-screen.
            double bx = _px[i] + 16;
            if (bx + cardW > w - 6) bx = _px[i] - 16 - cardW;
            if (bx < 6) bx = 6;
            double by = _hoverY - cardH / 2.0;
            if (by < 6) by = 6;
            if (by + cardH > h - 6) by = h - 6 - cardH;

            dc.DrawRoundedRectangle(CardBg, CardLn, new Rect(bx, by, cardW, cardH), 8, 8);

            double ty = by + padY;
            dc.DrawText(_roHead, new Point(bx + padX, ty));
            ty += _roHead.Height;
            if (_roDetail != null) { ty += 1; dc.DrawText(_roDetail, new Point(bx + padX, ty)); ty += _roDetail.Height; }
            ty += 3;
            dc.DrawRectangle(Divider, null, new Rect(bx + padX, Math.Floor(ty) + 0.5, cardW - 2 * padX, 1));
            ty += 4;

            for (int r = 0; r < _roL.Length; r++)
            {
                double rh = Math.Max(_roL[r].Height, _roV[r].Height);
                dc.DrawText(_roL[r], new Point(bx + padX, ty + (rh - _roL[r].Height) / 2.0));
                // No TextAlignment on DrawText: right-aligning means measuring, then placing.
                dc.DrawText(_roV[r], new Point(bx + cardW - padX - _roV[r].Width, ty + (rh - _roV[r].Height) / 2.0));
                ty += rh + rowGap;
            }
        }

        private void BuildReadout(int i, CurvePoint p, double dpi)
        {
            if (_roIdx == i && _roVer == _ver && _roDpi == dpi && _roL != null) return;
            _roIdx = i; _roVer = _ver; _roDpi = dpi;

            _roHead = FT(string.IsNullOrEmpty(p.Label) ? "-" : p.Label, 12.5, TextBr, dpi, Sans);
            // Session view carries the trade itself (spec section 6: "time . instrument . side . qty");
            // the Challenge view has no per-day equivalent, so the line simply does not exist.
            _roDetail = string.IsNullOrEmpty(p.Detail) ? null : FT(p.Detail, 10.5, MutedBr, dpi, Sans);

            double room = p.Balance - p.Floor;
            var labels = new List<string>();
            var values = new List<string>();
            var brushes = new List<Brush>();

            labels.Add("Profit Target");
            values.Add(_hasTarget ? Cash(_target) : "n/a");
            brushes.Add(_hasTarget ? GreenBr : DimBr);

            // Straight fixed levels first, in the order they are drawn, then the per-point numbers.
            // Unlike the target this row appears only when the rule does: the limit is bought per
            // account, and a permanent "Daily limit n/a" would be a row of noise on every hover for a
            // trader who never bought one.
            if (_hasDaily)
            {
                labels.Add("Daily limit");
                values.Add(Cash(_dailyLimit));
                brushes.Add(RedBr);
            }

            labels.Add("Balance");
            values.Add(Cash(p.Balance));
            brushes.Add(GoldTxt);

            // The exact stepped floor for THIS point, never a value read off the smoothed path.
            labels.Add("Minimum Balance");
            values.Add(Cash(p.Floor));
            brushes.Add(RedBr);

            labels.Add(_sessionView ? "Trade P&L" : "Day P&L");
            values.Add(Signed(p.DayPnL));
            brushes.Add(p.DayPnL >= 0 ? GreenBr : RedBr);

            labels.Add("Room to floor");
            values.Add(Cash(room));
            brushes.Add(room > 0 ? TextBr : RedBr);

            labels.Add("Fills");
            values.Add(p.Fills.ToString(CultureInfo.InvariantCulture));
            brushes.Add(TextBr);

            _roL = new FormattedText[labels.Count];
            _roV = new FormattedText[labels.Count];
            for (int r = 0; r < labels.Count; r++)
            {
                _roL[r] = FT(labels[r], 10.5, MutedBr, dpi, Sans);
                _roV[r] = FT(values[r], 11.5, brushes[r], dpi, Mono);
            }
        }

        private void DrawUntracked(DrawingContext dc, double w, double h, double dpi)
        {
            FormattedText a = FT("UNTRACKED", 15, DimBr, dpi, Sans);
            FormattedText b = FT("bind this account to a challenge to start measuring", 11, DimBr, dpi, Sans);
            double th = a.Height + 6 + b.Height;
            double y = (h - th) / 2.0;
            dc.DrawText(a, new Point((w - a.Width) / 2.0, y));
            dc.DrawText(b, new Point((w - b.Width) / 2.0, y + a.Height + 6));
        }

        // ---- helpers -----------------------------------------------------------------------------

        // Half-pixel offset so a 1px horizontal rule lands on one device pixel row, not across two.
        private static double Snap(double y) { return Math.Floor(y) + 0.5; }

        private static double NiceStep(double raw)
        {
            if (raw <= 0 || double.IsNaN(raw) || double.IsInfinity(raw)) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            double s;
            if      (n <= 1) s = 1;
            else if (n <= 2) s = 2;
            else if (n <= 5) s = 5;
            else             s = 10;
            return s * mag;
        }

        // Gutter form: "$53K" / "$50.1K" / "$620". Keeps the gutter narrow so the plot gets the width.
        private static string Compact(double v)
        {
            double a = Math.Abs(v);
            if (a >= 1000) return "$" + (v / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + "K";
            return "$" + v.ToString("0", CultureInfo.InvariantCulture);
        }

        // Readout form: full dollars, sign in front of the $ so a negative reads as "-$1,240".
        private static string Cash(double v)
        {
            return (v < 0 ? "-$" : "$") + Math.Abs(v).ToString("#,##0", CultureInfo.InvariantCulture);
        }

        private static string Signed(double v)
        {
            return (v < 0 ? "-$" : "+$") + Math.Abs(v).ToString("#,##0", CultureInfo.InvariantCulture);
        }

        // The 7-arg overload ending in pixelsPerDip. The 6-arg one is [Obsolete] on net48 and compiles
        // to a CS0618 the NinjaScript editor surfaces on every build.
        private static FormattedText FT(string s, double size, Brush b, double dpi, Typeface tf)
        {
            return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, b, dpi);
        }

        // ---- frozen-resource factories (static brushes MUST be frozen: every NT8 window runs its own
        //      dispatcher thread, and an unfrozen shared Freezable throws on the second window) ----
        private static Brush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        // cap defaults to Flat, which is WPF's own default and what the straight reference lines want:
        // a round cap would push a gridline half a thickness past the plot edge on both sides.
        private static Pen FrozenPen(Color c, double t, PenLineCap cap = PenLineCap.Flat)
        {
            var p = new Pen(FrozenBrush(c), t) { StartLineCap = cap, EndLineCap = cap };
            p.Freeze();
            return p;
        }

        private static Pen FrozenDash(Color c, double t, double dash, double space, PenLineCap cap = PenLineCap.Flat)
        {
            var ds = new DashStyle(new double[] { dash, space }, 0);
            ds.Freeze();   // the DashStyle must be frozen BEFORE the Pen, or Pen.Freeze() throws
            var p = new Pen(FrozenBrush(c), t)
            {
                DashStyle = ds, DashCap = PenLineCap.Round, StartLineCap = cap, EndLineCap = cap
            };
            p.Freeze();
            return p;
        }
    }
}
