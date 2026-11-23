using KSnakeBatteryTray.Models;
using KSnakeBatteryTray.Settings;

namespace KSnakeBatteryTray.UI;

public sealed class DiagnosticsForm : Form
{
    private readonly AppSettings _settings;
    private readonly Func<string, IReadOnlyList<BatterySample>> _getHistory;
    private readonly Func<Task> _refreshAction;
    private readonly Func<string> _getLogAction;
    private readonly Action _settingsChangedAction;

    // Shared reusable fonts (prevents GDI font leaks across refreshes)
    private readonly Font _fontTitle = new("Segoe UI", 13f, FontStyle.Bold);
    private readonly Font _fontCardName = new("Segoe UI", 10.2f, FontStyle.Bold);
    private readonly Font _fontCardSub = new("Segoe UI", 8.4f);
    private readonly Font _fontCardPct = new("Segoe UI", 17f, FontStyle.Bold);
    private readonly Font _fontActionBtn = new("Segoe UI", 8f);
    private readonly Font _fontLog = new("Consolas", 8.4f);

    private readonly Label _lblTitle;
    private readonly Label _lblUpdated;
    private readonly FlowLayoutPanel _devicesPanel;
    private readonly TextBox _txtLog;
    private readonly Button _btnRefresh;
    private readonly Button _btnCopy;

