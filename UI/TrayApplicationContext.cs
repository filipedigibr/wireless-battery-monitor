using System.Runtime.InteropServices;
using System.Text;
using KSnakeBatteryTray.Models;
using KSnakeBatteryTray.Services;
using KSnakeBatteryTray.Settings;
using Microsoft.Win32;

namespace KSnakeBatteryTray.UI;

public sealed class TrayApplicationContext : ApplicationContext
{
    public const string ShutdownEventName = @"Local\WirelessBatteryMonitor_ShutdownEvent";

    private readonly AppSettings _settings;
    private readonly MouseBatteryService _service;
    private readonly Dictionary<string, (NotifyIcon Tray, Icon CurrentIcon)> _extraIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _devicesSubmenu;
    private readonly ToolStripMenuItem _menuHeader;
    private readonly ToolStripMenuItem _multiIconMenuItem;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly System.Windows.Forms.Timer _debounceTimer;
    private readonly DeviceNotificationWindow _deviceWindow;
    private readonly Control _uiMarshal;
    private readonly TrayFlyoutForm _flyoutForm;
    private readonly DiagnosticsForm _diagnosticsForm;
    private readonly CancellationTokenSource _cts = new();
    private readonly EventWaitHandle _shutdownSignal;

    private readonly HashSet<string> _lowBatteryNotified = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fullChargeNotified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _knownConnectedDevices = new(StringComparer.OrdinalIgnoreCase);

    private NotifyIcon _primaryNotifyIcon;
    private Icon? _primaryIcon;
    private bool _hasCompletedFirstPoll;

    public TrayApplicationContext()
    {
        _settings = AppSettings.Load();

        _uiMarshal = new Control();
        _ = _uiMarshal.Handle;

        _service = new MouseBatteryService(_settings);
        _service.ReadingsUpdated += OnReadingsUpdated;

        _diagnosticsForm = new DiagnosticsForm(
            _settings,
            _service.GetHistory,
            TriggerRefreshAsync,
            _service.GetDiagnosticSnapshot,
            () => _service.ReapplyPresentationSettings());

        _flyoutForm = new TrayFlyoutForm(
            _settings,
            _service.GetHistory,
            TriggerRefreshAsync,
            ShowDiagnostics,
            SetSeparateIconPerDevice);

        _menuHeader = new ToolStripMenuItem("Monitor de Bateria — Lendo...")
        {
            Enabled = false,
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold)
        };
        _devicesSubmenu = new ToolStripMenuItem("Dispositivos conectados");
        _multiIconMenuItem = new ToolStripMenuItem("1 ícone por dispositivo conectado")
        {
            Checked = _settings.SeparateIconPerDevice,
            CheckOnClick = true
        };
        _contextMenu = BuildContextMenu();

        var initialReading = MouseBatteryService.SelectPrimary(_service.VisibleReadings, _settings.PreferredDeviceId);
        _primaryIcon = TrayIconRenderer.CreateIcon(initialReading, _settings.IconStyle);
        _primaryNotifyIcon = CreateNotifyIcon(_primaryIcon, "Monitor de Bateria — Iniciando...");

        _pollTimer = new System.Windows.Forms.Timer
        {
            Interval = _settings.PollIntervalSeconds * 1000
        };
        _pollTimer.Tick += async (_, _) => await TriggerRefreshAsync();
        _pollTimer.Start();

        _debounceTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        _debounceTimer.Tick += async (_, _) =>
        {
            _debounceTimer.Stop();
            await TriggerRefreshAsync();
        };

        _deviceWindow = new DeviceNotificationWindow(ScheduleDebouncedRefresh, RecreateTrayIconsAfterExplorerRestart);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _shutdownSignal = new EventWaitHandle(false, EventResetMode.ManualReset, ShutdownEventName);
        StartShutdownWatcher();

