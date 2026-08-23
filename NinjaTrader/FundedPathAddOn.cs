using System.Windows;
using NinjaTrader.Core;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;        // NTMenuItem, ControlCenter
using NinjaTrader.NinjaScript;

namespace FundedPath.NT
{
    // Control Center -> New -> "Funded Path" (spec section 5). Same shape as Liquidity Radar's
    // add-on: NinjaTrader instantiates one AddOnBase per NinjaScript compile and calls the window
    // hooks on the thread of every NTWindow it creates.
    public class FundedPathAddOn : NinjaTrader.NinjaScript.AddOnBase
    {
        private NTMenuItem _cockpitMenuItem;
        private NTMenuItem _existingMenuItem;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "FundedPath";
                Description = "Prop-firm challenge cockpit: floor, headroom and what breaks first. Enforcement is off unless you arm it per account.";
            }
            else if (State == State.Terminated)
            {
                // THE RECOMPILE TRAP. F5 builds a new NinjaTrader.Custom and loads it beside the old
                // one; NT8 raises Terminated on THIS (old) add-on instance, but nothing closes the
                // windows the old assembly created. They keep running old code on their own
                // dispatcher threads, so the trader reads yesterday's strings off today's build --
                // an hour lost to a "missing" feature that had already shipped.
                //
                // Statics are per-assembly, so FundedPathWindow.Snapshot() here returns exactly the
                // windows of THIS generation. After a recompile he is left with a fresh window or no
                // window, never a stale one.
                CloseOurWindows();
            }
        }

        static void CloseOurWindows()
        {
            Window[] doomed = FundedPathWindow.Snapshot();
            if (doomed.Length == 0)
                return;

            for (int i = 0; i < doomed.Length; i++)
            {
                Window w = doomed[i];
                try
                {
                    // Each cockpit window lives on one of NT8's spare dispatcher threads, not on the
                    // Control Center's, so Close() from here is a cross-thread call and throws.
                    // InvokeAsync because a synchronous Invoke onto a window whose thread is busy
                    // would block NinjaTrader's own shutdown/compile path.
                    w.Dispatcher.InvokeAsync(new System.Action(delegate
                    {
                        // Already gone, already closing, or torn down by the workspace: all of them
                        // are fine and none of them is an error worth a line in the log.
                        try { w.Close(); } catch { }
                    }));
                }
                catch { }
            }

            // One line, deliberately: it is also the only evidence that NT8 really does deliver
            // Terminated to the outgoing add-on on a recompile. If a recompile ever leaves a stale
            // window AND this line is missing from the Output tab, the assumption is what broke.
            NinjaTrader.Code.Output.Process(
                "[FundedPath] add-on terminated (recompile or shutdown) - closing " + doomed.Length + " open window(s).",
                NinjaTrader.NinjaScript.PrintTo.OutputTab1);
        }

        // Called on the thread of each new NTWindow, including after a recompile - so this runs again
        // on a Control Center that already exists, which is why OnWindowDestroyed has to be able to
        // pull the item back out.
        protected override void OnWindowCreated(Window window)
        {
            ControlCenter cc = window as ControlCenter;
            if (cc == null)
                return;

            _existingMenuItem = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (_existingMenuItem == null)
                return;

            _cockpitMenuItem = new NTMenuItem
            {
                Header = "Funded Path",
                // The ONE theme resource this add-on takes from NinjaTrader. The menu item lives
                // inside NT8's own chrome, so it must follow NT8's skin; every other surface in the
                // cockpit hardcodes the spec section 6 palette on purpose.
                Style  = Application.Current.TryFindResource("MainMenuItem") as Style
            };
            _existingMenuItem.Items.Add(_cockpitMenuItem);
            _cockpitMenuItem.Click += OnMenuItemClick;
        }

        // Recompile-safe cleanup: without this the New menu collects a duplicate "Funded Path"
        // on every compile, each one bound to a dead handler.
        protected override void OnWindowDestroyed(Window window)
        {
            if (_cockpitMenuItem != null && window is ControlCenter)
            {
                if (_existingMenuItem != null && _existingMenuItem.Items.Contains(_cockpitMenuItem))
                    _existingMenuItem.Items.Remove(_cockpitMenuItem);
                _cockpitMenuItem.Click -= OnMenuItemClick;
                _cockpitMenuItem = null;
            }
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            // RandomDispatcher hands the new window one of NT8's spare UI threads, so the cockpit
            // never shares a dispatcher with the Control Center. A throw here would otherwise reach
            // the menu click and be swallowed by NT8 with no trace at all.
            Globals.RandomDispatcher.InvokeAsync(new System.Action(() =>
            {
                try { new FundedPathWindow().Show(); }
                catch (System.Exception ex)
                {
                    NinjaTrader.Code.Output.Process("[FundedPath] window open failed: " + ex,
                        NinjaTrader.NinjaScript.PrintTo.OutputTab1);
                }
            }));
        }
    }
}
