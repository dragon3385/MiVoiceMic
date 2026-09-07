// MacUi.cs - macOS-flavoured design system on WinForms/GDI+ (C#5, no deps):
// theme colors/fonts, rounded cards, toggle switches, segmented controls,
// buttons, sidebar nav icons, remote vector painter, combo capture box.
// 中文：设计系统 —— 主题、卡片、开关、分段控件、按钮、侧边栏图标、遥控器矢量绘制、组合键捕获框
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class MacTheme {
    // palette (light)
    public static readonly Color SidebarBg = Color.FromArgb(240, 240, 244);
    public static readonly Color ContentBg = Color.FromArgb(245, 245, 247);
    public static readonly Color CardBg = Color.White;
    public static readonly Color CardBorder = Color.FromArgb(226, 226, 232);
    public static readonly Color Accent = Color.FromArgb(0x34, 0x78, 0xF6);
    public static readonly Color AccentSoft = Color.FromArgb(214, 231, 253);
    public static readonly Color Green = Color.FromArgb(0x34, 0xC7, 0x59);
    public static readonly Color Red = Color.FromArgb(0xFF, 0x3B, 0x30);
    public static readonly Color Yellow = Color.FromArgb(0xFF, 0xCC, 0x00);
    public static readonly Color TextPrimary = Color.FromArgb(29, 29, 31);
    public static readonly Color TextSecondary = Color.FromArgb(134, 134, 139);
    public static readonly Color TextTertiary = Color.FromArgb(174, 174, 178);
    public static readonly Color Separator = Color.FromArgb(232, 232, 237);
    public static readonly Color TrackOff = Color.FromArgb(233, 233, 235);
    public static readonly Color HoverGray = Color.FromArgb(238, 238, 242);

    public const string Family = "Segoe UI";

    static float scale = 1f;
    public static float Scale { get { return scale; } }

    public static void Init() {
        int dpi = 96;
        try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = (int)g.DpiX; } catch { }
        Init(dpi);
    }

    // Scale from a real window's DeviceDpi, never the desktop DC alone: in a
    // PerMonitorV2 process the desktop DC can report 96 on a 150% screen, so
    // every S() layout shrinks while GDI still draws fonts at screen DPI -
    // widgets end up outside their cards and unclickable.
    public static void Init(int dpi) {
        scale = dpi / 96f;
        if (scale < 1f) scale = 1f;
    }

    public static int S(int px) { return (int)Math.Round(px * scale); }
    public static float S(float px) { return px * scale; }
    public static float F(float pt) { return pt * scale; }

    // NOTE: point sizes are NOT pre-multiplied by DPI here - GDI+ scales point
    // fonts by the system DPI itself. Pre-scaling made text render 2.25x at 150%.
    public static Font Font(float size, FontStyle style = FontStyle.Regular) {
        return new Font(Family, size, style, GraphicsUnit.Point);
    }
}

static class Gfx {
    public static GraphicsPath RoundRect(Rectangle r, int radius) {
        var p = new GraphicsPath();
        if (radius < 1 || r.Width < 2 || r.Height < 2) {
            p.AddRectangle(r);
            return p;
        }
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Text(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment align) {
        if (string.IsNullOrEmpty(s)) return;
        var sf = new StringFormat(StringFormat.GenericTypographic);
        sf.LineAlignment = StringAlignment.Near;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        // NOTE: DrawString with a layout Rectangle clips whole lines that don't
        // fit its height (a 15.75pt line needs ~28px at 1.5x DPI) - draw from a
        // PointF instead so tight rects never swallow text.
        PointF p = new PointF(r.Left, r.Top);
        if (align == StringAlignment.Far) {
            SizeF sz = Measure(g, s, f);
            p = new PointF(r.Right - sz.Width, r.Top);
        } else if (align == StringAlignment.Center) {
            SizeF sz = Measure(g, s, f);
            p = new PointF(r.Left + (r.Width - sz.Width) / 2f, r.Top);
        }
        g.DrawString(s, f, new SolidBrush(c), p, sf);
    }

    public static SizeF Measure(Graphics g, string s, Font f) {
        var sf = new StringFormat(StringFormat.GenericTypographic);
        return g.MeasureString(s, f, int.MaxValue, sf);
    }

    public static void ClearCrisp(Graphics g, Color c, Rectangle r) {
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Default;
        using (var b = new SolidBrush(c)) g.FillRectangle(b, r);
    }
}

// ---- base for owner-drawn widgets ------------------------------------------
class MacWidget : Control {
    public MacWidget() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }
    protected void Prep(Graphics g) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    }
    protected void InvalidateSafe() { try { Invalidate(); } catch { } }
}

