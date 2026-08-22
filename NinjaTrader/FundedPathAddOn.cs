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
                Description = "Prop-firm challenge cockpit: floor, headroom and what breaks first. Read-only on the account.";
            }
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
