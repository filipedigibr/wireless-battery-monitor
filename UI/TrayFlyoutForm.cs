using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using KSnakeBatteryTray.Models;
using KSnakeBatteryTray.Settings;

namespace KSnakeBatteryTray.UI;

/// <summary>
/// Windows 11-style compact taskbar flyout opened by a single left-click on the tray icon.
/// Automatically hides when clicking outside or pressing Esc.
/// </summary>
public sealed class TrayFlyoutForm : Form
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly AppSettings _settings;
    private readonly Func<string, IReadOnlyList<BatterySample>> _getHistory;
    private readonly Func<Task> _refreshAction;
    private readonly Action _openDetailsAction;
    private readonly Action<bool> _toggleMultiIconAction;

    // Shared reusable fonts to avoid GDI handle leaks on dynamic card rebuilds
    private readonly Font _fontHeader = new("Segoe UI", 10.5f, FontStyle.Bold);
    private readonly Font _fontCardName = new("Segoe UI", 9.8f, FontStyle.Bold);
    private readonly Font _fontCardSub = new("Segoe UI", 8.2f);
    private readonly Font _fontCardPct = new("Segoe UI", 16f, FontStyle.Bold);
    private readonly Font _fontSmall = new("Segoe UI", 8.2f);

    private readonly Panel _headerPanel;
    private readonly FlowLayoutPanel _cardsPanel;
    private readonly Panel _footerPanel;
    private readonly CheckBox _chkMultiIcon;
    private readonly Button _btnRefresh;
    private readonly Button _btnDetails;

    private long _lastDeactivatedTick;

    public bool WasJustDeactivated => (Environment.TickCount64 - _lastDeactivatedTick) < 280;

    public TrayFlyoutForm(
        AppSettings settings,
        Func<string, IReadOnlyList<BatterySample>> getHistory,
        Func<Task> refreshAction,
        Action openDetailsAction,
        Action<bool> toggleMultiIconAction)
    {
        _settings = settings;
        _getHistory = getHistory;
        _refreshAction = refreshAction;
        _openDetailsAction = openDetailsAction;
        _toggleMultiIconAction = toggleMultiIconAction;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Width = 356;
        Height = 230;
        BackColor = Color.FromArgb(24, 25, 31);
        ForeColor = Color.White;

        _headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = Color.FromArgb(24, 25, 31),
            Padding = new Padding(14, 10, 12, 6)
        };

        var lblHeader = new Label
        {
            Text = "Bateria dos Dispositivos",
            Font = _fontHeader,
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(14, 11)
        };

        _btnRefresh = new Button
        {
            Text = "↻",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Size = new Size(30, 26),
            Location = new Point(Width - 42, 8),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(36, 38, 48),
            ForeColor = Color.FromArgb(220, 224, 232),
            Cursor = Cursors.Hand
        };
        _btnRefresh.FlatAppearance.BorderSize = 0;
        _btnRefresh.Click += async (_, _) =>
        {
            _btnRefresh.Enabled = false;
            try { await _refreshAction(); }
            finally { _btnRefresh.Enabled = true; }
        };

        _headerPanel.Controls.AddRange([lblHeader, _btnRefresh]);

        _cardsPanel = new FlowLayoutPanel
        {
            Location = new Point(12, 44),
            Width = Width - 24,
            Height = 130,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.FromArgb(24, 25, 31)
        };

        _footerPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            BackColor = Color.FromArgb(19, 20, 25)
        };

        _chkMultiIcon = new CheckBox
        {
            Text = "1 ícone por dispositivo",
            Font = _fontSmall,
            ForeColor = Color.FromArgb(195, 200, 210),
            AutoSize = true,
            Location = new Point(14, 13),
            Checked = _settings.SeparateIconPerDevice,
            Cursor = Cursors.Hand
        };
        _chkMultiIcon.CheckedChanged += (_, _) => _toggleMultiIconAction(_chkMultiIcon.Checked);

        _btnDetails = new Button
        {
            Text = "Detalhes / Gerenciar",
            Font = _fontSmall,
            Size = new Size(134, 26),
            Location = new Point(Width - 146, 9),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(38, 41, 52),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        _btnDetails.FlatAppearance.BorderSize = 0;
        _btnDetails.Click += (_, _) =>
        {
            Hide();
            _openDetailsAction();
        };

        _footerPanel.Controls.AddRange([_chkMultiIcon, _btnDetails]);

        Controls.Add(_cardsPanel);
        Controls.Add(_headerPanel);
        Controls.Add(_footerPanel);

        Deactivate += (_, _) =>
        {
            _lastDeactivatedTick = Environment.TickCount64;
            Hide();
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Hide();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch
        {
            // Fallback gracefully on Windows 10
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var borderPen = new Pen(Color.FromArgb(52, 56, 70), 1f);
        e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
    }

    public void ShowNearTaskbar(IReadOnlyList<BatteryReading> readings)
    {
        UpdateContent(readings);
        PositionNearTray();
        Show();
        Activate();
    }

    public void UpdateContent(IReadOnlyList<BatteryReading> readings)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateContent(readings));
            return;
        }

        if (_chkMultiIcon.Checked != _settings.SeparateIconPerDevice)
            _chkMultiIcon.Checked = _settings.SeparateIconPerDevice;

        _cardsPanel.SuspendLayout();
        foreach (Control c in _cardsPanel.Controls)
            c.Dispose();
        _cardsPanel.Controls.Clear();

        int cardW = readings.Count > 3 ? _cardsPanel.Width - 20 : _cardsPanel.Width - 4;
        int totalCardsH = 0;

        foreach (var r in readings)
        {
            var history = _getHistory(r.DeviceId);
            var card = CreateFlyoutCard(r, history, cardW);
            _cardsPanel.Controls.Add(card);
            totalCardsH += card.Height + card.Margin.Bottom;
        }

        _cardsPanel.ResumeLayout();

        int clampedCardsH = Math.Clamp(totalCardsH + 4, 96, 360);
        _cardsPanel.Height = clampedCardsH;
        Height = _headerPanel.Height + clampedCardsH + _footerPanel.Height + 6;
    }

    private void PositionNearTray()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var wa = screen.WorkingArea;
        const int margin = 12;

        // Place anchored near bottom-right of working area (standard Windows taskbar)
        int x = wa.Right - Width - margin;
        int y = wa.Bottom - Height - margin;

        // If taskbar is docked at top, anchor near top-right
        if (wa.Top > screen.Bounds.Top)
            y = wa.Top + margin;

        Location = new Point(Math.Max(wa.Left + margin, x), Math.Max(wa.Top + margin, y));
    }

    private Panel CreateFlyoutCard(BatteryReading reading, IReadOnlyList<BatterySample> history, int width)
    {
        bool hasExtras = reading.Dpi.HasValue || reading.PollingRateHz.HasValue || !string.IsNullOrEmpty(reading.EstimatedTimeLabel);
        bool hasChart = history.Count >= 2 && reading.IsConnected;
        int height = 74 + (hasExtras ? 20 : 0) + (hasChart ? 32 : 0);

        var card = new Panel
        {
            Width = width,
            Height = height,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.FromArgb(33, 35, 44)
        };

        Color accent = TrayIconRenderer.ResolveAccentColor(reading, lightTaskbar: false);

        string kindTag = reading.Kind switch
        {
            DeviceKind.Mouse => "Mouse",
            DeviceKind.Audio => "Áudio",
            DeviceKind.Keyboard => "Teclado",
            _ => "Disp."
        };

        var lblName = new Label
        {
            Text = reading.DisplayName,
            Font = _fontCardName,
            ForeColor = Color.White,
            Location = new Point(12, 9),
            Size = new Size(width - 90, 20),
            AutoEllipsis = true
        };

        string chg = reading.IsCharging ? " • Carregando ⚡" : "";
        string sub = reading.Mode == ConnectionMode.Bluetooth && reading.BatteryPercent is null
            ? $"{kindTag} • Bluetooth (sem bateria via BT)"
            : $"{kindTag} • {reading.ModeLabel}{chg}";

        var lblSub = new Label
        {
            Text = sub,
            Font = _fontCardSub,
            ForeColor = Color.FromArgb(161, 166, 178),
            Location = new Point(12, 29),
            Size = new Size(width - 90, 17),
            AutoEllipsis = true
        };

        var lblPct = new Label
        {
            Text = reading.BatteryPercent is int pct ? $"{pct}%" : "N/D",
            Font = _fontCardPct,
            ForeColor = accent,
            Location = new Point(width - 78, 10),
            Size = new Size(66, 32),
            TextAlign = ContentAlignment.MiddleRight
        };

        var bar = new SmoothBatteryBar(reading.BatteryPercent ?? 0, accent)
        {
            Location = new Point(12, 52),
            Size = new Size(width - 24, 10)
        };

        card.Controls.AddRange([lblName, lblSub, lblPct, bar]);

        int yCursor = 67;
        if (hasExtras)
        {
            var parts = new List<string>();
            if (reading.Dpi is int dpi) parts.Add($"{dpi} DPI");
            if (reading.PollingRateHz is int hz) parts.Add($"{hz} Hz");
            if (!string.IsNullOrEmpty(reading.EstimatedTimeLabel)) parts.Add(reading.EstimatedTimeLabel);

            var lblExtra = new Label
            {
                Text = string.Join("  •  ", parts),
                Font = _fontCardSub,
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(12, yCursor),
                Size = new Size(width - 24, 17),
                AutoEllipsis = true
            };
            card.Controls.Add(lblExtra);
            yCursor += 20;
        }

        if (hasChart)
        {
            var chart = new MiniSparklinePanel(history, accent)
            {
                Location = new Point(12, yCursor),
                Size = new Size(width - 24, 26)
            };
            card.Controls.Add(chart);
        }

        return card;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fontHeader.Dispose();
            _fontCardName.Dispose();
            _fontCardSub.Dispose();
            _fontCardPct.Dispose();
            _fontSmall.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class SmoothBatteryBar : Panel
{
    private readonly int _percent;
    private readonly Color _fillColor;

    public SmoothBatteryBar(int percent, Color fillColor)
    {
        _percent = Math.Clamp(percent, 0, 100);
        _fillColor = fillColor;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int r = Height / 2;

        using var bgBrush = new SolidBrush(Color.FromArgb(52, 56, 70));
        using var bgPath = CreatePill(0, 0, Width - 1, Height - 1, r);
        e.Graphics.FillPath(bgBrush, bgPath);

        if (_percent > 0)
        {
            int fillW = Math.Max(Height, (int)Math.Round((Width - 1) * (_percent / 100.0)));
            using var fillBrush = new SolidBrush(_fillColor);
            using var fillPath = CreatePill(0, 0, fillW, Height - 1, r);
            e.Graphics.FillPath(fillBrush, fillPath);
        }
    }

    private static GraphicsPath CreatePill(int x, int y, int w, int h, int r)
    {
        var path = new GraphicsPath();
        int d = Math.Min(r * 2, Math.Min(w, h));
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class MiniSparklinePanel : Panel
{
    private readonly IReadOnlyList<BatterySample> _samples;
    private readonly Color _accent;

    public MiniSparklinePanel(IReadOnlyList<BatterySample> samples, Color accent)
    {
        _samples = samples;
        _accent = accent;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(25, 27, 34);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_samples.Count < 2 || Width < 10 || Height < 8)
            return;

        // Take up to last 48 samples for a crisp mini history curve
        var slice = _samples.Count > 48 ? _samples.Skip(_samples.Count - 48).ToList() : _samples;
        int n = slice.Count;

        var pts = new PointF[n];
        float padX = 2f;
        float padY = 3f;
        float plotW = Width - padX * 2f;
        float plotH = Height - padY * 2f;

        for (int i = 0; i < n; i++)
        {
            float x = padX + (plotW * i / Math.Max(1, n - 1));
            float y = padY + plotH * (1f - Math.Clamp(slice[i].Percent, 0, 100) / 100f);
            pts[i] = new PointF(x, y);
        }

        using var fillPath = new GraphicsPath();
        fillPath.AddLines(pts);
        fillPath.AddLine(pts[^1], new PointF(pts[^1].X, Height));
        fillPath.AddLine(new PointF(pts[^1].X, Height), new PointF(pts[0].X, Height));
        fillPath.CloseFigure();

        using var areaBrush = new LinearGradientBrush(
            new Rectangle(0, 0, Width, Height),
            Color.FromArgb(85, _accent),
            Color.FromArgb(8, _accent),
            LinearGradientMode.Vertical);
        g.FillPath(areaBrush, fillPath);

        using var linePen = new Pen(_accent, 1.6f);
        g.DrawLines(linePen, pts);
    }
}
