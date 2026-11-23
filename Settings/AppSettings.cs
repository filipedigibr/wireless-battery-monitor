using System.Text.Json;
using Microsoft.Win32;

namespace KSnakeBatteryTray.Settings;

public enum TrayIconStyle
{
    BatteryAndNumber = 0,
    NumberOnly = 1,
    BatteryIconOnly = 2
}

public sealed class AppSettings
{
    public const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "WirelessBatteryMonitor";
    private const string LegacyRunValueName = "KSnakeX11BatteryTray";

    public static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSnakeBatteryTray");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    public TrayIconStyle IconStyle { get; set; } = TrayIconStyle.BatteryAndNumber;
    public int LowBatteryThreshold { get; set; } = 15;
    public bool NotifyOnLowBattery { get; set; } = true;
    public bool NotifyOnFullCharge { get; set; } = true;
    public bool NotifyOnConnectDisconnect { get; set; } = false;
    public bool RespectFocusAssist { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 20;

    /// <summary>
    /// If true, spawns one tray icon per connected device. If false, keeps 1 icon with a device selector in the menu/flyout.
    /// </summary>
    public bool SeparateIconPerDevice { get; set; } = false;

    /// <summary>
    /// Shows a subtle device-type indicator (mouse/audio/keyboard) when displaying multiple tray icons.
    /// </summary>
    public bool ShowDeviceTypeBadge { get; set; } = true;

    /// <summary>
    /// Optional pinned DeviceId when using single-icon mode. Null/empty = automatic (lowest battery).
    /// </summary>
    public string? PreferredDeviceId { get; set; }

    /// <summary>
    /// Optional user-defined display names keyed by DeviceId.
    /// </summary>
    public Dictionary<string, string> DeviceAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional custom accent colors (#RRGGBB) keyed by DeviceId.
    /// </summary>
    public Dictionary<string, string> DeviceAccents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// DeviceIds hidden from the tray/flyout by user choice.
    /// </summary>
    public HashSet<string> HiddenDeviceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
                    loaded.PollIntervalSeconds = Math.Clamp(loaded.PollIntervalSeconds, 5, 300);
                    loaded.LowBatteryThreshold = Math.Clamp(loaded.LowBatteryThreshold, 0, 50);
                    loaded.DeviceAliases = new Dictionary<string, string>(
                        loaded.DeviceAliases ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                    loaded.DeviceAccents = new Dictionary<string, string>(
                        loaded.DeviceAccents ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                    loaded.HiddenDeviceIds = new HashSet<string>(
                        loaded.HiddenDeviceIds ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch
        {
            // Fallback to default settings if corrupted.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Ignore write errors gracefully.
        }
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            var val = (key?.GetValue(RunValueName) as string) ?? (key?.GetValue(LegacyRunValueName) as string);
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetAutoStart(bool enable, string? explicitExePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            if (key is null) return false;

            key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);

            if (enable)
            {
                var exePath = explicitExePath ?? Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath)) return false;
                key.SetValue(RunValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