        _ = TriggerRefreshAsync();
    }

    private NotifyIcon CreateNotifyIcon(Icon icon, string tooltip)
    {
        var ni = new NotifyIcon
        {
            Icon = icon,
            Text = tooltip,
            ContextMenuStrip = _contextMenu,
            Visible = true
        };
        ni.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ToggleFlyout();
        };
        ni.DoubleClick += (_, _) =>
        {
            _flyoutForm.Hide();
            ShowDiagnostics();
        };
        return ni;
    }

    private void ToggleFlyout()
    {
        if (_flyoutForm.Visible)
        {
            _flyoutForm.Hide();
            return;
        }

        if (_flyoutForm.WasJustDeactivated)
            return;

        _flyoutForm.ShowNearTaskbar(_service.VisibleReadings);
    }

    private void RecreateTrayIconsAfterExplorerRestart()
    {
        RunOnUiThread(() =>
        {
            try
            {
                var oldText = _primaryNotifyIcon.Text;
                _primaryNotifyIcon.Visible = false;
                _primaryNotifyIcon.Dispose();

                var primary = MouseBatteryService.SelectPrimary(_service.VisibleReadings, _settings.PreferredDeviceId);
                var oldIcon = _primaryIcon;
                _primaryIcon = TrayIconRenderer.CreateIcon(primary, _settings.IconStyle);
                _primaryNotifyIcon = CreateNotifyIcon(_primaryIcon, oldText);
                oldIcon?.Dispose();

                ClearExtraIcons();
                UpdateTrayVisuals(_service.VisibleReadings);
            }
            catch
            {
                // Ignore transient Explorer restart races
            }
        });
    }

    private void SetSeparateIconPerDevice(bool enabled)
    {
        if (_settings.SeparateIconPerDevice == enabled) return;
        _settings.SeparateIconPerDevice = enabled;
        _settings.Save();
        _multiIconMenuItem.Checked = enabled;
        UpdateTrayVisuals(_service.VisibleReadings);
    }

    private void StartShutdownWatcher()
    {
        var watcher = new Thread(() =>
        {
            try
            {
                WaitHandle[] handles = [_shutdownSignal, _cts.Token.WaitHandle];
                int idx = WaitHandle.WaitAny(handles);
                if (idx == 0)
                    RunOnUiThread(ExitApplication);
            }
            catch { }
        })
        {
            IsBackground = true,
            Name = "ShutdownSignalWatcher"
        };
        watcher.Start();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_menuHeader);
        menu.Items.Add(_devicesSubmenu);
        menu.Items.Add(new ToolStripSeparator());

        var itemFlyout = new ToolStripMenuItem("Abrir painel rápido");
        itemFlyout.Click += (_, _) => ToggleFlyout();
        menu.Items.Add(itemFlyout);

        var itemDetails = new ToolStripMenuItem("Detalhes, renomear e diagnóstico...");
        itemDetails.Click += (_, _) => ShowDiagnostics();
        menu.Items.Add(itemDetails);

        var itemRefresh = new ToolStripMenuItem("Atualizar agora");
        itemRefresh.Click += async (_, _) => await TriggerRefreshAsync();
        menu.Items.Add(itemRefresh);

        menu.Items.Add(new ToolStripSeparator());

        // Multi-device tray icon toggle
        _multiIconMenuItem.Click += (_, _) => SetSeparateIconPerDevice(_multiIconMenuItem.Checked);
        menu.Items.Add(_multiIconMenuItem);

        // Device type badge toggle (for multi-icon mode)
        var typeBadgeItem = new ToolStripMenuItem("Indicador de tipo no ícone (mouse/fone)")
        {
            Checked = _settings.ShowDeviceTypeBadge,
            CheckOnClick = true
        };
        typeBadgeItem.Click += (_, _) =>
        {
            _settings.ShowDeviceTypeBadge = typeBadgeItem.Checked;
            _settings.Save();
            UpdateTrayVisuals(_service.VisibleReadings);
        };
        menu.Items.Add(typeBadgeItem);

        // Icon style submenu
        var styleMenu = new ToolStripMenuItem("Estilo do ícone");
        AddStyleOption(styleMenu, "Bateria + Número", TrayIconStyle.BatteryAndNumber);
        AddStyleOption(styleMenu, "Somente Número", TrayIconStyle.NumberOnly);
        AddStyleOption(styleMenu, "Somente Ícone de Bateria", TrayIconStyle.BatteryIconOnly);
        menu.Items.Add(styleMenu);

        // Alerts submenu
        var alertMenu = new ToolStripMenuItem("Notificações e alertas");
        foreach (int threshold in new[] { 10, 15, 20, 25, 0 })
        {
            string label = threshold == 0 ? "Bateria baixa: Desativado" : $"Bateria baixa: Avisar em {threshold}%";
            var item = new ToolStripMenuItem(label)
            {
                Checked = (_settings.NotifyOnLowBattery ? _settings.LowBatteryThreshold : 0) == threshold,
                Tag = "threshold"
            };
            item.Click += (_, _) =>
            {
                if (threshold == 0)
                {
                    _settings.NotifyOnLowBattery = false;
                }
                else
                {
                    _settings.NotifyOnLowBattery = true;
                    _settings.LowBatteryThreshold = threshold;
                    _lowBatteryNotified.Clear();
                }
                _settings.Save();
                foreach (ToolStripItem sibling in alertMenu.DropDownItems)
                {
                    if (sibling is ToolStripMenuItem mi && (mi.Tag as string) == "threshold")
                        mi.Checked = mi == item;
                }
            };
            alertMenu.DropDownItems.Add(item);
        }

        alertMenu.DropDownItems.Add(new ToolStripSeparator());

        var notifyConnectItem = new ToolStripMenuItem("Avisar ao conectar/desconectar dispositivo")
        {
            Checked = _settings.NotifyOnConnectDisconnect,
            CheckOnClick = true
        };
        notifyConnectItem.Click += (_, _) =>
        {
            _settings.NotifyOnConnectDisconnect = notifyConnectItem.Checked;
            _settings.Save();
        };
        alertMenu.DropDownItems.Add(notifyConnectItem);

        var focusAssistItem = new ToolStripMenuItem("Silenciar em jogos / tela cheia (Não Perturbe)")
        {
            Checked = _settings.RespectFocusAssist,
            CheckOnClick = true
        };
        focusAssistItem.Click += (_, _) =>
        {
            _settings.RespectFocusAssist = focusAssistItem.Checked;
            _settings.Save();
        };
        alertMenu.DropDownItems.Add(focusAssistItem);

        menu.Items.Add(alertMenu);

        // Poll interval submenu
        var intervalMenu = new ToolStripMenuItem("Intervalo de atualização");
        foreach (int secs in new[] { 10, 20, 30, 60 })
        {
            var item = new ToolStripMenuItem($"{secs} segundos")
            {
                Checked = _settings.PollIntervalSeconds == secs
            };
            item.Click += (_, _) =>
            {
                _settings.PollIntervalSeconds = secs;
                _settings.Save();
                _pollTimer.Interval = secs * 1000;
                foreach (ToolStripMenuItem sibling in intervalMenu.DropDownItems)
                    sibling.Checked = sibling == item;
            };
            intervalMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(intervalMenu);

        // Start with Windows
        var autoStartItem = new ToolStripMenuItem("Iniciar com o Windows")
        {
            Checked = AppSettings.IsAutoStartEnabled(),
            CheckOnClick = true
        };
        autoStartItem.Click += (_, _) =>
        {
            bool ok = AppSettings.SetAutoStart(autoStartItem.Checked);
            if (!ok)
                autoStartItem.Checked = AppSettings.IsAutoStartEnabled();
        };
        menu.Items.Add(autoStartItem);

        menu.Items.Add(new ToolStripSeparator());

        var itemExit = new ToolStripMenuItem("Sair");
        itemExit.Click += (_, _) => ExitApplication();
        menu.Items.Add(itemExit);

        return menu;
    }

    private void AddStyleOption(ToolStripMenuItem parent, string title, TrayIconStyle style)
    {
        var item = new ToolStripMenuItem(title) { Checked = _settings.IconStyle == style };
        item.Click += (_, _) =>
        {
            _settings.IconStyle = style;
            _settings.Save();
            foreach (ToolStripMenuItem sibling in parent.DropDownItems)
                sibling.Checked = sibling == item;
            UpdateTrayVisuals(_service.VisibleReadings);
        };
        parent.DropDownItems.Add(item);
    }

    private void RebuildDevicesMenu(IReadOnlyList<BatteryReading> readings)
    {
        _devicesSubmenu.DropDownItems.Clear();

        var autoItem = new ToolStripMenuItem("Automático (menor bateria)")
        {
            Checked = string.IsNullOrWhiteSpace(_settings.PreferredDeviceId)
        };
        autoItem.Click += (_, _) =>
        {
            _settings.PreferredDeviceId = null;
            _settings.Save();
            UpdateTrayVisuals(_service.VisibleReadings);
        };
        _devicesSubmenu.DropDownItems.Add(autoItem);
        _devicesSubmenu.DropDownItems.Add(new ToolStripSeparator());

        var connected = readings.Where(r => r.IsConnected).ToList();
        if (connected.Count == 0)
        {
            _devicesSubmenu.DropDownItems.Add(new ToolStripMenuItem("Nenhum dispositivo conectado") { Enabled = false });
            return;
        }

        foreach (var dev in connected)
        {
            string pctStr = dev.BatteryPercent is int pct ? $"{pct}%" : "N/D";
            string chgStr = dev.IsCharging ? " ⚡" : string.Empty;
            string dpiStr = dev.Dpi is int d ? $" • {d} DPI" : "";
            var item = new ToolStripMenuItem($"{dev.DisplayName} — {pctStr}{chgStr} ({dev.ShortModeLabel}{dpiStr})")
            {
                Checked = string.Equals(_settings.PreferredDeviceId, dev.DeviceId, StringComparison.OrdinalIgnoreCase)
            };
            string devId = dev.DeviceId;
            item.Click += (_, _) =>
            {
                _settings.PreferredDeviceId = devId;
                _settings.Save();
                UpdateTrayVisuals(_service.VisibleReadings);
            };
            _devicesSubmenu.DropDownItems.Add(item);
        }
    }

    private void ShowDiagnostics()
    {
        _diagnosticsForm.UpdateView(_service.AllReadings);
        if (!_diagnosticsForm.Visible)
            _diagnosticsForm.Show();
        _diagnosticsForm.BringToFront();
        _diagnosticsForm.Activate();
    }

    private async Task TriggerRefreshAsync()
    {
        if (_cts.IsCancellationRequested) return;
        try
        {
            await _service.RefreshAsync(_cts.Token);
        }
        catch (OperationCanceledException) { }
    }

    private void ScheduleDebouncedRefresh()
    {
        if (_deviceWindow.Handle == IntPtr.Zero) return;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            ScheduleDebouncedRefresh();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            RunOnUiThread(() => UpdateTrayVisuals(_service.VisibleReadings));
        }
    }

    private void OnReadingsUpdated(IReadOnlyList<BatteryReading> visibleReadings)
    {
        RunOnUiThread(() =>
        {
            UpdateTrayVisuals(visibleReadings);
            CheckNotifications(visibleReadings);

            if (_flyoutForm.Visible)
                _flyoutForm.UpdateContent(visibleReadings);

            if (_diagnosticsForm.Visible)
                _diagnosticsForm.UpdateView(_service.AllReadings);
        });
    }

    private void RunOnUiThread(Action action)
    {
        if (_cts.IsCancellationRequested || _uiMarshal.IsDisposed || !_uiMarshal.IsHandleCreated)
            return;

        if (_uiMarshal.InvokeRequired)
            _uiMarshal.BeginInvoke(action);
        else
            action();
    }

    private void UpdateTrayVisuals(IReadOnlyList<BatteryReading> readings)
    {
        RebuildDevicesMenu(readings);

        var primary = MouseBatteryService.SelectPrimary(readings, _settings.PreferredDeviceId);
        var connectedList = readings.Where(r => r.IsConnected).ToList();
        bool useMultiIcons = _settings.SeparateIconPerDevice && connectedList.Count > 1;
        bool showBadge = useMultiIcons && _settings.ShowDeviceTypeBadge;

        var oldPrimary = _primaryIcon;
        _primaryIcon = TrayIconRenderer.CreateIcon(primary, _settings.IconStyle, showBadge);
        _primaryNotifyIcon.Icon = _primaryIcon;
        oldPrimary?.Dispose();

        if (useMultiIcons)
        {
            _primaryNotifyIcon.Text = BuildSingleDeviceTooltip(primary);
            _menuHeader.Text = $"{connectedList.Count} dispositivos conectados";

            var extras = connectedList
                .Where(r => !string.Equals(r.DeviceId, primary.DeviceId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var activeExtraIds = new HashSet<string>(extras.Select(e => e.DeviceId), StringComparer.OrdinalIgnoreCase);

            foreach (var staleId in _extraIcons.Keys.Where(k => !activeExtraIds.Contains(k)).ToList())
            {
                var (tray, icon) = _extraIcons[staleId];
                tray.Visible = false;
                tray.Dispose();
                icon.Dispose();
                _extraIcons.Remove(staleId);
            }

            foreach (var dev in extras)
            {
                var newIcon = TrayIconRenderer.CreateIcon(dev, _settings.IconStyle, showBadge);
                string tip = BuildSingleDeviceTooltip(dev);

                if (_extraIcons.TryGetValue(dev.DeviceId, out var existing))
                {
                    existing.Tray.Icon = newIcon;
                    existing.Tray.Text = tip;
                    existing.CurrentIcon.Dispose();
                    _extraIcons[dev.DeviceId] = (existing.Tray, newIcon);
                }
                else
                {
                    var extraTray = CreateNotifyIcon(newIcon, tip);
                    _extraIcons[dev.DeviceId] = (extraTray, newIcon);
                }
            }
        }
        else
        {
            ClearExtraIcons();

            string pctPart = primary.BatteryPercent is int pct ? $"{pct}%" : "N/D";
            string chargePart = primary.IsCharging ? " ⚡" : string.Empty;

            _menuHeader.Text = primary.Mode switch
            {
                ConnectionMode.Sleep24G when primary.BatteryPercent is int last =>
                    $"{primary.DisplayName} — Repouso (última: {last}%)",
                ConnectionMode.Disconnected =>
                    "Nenhum dispositivo conectado",
                _ when connectedList.Count > 1 =>
                    $"{primary.DisplayName}: {pctPart}{chargePart} (+{connectedList.Count - 1})",
                _ =>
                    $"{primary.DisplayName} — {pctPart}{chargePart}"
            };

            _primaryNotifyIcon.Text = connectedList.Count > 1
                ? BuildMultiDeviceTooltip(connectedList)
                : BuildSingleDeviceTooltip(primary);
        }
    }

    private void ClearExtraIcons()
    {
        foreach (var (_, (tray, icon)) in _extraIcons)
        {
            tray.Visible = false;
            tray.Dispose();
            icon.Dispose();
        }
        _extraIcons.Clear();
    }

    private static string BuildSingleDeviceTooltip(BatteryReading reading)
    {
        string pctPart = reading.BatteryPercent is int pct ? $"{pct}%" : "Indisponível";
        string chargePart = reading.IsCharging ? " ⚡ Carregando" : string.Empty;
        string extras = string.Join(" • ", new[]
        {
            reading.Dpi is int d ? $"{d} DPI" : null,
            reading.PollingRateHz is int hz ? $"{hz}Hz" : null,
            reading.EstimatedTimeLabel
        }.Where(x => !string.IsNullOrEmpty(x)));

        string extraLine = string.IsNullOrEmpty(extras) ? string.Empty : $"\n{extras}";

        string tooltip = reading.Mode switch
        {
            ConnectionMode.Sleep24G when reading.BatteryPercent is int last =>
                $"{reading.DisplayName}\nEm repouso (2.4 GHz)\nÚltima leitura: {last}%",
            ConnectionMode.Disconnected =>
                "Monitor de Bateria\nNenhum dispositivo conectado",
            ConnectionMode.Bluetooth when reading.BatteryPercent is null =>
                $"{reading.DisplayName}\nModo: Bluetooth\nBateria indisponível via BT",
            _ =>
                $"{reading.DisplayName}\nBateria: {pctPart}{chargePart} ({reading.ShortModeLabel}){extraLine}"
        };

        return tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private static string BuildMultiDeviceTooltip(List<BatteryReading> devices)
    {
        var sb = new StringBuilder();
        foreach (var d in devices)
        {
            string pct = d.BatteryPercent is int p ? $"{p}%" : "N/D";
            string chg = d.IsCharging ? "⚡" : "";
            string line = $"{d.DisplayName}: {pct}{chg} ({d.ShortModeLabel})";
            if (sb.Length > 0 && sb.Length + line.Length + 1 > 124)
            {
                sb.Append("\n...");
                break;
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }

        string text = sb.ToString();
        return text.Length > 127 ? text[..127] : text;
    }

    private bool ShouldSuppressNotification()
    {
        if (!_settings.RespectFocusAssist)
            return false;

        try
        {
            if (SHQueryUserNotificationState(out int state) == 0)
            {
                // QUNS_BUSY(2), QUNS_RUNNING_D3D_FULL_SCREEN(3), QUNS_PRESENTATION_MODE(4), QUNS_QUIET_TIME(6)
                return state is 2 or 3 or 4 or 6;
            }
        }
        catch { }

        return false;
    }

    private void ShowNotificationIfAllowed(int timeoutMs, string title, string body, ToolTipIcon icon)
    {
        if (ShouldSuppressNotification())
            return;
        _primaryNotifyIcon.ShowBalloonTip(timeoutMs, title, body, icon);
    }

    private void CheckNotifications(IReadOnlyList<BatteryReading> readings)
    {
        var currentConnected = readings
            .Where(r => r.IsConnected)
            .ToDictionary(r => r.DeviceId, r => r, StringComparer.OrdinalIgnoreCase);

        // Optional connect/disconnect notifications (after initial startup poll)
        if (_hasCompletedFirstPoll && _settings.NotifyOnConnectDisconnect)
        {
            foreach (var (id, dev) in currentConnected)
            {
                if (!_knownConnectedDevices.ContainsKey(id))
                {
                    string pct = dev.BatteryPercent is int p ? $" ({p}%)" : "";
                    ShowNotificationIfAllowed(
                        3500,
                        $"Conectado — {dev.DisplayName}",
                        $"Modo: {dev.ModeLabel}{pct}",
                        ToolTipIcon.Info);
                }
            }

            foreach (var (id, oldName) in _knownConnectedDevices)
            {
                if (!currentConnected.ContainsKey(id))
                {
                    ShowNotificationIfAllowed(
                        3000,
                        $"Desconectado — {oldName}",
                        "O dispositivo foi desconectado ou entrou em repouso.",
                        ToolTipIcon.Info);
                }
            }
        }

        _knownConnectedDevices.Clear();
        foreach (var (id, dev) in currentConnected)
            _knownConnectedDevices[id] = dev.DisplayName;
        _hasCompletedFirstPoll = true;

        // Low battery & full charge notifications per device
        foreach (var reading in currentConnected.Values)
        {
            if (reading.BatteryPercent is not int pct)
                continue;

            if (_settings.NotifyOnLowBattery && _settings.LowBatteryThreshold > 0)
            {
                if (!reading.IsCharging && pct <= _settings.LowBatteryThreshold && !_lowBatteryNotified.Contains(reading.DeviceId))
                {
                    _lowBatteryNotified.Add(reading.DeviceId);
                    ShowNotificationIfAllowed(
                        5000,
                        $"Bateria baixa — {reading.DisplayName}",
                        $"Nível atual: {pct}% ({reading.ModeLabel}). Hora de recarregar.",
                        ToolTipIcon.Warning);
                }
                else if (reading.IsCharging || pct >= _settings.LowBatteryThreshold + 5)
                {
                    _lowBatteryNotified.Remove(reading.DeviceId);
                }
            }

            if (_settings.NotifyOnFullCharge)
            {
                if (reading.IsCharging && pct >= 100 && !_fullChargeNotified.Contains(reading.DeviceId))
                {
                    _fullChargeNotified.Add(reading.DeviceId);
                    ShowNotificationIfAllowed(
                        4000,
                        $"Carga completa — {reading.DisplayName}",
                        "A bateria atingiu 100%.",
                        ToolTipIcon.Info);
                }
                else if (!reading.IsCharging || pct < 98)
                {
                    _fullChargeNotified.Remove(reading.DeviceId);
                }
            }
        }
    }

    private void ExitApplication()
    {
        _cts.Cancel();
        _pollTimer.Stop();
        _debounceTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _deviceWindow.DestroyHandle();
        ClearExtraIcons();
        _primaryNotifyIcon.Visible = false;
        _primaryNotifyIcon.Dispose();
        _primaryIcon?.Dispose();
        _flyoutForm.Dispose();
        _diagnosticsForm.Dispose();
        _service.Dispose();
        _uiMarshal.Dispose();
        _shutdownSignal.Dispose();
        ExitThread();
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    private sealed class DeviceNotificationWindow : NativeWindow
    {
        private const int WM_DEVICECHANGE = 0x0219;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        private readonly uint _taskbarCreatedMsg;
        private readonly Action _onDeviceChanged;
        private readonly Action _onTaskbarRecreated;

        public DeviceNotificationWindow(Action onDeviceChanged, Action onTaskbarRecreated)
        {
            _onDeviceChanged = onDeviceChanged;
            _onTaskbarRecreated = onTaskbarRecreated;
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE)
                _onDeviceChanged();
            else if (_taskbarCreatedMsg != 0 && (uint)m.Msg == _taskbarCreatedMsg)
                _onTaskbarRecreated();

            base.WndProc(ref m);
        }
    }
}