    public DiagnosticsForm(
        AppSettings settings,
        Func<string, IReadOnlyList<BatterySample>> getHistory,
        Func<Task> refreshAction,
        Func<string> getLogAction,
        Action settingsChangedAction)
    {
        _settings = settings;
        _getHistory = getHistory;
        _refreshAction = refreshAction;
        _getLogAction = getLogAction;
        _settingsChangedAction = settingsChangedAction;

        Text = "Monitor de Bateria Wireless & Bluetooth — Detalhes e Gerenciamento";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(580, 520);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(20, 21, 26);
        ForeColor = Color.FromArgb(244, 244, 245);

        try
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);
        }
        catch { }

        _lblTitle = new Label
        {
            Text = "Dispositivos Detectados",
            Font = _fontTitle,
            Location = new Point(20, 14),
            AutoSize = true,
            ForeColor = Color.White
        };

        _lblUpdated = new Label
        {
            Text = "Última leitura: --",
            Location = new Point(20, 42),
            Size = new Size(540, 20),
            ForeColor = Color.FromArgb(161, 161, 170)
        };

        _devicesPanel = new FlowLayoutPanel
        {
            Location = new Point(20, 66),
            Size = new Size(540, 252),
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.FromArgb(20, 21, 26)
        };

        var lblLogHeader = new Label
        {
            Text = "Diagnóstico em tempo real (2.4 GHz Push / USB-C / Bluetooth):",
            Location = new Point(20, 326),
            AutoSize = true,
            ForeColor = Color.FromArgb(212, 212, 216)
        };

        _txtLog = new TextBox
        {
            Location = new Point(20, 350),
            Size = new Size(540, 114),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = _fontLog,
            BackColor = Color.FromArgb(14, 15, 19),
            ForeColor = Color.FromArgb(212, 212, 216),
            BorderStyle = BorderStyle.FixedSingle
        };

        _btnRefresh = new Button
        {
            Text = "Atualizar agora",
            Location = new Point(20, 474),
            Size = new Size(135, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        _btnRefresh.FlatAppearance.BorderSize = 0;
        _btnRefresh.Click += async (_, _) =>
        {
            _btnRefresh.Enabled = false;
            try { await _refreshAction(); }
            finally { _btnRefresh.Enabled = true; }
        };

        _btnCopy = new Button
        {
            Text = "Copiar diagnóstico",
            Location = new Point(165, 474),
            Size = new Size(148, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 47, 56),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        _btnCopy.FlatAppearance.BorderSize = 0;
        _btnCopy.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_txtLog.Text))
                Clipboard.SetText(_txtLog.Text);
        };

        var btnClose = new Button
        {
            Text = "Fechar",
            Location = new Point(470, 474),
            Size = new Size(90, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 47, 56),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        btnClose.FlatAppearance.BorderSize = 0;
        btnClose.Click += (_, _) => Hide();

        Controls.AddRange(
        [
            _lblTitle, _lblUpdated, _devicesPanel,
            lblLogHeader, _txtLog, _btnRefresh, _btnCopy, btnClose
        ]);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    public void UpdateView(IReadOnlyList<BatteryReading> allReadings)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateView(allReadings));
            return;
        }

        var latestTs = allReadings.Count > 0 ? allReadings.Max(r => r.Timestamp) : DateTime.Now;
        int activeCount = allReadings.Count(r => r.IsConnected);
        _lblTitle.Text = activeCount > 1
            ? $"Dispositivos Detectados ({activeCount} conectados)"
            : "Dispositivos Detectados";
        _lblUpdated.Text = $"Última leitura: {latestTs:HH:mm:ss}";

        _devicesPanel.SuspendLayout();
        foreach (Control old in _devicesPanel.Controls)
            old.Dispose();
        _devicesPanel.Controls.Clear();

        int cardWidth = allReadings.Count > 1 ? 516 : 532;
        foreach (var r in allReadings)
        {
            _devicesPanel.Controls.Add(CreateDeviceCard(r, cardWidth));
        }
        _devicesPanel.ResumeLayout();

        _txtLog.Text = _getLogAction();
        _txtLog.SelectionStart = _txtLog.TextLength;
        _txtLog.ScrollToCaret();
    }

    private Panel CreateDeviceCard(BatteryReading reading, int width)
    {
        bool isHidden = _settings.HiddenDeviceIds.Contains(reading.DeviceId);
        var history = _getHistory(reading.DeviceId);
        bool showChart = history.Count >= 2 && reading.IsConnected;
        bool canManage = reading.DeviceId != "none";

        int height = 84 + (showChart ? 32 : 0) + (canManage ? 32 : 0);

        var card = new Panel
        {
            Width = width,
            Height = height,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = isHidden ? Color.FromArgb(24, 25, 31) : Color.FromArgb(30, 32, 40)
        };

        Color accent = TrayIconRenderer.ResolveAccentColor(reading, lightTaskbar: false);

        string kindBadge = reading.Kind switch
        {
            DeviceKind.Mouse => "[Mouse]",
            DeviceKind.Audio => "[Áudio]",
            DeviceKind.Keyboard => "[Teclado]",
            _ => "[Disp.]"
        };

        string hiddenTag = isHidden ? " (Oculto)" : "";
        var lblName = new Label
        {
            Text = $"{kindBadge}  {reading.DisplayName}{hiddenTag}",
            Font = _fontCardName,
            Location = new Point(14, 10),
            Size = new Size(width - 110, 22),
            ForeColor = isHidden ? Color.FromArgb(148, 163, 184) : Color.White,
            AutoEllipsis = true
        };

        var subParts = new List<string> { reading.ModeLabel };
        if (reading.IsCharging) subParts.Add("Carregando ⚡");
        if (reading.Dpi is int dpi) subParts.Add($"{dpi} DPI");
        if (reading.PollingRateHz is int hz) subParts.Add($"{hz} Hz");
        if (!string.IsNullOrEmpty(reading.FirmwareVersion)) subParts.Add($"FW {reading.FirmwareVersion}");
        if (!string.IsNullOrEmpty(reading.EstimatedTimeLabel)) subParts.Add(reading.EstimatedTimeLabel);
        if (reading.Mode == ConnectionMode.Bluetooth && reading.BatteryPercent is null)
            subParts.Add("Sem bateria via BT");

        var lblSub = new Label
        {
            Text = string.Join(" • ", subParts),
            Font = _fontCardSub,
            Location = new Point(14, 34),
            Size = new Size(width - 110, 18),
            ForeColor = Color.FromArgb(161, 161, 170),
            AutoEllipsis = true
        };

        var lblPct = new Label
        {
            Text = reading.BatteryPercent is int pct ? $"{pct}%" : "N/D",
            Font = _fontCardPct,
            Location = new Point(width - 96, 10),
            Size = new Size(82, 36),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = accent
        };

        var bar = new SmoothBatteryBar(reading.BatteryPercent ?? 0, accent)
        {
            Location = new Point(14, 58),
            Size = new Size(width - 28, 12)
        };

        card.Controls.AddRange([lblName, lblSub, lblPct, bar]);

        int yCursor = 76;
        if (showChart)
        {
            var chart = new MiniSparklinePanel(history, accent)
            {
                Location = new Point(14, yCursor),
                Size = new Size(width - 28, 26)
            };
            card.Controls.Add(chart);
            yCursor += 32;
        }

        if (canManage)
        {
            string devId = reading.DeviceId;
            string currentDisplay = reading.DisplayName;

            var btnRename = CreateSmallButton("Renomear", new Point(14, yCursor), 80);
            btnRename.Click += (_, _) =>
            {
                string? newName = PromptRename(currentDisplay, reading.DeviceName);
                if (newName is null) return;
                if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == reading.DeviceName)
                    _settings.DeviceAliases.Remove(devId);
                else
                    _settings.DeviceAliases[devId] = newName.Trim();
                _settings.Save();
                _settingsChangedAction();
            };

            var btnColor = CreateSmallButton("Cor do ícone", new Point(100, yCursor), 92);
            btnColor.Click += (_, _) =>
            {
                using var dlg = new ColorDialog { FullOpen = true, Color = accent };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _settings.DeviceAccents[devId] = ColorTranslator.ToHtml(dlg.Color);
                    _settings.Save();
                    _settingsChangedAction();
                }
            };

            var btnResetColor = CreateSmallButton("Cor padrão", new Point(198, yCursor), 82);
            btnResetColor.Visible = _settings.DeviceAccents.ContainsKey(devId);
            btnResetColor.Click += (_, _) =>
            {
                _settings.DeviceAccents.Remove(devId);
                _settings.Save();
                _settingsChangedAction();
            };

            var btnHide = CreateSmallButton(isHidden ? "Mostrar" : "Ocultar", new Point(width - 94, yCursor), 80);
            btnHide.Click += (_, _) =>
            {
                if (isHidden)
                    _settings.HiddenDeviceIds.Remove(devId);
                else
                    _settings.HiddenDeviceIds.Add(devId);
                _settings.Save();
                _settingsChangedAction();
            };

            card.Controls.AddRange([btnRename, btnColor, btnResetColor, btnHide]);
        }

        return card;
    }

    private Button CreateSmallButton(string text, Point location, int width)
    {
        var btn = new Button
        {
            Text = text,
            Font = _fontActionBtn,
            Location = location,
            Size = new Size(width, 24),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(44, 47, 58),
            ForeColor = Color.FromArgb(228, 228, 234),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    private string? PromptRename(string currentValue, string defaultName)
    {
        using var dialog = new Form
        {
            Text = "Renomear dispositivo",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(340, 128),
            BackColor = Color.FromArgb(24, 25, 31),
            ForeColor = Color.White,
            Font = Font
        };

        var lbl = new Label
        {
            Text = $"Apelido para \"{defaultName}\" (vazio = padrão):",
            Location = new Point(16, 14),
            Size = new Size(308, 20),
            ForeColor = Color.FromArgb(212, 212, 216)
        };

        var txt = new TextBox
        {
            Text = currentValue,
            Location = new Point(16, 40),
            Size = new Size(308, 25),
            BackColor = Color.FromArgb(16, 17, 22),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };

        var btnOk = new Button
        {
            Text = "Salvar",
            DialogResult = DialogResult.OK,
            Location = new Point(156, 82),
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White
        };
        btnOk.FlatAppearance.BorderSize = 0;

        var btnCancel = new Button
        {
            Text = "Cancelar",
            DialogResult = DialogResult.Cancel,
            Location = new Point(244, 82),
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 47, 56),
            ForeColor = Color.White
        };
        btnCancel.FlatAppearance.BorderSize = 0;

        dialog.AcceptButton = btnOk;
        dialog.CancelButton = btnCancel;
        dialog.Controls.AddRange([lbl, txt, btnOk, btnCancel]);

        return dialog.ShowDialog(this) == DialogResult.OK ? txt.Text : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fontTitle.Dispose();
            _fontCardName.Dispose();
            _fontCardSub.Dispose();
            _fontCardPct.Dispose();
            _fontActionBtn.Dispose();
            _fontLog.Dispose();
        }
        base.Dispose(disposing);
    }
}
