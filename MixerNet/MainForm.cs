using System.Runtime.InteropServices;
using MixerNet.Audio;
using MixerNet.UI;

namespace MixerNet;

sealed class MainForm : Form
{
    const int W = 440;

    readonly MixEngine _engine = new();
    readonly AppConfig _cfg = AppConfig.Load();

    readonly DarkCombo _selA = new() { AccessibleName = "Source A" };
    readonly DarkCombo _selB = new() { AccessibleName = "Source B" };
    readonly DarkCombo _selOut = new() { AccessibleName = "Output device" };
    readonly DarkCombo _selApi = new() { AccessibleName = "Output API" };
    readonly Button _refresh = MakeButton("Refresh");
    readonly Button _start = MakeButton("Start");
    readonly Button _modeMix = MakeButton("A + B in both");
    readonly Button _modeSplit = MakeButton("A left, B right");
    readonly Button _muteA = MakeButton("Mute A");
    readonly Button _muteB = MakeButton("Mute B");
    readonly Button _invA = MakeButton("Invert R");
    readonly Button _invB = MakeButton("Invert R");
    readonly Label _warnA = WarnLabel(), _warnB = WarnLabel();
    readonly Label _status = new() { Text = "Stopped", ForeColor = Theme.Dim, TextAlign = ContentAlignment.MiddleRight };
    readonly Label _message = new() { ForeColor = Theme.Danger, AutoEllipsis = true };

    readonly DarkSlider _volA = new(0, 150, 100) { AccessibleName = "Source A volume" };
    readonly DarkSlider _volB = new(0, 150, 100) { AccessibleName = "Source B volume" };
    readonly DarkSlider _panA = new(-100, 100, 0, bipolar: true) { AccessibleName = "Source A pan" };
    readonly DarkSlider _panB = new(-100, 100, 0, bipolar: true) { AccessibleName = "Source B pan" };
    readonly DarkSlider _master = new(0, 150, 100) { AccessibleName = "Master volume" };
    readonly Label _volAv = ValueLabel(), _volBv = ValueLabel(), _panAv = ValueLabel(), _panBv = ValueLabel(), _masterV = ValueLabel();
    readonly LevelMeter _mA = new(), _mB = new(), _mL = new(), _mR = new();

    readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 50 };
    readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 400 };
    readonly float[] _smooth = new float[4];
    bool _running, _busy, _loading = true;

    static readonly string[] ApiLabels =
        { "Auto (WASAPI, then DirectSound, then WaveOut)", "WASAPI", "DirectSound", "WaveOut (MME)" };

    public MainForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Mixer";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(W, 864);
        MinimumSize = new Size(W + 16, 400);
        MaximizeBox = false;
        AutoScroll = true;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var title = new Label
        {
            Text = "Mixer",
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(14, 12),
        };
        _status.SetBounds(W - 214, 12, 200, 22);
        Controls.Add(title);
        Controls.Add(_status);

        var ver = typeof(MainForm).Assembly.GetName().Version;
        Controls.Add(new Label
        {
            Text = $"v{ver?.Major}.{ver?.Minor}.{ver?.Build}  ·  Copyright (c) 2026 by Gerry Hull, W1VE  ·  MIT",
            ForeColor = Theme.Dim,
            Font = new Font("Segoe UI", 7.5F),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(12, 838, W - 24, 18),
        });

        // --- Sound cards
        var c1 = AddCard(12, 44, W - 24, 344, "SOUND CARDS");
        int iw = c1.Width - 24;
        AddField(c1, "Source A", 32, _selA, iw);
        AddField(c1, "Source B", 80, _selB, iw);
        AddField(c1, "Output", 128, _selOut, iw);
        AddField(c1, "Output API", 176, _selApi, iw);
        _refresh.SetBounds(12, 230, (iw - 8) / 2, 30);
        _start.SetBounds(12 + (iw - 8) / 2 + 8, 230, (iw - 8) / 2, 30);
        _message.SetBounds(12, 268, iw, 68);
        c1.Controls.AddRange(new Control[] { _refresh, _start, _message });

        // --- Mode
        var c2 = AddCard(12, 398, W - 24, 74, "MODE");
        _modeMix.SetBounds(12, 32, iw / 2, 30);
        _modeSplit.SetBounds(12 + iw / 2, 32, iw - iw / 2, 30);
        _modeMix.AccessibleRole = _modeSplit.AccessibleRole = AccessibleRole.RadioButton;
        c2.Controls.AddRange(new Control[] { _modeMix, _modeSplit });

        // --- Strips
        int sw = (W - 24 - 10) / 2;
        BuildStrip(12, 482, sw, "A", _mA, _volA, _volAv, _panA, _panAv, _muteA, _invA, _warnA);
        BuildStrip(12 + sw + 10, 482, sw, "B", _mB, _volB, _volBv, _panB, _panBv, _muteB, _invB, _warnB);

        // --- Output
        var c4 = AddCard(12, 718, W - 24, 114, "OUTPUT");
        _mL.SetBounds(12, 32, iw, 8);
        _mR.SetBounds(12, 44, iw, 8);
        AddValueRow(c4, "Master", 62, _masterV, iw);
        _master.SetBounds(12, 82, iw, 22);
        c4.Controls.AddRange(new Control[] { _mL, _mR, _master });

        ResumeLayout(false);
        PerformLayout();

        WireEvents();
    }

    // ------------------------------------------------------------------ layout helpers

    static Button MakeButton(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand,
        };
        StyleButton(b, null);
        return b;
    }

    /// <summary>Colours a button: filled with <paramref name="fill"/>, or neutral when null.</summary>
    static void StyleButton(Button b, Color? fill)
    {
        if (fill is Color c)
        {
            b.BackColor = c;
            b.ForeColor = Theme.OnAccent;
            b.FlatAppearance.BorderColor = c;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(c, 0.15f);
            b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(c, 0.05f);
            b.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        }
        else
        {
            b.BackColor = Theme.Control;
            b.ForeColor = Theme.Text;
            b.FlatAppearance.BorderColor = Theme.Line;
            b.FlatAppearance.MouseOverBackColor = Theme.Hover;
            b.FlatAppearance.MouseDownBackColor = Theme.Line;
            b.Font = new Font("Segoe UI", 9F);
        }
    }

    static Label WarnLabel() => new() { ForeColor = Theme.Warn, Font = new Font("Segoe UI", 8F) };

    static Label ValueLabel() => new() { TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Text };

    Card AddCard(int x, int y, int w, int h, string caption)
    {
        var c = new Card { Bounds = new Rectangle(x, y, w, h) };
        c.Controls.Add(new Label
        {
            Text = caption,
            ForeColor = Theme.Dim,
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(10, 9),
        });
        Controls.Add(c);
        return c;
    }

    static void AddField(Control parent, string label, int y, Control field, int width)
    {
        parent.Controls.Add(new Label { Text = label, ForeColor = Theme.Dim, AutoSize = true, Location = new Point(10, y) });
        field.SetBounds(12, y + 18, width, 24);
        parent.Controls.Add(field);
    }

    static void AddValueRow(Control parent, string label, int y, Label value, int width)
    {
        parent.Controls.Add(new Label { Text = label, ForeColor = Theme.Dim, AutoSize = true, Location = new Point(10, y) });
        value.SetBounds(12 + width - 90, y - 2, 90, 20);
        parent.Controls.Add(value);
    }

    void BuildStrip(int x, int y, int w, string name, LevelMeter meter, DarkSlider vol, Label volV,
                    DarkSlider pan, Label panV, Button mute, Button invert, Label warn)
    {
        var c = AddCard(x, y, w, 226, name);
        int iw = w - 24;
        meter.SetBounds(12, 32, iw, 8);
        AddValueRow(c, "Volume", 52, volV, iw);
        vol.SetBounds(12, 72, iw, 22);
        AddValueRow(c, "Pan", 100, panV, iw);
        pan.SetBounds(12, 120, iw, 22);
        int half = (iw - 6) / 2;
        mute.SetBounds(12, 152, half, 30);
        invert.SetBounds(12 + half + 6, 152, iw - half - 6, 30);
        warn.SetBounds(10, 186, iw + 2, 32);
        c.Controls.AddRange(new Control[] { meter, vol, pan, mute, invert, warn });
    }

    // ------------------------------------------------------------------ events

    void WireEvents()
    {
        foreach (var label in ApiLabels) _selApi.Items.Add(label);

        _volA.ValueChanged += (_, _) => { _cfg.VolA = _volA.Value; Changed(); };
        _volB.ValueChanged += (_, _) => { _cfg.VolB = _volB.Value; Changed(); };
        _panA.ValueChanged += (_, _) => { _cfg.PanA = _panA.Value; Changed(); };
        _panB.ValueChanged += (_, _) => { _cfg.PanB = _panB.Value; Changed(); };
        _master.ValueChanged += (_, _) => { _cfg.Master = _master.Value; Changed(); };
        _muteA.Click += (_, _) => { _cfg.MuteA = !_cfg.MuteA; Changed(); };
        _muteB.Click += (_, _) => { _cfg.MuteB = !_cfg.MuteB; Changed(); };
        _invA.Click += (_, _) => { _cfg.InvertA = !_cfg.InvertA; Changed(); };
        _invB.Click += (_, _) => { _cfg.InvertB = !_cfg.InvertB; Changed(); };
        _invA.AccessibleName = "Invert source A right channel polarity";
        _invB.AccessibleName = "Invert source B right channel polarity";
        _modeMix.Click += (_, _) => { _cfg.Split = false; Changed(); };
        _modeSplit.Click += (_, _) => { _cfg.Split = true; Changed(); };

        _selA.SelectedIndexChanged += (_, _) => { _cfg.SourceA = Id(_selA); Changed(); };
        _selB.SelectedIndexChanged += (_, _) => { _cfg.SourceB = Id(_selB); Changed(); };
        _selOut.SelectedIndexChanged += (_, _) => { _cfg.Output = Id(_selOut); Changed(); };
        _selApi.SelectedIndexChanged += (_, _) => { _cfg.OutputApi = (OutputApi)Math.Max(0, _selApi.SelectedIndex); Changed(); };

        _refresh.Click += async (_, _) => await RefreshDevicesAsync();
        _start.Click += async (_, _) => { if (_running) await StopAsync(); else await StartAsync(); };

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _cfg.Save(); };
        _meterTimer.Tick += (_, _) => UpdateMeters();

        _engine.Error += msg =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(async () =>
            {
                await StopAsync();
                SetMessage(msg, error: true);
            }));
        };
    }

    static string? Id(ComboBox c) => (c.SelectedItem as DeviceItem)?.Id is { Length: > 0 } id ? id : null;

    void Changed()
    {
        if (_loading) return;
        RenderState();
        PushParams();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void PushParams()
    {
        _engine.VolA = _cfg.VolA / 100f;
        _engine.VolB = _cfg.VolB / 100f;
        _engine.PanA = _cfg.PanA / 100f;
        _engine.PanB = _cfg.PanB / 100f;
        _engine.Master = _cfg.Master / 100f;
        _engine.MuteA = _cfg.MuteA;
        _engine.MuteB = _cfg.MuteB;
        _engine.Split = _cfg.Split;
        _engine.InvertA = _cfg.InvertA;
        _engine.InvertB = _cfg.InvertB;
    }

    static string FmtPan(int v) => v == 0 ? "C" : v < 0 ? $"L{-v}" : $"R{v}";

    void RenderState()
    {
        _volAv.Text = _cfg.VolA + "%";
        _volBv.Text = _cfg.VolB + "%";
        _panAv.Text = FmtPan(_cfg.PanA);
        _panBv.Text = FmtPan(_cfg.PanB);
        _masterV.Text = _cfg.Master + "%";

        _muteA.Text = _cfg.MuteA ? "Unmute A" : "Mute A";
        _muteB.Text = _cfg.MuteB ? "Unmute B" : "Mute B";
        _muteA.AccessibleDescription = _cfg.MuteA ? "muted" : "not muted";
        _muteB.AccessibleDescription = _cfg.MuteB ? "muted" : "not muted";
        StyleButton(_muteA, _cfg.MuteA ? Theme.Danger : null);
        StyleButton(_muteB, _cfg.MuteB ? Theme.Danger : null);
        StyleButton(_invA, _cfg.InvertA ? Theme.Warn : null);
        StyleButton(_invB, _cfg.InvertB ? Theme.Warn : null);
        _invA.AccessibleDescription = _cfg.InvertA ? "on" : "off";
        _invB.AccessibleDescription = _cfg.InvertB ? "on" : "off";

        StyleButton(_modeMix, _cfg.Split ? null : Theme.Accent);
        StyleButton(_modeSplit, _cfg.Split ? Theme.Accent : null);
        _modeMix.AccessibleDescription = _cfg.Split ? "not selected" : "selected";
        _modeSplit.AccessibleDescription = _cfg.Split ? "selected" : "not selected";
        _panA.Enabled = _panB.Enabled = !_cfg.Split;

        _start.Text = _busy ? "…" : _running ? "Stop" : "Start";
        _start.Enabled = !_busy;
        StyleButton(_start, _running ? Theme.Danger : Theme.Accent);
        _status.Text = _running ? "Running" : "Stopped";
        _status.ForeColor = _running ? Theme.Ok : Theme.Dim;
    }

    void SetMessage(string text, bool error)
    {
        _message.Text = text;
        _message.ForeColor = error ? Theme.Danger : Theme.Dim;
    }

    // ------------------------------------------------------------------ devices / engine

    static void Fill(ComboBox c, List<DeviceItem> list, string? id, bool allowNone)
    {
        c.BeginUpdate();
        c.Items.Clear();
        if (allowNone) c.Items.Add(DeviceItem.None);
        foreach (var d in list) c.Items.Add(d);
        var match = c.Items.Cast<DeviceItem>().FirstOrDefault(d =>
            d.Id.Length > 0 && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match != null) c.SelectedItem = match;
        else if (c.Items.Count > 0) c.SelectedIndex = 0;
        c.EndUpdate();
    }

    async Task RefreshDevicesAsync()
    {
        try
        {
            var (sources, outputs) = await _engine.ListDevicesAsync();
            bool wasLoading = _loading;
            _loading = true;
            Fill(_selA, sources, _cfg.SourceA, true);
            Fill(_selB, sources, _cfg.SourceB, true);
            Fill(_selOut, outputs, _cfg.Output, false);
            _cfg.SourceA = Id(_selA);
            _cfg.SourceB = Id(_selB);
            _cfg.Output = Id(_selOut);
            _loading = wasLoading;
            if (!_running) SetMessage("", false);
        }
        catch (Exception ex)
        {
            SetMessage("Could not list devices: " + MixEngine.Describe(ex), true);
        }
    }

    async Task StartAsync()
    {
        _busy = true;
        SetMessage("Starting…", false);
        RenderState();
        try
        {
            PushParams();
            var info = await _engine.StartAsync(_cfg.SourceA, _cfg.SourceB, _cfg.Output ?? "", _cfg.OutputApi);
            _running = true;
            SetMessage(info, false);
            _meterTimer.Start();
        }
        catch (Exception ex)
        {
            _running = false;
            SetMessage(ex.Message, true);
        }
        finally
        {
            _busy = false;
            RenderState();
        }
    }

    async Task StopAsync()
    {
        _busy = true;
        RenderState();
        try { await _engine.StopAsync(); } catch { }
        _running = false;
        _busy = false;
        _meterTimer.Stop();
        Array.Clear(_smooth);
        _mA.Level = _mB.Level = _mL.Level = _mR.Level = 0;
        _warnA.Text = _warnB.Text = "";
        SetMessage("", false);
        RenderState();
    }

    void UpdateMeters()
    {
        var lv = _engine.TakeLevels();
        for (int i = 0; i < 4; i++) _smooth[i] = Math.Max(lv[i], _smooth[i] * 0.85f);
        _mA.Level = _smooth[0];
        _mB.Level = _smooth[1];
        _mL.Level = _smooth[2];
        _mR.Level = _smooth[3];

        SetPhaseWarning(_warnA, _engine.CorrelationA);
        SetPhaseWarning(_warnB, _engine.CorrelationB);
    }

    /// <summary>Warns when a source's L and R cancel each other (they would vanish when folded to mono).</summary>
    static void SetPhaseWarning(Label warn, float corr)
    {
        string text = !float.IsNaN(corr) && corr < -0.5f
            ? "L/R out of phase: will cancel in A left / B right. Click Invert R."
            : "";
        if (warn.Text != text) warn.Text = text;
    }

    // ------------------------------------------------------------------ lifecycle

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _volA.Value = _cfg.VolA;
        _volB.Value = _cfg.VolB;
        _panA.Value = _cfg.PanA;
        _panB.Value = _cfg.PanB;
        _master.Value = _cfg.Master;
        _selApi.SelectedIndex = Math.Clamp((int)_cfg.OutputApi, 0, ApiLabels.Length - 1);
        await RefreshDevicesAsync();
        _loading = false;
        _cfg.Save(); // create/refresh Mixer.json beside the exe right away
        RenderState();
        PushParams();
        if ((_cfg.SourceA != null || _cfg.SourceB != null) && _cfg.Output != null)
            await StartAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _meterTimer.Stop();
        _saveTimer.Stop();
        _cfg.Save();
        _engine.Dispose();
        base.OnFormClosing(e);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        try { DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int)); } catch { }
    }
}