// ---- toggle switch ----------------------------------------------------------
class MacToggle : MacWidget {
    public bool On;
    public event Action Toggled;

    public MacToggle(bool initial) {
        On = initial;
        Size = new Size(MacTheme.S(42), MacTheme.S(26));
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        On = !On;
        InvalidateSafe();
        if (Toggled != null) Toggled();
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Prep(g);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(r, Height / 2)) {
            using (var b = new SolidBrush(On ? MacTheme.Accent : MacTheme.TrackOff)) g.FillPath(b, path);
        }
        int d = Height - MacTheme.S(4);
        int x = On ? Width - d - MacTheme.S(2) : MacTheme.S(2);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, MacTheme.S(2), d, d);
        var shadow = new Rectangle(x, MacTheme.S(2), d, d);
        using (var pen = new Pen(Color.FromArgb(30, 0, 0, 0), MacTheme.Scale)) g.DrawEllipse(pen, shadow);
    }
}

// ---- segmented control ------------------------------------------------------
class MacSegmented : MacWidget {
    public List<string> Items = new List<string>();
    public int Selected;
    public event Action Changed;

    public MacSegmented(IEnumerable<string> items, int selected) {
        Items = new List<string>(items);
        Selected = selected;
        Height = MacTheme.S(28);
        Cursor = Cursors.Hand;
    }

    public string Value {
        get { return Selected >= 0 && Selected < Items.Count ? Items[Selected] : ""; }
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        var rects = SegmentRects();
        for (int i = 0; i < rects.Count; i++)
            if (rects[i].Contains(e.X, e.Y) && i != Selected) {
                Selected = i;
                InvalidateSafe();
                if (Changed != null) Changed();
                break;
            }
    }

    List<Rectangle> SegmentRects() {
        var list = new List<Rectangle>();
        if (Items.Count == 0) return list;
        int gap = MacTheme.S(2);
        int w = (Width - gap * (Items.Count - 1)) / Items.Count;
        int x = 0;
        for (int i = 0; i < Items.Count; i++) {
            list.Add(new Rectangle(x, 0, w, Height));
            x += w + gap;
        }
        return list;
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Prep(g);
        var rects = SegmentRects();
        using (var f = MacTheme.Font(9.75f)) {
            for (int i = 0; i < rects.Count; i++) {
                var r = rects[i];
                using (var path = Gfx.RoundRect(r, MacTheme.S(7))) {
                    using (var b = new SolidBrush(i == Selected ? MacTheme.Accent : MacTheme.TrackOff)) g.FillPath(b, path);
                }
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(Items[i], f, new SolidBrush(i == Selected ? Color.White : MacTheme.TextSecondary),
                    new RectangleF(r.X, r.Y - MacTheme.Scale, r.Width, r.Height), sf);
            }
        }
    }
}

// ---- buttons ----------------------------------------------------------------
class MacButton : MacWidget {
    public bool Primary;
    public bool Small;
    public string Caption = "";

    public event Action Clicked;

    public MacButton(string caption, bool primary, bool small) {
        Caption = caption;
        Primary = primary;
        Small = small;
        Height = MacTheme.S(small ? 26 : 32);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; InvalidateSafe(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; InvalidateSafe(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; InvalidateSafe(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) {
        pressed = false; InvalidateSafe();
        if (hovered && Clicked != null) Clicked();
        base.OnMouseUp(e);
    }
    bool hovered, pressed;

    public void PerformClick() { if (Clicked != null) Clicked(); }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Prep(g);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill = Primary ? MacTheme.Accent : Color.White;
        if (Primary && hovered) fill = Color.FromArgb(0x2F, 0x6B, 0xE0);
        if (!Primary && hovered) fill = MacTheme.HoverGray;
        if (pressed) fill = Primary ? Color.FromArgb(0x2A, 0x60, 0xCC) : Color.FromArgb(230, 230, 235);
        using (var path = Gfx.RoundRect(r, MacTheme.S(8))) {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var pen = new Pen(Primary ? fill : Color.FromArgb(208, 208, 214))) g.DrawPath(pen, path);
        }
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using (var f = MacTheme.Font(Small ? 9f : 9.75f)) {
            g.DrawString(Caption, f, new SolidBrush(Primary ? Color.White : MacTheme.TextPrimary),
                new RectangleF(r.X, r.Y - MacTheme.Scale, r.Width, r.Height), sf);
        }
    }
}

// ---- card panel -------------------------------------------------------------
class MacCard : Panel {
    public string Title = "";
    /// Card-local content painting (coordinates relative to the card, so child
    /// controls like toggles can never cover it - it paints WITH the card).
    public event Action<Graphics> PaintContent;

