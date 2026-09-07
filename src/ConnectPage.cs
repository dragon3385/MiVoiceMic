// ConnectPage.cs - 连接与语音: device card (remote art, status, battery, level
// meter, usage stats) + fully interactive voice-hotkey / audio / remote cards.
// 中文：连接与语音页 —— 设备卡 + 语音热键/音频增益/遥控器行为三张可交互设置卡
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

class MacSlider : MacWidget {
    public int Min = -24, Max = 24, Value_;
    public bool EnabledLook = true;
    public event Action Changed;

    public MacSlider(int initial) { Value_ = initial; Height = MacTheme.S(22); }

    int PosFromX(int x) {
        int pad = MacTheme.S(10);
        int w = Width - pad * 2;
        if (w <= 0) return Min;
        int t = (int)Math.Round((double)(x - pad) / w * (Max - Min)) + Min;
        return Math.Max(Min, Math.Min(Max, t));
    }
    int XFromVal(int v) {
        int pad = MacTheme.S(10);
        int w = Width - pad * 2;
        return pad + (int)Math.Round((double)(v - Min) / (Max - Min) * w);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        Value_ = PosFromX(e.X);
        InvalidateSafe();
        if (Changed != null) Changed();
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        if (e.Button != MouseButtons.Left) return;
        Value_ = PosFromX(e.X);
        InvalidateSafe();
        if (Changed != null) Changed();
    }

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        Prep(g);
        int y = Height / 2;
        int x0 = MacTheme.S(10), x1 = Width - MacTheme.S(10);
        int kx = XFromVal(Value_);
        Color track = EnabledLook ? MacTheme.Separator : Color.FromArgb(240, 240, 243);
        Color fill = EnabledLook ? MacTheme.Accent : Color.FromArgb(200, 205, 214);
        using (var pen = new Pen(track, MacTheme.S(4))) { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawLine(pen, x0, y, x1, y); }
        using (var pen = new Pen(fill, MacTheme.S(4))) { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawLine(pen, x0, y, kx, y); }
        int d = MacTheme.S(16);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, kx - d / 2, y - d / 2, d, d);
        using (var pen = new Pen(EnabledLook ? Color.FromArgb(208, 214, 224) : Color.FromArgb(225, 225, 230))) g.DrawEllipse(pen, kx - d / 2, y - d / 2, d, d);
    }
}

class ConnectPage : MacPage {
    MacCard deviceCard, hotkeyCard, audioCard, remoteCard;
    MacSegmented presetSeg, modeSeg;
    MacToggle agcToggle, micToggle, dumpToggle, f5Toggle, autoPairToggle, autoStartToggle;
    MacSlider gainSlider;
    MacButton reconnectBtn, testHotkeyBtn, testToneBtn;
    ComboCaptureBox comboBox;
    MacTextBox leadBox;
    Timer leadDebounce;                                   // saves once typing pauses, not per keystroke
    string gainText = "+0 dB";
    readonly Font titleFont, cardHeadFont, bodyFont, smallFont;

