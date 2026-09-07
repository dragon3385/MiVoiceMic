// KeyMapPage.cs - 按键映射: canvas with the remote in the middle and the cards
// arranged to match each button's physical position so connector lines never
// cross: 电源键 top-left, 语音键 top-right, 方向上/左 on the left below,
// 方向右/确定键/方向下 on the right below. The six keys that need the
// RemoteMapper KMDF driver sit in a bottom grid with dashed drop lines, and a
// button launches the bundled driver installer (elevated).
// 中文：按键映射页 —— 遥控器居中、卡片按实物位置环绕、需驱动键网格与内置驱动安装入口
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

class KeyMapPage : MacPage {
    MacToggle enableToggle;
    MapCanvas canvas;
    MacButton driverBtn;
    readonly Font titleFont, noteFont;

    public KeyMapPage(App app) : base(app) {
        titleFont = MacTheme.Font(14.5f, FontStyle.Bold);
        noteFont = MacTheme.Font(8.5f);

        enableToggle = new MacToggle(App.Config.keymap.enabled) { Top = MacTheme.S(28) };
        enableToggle.Toggled += delegate {
            App.Config.keymap.enabled = enableToggle.On;
            Save();
            canvas.SetEnabled(enableToggle.On);
        };

        driverBtn = new MacButton("安装内核驱动（高级）", false, true) { Width = MacTheme.S(150) };
        driverBtn.Clicked += delegate {
            using (var dlg = new DriverInstallDialog()) dlg.ShowDialog(FindForm());
        };

        canvas = new MapCanvas(app);
        canvas.SetEnabled(App.Config.keymap.enabled);
        canvas.RequestEnableMapping = delegate { enableToggle.On = true; };

        Controls.Add(canvas);
        Controls.Add(enableToggle);
        Controls.Add(driverBtn);
    }

    void Save() {
        try { App.Config.Save(); App.ApplyConfig(App.Config); } catch (Exception ex) { Log.Error("[UI] keymap save: " + ex.Message); }
    }

    protected override void OnResize(EventArgs e) {
        base.OnResize(e);
        int pad = MacTheme.S(26);
        enableToggle.Location = new Point(Width - pad - enableToggle.Width - driverBtn.Width - MacTheme.S(12), MacTheme.S(28));
        driverBtn.Location = new Point(Width - pad - driverBtn.Width, MacTheme.S(26));
        canvas.Bounds = new Rectangle(0, MacTheme.S(62), Width, Height - MacTheme.S(62) - MacTheme.S(40));
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Gfx.Text(g, "按键映射", titleFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(26), MacTheme.S(22), MacTheme.S(300), MacTheme.S(28)), StringAlignment.Near);
        Gfx.Text(g, "启用自定义按键功能", MacTheme.Font(9.75f), MacTheme.TextPrimary,
            new RectangleF(enableToggle.Left - MacTheme.S(150), MacTheme.S(30), MacTheme.S(140), MacTheme.S(20)), StringAlignment.Far);
        Gfx.Text(g, "虚线连线的按键需要内核驱动才会到达 Windows（配置先保存、装驱动后自动生效）",
            noteFont, MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(26), Height - MacTheme.S(28), Width - MacTheme.S(52), MacTheme.S(18)), StringAlignment.Near);
    }

    public override void OnActionFired(string keyId, string desc) { canvas.Flash(keyId); }
}

// ---- canvas ------------------------------------------------------------------
class MapCanvas : Control {
    readonly App app;
    readonly List<MapCard> cards = new List<MapCard>();
    readonly List<DriverKeyCard> driverCards = new List<DriverKeyCard>();
    public bool Enabled_;
    public Action RequestEnableMapping;
    string flashId;
    int flashTick;
    bool hadLit;                                           // art had lit keys on the previous tick
    Timer flashTimer;

    // card placement mirrors the remote's physical layout so lines never cross:
    // power top-left, up/left down the left side; voice top-right, right/ok/down
    // down the right side
    static readonly string[] LeftOrder = { "power", "up", "left" };
    static readonly string[] RightOrder = { "voice", "right", "ok", "down" };
    static readonly string[] GridOrder = { "back", "home", "menu", "volup", "voldown", "tv" };