    public MacCard() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = MacTheme.ContentBg;
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Gfx.RoundRect(r, MacTheme.S(10))) {
            using (var b = new SolidBrush(MacTheme.CardBg)) g.FillPath(b, path);
            using (var pen = new Pen(MacTheme.CardBorder)) g.DrawPath(pen, path);
        }
        if (Title.Length > 0) {
            using (var f = MacTheme.Font(11f, FontStyle.Bold)) {
                Gfx.Text(g, Title, f, MacTheme.TextPrimary,
                    new RectangleF(MacTheme.S(16), MacTheme.S(12), Width - MacTheme.S(32), MacTheme.S(22)), StringAlignment.Near);
            }
        }
        var h = PaintContent;
        if (h != null) { try { h(g); } catch (Exception ex) { Log.Error("[CARD] paint: " + ex.Message); } }
        base.OnPaint(e);
    }
}

// ---- sidebar nav icon painter ------------------------------------------------
static class NavIcons {
    public static void Draw(Graphics g, string kind, Rectangle r, Color c) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(c, Math.Max(1.4f, 1.6f * MacTheme.Scale))) {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            int w = r.Width, h = r.Height;
            if (kind == "link") {                     // chain link
                g.DrawArc(pen, r.X + w / 10, r.Y + h / 4, w * 3 / 10, h / 2, 120, 300);
                g.DrawArc(pen, r.X + w * 6 / 10, r.Y + h / 4, w * 3 / 10, h / 2, 300, 300);
                g.DrawLine(pen, r.X + w * 38 / 100, r.Y + h * 62 / 100, r.X + w * 62 / 100, r.Y + h * 38 / 100);
            } else if (kind == "keys") {              // keyboard
                var kb = new Rectangle(r.X, r.Y + h / 4, w, h / 2);
                g.DrawPath(pen, Gfx.RoundRect(kb, MacTheme.S(3)));
                int dot = Math.Max(1, MacTheme.S(2));
                using (var b = new SolidBrush(c)) {
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 2; j++)
                            g.FillEllipse(b, kb.X + kb.Width * (2 + i * 3) / 11 - dot, kb.Y + kb.Height * (1 + j * 2) / 4 - dot, dot * 2, dot * 2);
                    g.FillEllipse(b, kb.X + kb.Width / 2 - MacTheme.S(5), kb.Y + kb.Height * 3 / 4 - dot, MacTheme.S(10), dot * 2);
                }
            } else if (kind == "check") {            // checklist
                g.DrawPath(pen, Gfx.RoundRect(new Rectangle(r.X, r.Y, w, h), MacTheme.S(4)));
                g.DrawLine(pen, r.X + w / 5, r.Y + h * 3 / 10, r.X + w * 4 / 10, r.Y + h * 3 / 5);
                g.DrawLine(pen, r.X + w * 4 / 10, r.Y + h * 3 / 5, r.X + w * 4 / 5, r.Y + h / 5);
                g.DrawLine(pen, r.X + w / 5, r.Y + h * 3 / 4, r.X + w * 4 / 5, r.Y + h * 3 / 4);
            } else if (kind == "info") {             // info
                g.DrawEllipse(pen, r);
                int dot = Math.Max(1, MacTheme.S(1));
                g.FillEllipse(new SolidBrush(c), r.X + w / 2 - dot, r.Y + h / 4, dot * 2, dot * 2);
                g.DrawLine(pen, r.X + w / 2, r.Y + h * 9 / 20, r.X + w / 2, r.Y + h * 3 / 4);
            }
        }
    }
}

