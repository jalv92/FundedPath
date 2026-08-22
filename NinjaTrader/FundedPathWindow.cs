using System;
using System.Windows.Controls;
using System.Xml.Linq;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;

namespace FundedPath.NT
{
    // The floating cockpit window. IWorkspacePersistence is what makes NT8 reopen it (and its tabs)
    // when the workspace is loaded; the per-tab state itself round-trips through FundedPathTab's
    // Save/Restore, which this class only forwards to.
    public class FundedPathWindow : NTWindow, IWorkspacePersistence
    {
        public FundedPathWindow()
        {
            Caption = "Funded Path";
            Width   = 1180;
            Height  = 700;

            TabControl tc = new TabControl();
            TabControlManager.SetIsMovable(tc, true);
            TabControlManager.SetCanAddTabs(tc, true);
            TabControlManager.SetCanRemoveTabs(tc, true);
            // The factory is what lets NT8 rebuild a tab after a workspace restore and what serves
            // the "+" button; without it the tab strip's add button creates nothing.
            TabControlManager.SetFactory(tc, new FundedPathTabFactory());
            Content = tc;

            tc.AddNTTabPage(new FundedPathTab());

            // WorkspaceOptions has to be assigned after the window is loaded, not in the ctor: NT8
            // hands a restored window its own options object first, and overwriting that with a
            // fresh Guid would orphan the saved state.
            Loaded += (o, e) =>
            {
                if (WorkspaceOptions == null)
                    WorkspaceOptions = new WorkspaceOptions("FundedPath-" + Guid.NewGuid().ToString("N"), this);
            };
        }

        public void Restore(XDocument document, XElement element)
        {
            if (MainTabControl != null)
                MainTabControl.RestoreFromXElement(element);
        }

        public void Save(XDocument document, XElement element)
        {
            if (MainTabControl != null)
                MainTabControl.SaveToXElement(element);
        }

        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    public class FundedPathTabFactory : INTTabFactory
    {
        public NTWindow CreateParentWindow() { return new FundedPathWindow(); }
        // The default on isNewWindow is part of the INTTabFactory signature - omitting it does not
        // compile. typeName is ignored: this window hosts exactly one kind of tab.
        public NTTabPage CreateTabPage(string typeName, bool isNewWindow = false) { return new FundedPathTab(); }
    }
}