    public MapCanvas(App app) {
        this.app = app;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = MacTheme.ContentBg;
        RebuildCards();
        flashTimer = new Timer { Interval = 120 };
        flashTimer.Tick += delegate {
            bool lit = UiState.HasLitKey();
            if (flashId != null && Environment.TickCount - flashTick > 1100) { flashId = null; Invalidate(); }
            else if (flashId != null) Invalidate();
            else if (lit) Invalidate();                         // pressed-key light: keep repainting while lit
            else if (hadLit) Invalidate();                      // light just went off: paint the art dark once
            hadLit = lit;
        };
        flashTimer.Start();
    }

    public void SetEnabled(bool on) { Enabled_ = on; Invalidate(true); foreach (var c in cards) c.Invalidate(); }
    public void Flash(string keyId) { flashId = keyId; flashTick = Environment.TickCount; Invalidate(); }

    public void RebuildCards() {
        foreach (MapCard c in cards) Controls.Remove(c);
        foreach (DriverKeyCard c in driverCards) Controls.Remove(c);
        cards.Clear();
        driverCards.Clear();
        foreach (KeyMapEntry e in app.Config.keymap.keys) {
            if (Array.IndexOf(GridOrder, e.id) >= 0) {
                var dc = new DriverKeyCard(e);
                dc.ClickedCard += delegate { OpenEditor(dc.Entry); };
                dc.HoverChanged += delegate { Invalidate(); };
                driverCards.Add(dc);
            } else {
                var card = new MapCard(e) { Canvas = this };
                card.ClickedCard += delegate { OpenEditor(card.Entry); };
                card.HoverChanged += delegate { Invalidate(); };
                cards.Add(card);
            }
        }
        foreach (var c in cards) Controls.Add(c);
        foreach (var c in driverCards) Controls.Add(c);
        LayoutCards();
        Invalidate();
    }