// ---- remote vector painter ----------------------------------------------------
// Virtual layout space is 100 x 300; scale into the target rect. Key hotspots
// (centres, virtual coords) are exposed for mapping-card connectors.
static class RemotePainter {
    // Virtual layout space is 100 x 300, matched to the real RC003 remote:
    //   top row: power (left) + voice/mic (right)
    //   middle:  one large dark disc - D-pad dots on the edge, OK in the center
    //   bottom:  two columns - back/home/menu | vol+ / vol- (shared pill) / TV
    public const float VW = 100f, VH = 300f;

    static readonly System.Collections.Generic.Dictionary<string, PointF> spots = BuildHotspots();

    static System.Collections.Generic.Dictionary<string, PointF> BuildHotspots() {
        var d = new System.Collections.Generic.Dictionary<string, PointF>();
        d["voice"] = new PointF(70f, 30f);
        d["up"] = new PointF(50f, 77f);
        d["down"] = new PointF(50f, 133f);
        d["left"] = new PointF(22f, 105f);
        d["right"] = new PointF(78f, 105f);
        d["ok"] = new PointF(50f, 105f);
        // driver-required keys (no input path without the KMDF driver)
        d["back"] = new PointF(31f, 172f);
        d["home"] = new PointF(31f, 212f);
        d["volup"] = new PointF(70f, 176f);
        d["voldown"] = new PointF(70f, 208f);
        d["menu"] = new PointF(31f, 252f);
        d["tv"] = new PointF(70f, 252f);
        d["power"] = new PointF(30f, 30f);
        return d;
    }

    public static System.Collections.Generic.Dictionary<string, PointF> Hotspots() { return spots; }

    // palette
    static readonly Color BodyTop = Color.FromArgb(252, 252, 253);
    static readonly Color BodyBot = Color.FromArgb(233, 233, 238);
    static readonly Color BodyEdge = Color.FromArgb(206, 206, 212);
    static readonly Color KeyDark = Color.FromArgb(43, 43, 47);
    static readonly Color KeyDarkEdge = Color.FromArgb(28, 28, 30);
    static readonly Color Glyph = Color.FromArgb(224, 224, 228);
    static readonly Color GlyphDim = Color.FromArgb(150, 150, 158);
    static readonly Color DotColor = Color.FromArgb(96, 96, 104);

    public static void Draw(Graphics g, RectangleF area, bool talking, string flashKey) {
        Draw(g, area, talking, flashKey, null);
    }

