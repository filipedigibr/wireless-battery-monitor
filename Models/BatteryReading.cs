namespace KSnakeBatteryTray.Models;

public enum ConnectionMode
{
    Disconnected,
    Sleep24G,
    Wired,
    Wireless24G,
    Bluetooth
}

public enum DeviceKind
{
    Mouse,
    Audio,
    Keyboard,
    Other
}

public sealed record BatterySample(DateTime TimestampUtc, int Percent, bool IsCharging);

public sealed record BatteryReading(
    string DeviceId,
    ConnectionMode Mode,
    DeviceKind Kind,
    string DeviceName,
    int? BatteryPercent,
    bool IsCharging,
    string? FirmwareVersion,
    DateTime Timestamp,
    string? Detail = null,
    int? Dpi = null,
    int? PollingRateHz = null,
    string? EstimatedTimeLabel = null,
    string? CustomAlias = null,
    string? CustomAccentHex = null)
{
    public bool IsConnected => Mode is ConnectionMode.Wired or ConnectionMode.Wireless24G or ConnectionMode.Bluetooth;

    public string DisplayName => !string.IsNullOrWhiteSpace(CustomAlias) ? CustomAlias : DeviceName;

    public string ModeLabel => Mode switch
    {
        ConnectionMode.Wireless24G => "Wireless 2.4 GHz",
        ConnectionMode.Wired => "Cabo USB-C",
        ConnectionMode.Bluetooth => "Bluetooth",
        ConnectionMode.Sleep24G => "Em repouso (2.4 GHz)",
        _ => "Desconectado"
    };

    public string ShortModeLabel => Mode switch
    {
        ConnectionMode.Wireless24G => "2.4G",
        ConnectionMode.Wired => "USB",
        ConnectionMode.Bluetooth => "BT",
        ConnectionMode.Sleep24G => "Repouso",
        _ => "Off"
    };

    public static BatteryReading Disconnected(string? detail = null) =>
        new("none", ConnectionMode.Disconnected, DeviceKind.Mouse, "Nenhum dispositivo", null, false, null, DateTime.Now, detail);
}
