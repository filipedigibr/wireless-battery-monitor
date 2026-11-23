using System.Runtime.InteropServices;
using KSnakeBatteryTray.Services;
using KSnakeBatteryTray.Settings;
using KSnakeBatteryTray.UI;

namespace KSnakeBatteryTray;

internal static class Program
{
    private const string MutexName = @"Local\WirelessBatteryMonitor_SingleInstance";

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--probe-gatt", StringComparison.OrdinalIgnoreCase)))
        {
            if (!Console.IsOutputRedirected)
                AttachConsole(-1);
            DumpBluetoothGattAsync().GetAwaiter().GetResult();
            Console.Out.Flush();
            return;
        }

        if (args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            if (!Console.IsOutputRedirected)
                AttachConsole(-1);
            using var svc = new MouseBatteryService(AppSettings.Load());
            var readings = svc.RefreshAsync().GetAwaiter().GetResult();
            Console.WriteLine($"Devices found: {readings.Count}");
            foreach (var r in readings)
            {
                string dpi = r.Dpi is int d ? $" | DPI={d}" : "";
                string hz = r.PollingRateHz is int p ? $" | {p}Hz" : "";
                string est = !string.IsNullOrEmpty(r.EstimatedTimeLabel) ? $" | Est={r.EstimatedTimeLabel}" : "";
                Console.WriteLine($"- [{r.Kind}] {r.DisplayName} ({r.DeviceId}) | Mode={r.ModeLabel} | Battery={r.BatteryPercent?.ToString() ?? "N/D"}% | Charging={r.IsCharging}{dpi}{hz} | FW={r.FirmwareVersion ?? "N/D"}{est}");
            }
            Console.WriteLine("--- Log ---");
            Console.WriteLine(svc.GetDiagnosticSnapshot());
            Console.Out.Flush();
            return;
        }

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
            return;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogFatalError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                LogFatalError(ex);
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }

    private static async Task DumpBluetoothGattAsync()
    {
        var selector = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var devs = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector, new[] { "System.Devices.Aep.IsConnected" });
        foreach (var d in devs)
        {
            bool conn = d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is true;
            Console.WriteLine($"DEV: {d.Name} | Connected={conn} | Id={d.Id}");
            if (!conn) continue;
            using var ble = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(d.Id);
            if (ble is null) continue;
            var svcs = await ble.GetGattServicesAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
            Console.WriteLine($"  Services ({svcs.Status}): {svcs.Services.Count}");
            foreach (var s in svcs.Services)
            {
                using (s)
                {
                    Console.WriteLine($"  SVC {s.Uuid}");
                    var chars = await s.GetCharacteristicsAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
                    if (chars.Status == Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
                    {
                        foreach (var ch in chars.Characteristics)
                        {
                            string valStr = "";
                            if (ch.CharacteristicProperties.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Read))
                            {
                                var r = await ch.ReadValueAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
                                if (r.Status == Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success && r.Value is not null)
                                {
                                    var bytes = new byte[r.Value.Length];
                                    Windows.Storage.Streams.DataReader.FromBuffer(r.Value).ReadBytes(bytes);
                                    valStr = " Val=" + BitConverter.ToString(bytes);
                                }
                            }
                            Console.WriteLine($"    CHAR {ch.Uuid} Props={ch.CharacteristicProperties}{valStr}");
                        }
                    }
                }
            }
        }
    }

    private static void LogFatalError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDir);
            File.AppendAllText(Path.Combine(AppSettings.SettingsDir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
