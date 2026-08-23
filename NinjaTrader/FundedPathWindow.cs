using System;
using System.Collections.Generic;
using System.Windows;
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
        // ---- the open windows of THIS compiled assembly ----------------------------------------
        //
        // F5 in the NinjaScript Editor builds a NEW NinjaTrader.Custom assembly and loads it beside
        // the old one. Nothing closes the windows the OLD assembly created: an NTWindow lives on one
        // of NT8's spare dispatcher threads with a running timer, and that alone roots the old
        // assembly forever. The result cost the trader an hour: he recompiled, the new DLL on disk
        // carried the new strings, his open Funded Path window kept painting the old ones, and he
        // reported a feature as missing that had in fact shipped.
        //
        // This list is what FundedPathAddOn closes when ITS State reaches Terminated -- which NT8
        // raises on the old add-on instance during a recompile. STATIC and per-assembly, which is
        // exactly the scope wanted: each compile gets its own copy of this field, so the old
        // add-on's terminate path sees precisely the windows of its own generation, including any
        // the workspace restored (those never pass through the New menu, so registering at the menu
        // click would miss them).
        static readonly List<Window> OpenWindows = new List<Window>();

        // A copy, so the caller can close windows without holding the lock while WPF runs.
        public static Window[] Snapshot()
        {
            lock (OpenWindows) return OpenWindows.ToArray();
        }

        public FundedPathWindow()
        {
            // Loaded rather than the constructor: a window that throws on the way up must not be
            // left in the list, and Closed is the exact mirror. Loaded can fire more than once for a
            // re-parented window, hence the Contains guard -- a double entry would be closed twice.
            Loaded += delegate
            {
                lock (OpenWindows)
                    if (!OpenWindows.Contains(this)) OpenWindows.Add(this);
            };
            Closed += delegate
            {
                lock (OpenWindows) OpenWindows.Remove(this);
            };

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
