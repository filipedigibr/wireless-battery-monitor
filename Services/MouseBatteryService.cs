using KSnakeBatteryTray.Models;
using KSnakeBatteryTray.Providers;
using KSnakeBatteryTray.Settings;

namespace KSnakeBatteryTray.Services;

public sealed class MouseBatteryService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly YjxHidProvider _hidProvider = new();
    private readonly BluetoothBatteryProvider _btProvider = new();
    private readonly BatteryHistoryStore _history = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly Queue<string> _diagnosticLog = new();
    private readonly object _logLock = new();
    private readonly object _stateLock = new();

    private int? _lastKnownX11Battery;
    private bool _lastKnownX11Charging;
    private string? _lastKnownX11Firmware;
    private int? _lastKnownX11Dpi;
    private int? _lastKnownX11PollingHz;

    public IReadOnlyList<BatteryReading> AllReadings { get; private set; } =
        [BatteryReading.Disconnected("Iniciando leitura...")];

    public IReadOnlyList<BatteryReading> VisibleReadings => FilterVisible(AllReadings);

    public event Action<IReadOnlyList<BatteryReading>>? ReadingsUpdated;

    public MouseBatteryService(AppSettings? settings = null)
    {
        _settings = settings ?? new AppSettings();
        _hidProvider.PushEventReceived += OnHidPushEvent;
    }

    public IReadOnlyList<BatterySample> GetHistory(string deviceId) => _history.GetSamples(deviceId);

    public string GetDiagnosticSnapshot()
    {
        lock (_logLock)
        {
            return string.Join(Environment.NewLine, _diagnosticLog);
        }
    }

    public void ReapplyPresentationSettings()
    {
        List<BatteryReading> updated;
        lock (_stateLock)
        {
            updated = AllReadings.Select(ApplyCustomization).ToList();
            AllReadings = updated;
        }
        ReadingsUpdated?.Invoke(VisibleReadings);
    }

    public async Task<IReadOnlyList<BatteryReading>> RefreshAsync(CancellationToken ct = default)
    {
        if (!await _pollLock.WaitAsync(0, ct))
            return VisibleReadings;

        try
        {
            var hidTask = _hidProvider.ProbeAsync(ct);
            var btTask = _btProvider.ProbeAllAsync(ct);

            await Task.WhenAll(hidTask, btTask);

            var hidResult = hidTask.Result;
            var btResult = btTask.Result;

            AppendLog(hidResult.DiagnosticLine);
            foreach (var line in btResult.DiagnosticLines)
                AppendLog(line);

            if (!string.IsNullOrEmpty(hidResult.FirmwareVersion))
                _lastKnownX11Firmware = hidResult.FirmwareVersion;
            if (hidResult.Dpi.HasValue)
                _lastKnownX11Dpi = hidResult.Dpi;
            if (hidResult.PollingRateHz.HasValue)
                _lastKnownX11PollingHz = hidResult.PollingRateHz;

            var readings = new List<BatteryReading>();

            // 1) K-Snake X11 Pro via USB-C Wired or 2.4 GHz Dongle
            if (hidResult.DeviceFound && hidResult.IsOnline && hidResult.Mode is ConnectionMode.Wired or ConnectionMode.Wireless24G)
            {
                if (hidResult.BatteryPercent is int pct)
                {
                    _lastKnownX11Battery = pct;
                    _lastKnownX11Charging = hidResult.IsCharging;
                    _history.Record("yjx:x11", pct, hidResult.IsCharging);
                }

                string? estimate = _history.EstimateRemainingLabel("yjx:x11", hidResult.BatteryPercent ?? _lastKnownX11Battery, hidResult.IsCharging);
                readings.Add(ApplyCustomization(new BatteryReading(
                    "yjx:x11",
                    hidResult.Mode,
                    DeviceKind.Mouse,
                    "K-Snake X11 Pro",
                    hidResult.BatteryPercent ?? _lastKnownX11Battery,
                    hidResult.IsCharging,
                    _lastKnownX11Firmware,
                    DateTime.Now,
                    hidResult.DiagnosticLine,
                    _lastKnownX11Dpi,
                    _lastKnownX11PollingHz,
                    estimate)));
            }

            // 2) ALL connected Bluetooth devices (BLE mice/keyboards + Classic BT audio/headsets)
            bool hasX11OverBluetooth = false;
            foreach (var bt in btResult.Devices)
            {
                bool isX11Bt = bt.DeviceName.Contains("X11", StringComparison.OrdinalIgnoreCase)
                            || bt.DeviceName.Contains("K-Snake", StringComparison.OrdinalIgnoreCase);
                if (isX11Bt)
                    hasX11OverBluetooth = true;

                if (bt.BatteryPercent is int btPct)
                    _history.Record(bt.DeviceId, btPct, false);

                string? estimate = _history.EstimateRemainingLabel(bt.DeviceId, bt.BatteryPercent, false);

                readings.Add(ApplyCustomization(new BatteryReading(
                    bt.DeviceId,
                    ConnectionMode.Bluetooth,
                    bt.Kind,
                    bt.DeviceName,
                    bt.BatteryPercent,
                    false,
                    isX11Bt ? _lastKnownX11Firmware : null,
                    DateTime.Now,
                    bt.Detail,
                    null,
                    null,
                    estimate)));
            }

            // 3) 2.4 GHz Dongle plugged in, mouse asleep (and not switched to X11BT)
            if (readings.Count == 0 && hidResult.DeviceFound && hidResult.Mode == ConnectionMode.Sleep24G && !hasX11OverBluetooth)
            {
                readings.Add(ApplyCustomization(new BatteryReading(
                    "yjx:x11",
                    ConnectionMode.Sleep24G,
                    DeviceKind.Mouse,
                    "K-Snake X11 Pro",
                    _lastKnownX11Battery,
                    false,
                    _lastKnownX11Firmware,
                    DateTime.Now,
                    hidResult.DiagnosticLine,
                    _lastKnownX11Dpi,
                    _lastKnownX11PollingHz,
                    null)));
            }

            // 4) Fallback if nothing is connected
            if (readings.Count == 0)
            {
                readings.Add(BatteryReading.Disconnected(hidResult.DiagnosticLine));
            }

            lock (_stateLock)
            {
                AllReadings = readings;
            }

            var visible = FilterVisible(readings);
            ReadingsUpdated?.Invoke(visible);
            return visible;
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private void OnHidPushEvent(YjxHidProvider.HidPushEvent push)
    {
        if (!string.IsNullOrEmpty(push.LogLine))
            AppendLog(push.LogLine);

        if (push.BatteryPercent is int p)
            _lastKnownX11Battery = p;
        if (push.IsCharging is bool chg)
            _lastKnownX11Charging = chg;
        if (push.Dpi is int d)
            _lastKnownX11Dpi = d;
        if (push.PollingRateHz is int hz)
            _lastKnownX11PollingHz = hz;

        if (_lastKnownX11Battery is int batVal && push.IsOnline != false)
            _history.Record("yjx:x11", batVal, _lastKnownX11Charging);

        List<BatteryReading> updated;
        lock (_stateLock)
        {
            updated = AllReadings.Where(r => r.Mode != ConnectionMode.Disconnected).ToList();
            int existingIdx = updated.FindIndex(r => string.Equals(r.DeviceId, "yjx:x11", StringComparison.OrdinalIgnoreCase));

            var mode = push.IsOnline == false ? ConnectionMode.Sleep24G : push.Mode;
            string? estimate = mode == ConnectionMode.Sleep24G
                ? null
                : _history.EstimateRemainingLabel("yjx:x11", _lastKnownX11Battery, _lastKnownX11Charging);

            var next = ApplyCustomization(new BatteryReading(
                "yjx:x11",
                mode,
                DeviceKind.Mouse,
                "K-Snake X11 Pro",
                _lastKnownX11Battery,
                mode != ConnectionMode.Sleep24G && _lastKnownX11Charging,
                _lastKnownX11Firmware,
                DateTime.Now,
                push.LogLine,
                _lastKnownX11Dpi,
                _lastKnownX11PollingHz,
                estimate));

            if (existingIdx >= 0)
                updated[existingIdx] = next;
            else
                updated.Insert(0, next);

            AllReadings = updated;
        }

        ReadingsUpdated?.Invoke(FilterVisible(updated));
    }

    private BatteryReading ApplyCustomization(BatteryReading r)
    {
        _settings.DeviceAliases.TryGetValue(r.DeviceId, out string? alias);
        _settings.DeviceAccents.TryGetValue(r.DeviceId, out string? accent);
        return r with { CustomAlias = alias, CustomAccentHex = accent };
    }

    private IReadOnlyList<BatteryReading> FilterVisible(IReadOnlyList<BatteryReading> all)
    {
        var visible = all.Where(r => !_settings.HiddenDeviceIds.Contains(r.DeviceId)).ToList();
        if (visible.Count == 0)
            return [BatteryReading.Disconnected("Todos os dispositivos estão ocultos ou desconectados.")];
        return visible;
    }

    public static BatteryReading SelectPrimary(IReadOnlyList<BatteryReading> readings, string? preferredDeviceId)
    {
        if (readings.Count == 0)
            return BatteryReading.Disconnected();

        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            var pinned = readings.FirstOrDefault(r =>
                string.Equals(r.DeviceId, preferredDeviceId, StringComparison.OrdinalIgnoreCase));
            if (pinned is not null)
                return pinned;
        }

        var withBattery = readings
            .Where(r => r.IsConnected && r.BatteryPercent is not null)
            .OrderBy(r => r.BatteryPercent!.Value)
            .FirstOrDefault();

        if (withBattery is not null)
            return withBattery;

        return readings.FirstOrDefault(r => r.IsConnected) ?? readings[0];
    }

    private void AppendLog(string line)
    {
        lock (_logLock)
        {
            _diagnosticLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (_diagnosticLog.Count > 80)
                _diagnosticLog.Dequeue();
        }
    }

    public void Dispose()
    {
        _hidProvider.PushEventReceived -= OnHidPushEvent;
        _hidProvider.Dispose();
        _history.Flush();
        _pollLock.Dispose();
    }
}