    public void OpenEditor(KeyMapEntry entry) {
        using (var dlg = new KeyMapEditor(entry, Enabled_)) {
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) {
                entry.click = dlg.ResultClick;
                entry.hold = dlg.ResultHold;
                bool enableNow = dlg.EnableMappingOnOk && entry.Mapped && app.Config.keymap.enabled != true;
                if (enableNow) app.Config.keymap.enabled = true;
                try {
                    app.Config.Save();
                    app.ApplyConfig(app.Config);
                } catch (Exception ex) { Log.Error("[UI] keymap save: " + ex.Message); }
                if (enableNow) {
                    SetEnabled(true);
                    if (RequestEnableMapping != null) RequestEnableMapping();
                }
                foreach (MapCard c in cards) if (c.Entry == entry) c.RefreshEntry();
                foreach (DriverKeyCard c in driverCards) if (c.Entry == entry) c.RefreshEntry();
                Invalidate();
            }
        }
    }

    // remote art rect (virtual 100x300)
    RectangleF ArtRect() {
        float artH = Height - MacTheme.S(200);
        float artW = artH * RemotePainter.VW / RemotePainter.VH;
        return new RectangleF(Width / 2f - artW / 2f, MacTheme.S(2), artW, artH);
    }

    public PointF Hotspot(string keyId) {
        var art = ArtRect();
        var hs = RemotePainter.Hotspots();
        PointF v;
        if (!hs.TryGetValue(keyId, out v)) return new PointF(art.X + art.Width / 2, art.Y + art.Height / 2);
        float k = Math.Min(art.Width / RemotePainter.VW, art.Height / RemotePainter.VH);
        float ox = art.X + (art.Width - RemotePainter.VW * k) / 2f;
        float oy = art.Y + (art.Height - RemotePainter.VH * k) / 2f;
        return new PointF(ox + v.X * k, oy + v.Y * k);
    }

    void LayoutCards() {
        int cardW = MacTheme.S(230), cardH = MacTheme.S(80);
        int pad = MacTheme.S(20), gap = MacTheme.S(14);

        var byId = new Dictionary<string, MapCard>();
        foreach (MapCard c in cards) byId[c.Entry.id] = c;

        // left column: power / up / left (top -> bottom)
        int y = MacTheme.S(4);
        foreach (string id in LeftOrder) {
            MapCard c;
            if (byId.TryGetValue(id, out c)) { c.Bounds = new Rectangle(pad, y, cardW, cardH); y += cardH + gap; }
        }

        // right column: voice / right / ok / down (top -> bottom)
        y = MacTheme.S(4);
        foreach (string id in RightOrder) {
            MapCard c;
            if (byId.TryGetValue(id, out c)) { c.Bounds = new Rectangle(Width - pad - cardW, y, cardW, cardH); y += cardH + gap; }
        }

        // driver grid: one row of six, left group (back/home/menu) on the left,
        // right group (volup/voldown/tv) on the right - keeps drop lines parallel
        int gw = MacTheme.S(108), gh = MacTheme.S(54), ggap = MacTheme.S(10);
        int rowY = Height - MacTheme.S(78);
        var gridById = new Dictionary<string, DriverKeyCard>();
        foreach (DriverKeyCard c in driverCards) gridById[c.Entry.id] = c;
        int groupGap = MacTheme.S(46);
        int totalW = 6 * gw + 5 * ggap + groupGap;
        int x0 = Math.Max(MacTheme.S(14), (Width - totalW) / 2);
        int x = x0;
        foreach (string id in GridOrder) {
            DriverKeyCard c;
            if (!gridById.TryGetValue(id, out c)) continue;
            c.Bounds = new Rectangle(x, rowY, gw, gh);
            x += gw + ggap;
            if (id == "menu") x += groupGap;                 // split left/right groups
        }
    }

    int GridHeaderY { get { return Height - MacTheme.S(112); } }

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutCards(); Invalidate(); }

    void DrawConnector(Graphics g, PointF p1, PointF p2, bool hot, bool dashed) {
        Color line = !Enabled_ ? Color.FromArgb(210, 210, 216)
                   : hot ? MacTheme.Accent : Color.FromArgb(178, 183, 193);
        float dx = Math.Abs(p2.X - p1.X);
        var c1 = new PointF(p1.X + (p1.X < p2.X ? dx * 0.45f : -dx * 0.45f), p1.Y);
        var c2 = new PointF(p2.X + (p1.X < p2.X ? -dx * 0.45f : dx * 0.45f), p2.Y);
        using (var pen = new Pen(line, hot ? 2.2f * MacTheme.Scale : 1.6f * MacTheme.Scale)) {
            pen.DashStyle = dashed || !Enabled_ ? DashStyle.Dash : DashStyle.Solid;
            g.DrawBezier(pen, p1, c1, c2, p2);
        }
    }

    void DrawDot(Graphics g, PointF p, bool hot) {
        int d = MacTheme.S(hot ? 7 : 5);
        using (var b = new SolidBrush(hot && Enabled_ ? MacTheme.Accent : Color.FromArgb(178, 183, 193)))
            g.FillEllipse(b, p.X - d / 2, p.Y - d / 2, d, d);
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        // remote first: connectors draw on top so their endpoints stay visible
        // where they reach the buttons
        var s = UiState.Take();
        RemotePainter.Draw(g, ArtRect(), s.Talking, flashId, s.KeysLit);

        foreach (MapCard card in cards) {
            bool leftSide = Array.IndexOf(LeftOrder, card.Entry.id) >= 0;
            PointF p1 = Hotspot(card.Entry.id);
            var p2f = new PointF(leftSide ? card.Right : card.Left, card.Top + card.Height / 2f);
            bool hot = flashId == card.Entry.id || card.Hovered || s.IsLit(card.Entry.id);
            DrawConnector(g, p1, p2f, hot, card.Entry.needsDriver);
            if (card.Entry.needsDriver) {
                Gfx.Text(g, "需驱动", MacTheme.Font(8f), MacTheme.TextTertiary,
                    new RectangleF(card.Left + MacTheme.S(14), card.Bottom - MacTheme.S(20), MacTheme.S(70), MacTheme.S(15)), StringAlignment.Near);
            }
            DrawDot(g, p1, hot);
        }

        // driver keys: dashed drop lines from their buttons down to the grid
        foreach (DriverKeyCard card in driverCards) {
            PointF p1 = Hotspot(card.Entry.id);
            var p2f = new PointF(card.Left + card.Width / 2f, card.Top);
            bool hot = flashId == card.Entry.id || card.Hovered || s.IsLit(card.Entry.id);
            Color line = hot ? MacTheme.Accent : Color.FromArgb(203, 207, 215);
            float dy = p2f.Y - p1.Y;
            var c1 = new PointF(p1.X, p1.Y + dy * 0.35f);
            var c2 = new PointF(p2f.X, p2f.Y - dy * 0.2f);
            using (var pen = new Pen(line, hot ? 2f * MacTheme.Scale : 1.4f * MacTheme.Scale)) {
                pen.DashStyle = DashStyle.Dash;
                g.DrawBezier(pen, p1, c1, c2, p2f);
            }
        }

        // stopped banner
        if (!Enabled_) {
            var banner = new Rectangle(Width / 2 - MacTheme.S(220), Height - MacTheme.S(198), MacTheme.S(440), MacTheme.S(24));
            using (var path = Gfx.RoundRect(banner, MacTheme.S(12))) {
                using (var b = new SolidBrush(Color.FromArgb(250, 250, 252))) g.FillPath(b, path);
                using (var pen = new Pen(MacTheme.CardBorder)) g.DrawPath(pen, path);
            }
            using (var f = MacTheme.Font(9f)) {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("自定义按键已停用 — 遥控器按键保持系统默认行为", f, new SolidBrush(MacTheme.TextSecondary), banner, sf);
            }
        }

        // driver grid header
        Gfx.Text(g, "虚线连线的按键需安装内核驱动（右上角「安装内核驱动」按钮，转为 F13-F19 后自动生效）",
            MacTheme.Font(8.5f), MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(20), GridHeaderY, Width - MacTheme.S(40), MacTheme.S(16)), StringAlignment.Near);
    }
}

