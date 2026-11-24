using System.Runtime.InteropServices;
using System.Text;
using KSnakeBatteryTray.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace KSnakeBatteryTray.Providers;

/// <summary>
/// Discovers all connected Bluetooth LE and Classic Bluetooth peripherals (mice, headsets, keyboards, etc.),
/// reads battery percentages via AEP/PnP DEVPKEY_Bluetooth_Battery or GATT Battery Service (0x180F/0x2A19),
/// and reads firmware version universally from GATT Device Information Service (0x180A -> 0x2A26/0x2A28) for BLE devices.
/// </summary>
public sealed class BluetoothBatteryProvider
{
    private const string PropIsConnected = "System.Devices.Aep.IsConnected";
    private const string PropContainerId = "System.Devices.Aep.ContainerId";
    private const string PropCategory = "System.Devices.Aep.Category";
    private const string PropBatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private static readonly DEVPROPKEY DevPkeyBluetoothBattery = new()
    {
        fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
        pid = 2
    };

    private static readonly DEVPROPKEY DevPkeyContainerId = new()
    {
        fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
        pid = 2
    };

    // Per-device firmware cache (cleared automatically when a device disconnects)
    private readonly Dictionary<string, string?> _firmwareCache = new(StringComparer.OrdinalIgnoreCase);

    public sealed record BluetoothDeviceReading(
        string DeviceId,
        string DeviceName,
        DeviceKind Kind,
        int? BatteryPercent,
        string? FirmwareVersion,
        string Detail);

    public sealed record BluetoothScanResult(
        IReadOnlyList<BluetoothDeviceReading> Devices,
        IReadOnlyList<string> DiagnosticLines);

    public async Task<BluetoothScanResult> ProbeAllAsync(CancellationToken ct = default)
    {
        var diagnostics = new List<string>();
        var byKey = new Dictionary<string, BluetoothDeviceReading>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var containerBatteryMap = BuildContainerBatteryMap();

            // 1) Scan Bluetooth LE devices
            await ScanBleAsync(containerBatteryMap, byKey, diagnostics, ct);

            // 2) Scan Classic Bluetooth devices (e.g. headphones/earbuds via HFP battery)
            await ScanClassicAsync(containerBatteryMap, byKey, diagnostics, ct);

            // Evict cached firmware for disconnected devices so swapped/updated hardware reads fresh data
            foreach (var cachedId in _firmwareCache.Keys.Where(k => !byKey.ContainsKey(k)).ToList())
                _firmwareCache.Remove(cachedId);

            if (byKey.Count == 0 && diagnostics.Count == 0)
                diagnostics.Add("BT: Nenhum dispositivo Bluetooth conectado com suporte a bateria.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"BT: Erro geral ao consultar: {ex.Message}");
        }

        var ordered = byKey.Values
            .OrderBy(d => d.Kind == DeviceKind.Mouse ? 0 : d.Kind == DeviceKind.Keyboard ? 1 : d.Kind == DeviceKind.Audio ? 2 : 3)
            .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BluetoothScanResult(ordered, diagnostics);
    }

