// MainWindow.cs - standard resizable Windows window with a sidebar navigation
// (连接与语音 / 按键映射 / 环境自检 / 关于) and content page host. Closing the
// window hides to the tray (app keeps working); tray menu "退出" really exits.
// Cross-thread state arrives via UiState snapshots on a UI timer.
// 中文：主窗口 —— 标准可缩放窗口 + 侧边栏四页导航；关窗最小化到托盘
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

abstract class MacPage : UserControl {
    public App App;
    public MacPage(App app) {
        App = app;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = MacTheme.ContentBg;
    }
    /// Pulled on the UI thread from UiState (only when the version changed).
    public virtual void OnSnapshot(UiState.Snapshot s) { }
    /// Called when this page becomes visible.
    public virtual void OnActivated() { }
    /// Remote key action fired (for canvas flash); worker thread origin.
    public virtual void OnActionFired(string keyId, string desc) { }

    /// Force the page's OnResize layout even when its bounds did not change
    /// (needed after a DPI-scale correction).
    public void Relayout() { OnResize(EventArgs.Empty); }
}

sealed class MainWindow : Form {
    readonly App app;
    MacPage[] pages;
    int current;
    readonly string[] navTitles = { "连接与语音", "按键映射", "环境自检", "关于" };
    readonly string[] navIcons = { "link", "keys", "check", "info" };
    Rectangle[] navRects;
    int navHover = -1;
    bool balloonShown;

    Timer statusTimer;
    long lastVersion = -1;
    readonly Font navFont;
    readonly Font brandFont;

    // last mapped-action flash (set from worker thread, consumed by timer)
    readonly object flashGate = new object();
    string flashKey, flashDesc;
    long flashTick;

    public MainWindow(App app) {
        this.app = app;
        navFont = MacTheme.Font(9.75f);
        brandFont = MacTheme.Font(11f, FontStyle.Bold);

        // pages must exist before Form property assignments: setting
        // MinimumSize fires OnSizeChanged -> LayoutPages
        var pageList = new System.Collections.Generic.List<MacPage>();
        pageList.Add(new ConnectPage(app));
        pageList.Add(new KeyMapPage(app));
        pageList.Add(new DiagPage(app));
        pageList.Add(new AboutPage(app));
        pages = pageList.ToArray();

        Text = "MiVoiceMic — 小米遥控器语音";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(MacTheme.S(900), MacTheme.S(680));
        Size = new Size(MacTheme.S(1060), MacTheme.S(760));
        MaximizeBox = true;
        BackColor = MacTheme.SidebarBg;
        DoubleBuffered = true;
        Font = MacTheme.Font(9.5f);

        foreach (MacPage p in pages) { p.Visible = false; Controls.Add(p); }
        SelectPage(0);

        statusTimer = new Timer { Interval = 200 };
        statusTimer.Tick += delegate { PumpSnapshot(); };
        statusTimer.Start();

        InputRouter.ActionFired += delegate (string id, string desc) {
            lock (flashGate) { flashKey = id; flashDesc = desc; flashTick = Environment.TickCount; }
        };
    }

    void PumpSnapshot() {
        var s = UiState.Take();
        if (s.Version != lastVersion) {
            lastVersion = s.Version;
            pages[current].OnSnapshot(s);
        }
        lock (flashGate) {
            if (flashKey != null && Environment.TickCount - flashTick < 1200) {
                if (s.IsLit(flashKey)) {
                    pages[current].OnActionFired(flashKey, flashDesc);
                } else {                                           // key released: its flash ends with the press
                    flashKey = null;
                    pages[current].OnActionFired(null, null);
                }
            } else if (flashKey != null) {                         // flash window over
                flashKey = null;
                pages[current].OnActionFired(null, null);
            }
        }
        Invalidate();                                          // sidebar status line
    }

    public System.Windows.Forms.Control CurrentPage { get { return pages != null && current < pages.Length ? pages[current] : null; } }

    public void SelectPage(int idx) {
        if (idx < 0 || idx >= pages.Length) return;
        for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == idx;
        current = idx;
        pages[idx].OnActivated();
        Invalidate();
    }

    // ---- layout -------------------------------------------------------------
    Rectangle SidebarRect { get { return new Rectangle(0, 0, MacTheme.S(196), ClientSize.Height); } }
    Rectangle ContentRect {
        get {
            var sb = SidebarRect;
            return new Rectangle(sb.Right, 0, ClientSize.Width - sb.Right, ClientSize.Height);
        }
    }

    protected override void OnLoad(EventArgs e) {
        base.OnLoad(e);
        LayoutPages();
        LayoutNav();
    }

    void LayoutPages() {
        if (pages == null) return;
        var c = ContentRect;
        foreach (MacPage p in pages) p.Bounds = c;
        Invalidate();
    }

    void LayoutNav() {
        int n = navTitles.Length;
        navRects = new Rectangle[n];
        int x = MacTheme.S(12), y = MacTheme.S(64), w = SidebarRect.Width - MacTheme.S(24), h = MacTheme.S(36);
        for (int i = 0; i < n; i++) {
            navRects[i] = new Rectangle(x, y, w, h);
            y += h + MacTheme.S(4);
        }
    }