    /// <param name="lit">key ids currently lit by live remote key activity
    /// (pressed/held); rendered like the action flash, for every key.</param>
    public static void Draw(Graphics g, RectangleF area, bool talking, string flashKey, ICollection<string> lit) {
        Func<string, bool> hot = delegate (string id) {
            return flashKey == id || (lit != null && lit.Contains(id));
        };
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float k = Math.Min(area.Width / VW, area.Height / VH);
        float ox = area.X + (area.Width - VW * k) / 2f;
        float oy = area.Y + (area.Height - VH * k) / 2f;
        Func<float, float, PointF> M = delegate (float vx, float vy) {
            return new PointF(ox + vx * k, oy + vy * k);
        };
        using (Pen glyphPen = new Pen(Glyph, Math.Max(1.2f, 1.5f * k)))
        using (Pen dimPen = new Pen(GlyphDim, Math.Max(1.1f, 1.3f * k)))
        using (Pen hotPen = new Pen(MacTheme.Accent, Math.Max(1.4f, 1.8f * k))) {
            glyphPen.StartCap = LineCap.Round; glyphPen.EndCap = LineCap.Round;
            dimPen.StartCap = LineCap.Round; dimPen.EndCap = LineCap.Round;
            hotPen.StartCap = LineCap.Round; hotPen.EndCap = LineCap.Round;

            // dark circular key (bottom grid); hot = accent ring like the OK key
            Action<float, float, float, bool> circleKey = delegate (float cx, float cy, float r, bool isHot) {
                var rc = new RectangleF(M(cx - r, cy - r).X, M(cx - r, cy - r).Y, 2 * r * k, 2 * r * k);
                using (var b = new LinearGradientBrush(rc, Color.FromArgb(52, 52, 56), KeyDark, 55f)) g.FillEllipse(b, rc);
                using (var p2 = new Pen(isHot ? MacTheme.Accent : KeyDarkEdge, isHot ? Math.Max(1.4f, 1.8f * k) : 1f)) g.DrawEllipse(p2, rc);
            };

            // ---- body ----
            var body = new RectangleF(ox + 4 * k, oy, (VW - 8) * k, VH * k);
            using (var path = RoundedF(body, (int)(13 * k))) {
                using (var lg = new LinearGradientBrush(body, BodyTop, BodyBot, 90f)) g.FillPath(lg, path);
                using (var pen = new Pen(BodyEdge)) g.DrawPath(pen, path);
            }

            // ---- top row: power (dead) + voice ----
            circleKey(30, 30, 9.5f, hot("power"));
            {
                var c = M(30, 30);
                g.DrawArc(dimPen, c.X - 4 * k, c.Y - 4 * k, 8 * k, 8 * k, -50, 285);
                g.DrawLine(dimPen, c.X, c.Y - 6.5f * k, c.X, c.Y - 1.5f * k);
            }
            bool voiceHot = talking || hot("voice");
            {
                var rc = new RectangleF(M(60.5f, 20.5f).X, M(60.5f, 20.5f).Y, 19 * k, 19 * k);
                using (var b = new SolidBrush(voiceHot ? MacTheme.Accent : Color.FromArgb(52, 52, 56))) g.FillEllipse(b, rc);
                using (var p2 = new Pen(voiceHot ? Color.FromArgb(20, 90, 200) : KeyDarkEdge)) g.DrawEllipse(p2, rc);
                var c = M(70, 30);
                using (var p2 = new Pen(Color.White, Math.Max(1.2f, 1.4f * k))) {
                    p2.StartCap = LineCap.Round; p2.EndCap = LineCap.Round;
                    using (var cap = RoundedF(new RectangleF(c.X - 2.2f * k, c.Y - 6f * k, 4.4f * k, 7.5f * k), (int)Math.Max(1, 2.2f * k))) {
                        using (var fb = new SolidBrush(Color.White)) g.FillPath(fb, cap);
                    }
                    g.DrawArc(p2, c.X - 4.2f * k, c.Y - 2.5f * k, 8.4f * k, 6f * k, 10, 160);
                    g.DrawLine(p2, c.X, c.Y + 3.5f * k, c.X, c.Y + 5.5f * k);
                }
            }

            // ---- middle: one big dark disc (D-pad edge dots + OK center) ----
            {
                var rc = new RectangleF(M(17, 72).X, M(17, 72).Y, 66 * k, 66 * k);
                using (var lg = new LinearGradientBrush(rc, Color.FromArgb(58, 58, 63), Color.FromArgb(34, 34, 37), 90f)) g.FillEllipse(lg, rc);
                using (var p2 = new Pen(KeyDarkEdge, Math.Max(1f, 1.2f * k))) g.DrawEllipse(p2, rc);
            }
            // D-pad edge dots (tiny, like the hardware); ring for the active direction
            string[] dotIds = { "up", "down", "left", "right" };
            for (int i = 0; i < dotIds.Length; i++) {
                var d = spots[dotIds[i]];
                var c = M(d.X, d.Y);
                bool hotDot = hot(dotIds[i]);
                float dr = Math.Max(1.5f, 1.6f * k);
                using (var b = new SolidBrush(hotDot ? MacTheme.Accent : DotColor))
                    g.FillEllipse(b, c.X - dr, c.Y - dr, dr * 2, dr * 2);
            }
            string dirHot = hot("up") ? "up" : hot("down") ? "down" : hot("left") ? "left" : hot("right") ? "right" : null;
            if (dirHot != null) {
                PointF h = spots[dirHot];
                var c = M(h.X, h.Y);
                using (var p2 = new Pen(MacTheme.Accent, Math.Max(1.6f, 2f * k))) g.DrawEllipse(p2, c.X - 7 * k, c.Y - 7 * k, 14 * k, 14 * k);
            }
            // OK center
            {
                var rc = new RectangleF(M(36, 91).X, M(36, 91).Y, 28 * k, 28 * k);
                bool okHot = hot("ok");
                using (var lg = new LinearGradientBrush(rc, Color.FromArgb(64, 64, 70), Color.FromArgb(44, 44, 48), 90f)) g.FillEllipse(lg, rc);
                using (var p2 = new Pen(okHot ? MacTheme.Accent : Color.FromArgb(24, 24, 26), Math.Max(1f, okHot ? 2f * k : 1.2f * k))) g.DrawEllipse(p2, rc);
                DrawCentered(g, "OK", MacTheme.Font(8.5f, FontStyle.Bold), okHot ? MacTheme.Accent : Color.FromArgb(168, 168, 176), rc);
            }

            // ---- bottom grid: left column back / home / menu ----
            circleKey(31, 172, 13f, hot("back"));
            {
                var c = M(31, 172);
                g.DrawLine(glyphPen, c.X + 2.5f * k, c.Y - 5f * k, c.X - 2.5f * k, c.Y);
                g.DrawLine(glyphPen, c.X - 2.5f * k, c.Y, c.X + 2.5f * k, c.Y + 5f * k);
            }
            circleKey(31, 212, 13f, hot("home"));
            {
                var c = M(31, 212);
                g.DrawLine(glyphPen, c.X - 5.5f * k, c.Y + 0.5f * k, c.X, c.Y - 5f * k);
                g.DrawLine(glyphPen, c.X, c.Y - 5f * k, c.X + 5.5f * k, c.Y + 0.5f * k);
                g.DrawLine(glyphPen, c.X - 4f * k, c.Y - 1f * k, c.X - 4f * k, c.Y + 5f * k);
                g.DrawLine(glyphPen, c.X + 4f * k, c.Y - 1f * k, c.X + 4f * k, c.Y + 5f * k);
                g.DrawLine(glyphPen, c.X - 4f * k, c.Y + 5f * k, c.X + 4f * k, c.Y + 5f * k);
            }
            circleKey(31, 252, 13f, hot("menu"));
            {
                var c = M(31, 252);
                for (int i = -1; i <= 1; i++)
                    g.DrawLine(glyphPen, c.X - 5f * k, c.Y + i * 3.2f * k, c.X + 5f * k, c.Y + i * 3.2f * k);
            }

            // ---- bottom grid: right column vol pill (+ / -) and TV ----
            {
                bool volUpHot = hot("volup"), volDownHot = hot("voldown");
                var pill = new RectangleF(M(55, 160).X, M(55, 160).Y, 30 * k, 64 * k);
                using (var path = RoundedF(pill, (int)Math.Max(1, 15 * k))) {
                    using (var lg = new LinearGradientBrush(pill, Color.FromArgb(52, 52, 56), KeyDark, 55f)) g.FillPath(lg, path);
                    using (var p2 = new Pen(volUpHot || volDownHot ? MacTheme.Accent : KeyDarkEdge, volUpHot || volDownHot ? Math.Max(1.4f, 1.8f * k) : 1f)) g.DrawPath(p2, path);
                }
                var cp = M(70, 176);
                Pen vp = volUpHot ? hotPen : glyphPen;           // borrowed, NOT disposed here - glyphPen draws the TV glyph below
                g.DrawLine(vp, cp.X - 4.5f * k, cp.Y, cp.X + 4.5f * k, cp.Y);
                g.DrawLine(vp, cp.X, cp.Y - 4.5f * k, cp.X, cp.Y + 4.5f * k);
                var cm = M(70, 208);
                g.DrawLine(volDownHot ? hotPen : glyphPen, cm.X - 4.5f * k, cm.Y, cm.X + 4.5f * k, cm.Y);
            }
            circleKey(70, 252, 13f, hot("tv"));
            {
                var c = M(70, 252);
                using (var p2 = (Pen)glyphPen) {
                    var tvRect = new RectangleF(c.X - 6f * k, c.Y - 5f * k, 12f * k, 10f * k);
                    using (var path = RoundedF(tvRect, (int)Math.Max(1, 2.5f * k))) g.DrawPath(p2, path);
                }
                using (var f = new Font(MacTheme.Family, Math.Max(4.5f, 4.5f * k * MacTheme.Scale), FontStyle.Bold, GraphicsUnit.Point)) {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    g.DrawString("TV", f, new SolidBrush(Glyph), new RectangleF(c.X - 6f * k, c.Y - 5.5f * k, 12f * k, 10.5f * k), sf);
                }
            }

            // brand text
            DrawCentered(g, "XIAOMI", MacTheme.Font(6f), Color.FromArgb(178, 178, 186),
                new RectangleF(M(20, 280).X, M(20, 280).Y, 60 * k, 10 * k));
        }
    }