    private async Task ScanBleAsync(
        Dictionary<Guid, int> containerBatteryMap,
        Dictionary<string, BluetoothDeviceReading> results,
        List<string> diagnostics,
        CancellationToken ct)
    {
        var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
        var additionalProps = new[] { PropIsConnected, PropContainerId, PropCategory, PropBatteryKey };
        var devices = await DeviceInformation.FindAllAsync(selector, additionalProps).AsTask(ct);

        int connectedCount = 0;
        foreach (var dev in devices)
        {
            bool isConnected = dev.Properties.TryGetValue(PropIsConnected, out var c) && c is true;
            if (!isConnected) continue;
            connectedCount++;

            string name = string.IsNullOrWhiteSpace(dev.Name) ? "Dispositivo BLE" : dev.Name.Trim();
            var kind = DetectDeviceKind(dev, name);
            bool isX11 = IsX11Name(name);
            Guid? containerId = dev.Properties.TryGetValue(PropContainerId, out var cidObj) && cidObj is Guid g ? g : null;
            string dedupeKey = containerId.HasValue ? $"bt:{containerId.Value:N}" : $"ble:{dev.Id}";

            int? battery = null;
            var detail = new StringBuilder($"BLE '{name}'");

            // Strategy A: AEP DEVPKEY_Bluetooth_Battery
            if (dev.Properties.TryGetValue(PropBatteryKey, out var propBat) && propBat is byte bVal && bVal <= 100)
            {
                battery = bVal;
                detail.Append($" | AEP={bVal}%");
            }

            // Strategy B: PnP ContainerId map
            if (battery is null && containerId.HasValue && containerBatteryMap.TryGetValue(containerId.Value, out int pnpBat))
            {
                battery = pnpBat;
                detail.Append($" | PnP={pnpBat}%");
            }

            bool needBatteryGatt = battery is null;
            bool needFirmwareGatt = !_firmwareCache.ContainsKey(dedupeKey);
            string? firmware = _firmwareCache.TryGetValue(dedupeKey, out var cachedFw) ? cachedFw : null;

            // Strategy C: Open BluetoothLEDevice when we need GATT Battery (0x180F) or one-time Firmware (0x180A)
            if (needBatteryGatt || needFirmwareGatt)
            {
                try
                {
                    using var bleDevice = await BluetoothLEDevice.FromIdAsync(dev.Id).AsTask(ct);
                    if (bleDevice is not null && bleDevice.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    {
                        if (needBatteryGatt)
                        {
                            var batSvc = await bleDevice.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached).AsTask(ct);
                            if (batSvc.Status == GattCommunicationStatus.Success && batSvc.Services.Count > 0)
                            {
                                foreach (var svc in batSvc.Services)
                                {
                                    using (svc)
                                    {
                                        var chRes = await svc.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached).AsTask(ct);
                                        if (chRes.Status == GattCommunicationStatus.Success && chRes.Characteristics.Count > 0)
                                        {
                                            var rd = await chRes.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct);
                                            if (rd.Status == GattCommunicationStatus.Success && rd.Value is not null && rd.Value.Length >= 1)
                                            {
                                                using var reader = DataReader.FromBuffer(rd.Value);
                                                byte pct = reader.ReadByte();
                                                if (pct <= 100)
                                                {
                                                    battery = pct;
                                                    detail.Append($" | GATT 0x180F={pct}%");
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            else if (isX11 || kind == DeviceKind.Mouse)
                            {
                                detail.Append(" | Sem serviço GATT 0x180F (firmware não informa bateria via BT)");
                            }
                        }

                        if (needFirmwareGatt && (battery is not null || isX11 || kind == DeviceKind.Mouse))
                        {
                            firmware = await TryReadBleFirmwareAsync(bleDevice, isX11, ct);
                            _firmwareCache[dedupeKey] = firmware;
                        }
                    }
                }
                catch (Exception ex)
                {
                    detail.Append($" | GATT err: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty(firmware))
                detail.Append($" | FW={firmware}");

            if (battery is not null || isX11 || kind == DeviceKind.Mouse)
            {
                var line = detail.ToString();
                diagnostics.Add(line);
                UpsertReading(results, dedupeKey, new BluetoothDeviceReading(dedupeKey, name, kind, battery, firmware, line));
            }
        }

        if (connectedCount == 0)
            diagnostics.Add("BLE: Nenhum dispositivo Bluetooth LE conectado.");
    }

    private static async Task<string?> TryReadBleFirmwareAsync(BluetoothLEDevice bleDevice, bool preferSoftwareRevision, CancellationToken ct)
    {
        try
        {
            var devInfoSvc = await bleDevice.GetGattServicesForUuidAsync(GattServiceUuids.DeviceInformation, BluetoothCacheMode.Cached).AsTask(ct);
            if (devInfoSvc.Status != GattCommunicationStatus.Success || devInfoSvc.Services.Count == 0)
                return null;

            Guid[] preferredOrder = preferSoftwareRevision
                ? [GattCharacteristicUuids.SoftwareRevisionString, GattCharacteristicUuids.FirmwareRevisionString]
                : [GattCharacteristicUuids.FirmwareRevisionString, GattCharacteristicUuids.SoftwareRevisionString];

            foreach (var svc in devInfoSvc.Services)
            {
                using (svc)
                {
                    foreach (var charUuid in preferredOrder)
                    {
                        var chRes = await svc.GetCharacteristicsForUuidAsync(charUuid, BluetoothCacheMode.Cached).AsTask(ct);
                        if (chRes.Status != GattCommunicationStatus.Success || chRes.Characteristics.Count == 0)
                            continue;

                        var rd = await chRes.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Cached).AsTask(ct);
                        if (rd.Status != GattCommunicationStatus.Success || rd.Value is null || rd.Value.Length == 0)
                            continue;

                        var bytes = new byte[rd.Value.Length];
                        using (var reader = DataReader.FromBuffer(rd.Value))
                            reader.ReadBytes(bytes);

                        string cleaned = Encoding.UTF8.GetString(bytes).Trim('\0', ' ', '\r', '\n', '\t');
                        if (!string.IsNullOrWhiteSpace(cleaned) && cleaned != "0.0.0" && cleaned != "0")
                            return cleaned;
                    }
                }
            }
        }
        catch
        {
            // Non-fatal: device simply doesn't expose Device Information Service
        }

        return null;
    }

    private static async Task ScanClassicAsync(
        Dictionary<Guid, int> containerBatteryMap,
        Dictionary<string, BluetoothDeviceReading> results,
        List<string> diagnostics,
        CancellationToken ct)
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var additionalProps = new[] { PropIsConnected, PropContainerId, PropCategory, PropBatteryKey };
        var devices = await DeviceInformation.FindAllAsync(selector, additionalProps).AsTask(ct);

        foreach (var dev in devices)
        {
            bool isConnected = dev.Properties.TryGetValue(PropIsConnected, out var c) && c is true;
            if (!isConnected) continue;

            string name = string.IsNullOrWhiteSpace(dev.Name) ? "Dispositivo Bluetooth" : dev.Name.Trim();
            var kind = DetectDeviceKind(dev, name);
            Guid? containerId = dev.Properties.TryGetValue(PropContainerId, out var cidObj) && cidObj is Guid g ? g : null;
            string dedupeKey = containerId.HasValue ? $"bt:{containerId.Value:N}" : $"btclassic:{dev.Id}";

            int? battery = null;
            string source = "";

            if (dev.Properties.TryGetValue(PropBatteryKey, out var propBat) && propBat is byte bVal && bVal <= 100)
            {
                battery = bVal;
                source = $"AEP={bVal}%";
            }
            else if (containerId.HasValue && containerBatteryMap.TryGetValue(containerId.Value, out int pnpBat))
            {
                battery = pnpBat;
                source = $"PnP HFP={pnpBat}%";
            }

            if (battery is not null)
            {
                string line = $"BT Clássico '{name}' | {source}";
                diagnostics.Add(line);
                UpsertReading(results, dedupeKey, new BluetoothDeviceReading(dedupeKey, name, kind, battery, null, line));
            }
        }
    }

    private static void UpsertReading(
        Dictionary<string, BluetoothDeviceReading> map, string key, BluetoothDeviceReading incoming)
    {
        if (!map.TryGetValue(key, out var existing))
        {
            map[key] = incoming;
            return;
        }

        int? bestBattery = existing.BatteryPercent ?? incoming.BatteryPercent;
        string? bestFirmware = !string.IsNullOrEmpty(existing.FirmwareVersion) ? existing.FirmwareVersion : incoming.FirmwareVersion;
        map[key] = existing with { BatteryPercent = bestBattery, FirmwareVersion = bestFirmware };
    }

    private static bool IsX11Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("X11", StringComparison.OrdinalIgnoreCase)
            || name.Contains("K-Snake", StringComparison.OrdinalIgnoreCase)
            || name.Contains("KSnake", StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceKind DetectDeviceKind(DeviceInformation dev, string name)
    {
        string cats = string.Empty;
        if (dev.Properties.TryGetValue(PropCategory, out var catObj))
        {
            if (catObj is string s) cats = s;
            else if (catObj is IEnumerable<string> list) cats = string.Join(" ", list);
        }

        string combined = $"{cats} {name}";
        if (combined.Contains("Mouse", StringComparison.OrdinalIgnoreCase) || IsX11Name(name))
            return DeviceKind.Mouse;
        if (combined.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) || combined.Contains("Teclado", StringComparison.OrdinalIgnoreCase))
            return DeviceKind.Keyboard;
        if (combined.Contains("Audio", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Headphone", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Headset", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Earbud", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Speaker", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Bowie", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Pods", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("Buds", StringComparison.OrdinalIgnoreCase))
            return DeviceKind.Audio;

        return DeviceKind.Other;
    }

    private static Dictionary<Guid, int> BuildContainerBatteryMap()
    {
        var map = new Dictionary<Guid, int>();
        try
        {
            if (CM_Get_Device_ID_List_SizeW(out uint charLen, null, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS || charLen == 0)
                return map;

            var buf = new char[charLen];
            if (CM_Get_Device_ID_ListW(null, buf, charLen, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS)
                return map;

            var raw = new string(buf);
            foreach (var instanceId in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!instanceId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) &&
                    !instanceId.StartsWith("HID\\{00001812", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                    continue;

                if (TryGetByteProperty(devInst, in DevPkeyBluetoothBattery, out byte bat) && bat <= 100)
                {
                    if (TryGetGuidProperty(devInst, in DevPkeyContainerId, out Guid cid) && cid != Guid.Empty)
                        map[cid] = bat;
                }
            }
        }
        catch { }

        return map;
    }

    private static bool TryGetGuidProperty(uint devInst, in DEVPROPKEY key, out Guid value)
    {
        value = Guid.Empty;
        byte[] buffer = new byte[16];
        uint size = (uint)buffer.Length;
        if (CM_Get_DevNode_PropertyW(devInst, in key, out uint propType, buffer, ref size, 0) == CR_SUCCESS &&
            propType == DEVPROP_TYPE_GUID && size == 16)
        {
            value = new Guid(buffer);
            return true;
        }
        return false;
    }

    private static bool TryGetByteProperty(uint devInst, in DEVPROPKEY key, out byte value)
    {
        value = 0;
        byte[] buffer = new byte[4];
        uint size = (uint)buffer.Length;
        if (CM_Get_DevNode_PropertyW(devInst, in key, out uint propType, buffer, ref size, 0) == CR_SUCCESS &&
            (propType == DEVPROP_TYPE_BYTE || propType == DEVPROP_TYPE_UINT32) && size >= 1)
        {
            value = buffer[0];
            return true;
        }
        return false;
    }

    #region CfgMgr32 P/Invoke

    private const uint CR_SUCCESS = 0;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x00000100;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    private const uint DEVPROP_TYPE_BYTE = 0x00000003;
    private const uint DEVPROP_TYPE_UINT32 = 0x00000007;
    private const uint DEVPROP_TYPE_GUID = 0x0000000D;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_ID_ListW(string? pszFilter, [Out] char[] buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        in DEVPROPKEY propertyKey,
        out uint propertyType,
        [Out] byte[] propertyBuffer,
        ref uint propertyBufferSize,
        uint ulFlags);

    #endregion
}