// ---- mapping card -------------------------------------------------------------
class MapCard : Control {
    public KeyMapEntry Entry;
    public MapCanvas Canvas;
    public bool Hovered;
    public event Action ClickedCard;
    public event Action HoverChanged;

    public MapCard(KeyMapEntry entry) {
        Entry = entry;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        BackColor = MacTheme.ContentBg;
    }

    public void RefreshEntry() { Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { Hovered = true; HoverChanged(); Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hovered = false; HoverChanged(); Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) {
        base.OnClick(e);
        if (ClickedCard != null) ClickedCard();
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        bool mapped = Entry.Mapped;
        Color border = Hovered && Canvas != null && Canvas.Enabled_ ? Color.FromArgb(0xB5, 0xCF, 0xF7) : MacTheme.CardBorder;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(r, MacTheme.S(10))) {
            using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
            using (var pen = new Pen(border)) g.DrawPath(pen, path);
        }

        int x = MacTheme.S(14);
        using (var f = MacTheme.Font(11f, FontStyle.Bold))
            Gfx.Text(g, Entry.name, f, MacTheme.TextPrimary, new RectangleF(x, MacTheme.S(11), Width, MacTheme.S(18)), StringAlignment.Near);

        string vk = Entry.vk;
        using (var f = MacTheme.Font(8f)) {
            SizeF sz = Gfx.Measure(g, vk, f);
            var badge = new RectangleF(Width - sz.Width - MacTheme.S(24), MacTheme.S(11), sz.Width + MacTheme.S(12), MacTheme.S(16));
            using (var path = Gfx.RoundRect(new Rectangle((int)badge.X, (int)badge.Y, (int)badge.Width, (int)badge.Height), MacTheme.S(8))) {
                using (var b = new SolidBrush(Color.FromArgb(240, 242, 245))) g.FillPath(b, path);
            }
            Gfx.Text(g, vk, f, MacTheme.TextTertiary, badge, StringAlignment.Center);
        }

        int ay = MacTheme.S(42);
        if (mapped) {
            DrawActionLine(g, "点按", Entry.click, ay);
            DrawActionLine(g, "长按", Entry.hold, ay + MacTheme.S(24));
        } else {
            Gfx.Text(g, "未映射 · 保持系统默认行为", MacTheme.Font(9f), MacTheme.TextTertiary,
                new RectangleF(x, ay + MacTheme.S(8), Width - MacTheme.S(28), MacTheme.S(18)), StringAlignment.Near);
        }
        Gfx.Text(g, Hovered ? "点击编辑" : "", MacTheme.Font(8f), MacTheme.Accent,
            new RectangleF(Width - MacTheme.S(72), Height - MacTheme.S(22), MacTheme.S(58), MacTheme.S(16)), StringAlignment.Far);
    }

    void DrawActionLine(Graphics g, string label, KeyMapAction a, int y) {
        int x = MacTheme.S(14);
        Gfx.Text(g, label, MacTheme.Font(9f), MacTheme.TextTertiary, new RectangleF(x, y, MacTheme.S(30), MacTheme.S(16)), StringAlignment.Near);
        string text = a != null && a.HasPayload ? KeyMapNames.Describe(a) : "未设置";
        Color c = a != null && a.HasPayload ? MacTheme.TextPrimary : MacTheme.TextTertiary;
        Gfx.Text(g, text, MacTheme.Font(9f), c, new RectangleF(x + MacTheme.S(36), y, Width - MacTheme.S(104), MacTheme.S(18)), StringAlignment.Near);
    }
}