    static GraphicsPath RoundedF(RectangleF r, int radius) {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d < 2) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void DrawCentered(Graphics g, string s, Font f, Color c, RectangleF r) {
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.DrawString(s, f, new SolidBrush(c), r, sf);
    }
}

// ---- combo capture box -------------------------------------------------------
// Focus-based combo capture: clicking focuses the control; the pressed key
// (WinForms KeyDown) plus its modifiers become the combo. No global keyboard
// hook involved - this survives dialogs, AV software and IME interference.
class ComboCaptureBox : MacWidget {
    public string Value = "";
    public event Action ValueChanged;
    bool armed;
    /// True while waiting for the user to press the combo.
    public bool Capturing { get { return armed; } }
    readonly List<ushort> held = new List<ushort>();

    public ComboCaptureBox(string initial) {
        Value = initial ?? "";
        Height = MacTheme.S(30);
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    static bool IsModifier(ushort vk) {
        // raw left/right codes AND the generic Shift/Ctrl/Alt codes WinForms
        // reports (0x10/0x11/0x12) depending on key sequence
        if (vk >= 0xA0 && vk <= 0xA5) return true;
        if (vk == 0x5B || vk == 0x5C) return true;
        return vk == 0x10 || vk == 0x11 || vk == 0x12;
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        if (armed) return;
        Focus();
        armed = true;                                        // capture starts on CLICK only
        held.Clear();
        InvalidateSafe();
    }

    protected override void OnLostFocus(EventArgs e) {
        base.OnLostFocus(e);
        armed = false;
        held.Clear();
        InvalidateSafe();
    }

    protected override bool IsInputKey(Keys keyData) {
        // while armed, keep every key (Tab/arrows/Esc included) inside the box
        return armed ? true : base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        if (!armed) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        if (e.KeyCode == Keys.Escape) {                      // Esc = cancel this capture
            armed = false;
            held.Clear();
            InvalidateSafe();
            return;
        }
        ushort vk = (ushort)e.KeyValue;
        if (vk == 0) return;
        if (IsModifier(vk)) {
            // track only the raw left/right codes for display; generic
            // Shift/Ctrl/Alt codes are already covered by e.Modifiers below
            if (vk >= 0xA0 || vk == 0x5B || vk == 0x5C) {
                if (!held.Contains(vk)) held.Add(vk);
            }
            InvalidateSafe();
            return;
        }
        var combo = new List<ushort>();
        if ((e.Modifiers & Keys.Control) != 0) combo.Add(0xA2);
        if ((e.Modifiers & Keys.Shift) != 0) combo.Add(0xA0);
        if ((e.Modifiers & Keys.Alt) != 0) combo.Add(0xA4);
        if ((e.Modifiers & Keys.LWin) != 0 || (e.Modifiers & Keys.RWin) != 0) combo.Add(0x5B);
        foreach (ushort m in held) if (!combo.Contains(m)) combo.Add(m);
        combo.Add(vk);
        Value = KeyMapNames.FormatCombo(combo.ToArray());
        armed = false;
        held.Clear();
        InvalidateSafe();
        if (ValueChanged != null) ValueChanged();
    }

    protected override void OnKeyUp(KeyEventArgs e) {
        base.OnKeyUp(e);
        if (!armed) return;
        e.Handled = true;
        held.Remove((ushort)e.KeyValue);
        InvalidateSafe();
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Prep(g);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        bool focused = armed;
        using (var path = Gfx.RoundRect(r, MacTheme.S(7))) {
            using (var b = new SolidBrush(focused ? MacTheme.AccentSoft : Color.White)) g.FillPath(b, path);
            using (var pen = new Pen(focused ? MacTheme.Accent : Color.FromArgb(208, 208, 214))) g.DrawPath(pen, path);
        }
        string text;
        Color color = focused ? MacTheme.Accent : MacTheme.TextPrimary;
        if (armed) {
            var sb = new System.Text.StringBuilder();
            foreach (ushort vk in held) sb.Append(KeyMapNames.Friendly(vk)).Append(" + ");
            text = (sb.Length > 0 ? sb.ToString(0, sb.Length - 3) : "按下组合键") + "  ·  Esc 取消";
        } else {
            ushort[] combo;
            text = KeyMapNames.TryParseCombo(Value, out combo) && combo.Length > 0
                ? KeyMapNames.FriendlyCombo(combo) + "（点击可修改）"
                : "点击捕获组合键";
        }
        using (var f = MacTheme.Font(9.5f)) {
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, f, new SolidBrush(color), new RectangleF(r.X, r.Y - MacTheme.Scale, r.Width, r.Height), sf);
        }
    }
}