    public ConnectPage(App app) : base(app) {
        titleFont = MacTheme.Font(14.5f, FontStyle.Bold);
        cardHeadFont = MacTheme.Font(10.5f, FontStyle.Bold);
        bodyFont = MacTheme.Font(9.5f);
        smallFont = MacTheme.Font(8.5f);

        deviceCard = new MacCard { Title = "设备" };
        hotkeyCard = new MacCard { Title = "语音热键" };
        audioCard = new MacCard { Title = "音频与增益" };
        remoteCard = new MacCard { Title = "遥控器行为" };
        Controls.Add(deviceCard); Controls.Add(hotkeyCard);
        Controls.Add(audioCard); Controls.Add(remoteCard);

        // ---- hotkey card ----
        presetSeg = new MacSegmented(new[] { "微信输入法", "Win+H", "自定义", "不注入" }, PresetIndex());
        presetSeg.Changed += delegate { ApplyPreset(presetSeg.Selected); };

        comboBox = new ComboCaptureBox(ComboText());
        comboBox.ValueChanged += delegate { OnComboEdited(); };

        modeSeg = new MacSegmented(new[] { "按住跟随", "点按开关" },
            App.Config.hotkey.mode.Equals("tap", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        modeSeg.Changed += delegate { OnModeEdited(); };

        leadBox = new MacTextBox(App.Config.switchLeadMs.ToString()) { Width = MacTheme.S(74) };
        leadDebounce = new Timer { Interval = 400 };
        leadDebounce.Tick += delegate { leadDebounce.Stop(); OnLeadChanged(); };
        leadBox.Box.TextChanged += delegate { leadDebounce.Stop(); leadDebounce.Start(); };

        testHotkeyBtn = new MacButton("测试热键", false, true) { Width = MacTheme.S(120) };
        testHotkeyBtn.Clicked += delegate { TestHotkey(); };

        // ---- audio card ----
        agcToggle = new MacToggle(App.Config.agc);
        agcToggle.Toggled += delegate {
            App.Config.agc = agcToggle.On;
            App.SetAudioGain(agcToggle.On, App.Config.gainDb);
            Save(); RefreshAudioUI();
        };
        gainSlider = new MacSlider((int)App.Config.gainDb);
        gainSlider.Changed += delegate {
            if (App.Config.agc) {                       // dragging the slider turns AGC off
                App.Config.agc = false;
                agcToggle.On = false;
            }
            App.Config.gainDb = gainSlider.Value_;
            App.SetAudioGain(App.Config.agc, App.Config.gainDb);
            gainText = (gainSlider.Value_ >= 0 ? "+" : "") + gainSlider.Value_ + " dB";
            Save();
        };
        testToneBtn = new MacButton("发送 1 秒测试音", false, true) { Width = MacTheme.S(150) };
        testToneBtn.Clicked += delegate { App.PlayTestTone(); };

        micToggle = new MacToggle(App.Config.switchDefaultMic);
        micToggle.Toggled += delegate { App.Config.switchDefaultMic = micToggle.On; Save(); };
        dumpToggle = new MacToggle(App.Config.dumpAudio);
        dumpToggle.Toggled += delegate { App.Config.dumpAudio = dumpToggle.On; Save(); };

        // ---- remote card ----
        f5Toggle = new MacToggle(App.Config.blockF5);
        f5Toggle.Toggled += delegate { App.Config.blockF5 = f5Toggle.On; Save(); };
        autoPairToggle = new MacToggle(App.Config.autoPair);
        autoPairToggle.Toggled += delegate { App.Config.autoPair = autoPairToggle.On; Save(); };
        autoStartToggle = new MacToggle(AutoStart.IsEnabled());
        autoStartToggle.Toggled += delegate {
            AutoStart.Set(autoStartToggle.On);
            Log.Info("[UI] 开机自启 -> " + (autoStartToggle.On ? "开" : "关"));
        };

        reconnectBtn = new MacButton("重新连接", false, false);
        reconnectBtn.Width = MacTheme.S(130);
        reconnectBtn.Clicked += delegate {
            try { App.Reconnect(); } catch (Exception ex) { Log.Error("[UI] reconnect: " + ex.Message); }
        };

        Controls.Add(presetSeg); Controls.Add(comboBox); Controls.Add(modeSeg); Controls.Add(leadBox);
        Controls.Add(testHotkeyBtn); Controls.Add(testToneBtn);
        Controls.Add(agcToggle); Controls.Add(gainSlider);
        Controls.Add(micToggle); Controls.Add(dumpToggle);
        Controls.Add(f5Toggle); Controls.Add(autoPairToggle); Controls.Add(autoStartToggle);
        Controls.Add(reconnectBtn);

        // cards were added before the widgets, so they sit ABOVE them in the
        // z-order and hide/block every toggle, slider and button - push the
        // cards (pure painting panels) behind the interactive controls.
        deviceCard.SendToBack(); hotkeyCard.SendToBack();
        audioCard.SendToBack(); remoteCard.SendToBack();

        deviceCard.PaintContent += PaintDeviceContent;
        hotkeyCard.PaintContent += PaintHotkeyContent;
        audioCard.PaintContent += PaintAudioContent;
        remoteCard.PaintContent += PaintRemoteContent;

        RefreshAudioUI();
    }

    void Save() {
        try { App.Config.Save(); App.ApplyConfig(App.Config); } catch (Exception ex) { Log.Error("[UI] save: " + ex.Message); }
    }

    int PresetIndex() {
        var cfg = App.Config;
        if (!cfg.hotkeyEnabled) return 3;
        if (cfg.hotkey.preset == "wetype" || cfg.hotkey.preset == "winh") return cfg.hotkey.preset == "wetype" ? 0 : 1;
        return 2;
    }

    string ComboText() { return string.Join("+", App.Config.hotkey.keys.ToArray()); }

    void ApplyPreset(int idx) {
        var cfg = App.Config;
        if (idx == 0) {
            cfg.hotkey.mode = "hold";
            cfg.hotkey.keys = new System.Collections.Generic.List<string> { "LCTRL", "LWIN" };
            cfg.hotkey.preset = "wetype"; cfg.hotkeyEnabled = true;
        } else if (idx == 1) {
            cfg.hotkey.mode = "tap";
            cfg.hotkey.keys = new System.Collections.Generic.List<string> { "LWIN", "H" };
            cfg.hotkey.preset = "winh"; cfg.hotkeyEnabled = true;
        } else if (idx == 2) {
            cfg.hotkey.preset = "custom"; cfg.hotkeyEnabled = true;
        } else {
            cfg.hotkeyEnabled = false; cfg.hotkey.preset = "none";
        }
        comboBox.Value = ComboText();
        modeSeg.Selected = cfg.hotkey.mode.Equals("tap", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Save();
        Invalidate();
    }

    // editing the combo or injection mode switches to the custom preset
    void OnComboEdited() {
        var cfg = App.Config;
        ushort[] parsed;
        if (!KeyMapNames.TryParseCombo(comboBox.Value, out parsed) || parsed.Length == 0) return;
        var names = new System.Collections.Generic.List<string>();
        foreach (ushort v in parsed) names.Add(KeyMapNames.Name(v));
        cfg.hotkey.keys = names;
        cfg.hotkey.preset = "custom";
        cfg.hotkeyEnabled = true;
        if (presetSeg.Selected != 2) presetSeg.Selected = 2;    // setter does not fire Changed
        Save();
    }

    void OnModeEdited() {
        var cfg = App.Config;
        cfg.hotkey.mode = modeSeg.Selected == 1 ? "tap" : "hold";
        if (presetSeg.Selected != 2 && presetSeg.Selected != 3) {
            cfg.hotkey.preset = "custom";
            presetSeg.Selected = 2;
        }
        Save();
    }

    void OnLeadChanged() {
        int ms;
        if (!int.TryParse(leadBox.Text_.Trim(), out ms)) return;
        ms = Math.Max(0, Math.Min(1000, ms));
        App.Config.switchLeadMs = ms;
        Save();
    }

    void TestHotkey() {
        try {
            ushort[] vks = KeyMapNames.ParseList(App.Config.hotkey.keys);
            if (vks.Length == 0) return;
            KeySender.Tap(vks);
            Log.Info("[UI] 测试热键: " + KeyMapNames.FriendlyCombo(vks));
        } catch (Exception ex) { Log.Error("[UI] test hotkey: " + ex.Message); }
    }

    void RefreshAudioUI() {
        gainSlider.EnabledLook = true;                       // always adjustable now
        gainText = (gainSlider.Value_ >= 0 ? "+" : "") + gainSlider.Value_ + " dB";
        Invalidate();
    }

    // ---- layout ----------------------------------------------------------------
    protected override void OnResize(EventArgs e) {
        base.OnResize(e);
        int pad = MacTheme.S(26);
        int titleH = MacTheme.S(60);

        int leftW = MacTheme.S(290);
        deviceCard.Bounds = new Rectangle(pad, titleH, leftW, Height - titleH - pad);

        int rightX = pad + leftW + MacTheme.S(16);
        int rightW = Width - rightX - pad;
        if (rightW < MacTheme.S(200)) rightW = MacTheme.S(200);

        hotkeyCard.Bounds = new Rectangle(rightX, titleH, rightW, MacTheme.S(192));
        audioCard.Bounds = new Rectangle(rightX, hotkeyCard.Bottom + MacTheme.S(10), rightW, MacTheme.S(216));
        remoteCard.Bounds = new Rectangle(rightX, audioCard.Bottom + MacTheme.S(10), rightW, MacTheme.S(146));

        int cx = rightX + MacTheme.S(16);
        int cw = rightW - MacTheme.S(32);

        presetSeg.Bounds = new Rectangle(cx, hotkeyCard.Top + MacTheme.S(50), cw, MacTheme.S(28));
        int halfW = (cw - MacTheme.S(12)) / 2;
        comboBox.Bounds = new Rectangle(cx, hotkeyCard.Top + MacTheme.S(86), halfW, MacTheme.S(30));
        modeSeg.Bounds = new Rectangle(cx + halfW + MacTheme.S(12), hotkeyCard.Top + MacTheme.S(86), halfW, MacTheme.S(28));
        leadBox.Location = new Point(cx + MacTheme.S(150), hotkeyCard.Top + MacTheme.S(124));
        testHotkeyBtn.Location = new Point(rightX + rightW - MacTheme.S(136), hotkeyCard.Top + MacTheme.S(124));

        int ay = audioCard.Top;
        agcToggle.Location = new Point(rightX + rightW - MacTheme.S(58), ay + MacTheme.S(34) - MacTheme.S(2));
        gainSlider.Bounds = new Rectangle(cx + MacTheme.S(140), ay + MacTheme.S(84), cw - MacTheme.S(140) - MacTheme.S(64), MacTheme.S(22));
        testToneBtn.Location = new Point(cx, ay + MacTheme.S(114));
        micToggle.Location = new Point(rightX + rightW - MacTheme.S(58), ay + MacTheme.S(148) - MacTheme.S(2));
        dumpToggle.Location = new Point(rightX + rightW - MacTheme.S(58), ay + MacTheme.S(186) - MacTheme.S(2));

        f5Toggle.Location = new Point(rightX + rightW - MacTheme.S(58), remoteCard.Top + MacTheme.S(34) - MacTheme.S(2));
        autoPairToggle.Location = new Point(rightX + rightW - MacTheme.S(58), remoteCard.Top + MacTheme.S(66) - MacTheme.S(2));
        autoStartToggle.Location = new Point(rightX + rightW - MacTheme.S(58), remoteCard.Top + MacTheme.S(98) - MacTheme.S(2));

        reconnectBtn.Location = new Point(deviceCard.Left + (deviceCard.Width - reconnectBtn.Width) / 2,
            deviceCard.Bottom - MacTheme.S(52));
        Invalidate();
    }

    public override void OnSnapshot(UiState.Snapshot s) { Invalidate(true); }  // include cards

    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Gfx.Text(g, "连接与语音", titleFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(26), MacTheme.S(22), Width, MacTheme.S(28)), StringAlignment.Near);
    }

    // ---- card-local content painters (coordinates relative to each card) ----
    void PaintDeviceContent(Graphics g) {
        var s = UiState.Take();
        int w = deviceCard.Width, hgt = deviceCard.Height;
        var art = new RectangleF(MacTheme.S(16), MacTheme.S(20), w - MacTheme.S(32), hgt - MacTheme.S(244));
        RemotePainter.Draw(g, art, s.Talking, null, s.KeysLit);

        float infoY = art.Bottom + MacTheme.S(4);
        string devName = string.IsNullOrEmpty(s.Device) ? "小米蓝牙遥控器 2 Pro" : s.Device;
        Gfx.Text(g, devName, cardHeadFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(16), infoY, w - MacTheme.S(32), MacTheme.S(20)), StringAlignment.Center);
        infoY += MacTheme.S(30);

        string stText = s.Linked ? "已连接" : "未连接";
        SizeF stSize = Gfx.Measure(g, stText, bodyFont);
        float pillW = stSize.Width + MacTheme.S(28);
        var pill = new RectangleF((w - pillW) / 2f, infoY, pillW, MacTheme.S(23));
        using (var path = Gfx.RoundRect(new Rectangle((int)pill.X, (int)pill.Y, (int)pill.Width, (int)pill.Height), MacTheme.S(11))) {
            using (var b = new SolidBrush(s.Linked ? Color.FromArgb(232, 248, 238) : Color.FromArgb(240, 240, 244))) g.FillPath(b, path);
        }
        using (var b = new SolidBrush(s.Linked ? MacTheme.Green : MacTheme.TextTertiary))
            g.FillEllipse(b, pill.X + MacTheme.S(10), pill.Y + pill.Height / 2 - MacTheme.S(3.5f), MacTheme.S(7), MacTheme.S(7));
        Gfx.Text(g, stText, bodyFont, s.Linked ? Color.FromArgb(0x1E, 0x8E, 0x45) : MacTheme.TextSecondary,
            new RectangleF(pill.X + MacTheme.S(20), pill.Y + MacTheme.S(3), pill.Width, pill.Height), StringAlignment.Near);
        infoY += MacTheme.S(34);

        if (s.Battery >= 0) {
            bool charging = s.Charging == 1;
            Gfx.Text(g, "电量 " + s.Battery + "%" + (charging ? " · 充电中" : ""), bodyFont, MacTheme.TextSecondary,
                new RectangleF(MacTheme.S(16), infoY, w - MacTheme.S(32), MacTheme.S(18)), StringAlignment.Center);
            var bar = new RectangleF(MacTheme.S(40), infoY + MacTheme.S(22), w - MacTheme.S(80), MacTheme.S(8));
            using (var path = Gfx.RoundRect(new Rectangle((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height), MacTheme.S(4))) {
                using (var b = new SolidBrush(MacTheme.TrackOff)) g.FillPath(b, path);
            }
            float fillW = bar.Width * s.Battery / 100f;
            if (fillW > 2) {
                var fill = new RectangleF(bar.X, bar.Y, fillW, bar.Height);
                using (var path = Gfx.RoundRect(new Rectangle((int)fill.X, (int)fill.Y, (int)fill.Width, (int)fill.Height), MacTheme.S(4))) {
                    Color bc = charging ? MacTheme.Green
                        : s.Battery <= 15 ? MacTheme.Red : s.Battery <= 30 ? MacTheme.Yellow : MacTheme.Green;
                    using (var b = new SolidBrush(bc)) g.FillPath(b, path);
                }
            }
        }

        // bottom block is anchored to the card bottom: stats, then the talking
        // indicator (reserved slot), then the reconnect button (see OnResize)
        string stats = App.Config.stats != null && App.Config.stats.daySessions > 0
            ? "今日语音 " + App.Config.stats.daySessions + " 次 · " + FormatSeconds(App.Config.stats.daySeconds)
            : "今日还没有语音输入";
        Gfx.Text(g, stats, smallFont, MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(16), hgt - MacTheme.S(118), w - MacTheme.S(32), MacTheme.S(16)), StringAlignment.Center);

        if (s.Talking) {
            Gfx.Text(g, "正在说话…", bodyFont, MacTheme.Accent,
                new RectangleF(MacTheme.S(16), hgt - MacTheme.S(96), w - MacTheme.S(32), MacTheme.S(18)), StringAlignment.Center);
            var bar = new RectangleF(MacTheme.S(40), hgt - MacTheme.S(75), w - MacTheme.S(80), MacTheme.S(6));
            using (var path = Gfx.RoundRect(new Rectangle((int)bar.X, (int)bar.Y, (int)bar.Width, (int)bar.Height), MacTheme.S(3))) {
                using (var b = new SolidBrush(MacTheme.TrackOff)) g.FillPath(b, path);
            }
            float lv = Math.Min(1f, Math.Max(0.04f, (float)s.Level));
            float lw = bar.Width * lv;
            if (lw > 2) {
                var fill = new RectangleF(bar.X, bar.Y, lw, bar.Height);
                using (var path = Gfx.RoundRect(new Rectangle((int)fill.X, (int)fill.Y, (int)fill.Width, (int)fill.Height), MacTheme.S(3))) {
                    using (var b = new SolidBrush(MacTheme.Accent)) g.FillPath(b, path);
                }
            }
        }
    }

