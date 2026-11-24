using System.Text.Json;
using KSnakeBatteryTray.Models;
using KSnakeBatteryTray.Settings;

namespace KSnakeBatteryTray.Services;

/// <summary>
/// Persists recent battery telemetry per device (last 72 hours) and estimates
/// remaining battery life or time until full charge using Ordinary Least Squares (OLS)
/// linear regression over the active session, resilient to voltage jitter and manual refreshes.
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
                bool chargingChanged = last.IsCharging != isCharging;
                bool percentChanged = last.Percent != percent;
                bool intervalElapsed = (now - last.TimestampUtc) >= TimeSpan.FromMinutes(10);

                if (chargingChanged || percentChanged || intervalElapsed)
                {
                    // Avoid flooding if percentage fluctuates within 45 seconds without changing charging state
                    if (!chargingChanged && percentChanged && (now - last.TimestampUtc) < TimeSpan.FromSeconds(45))
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
            if (!_history.TryGetValue(deviceId, out var list) || list.Count < 3)
                return null;
            samples = list.ToList();
        }

        // Walk backwards to isolate the current contiguous charging/discharging session.
        // Tolerates normal +/-2-5% LiPo voltage jitter and only splits on real charging transitions,
        // long idle gaps (>4h), or sustained external charge/discharge jumps (>=10%).
        int endIdx = samples.Count - 1;
        if (samples[endIdx].IsCharging != isCharging)
            return null;

        int startIdx = endIdx;
        for (int i = endIdx - 1; i >= 0; i--)
        {
            var s = samples[i];
            if (s.IsCharging != isCharging)
                break;

            var gap = samples[i + 1].TimestampUtc - s.TimestampUtc;
            if (gap > TimeSpan.FromHours(4))
                break;

            // Detect sustained opposite jump (>=10% comparing local medians across the step)
            double leftMed = LocalMedianPercent(samples, Math.Max(0, i - 2), i);
            double rightMed = LocalMedianPercent(samples, i + 1, Math.Min(endIdx, i + 3));

            if (!isCharging && (rightMed - leftMed) >= 10.0)
                break;
            if (isCharging && (leftMed - rightMed) >= 10.0)
                break;

            startIdx = i;
        }

        int count = endIdx - startIdx + 1;
        if (count < 3)
            return null;

        // Anchor the regression purely on recorded sample timestamps (NOT DateTime.UtcNow),
        // so manual "Atualizar agora" clicks never artificially inflate the remaining estimate.
        DateTime t0 = samples[startIdx].TimestampUtc;
        DateTime tLast = samples[endIdx].TimestampUtc;
        double spanHours = (tLast - t0).TotalHours;

        double minSpanHours = isCharging ? (5.0 / 60.0) : (20.0 / 60.0);
        if (spanHours < minSpanHours)
            return null;

        // Ordinary Least Squares (OLS) linear regression: y(t) = a + b*t (t in hours, y in %)
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        for (int i = startIdx; i <= endIdx; i++)
        {
            double x = (samples[i].TimestampUtc - t0).TotalHours;
            double y = samples[i].Percent;
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        double denom = count * sumXX - sumX * sumX;
        if (denom <= 1e-9)
            return null;

        double slopePerHour = (count * sumXY - sumX * sumY) / denom;
        double fittedDelta = slopePerHour * spanHours;

        if (isCharging)
        {
            if (slopePerHour < 1.0 || fittedDelta < 2.0)
                return null;

            double hoursToFull = (100.0 - pct) / slopePerHour;
            if (hoursToFull < 0.05 || hoursToFull > 12.0)
                return null;

            return $"~{FormatDuration(TimeSpan.FromHours(hoursToFull))} até 100%";
        }
        else
        {
            double drainPerHour = -slopePerHour;
            if (drainPerHour < 0.15 || -fittedDelta < 2.0)
                return null;

            double hoursLeft = pct / drainPerHour;
            if (hoursLeft < 0.1 || hoursLeft > 200.0)
                return null;

            return $"~{FormatDuration(TimeSpan.FromHours(hoursLeft))} restantes";
        }
    }

    private static double LocalMedianPercent(List<BatterySample> samples, int from, int to)
    {
        int len = to - from + 1;
        if (len <= 0) return 0;
        if (len == 1) return samples[from].Percent;

        Span<int> vals = len <= 8 ? stackalloc int[len] : new int[len];
        for (int k = 0; k < len; k++)
            vals[k] = samples[from + k].Percent;
        vals.Sort();

        return (len % 2 == 1)
            ? vals[len / 2]
            : (vals[len / 2 - 1] + vals[len / 2]) / 2.0;
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
