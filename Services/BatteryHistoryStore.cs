using System.Text.Json;
using KSnakeBatteryTray.Models;
using KSnakeBatteryTray.Settings;

namespace KSnakeBatteryTray.Services;

/// <summary>
/// Persists recent battery telemetry per device (last 72 hours) and estimates
/// remaining battery life or time until full charge based on observed drain/charge rates.
/// </summary>
public sealed class BatteryHistoryStore
{
    private static readonly string HistoryFile = Path.Combine(AppSettings.SettingsDir, "history.json");
    private const int MaxSamplesPerDevice = 240;
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(72);

    private readonly object _lock = new();
    private Dictionary<string, List<BatterySample>> _history = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastSaveUtc = DateTime.MinValue;

    public BatteryHistoryStore()
    {
        Load();
    }

    public void Record(string deviceId, int percent, bool isCharging)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == "none" || percent is < 0 or > 100)
            return;

        DateTime now = DateTime.UtcNow;
        bool shouldSave = false;

        lock (_lock)
        {
            if (!_history.TryGetValue(deviceId, out var list))
            {
                list = new List<BatterySample>();
                _history[deviceId] = list;
            }

            // Prune samples older than 72h
            DateTime cutoff = now - RetentionWindow;
            list.RemoveAll(s => s.TimestampUtc < cutoff);

            if (list.Count == 0)
            {
                list.Add(new BatterySample(now, percent, isCharging));
                shouldSave = true;
            }
            else
            {
                var last = list[^1];
                bool stateChanged = last.Percent != percent || last.IsCharging != isCharging;
                bool intervalElapsed = (now - last.TimestampUtc) >= TimeSpan.FromMinutes(10);

                if (stateChanged || intervalElapsed)
                {
                    // Avoid flooding if percentage fluctuates rapidly within 15 seconds
                    if (stateChanged && (now - last.TimestampUtc) < TimeSpan.FromSeconds(15) && last.IsCharging == isCharging)
                    {
                        list[^1] = new BatterySample(now, percent, isCharging);
                    }
                    else
                    {
                        list.Add(new BatterySample(now, percent, isCharging));
                        if (list.Count > MaxSamplesPerDevice)
                            list.RemoveRange(0, list.Count - MaxSamplesPerDevice);
                    }
                    shouldSave = true;
                }
            }

            if (shouldSave && (now - _lastSaveUtc) >= TimeSpan.FromSeconds(30))
            {
                _lastSaveUtc = now;
                SaveUnsafe();
            }
        }
    }

    public IReadOnlyList<BatterySample> GetSamples(string deviceId)
    {
        lock (_lock)
        {
            if (_history.TryGetValue(deviceId, out var list))
                return list.ToList();
            return Array.Empty<BatterySample>();
        }
    }

    public string? EstimateRemainingLabel(string deviceId, int? currentPercent, bool isCharging)
    {
        if (currentPercent is not int pct)
            return null;

        if (isCharging && pct >= 100)
            return "Carga completa";

        List<BatterySample> samples;
        lock (_lock)
        {
            if (!_history.TryGetValue(deviceId, out var list) || list.Count < 2)
                return null;
            samples = list.ToList();
        }

        // Walk backwards to find the start of the current contiguous charging/discharging session
        // (ignoring gaps > 3 hours where the PC or mouse was turned off).
        DateTime now = DateTime.UtcNow;
        BatterySample anchor = samples[^1];
        if (anchor.IsCharging != isCharging)
            return null;

        BatterySample? earliest = null;
        for (int i = samples.Count - 2; i >= 0; i--)
        {
            var s = samples[i];
            if (s.IsCharging != isCharging)
                break;

            var gap = samples[i + 1].TimestampUtc - s.TimestampUtc;
            if (gap > TimeSpan.FromHours(3))
                break;

            // Stop at a session boundary where battery jumped in the opposite direction (e.g. charged externally)
            if (!isCharging && s.Percent < samples[i + 1].Percent - 2)
                break;
            if (isCharging && s.Percent > samples[i + 1].Percent + 2)
                break;

            earliest = s;
        }

        if (earliest is null)
            return null;

        double elapsedHours = (now - earliest.TimestampUtc).TotalHours;
        if (elapsedHours < 0.05) // Need at least ~3 minutes of data
            return null;

        if (isCharging)
        {
            int gained = pct - earliest.Percent;
            if (gained < 2)
                return null;

            double ratePerHour = gained / elapsedHours;
            if (ratePerHour <= 0.5)
                return null;

            double hoursToFull = (100 - pct) / ratePerHour;
            if (hoursToFull < 0.05 || hoursToFull > 12)
                return null;

            return $"~{FormatDuration(TimeSpan.FromHours(hoursToFull))} até 100%";
        }
        else
        {
            int drained = earliest.Percent - pct;
            if (drained < 2)
                return null;

            double drainPerHour = drained / elapsedHours;
            if (drainPerHour <= 0.1)
                return null;

            double hoursLeft = pct / drainPerHour;
            if (hoursLeft < 0.1 || hoursLeft > 200)
                return null;

            return $"~{FormatDuration(TimeSpan.FromHours(hoursLeft))} restantes";
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            SaveUnsafe();
        }
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalMinutes < 60)
            return $"{Math.Max(1, (int)Math.Round(ts.TotalMinutes))} min";

        int hours = (int)ts.TotalHours;
        int mins = ts.Minutes;
        if (hours >= 24 || mins < 10)
            return $"{hours}h";
        return $"{hours}h {mins}m";
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(HistoryFile)) return;
            var json = File.ReadAllText(HistoryFile);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, List<BatterySample>>>(json);
            if (parsed is not null)
                _history = new Dictionary<string, List<BatterySample>>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _history = new Dictionary<string, List<BatterySample>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveUnsafe()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDir);
            var json = JsonSerializer.Serialize(_history);
            File.WriteAllText(HistoryFile, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