// ---- compact card for driver-required keys ------------------------------------
class DriverKeyCard : Control {
    public KeyMapEntry Entry;
    public bool Hovered;
    public event Action ClickedCard;
    public event Action HoverChanged;

    public DriverKeyCard(KeyMapEntry entry) {
        Entry = entry;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        BackColor = MacTheme.ContentBg;
    }

    public void RefreshEntry() { Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { Hovered = true; HoverChanged(); Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hovered = false; HoverChanged(); Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) {
        base.OnClick(e);
        if (ClickedCard != null) ClickedCard();
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(r, MacTheme.S(9))) {
            using (var b = new SolidBrush(Hovered ? Color.FromArgb(250, 251, 253) : Color.White)) g.FillPath(b, path);
            using (var pen = new Pen(Hovered ? Color.FromArgb(0xB5, 0xCF, 0xF7) : MacTheme.CardBorder)) g.DrawPath(pen, path);
        }

        using (var f = MacTheme.Font(9.5f, FontStyle.Bold))
            Gfx.Text(g, Entry.name, f, MacTheme.TextPrimary, new RectangleF(MacTheme.S(12), MacTheme.S(7), Width - MacTheme.S(24), MacTheme.S(16)), StringAlignment.Near);

        string status = Entry.Mapped ? KeyMapNames.Describe(Entry.click.HasPayload ? Entry.click : Entry.hold) : "未映射";
        using (var f = MacTheme.Font(8.5f))
            Gfx.Text(g, status, f, Entry.Mapped ? MacTheme.TextSecondary : MacTheme.TextTertiary,
                new RectangleF(MacTheme.S(12), MacTheme.S(30), Width - MacTheme.S(24), MacTheme.S(15)), StringAlignment.Near);

        using (var f = MacTheme.Font(8f)) {
            SizeF sz = Gfx.Measure(g, Entry.vk, f);
            var badge = new RectangleF(Width - sz.Width - MacTheme.S(22), MacTheme.S(6), sz.Width + MacTheme.S(12), MacTheme.S(15));
            using (var path = Gfx.RoundRect(new Rectangle((int)badge.X, (int)badge.Y, (int)badge.Width, (int)badge.Height), MacTheme.S(7))) {
                using (var b = new SolidBrush(Color.FromArgb(240, 242, 245))) g.FillPath(b, path);
            }
            Gfx.Text(g, Entry.vk, f, MacTheme.TextTertiary, badge, StringAlignment.Center);
        }
    }
}

// ---- bundled kernel-driver installer (runs the RemoteMapper scripts elevated) --
class DriverInstallDialog : Form {
    readonly Font body;
    readonly string driverDir;

    public DriverInstallDialog() {
        Text = "安装内核驱动（高级）";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = MacTheme.ContentBg;
        int w = MacTheme.S(560), h = MacTheme.S(430);
        MinimumSize = new Size(w, h); MaximumSize = new Size(w, h); Size = new Size(w, h);
        ShowInTaskbar = false;
        MaximizeBox = false; MinimizeBox = false;
        DoubleBuffered = true;
        Font = MacTheme.Font(9.5f);
        body = MacTheme.Font(9f);

        driverDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "driver", "MiRemoteHidFilter");