    void PaintHotkeyContent(Graphics g) {
        int w = hotkeyCard.Width;
        Gfx.Text(g, "选择按住语音键时要触发的语音工具", smallFont, MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(16), MacTheme.S(32), w - MacTheme.S(32), MacTheme.S(16)), StringAlignment.Near);
        Gfx.Text(g, "组合键", smallFont, MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(16), MacTheme.S(94), MacTheme.S(140), MacTheme.S(16)), StringAlignment.Near);
        Gfx.Text(g, "切麦后延迟", smallFont, MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(16), MacTheme.S(130), MacTheme.S(140), MacTheme.S(16)), StringAlignment.Near);
        Gfx.Text(g, "毫秒", smallFont, MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(244), MacTheme.S(131), MacTheme.S(60), MacTheme.S(16)), StringAlignment.Near);
        if (presetSeg.Selected == 3) {
            Gfx.Text(g, "不注入热键：仅把遥控器语音推流到虚拟声卡（自动切麦不受影响）", smallFont, MacTheme.TextTertiary,
                new RectangleF(MacTheme.S(16), MacTheme.S(168), w - MacTheme.S(32), MacTheme.S(16)), StringAlignment.Near);
        } else {
            Gfx.Text(g, "测试热键 = 立即点按一次上面的组合键；改组合键会切换为「自定义」", smallFont, MacTheme.TextTertiary,
                new RectangleF(MacTheme.S(16), MacTheme.S(168), w - MacTheme.S(32), MacTheme.S(16)), StringAlignment.Near);
        }
    }

    void PaintAudioContent(Graphics g) {
        int w = audioCard.Width;
        Gfx.Text(g, "自适应增益 (AGC)", bodyFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(16), MacTheme.S(36), w - MacTheme.S(110), MacTheme.S(20)), StringAlignment.Near);
        Gfx.Text(g, "推荐开启；拖动下面的滑条会自动关闭 AGC", smallFont, MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(16), MacTheme.S(56), w - MacTheme.S(110), MacTheme.S(16)), StringAlignment.Near);
        Gfx.Text(g, "固定增益", bodyFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(16), MacTheme.S(86), MacTheme.S(120), MacTheme.S(20)), StringAlignment.Near);
        Gfx.Text(g, gainText, bodyFont, MacTheme.TextPrimary,
            new RectangleF(w - MacTheme.S(70), MacTheme.S(86), MacTheme.S(54), MacTheme.S(20)), StringAlignment.Near);
        Gfx.Text(g, "说话时自动切换默认麦克风", bodyFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(16), MacTheme.S(150), w - MacTheme.S(110), MacTheme.S(20)), StringAlignment.Near);
        Gfx.Text(g, "切到 " + App.Config.cableCaptureName + "，松开后恢复原麦克风", smallFont, MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(16), MacTheme.S(170), w - MacTheme.S(110), MacTheme.S(16)), StringAlignment.Near);
        Gfx.Text(g, "保存语音录音（调试）", bodyFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(16), MacTheme.S(188), w - MacTheme.S(110), MacTheme.S(20)), StringAlignment.Near);
    }

    void PaintRemoteContent(Graphics g) {
        int w = remoteCard.Width;
        Gfx.Text(g, "拦截语音键发出的 F5", bodyFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(16), MacTheme.S(36), w - MacTheme.S(110), MacTheme.S(20)), StringAlignment.Near);
        Gfx.Text(g, "扫描到未配对遥控器时自动配对", bodyFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(16), MacTheme.S(68), w - MacTheme.S(110), MacTheme.S(20)), StringAlignment.Near);
        Gfx.Text(g, "开机自动启动 MiVoiceMic", bodyFont, MacTheme.TextPrimary,
            new RectangleF(MacTheme.S(16), MacTheme.S(100), w - MacTheme.S(110), MacTheme.S(20)), StringAlignment.Near);
        Gfx.Text(g, "以上设置立即生效并自动保存", smallFont, MacTheme.TextTertiary,
            new RectangleF(MacTheme.S(16), MacTheme.S(124), w - MacTheme.S(110), MacTheme.S(16)), StringAlignment.Near);
    }

    static string FormatSeconds(double secs) {
        int m = (int)(secs / 60), s = (int)Math.Round(secs % 60);
        return m > 0 ? m + " 分 " + s + " 秒" : s + " 秒";
    }
}

// tiny owner-drawn text label (keeps GDI+ rendering consistent)
class LabelElement : MacWidget {
    public new string Text = "";
    public Color Color_ = MacTheme.TextSecondary;
    public ContentAlignment Align = ContentAlignment.TopLeft;

    public LabelElement(string text, Font f) { Text = text; Font = f; Height = MacTheme.S(18); }

    protected override void OnPaint(PaintEventArgs e) {
        Prep(e.Graphics);
        var r = new RectangleF(0, 0, Width, Height);
        Gfx.Text(e.Graphics, Text ?? "", Font, Color_, r,
            Align == ContentAlignment.TopRight ? StringAlignment.Far : StringAlignment.Near);
    }
}