    protected override void OnSizeChanged(EventArgs e) {
        base.OnSizeChanged(e);
        if (pages == null) return;
        LayoutPages();
        LayoutNav();
    }

    // ---- input ---------------------------------------------------------------
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        int h = -1;
        if (navRects != null)
            for (int i = 0; i < navRects.Length; i++)
                if (navRects[i].Contains(e.Location)) h = i;
        if (h != navHover) { navHover = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) {
        base.OnMouseLeave(e);
        navHover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e) {
        base.OnMouseClick(e);
        if (navRects != null)
            for (int i = 0; i < navRects.Length; i++)
                if (navRects[i].Contains(e.Location)) { SelectPage(i); return; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        base.OnFormClosing(e);
        if (e.CloseReason == CloseReason.UserClosing) {
            e.Cancel = true;                                   // tray app: keep running
            Hide();
            if (!balloonShown) {
                balloonShown = true;
                try {
                    var ni = TrayIcon.Instance;
                    if (ni != null) ni.ShowBalloonTip(2500, "MiVoiceMic",
                        "程序仍在后台运行，语音输入不受影响；右键托盘图标可退出。", ToolTipIcon.Info);
                } catch { }
            }
        }
    }

    // ---- per-monitor DPI ------------------------------------------------------
    // PMv2 delivers WM_DPICHANGED when the window crosses monitors; GDI+ point
    // fonts rescale on their own, but MacTheme.Scale-driven layout coordinates
    // do not - re-derive the scale, take the suggested rect (keeps the window's
    // physical size) and relayout everything. Widget sizes fixed at construction
    // (toggles, buttons) keep their old pixel size until recreated - accepted.
    const int WM_DPICHANGED = 0x02E0;
    [StructLayout(LayoutKind.Sequential)] struct DpiRect { public int L, T, R, B; }

    protected override void WndProc(ref Message m) {
        if (m.Msg == WM_DPICHANGED && m.WParam != IntPtr.Zero && m.LParam != IntPtr.Zero) {
            MacTheme.Init((int)(short)((long)m.WParam & 0xFFFF));     // LOWORD = new DPI
            var r = (DpiRect)Marshal.PtrToStructure(m.LParam, typeof(DpiRect));
            SetBounds(r.L, r.T, r.R - r.L, r.B - r.T);                // fires OnSizeChanged -> LayoutPages/LayoutNav
            foreach (MacPage p in pages) p.Relayout();
            Invalidate(true);
        }
        base.WndProc(ref m);
    }

    // ---- paint ----------------------------------------------------------------
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var sb = SidebarRect;
        using (var b = new SolidBrush(MacTheme.SidebarBg)) g.FillRectangle(b, sb);
        using (var b = new SolidBrush(MacTheme.ContentBg)) g.FillRectangle(b, ContentRect);
        using (var pen = new Pen(MacTheme.Separator)) g.DrawLine(pen, sb.Right - 1, 0, sb.Right - 1, ClientSize.Height);

        // brand
        Gfx.Text(g, "MiVoiceMic", brandFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(18), MacTheme.S(24), sb.Width - MacTheme.S(24), MacTheme.S(22)), StringAlignment.Near);

        // nav items
        if (navRects != null) {
            for (int i = 0; i < navRects.Length; i++) {
                var r = navRects[i];
                bool sel = i == current, hov = i == navHover;
                if (sel || hov) {
                    using (var path = Gfx.RoundRect(r, MacTheme.S(8))) {
                        using (var b = new SolidBrush(sel ? MacTheme.AccentSoft : MacTheme.HoverGray)) g.FillPath(b, path);
                    }
                }
                Color tint = sel ? Color.FromArgb(0x0A, 0x63, 0xD8) : (hov ? MacTheme.TextPrimary : MacTheme.TextSecondary);
                var iconRect = new Rectangle(r.X + MacTheme.S(10), r.Y + (r.Height - MacTheme.S(17)) / 2, MacTheme.S(17), MacTheme.S(17));
                NavIcons.Draw(g, navIcons[i], iconRect, tint);
                Gfx.Text(g, navTitles[i], navFont, tint,
                    new RectangleF(iconRect.Right + MacTheme.S(9), r.Y + (r.Height - MacTheme.S(18)) / 2, r.Right - iconRect.Right - MacTheme.S(12), MacTheme.S(20)), StringAlignment.Near);
            }
        }

        // sidebar footer: connection status
        var s = UiState.Take();
        string status = s.Linked ? "已连接" : "未连接";
        if (s.Battery >= 0) status += " · 电量 " + s.Battery + "%";
        using (var dot = new SolidBrush(s.Linked ? MacTheme.Green : MacTheme.TextTertiary))
            g.FillEllipse(dot, sb.X + MacTheme.S(18), ClientSize.Height - MacTheme.S(30), MacTheme.S(8), MacTheme.S(8));
        Gfx.Text(g, status, MacTheme.Font(8.75f), MacTheme.TextSecondary,
            new RectangleF(sb.X + MacTheme.S(32), ClientSize.Height - MacTheme.S(33), sb.Width - MacTheme.S(40), MacTheme.S(18)), StringAlignment.Near);
    }
}