        var step1 = new MacButton("第 1 步：开启测试签名（然后重启电脑）", false, false) { Width = MacTheme.S(300) };
        step1.Clicked += delegate {
            bool? sb = SecureBootEnabled();
            if (sb == true) {
                var r = MessageBox.Show(this,
                    "你的电脑开启了 Secure Boot，测试签名无法开启，测试签名路线在当前设置下走不通。\n\n" +
                    "如果坚持这条路线：重启进入 UEFI/BIOS 关闭 Secure Boot，再回来执行本步骤。\n" +
                    "注意：① 若系统盘启用了 BitLocker，请先备份 48 位恢复密钥（改 Secure Boot 可能触发恢复密钥输入）；\n" +
                    "② 部分要求 Secure Boot 的反作弊游戏会无法运行；③ 不想折腾可放弃，程序其余功能不受影响。\n\n" +
                    "仍要继续执行吗？",
                    "Secure Boot 已开启", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            RunScript("prepare-test-mode.bat");
        };

        var step2 = new MacButton("第 2 步：安装驱动（重启回来后执行，再重启一次）", false, false) { Width = MacTheme.S(420) };
        step2.Clicked += delegate { RunScript("install-driver.bat"); };

        var uninstallBtn = new MacButton("卸载驱动", false, true) { Width = MacTheme.S(110) };
        uninstallBtn.Clicked += delegate { RunScript("uninstall-driver.bat"); };

        var closeBtn = new MacButton("关闭", false, false) { Width = MacTheme.S(80) };
        closeBtn.Clicked += delegate { Close(); };

        int by = Height - MacTheme.S(64);
        step1.Location = new Point(MacTheme.S(28), by);
        step2.Location = new Point(MacTheme.S(28), by - MacTheme.S(44));
        uninstallBtn.Location = new Point(MacTheme.S(28), Height - MacTheme.S(52));
        closeBtn.Location = new Point(Width - MacTheme.S(28) - closeBtn.Width, Height - MacTheme.S(52));

        Controls.Add(step1); Controls.Add(step2); Controls.Add(uninstallBtn); Controls.Add(closeBtn);
    }

    /// True/False when Confirm-SecureBootUEFI answers, null when unknown
    /// (needs admin on some systems - then the script does its own check).
    System.Nullable<bool> SecureBootEnabled() {
        try {
            var psi = new System.Diagnostics.ProcessStartInfo {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Confirm-SecureBootUEFI\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var p = System.Diagnostics.Process.Start(psi)) {
                string outp = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(15000);
                if (outp.IndexOf("True", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (outp.IndexOf("False", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }
        } catch { }
        return null;
    }

    void RunScript(string bat) {
        string path = System.IO.Path.Combine(driverDir, bat);
        if (!System.IO.File.Exists(path)) {
            MessageBox.Show(this, "未找到 " + path + "\n请确认程序目录下带有 driver 文件夹（完整发行包自带）。", "缺少驱动文件");
            return;
        }
        try {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = path,
                WorkingDirectory = driverDir,
                UseShellExecute = true
            });                                                  // 脚本自带 fltmc 检查 + UAC 自提权
            Log.Info("[DRIVER] launched " + bat);
        } catch (Exception ex) {
            Log.Error("[DRIVER] " + ex.Message);
            MessageBox.Show(this, "启动脚本失败：" + ex.Message, "错误");
        }
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var b = new SolidBrush(MacTheme.ContentBg)) g.FillRectangle(b, ClientRectangle);
        int x = MacTheme.S(28);
        int y = MacTheme.S(24);

        Action<string, Font, Color> line = delegate(string s, Font f, Color c) {
            Gfx.Text(g, s, f, c, new RectangleF(x, y, Width - MacTheme.S(56), MacTheme.S(18)), StringAlignment.Near);
            y += MacTheme.S(20);
        };

        line("作用", MacTheme.Font(10f, FontStyle.Bold), MacTheme.TextPrimary);
        line("把 返回 / 主页 / 菜单 / 直播 / 电源 / 音量± 这 7 个键转换为 F13-F19，让 Windows 能收到，", body, MacTheme.TextSecondary);
        line("按键映射页里已保存的配置随即自动生效。驱动来自 RemoteMapper 项目（GPL-3.0，已精确绑定本遥控器）。", body, MacTheme.TextSecondary);
        y += MacTheme.S(4);
        line("要求与影响（请仔细阅读）", MacTheme.Font(10f, FontStyle.Bold), MacTheme.TextPrimary);
        line("1. 需要管理员权限（脚本会弹出 UAC 确认）。", body, MacTheme.TextSecondary);
        line("2. 需要开启 Windows 测试签名：桌面右下角会出现“测试模式”水印；", body, MacTheme.TextSecondary);
        line("   开启了 Secure Boot 的电脑不支持，此法不可用。", body, MacTheme.TextSecondary);
        line("3. 第 1 步后需要重启一次，第 2 步安装完成后还需要再重启一次。", body, MacTheme.TextSecondary);
        line("4. 遥控器需已配对。不想用了请点“卸载驱动”。", body, MacTheme.TextSecondary);
        y += MacTheme.S(4);
        line("步骤", MacTheme.Font(10f, FontStyle.Bold), MacTheme.TextPrimary);
        line("点下方按钮按顺序执行；脚本窗口会显示每一步的结果，失败会把原因打印在窗口里。", body, MacTheme.TextSecondary);
    }
}
