using System.Runtime.InteropServices;
using KSnakeBatteryTray.Models;
using Microsoft.Win32.SafeHandles;

namespace KSnakeBatteryTray.Providers;

/// <summary>
/// Communicates with K-Snake X11 Pro (YJX-CHIP) over USB HID (2.4 GHz dongle and USB-C wired).
/// Supports BOTH synchronous queries and real-time asynchronous push events (instant charging, battery,
/// DPI, polling rate, and sleep/wake updates via a 0%-CPU overlapped background reader):
///   - Wired USB-C : VID 0xA8A4, PID 0x2255, UsagePage 0xFF01, Usage 0x0010 (65-byte reports)
///   - 2.4 GHz     : VID 0xA8A5, PID 0x2255, UsagePage 0xFF01, Usage 0x0010 (65-byte reports)
///   - Online cmd  : [0x00, 0x55, 0xED, 0x00, 0x01, 0x2E, 0x00, 0x00] -> resp[9]==0x02
///   - Battery cmd : [0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01] -> pct=resp[9], chg=resp[10]
///   - Config cmd  : [0x00, 0x55, 0x0E, 0xA5, 0x0B, 0x2F, 0x01, 0x01] -> rate=resp[11]-1, dpiIdx=resp[13]-1, slots=resp[14..25]
///   - Version cmd : [0x00, 0x55, 0x03]                               -> ASCII at resp[24..26]
///   - Push frames : [0x00, 0xAA, 0xFA, ..., resp[9]==0xD0 ? (bat=resp[10], chg=resp[11]) : (dpiIdx=resp[10]-1, rateIdx=resp[11]-1)]
///                   [0x00, 0xAA, 0xED, ..., online=(resp[9]==0x02)]
/// </summary>
public sealed class YjxHidProvider : IDisposable
{
    public const ushort VendorIdWired = 0xA8A4;
    public const ushort VendorIdWireless = 0xA8A5;
    public const ushort ProductIdX11 = 0x2255;
    public const ushort TargetUsagePage = 0xFF01;
    public const ushort TargetUsage = 0x0010;
    private const int ReportLength = 65;

    private static readonly int[] PollingRatesHz = [125, 250, 500, 1000];
    private static readonly int[] DefaultDpiSlots = [800, 1200, 1600, 3200, 5000, 12000];

    private readonly SemaphoreSlim _cmdLock = new(1, 1);
    private readonly object _connLock = new();
    private readonly object _pendingLock = new();

    private SafeFileHandle? _activeHandle;
    private string? _activePath;
    private ConnectionMode _activeMode = ConnectionMode.Disconnected;
    private CancellationTokenSource? _readerCts;
    private Thread? _readerThread;

    private byte? _pendingCmdByte;
    private TaskCompletionSource<byte[]>? _pendingTcs;

    private string? _cachedFirmware;
    private int[] _dpiSlots = (int[])DefaultDpiSlots.Clone();
    private int? _lastDpi;
    private int? _lastPollingRateHz;
    private bool _disposed;

    public sealed record HidProbeResult(
        bool DeviceFound,
        ConnectionMode Mode,
        bool IsOnline,
        int? BatteryPercent,
        bool IsCharging,
        string? FirmwareVersion,
        int? Dpi,
        int? PollingRateHz,
        string DiagnosticLine);

    public sealed record HidPushEvent(
        ConnectionMode Mode,
        bool? IsOnline = null,
        int? BatteryPercent = null,
        bool? IsCharging = null,
        int? Dpi = null,
        int? PollingRateHz = null,
        string? LogLine = null);

    public event Action<HidPushEvent>? PushEventReceived;

    public async Task<HidProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        var candidates = EnumerateTargetDevices();
        if (candidates.Count == 0)
        {
            CloseConnection();
            return new HidProbeResult(
                false, ConnectionMode.Disconnected, false, null, false,
                _cachedFirmware, _lastDpi, _lastPollingRateHz,
                "HID: Nenhum receptor 2.4G (A8A5:2255) ou cabo USB-C (A8A4:2255) detectado.");
        }

        // Prefer USB-C Wired over 2.4G Dongle if both are plugged in simultaneously.
        var target = candidates.OrderBy(c => c.Mode == ConnectionMode.Wired ? 0 : 1).First();

        if (!EnsureConnection(target, out int openErr))
        {
            return new HidProbeResult(
                true, target.Mode, false, null, false,
                _cachedFirmware, _lastDpi, _lastPollingRateHz,
                $"HID: Falha ao abrir interface {target.Mode} (Win32 error {openErr}).");
        }

        await _cmdLock.WaitAsync(ct);
        try
        {
            bool isOnline = true;
            if (target.Mode == ConnectionMode.Wireless24G)
            {
                var onlineResp = await SendCommandAsync([0x00, 0x55, 0xED, 0x00, 0x01, 0x2E, 0x00, 0x00], 0xED, 700, ct);
                if (onlineResp is null)
                {
                    return new HidProbeResult(
                        true, ConnectionMode.Sleep24G, false, null, false,
                        _cachedFirmware, _lastDpi, _lastPollingRateHz,
                        "HID 2.4G: Sem resposta ao comando de status (0xED).");
                }

                isOnline = onlineResp.Length > 9 && onlineResp[9] == 0x02;
                if (!isOnline)
                {
                    return new HidProbeResult(
                        true, ConnectionMode.Sleep24G, false, null, false,
                        _cachedFirmware, _lastDpi, _lastPollingRateHz,
                        $"HID 2.4G: Dongle conectado, mouse offline/repouso (status=0x{(onlineResp.Length > 9 ? onlineResp[9] : 0):X2}).");
                }
            }

            if (string.IsNullOrEmpty(_cachedFirmware))
            {
                var verResp = await SendCommandAsync([0x00, 0x55, 0x03], 0x03, 500, ct);
                if (verResp is not null && verResp.Length >= 27)
                {
                    static char DigitOrZero(byte b) => (b >= '0' && b <= '9') ? (char)b : '0';
                    _cachedFirmware = $"{DigitOrZero(verResp[24])}.{DigitOrZero(verResp[25])}.{DigitOrZero(verResp[26])}";
                }
            }

            // Query DPI & Polling Rate config (fast, ~15ms)
            var cfgResp = await SendCommandAsync([0x00, 0x55, 0x0E, 0xA5, 0x0B, 0x2F, 0x01, 0x01, 0x00, 0x00, 0x00], 0x0E, 500, ct);
            if (cfgResp is not null && cfgResp.Length >= 26)
            {
                ParseMouseConfig(cfgResp);
            }

            var batResp = await SendCommandAsync([0x00, 0x55, 0x30, 0xA5, 0x0B, 0x2E, 0x01, 0x01, 0x00, 0x00, 0x00], 0x30, 800, ct);
            if (batResp is null || batResp.Length < 11)
            {
                return new HidProbeResult(
                    true, target.Mode, isOnline, null, false,
                    _cachedFirmware, _lastDpi, _lastPollingRateHz,
                    $"HID ({target.Mode}): Mouse online, mas sem resposta válida de bateria (0x30).");
            }

            int rawBattery = batResp[9];
            bool isCharging = batResp[10] != 0;
            int? pct = rawBattery is >= 0 and <= 100 ? rawBattery : null;

            string dpiPart = _lastDpi is int d ? $" | DPI={d}" : "";
            string hzPart = _lastPollingRateHz is int hz ? $" | {hz}Hz" : "";

            return new HidProbeResult(
                true, target.Mode, true, pct, isCharging,
                _cachedFirmware, _lastDpi, _lastPollingRateHz,
                $"HID ({target.Mode}): OK | Bat={pct?.ToString() ?? "?"}% | Chg={isCharging}{dpiPart}{hzPart} | FW={_cachedFirmware ?? "?"}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CloseConnection();
            return new HidProbeResult(
                true, target.Mode, false, null, false,
                _cachedFirmware, _lastDpi, _lastPollingRateHz,
                $"HID: Erro de comunicação: {ex.Message}");
        }
        finally
        {
            _cmdLock.Release();
        }
    }

    private void ParseMouseConfig(byte[] resp)
    {
        // resp[0] is Report ID 0x00; official driver payload t[i] corresponds to resp[i + 1].
        bool isEmpty = (resp[14] == 0 && resp[15] == 0 && resp[16] == 0) ||
                       (resp[14] == 0xFF && resp[15] == 0xFF && resp[16] == 0xFF);

        if (isEmpty)
        {
            _dpiSlots = (int[])DefaultDpiSlots.Clone();
            _lastPollingRateHz = 1000;
            _lastDpi = _dpiSlots[2];
            return;
        }

        int rateIdx = resp[11] - 1;
        if (rateIdx >= 0 && rateIdx < PollingRatesHz.Length)
            _lastPollingRateHz = PollingRatesHz[rateIdx];

        var slots = new int[6];
        for (int k = 0; k < 6; k++)
        {
            int val = resp[14 + 2 * k] | (resp[15 + 2 * k] << 8);
            slots[k] = val is >= 100 and <= 30000 ? val : DefaultDpiSlots[k];
        }
        _dpiSlots = slots;

        int dpiIdx = resp[13] - 1;
        if (dpiIdx >= 0 && dpiIdx < _dpiSlots.Length)
            _lastDpi = _dpiSlots[dpiIdx];
    }

    private bool EnsureConnection(TargetHidDevice target, out int errorCode)
    {
        errorCode = 0;
        lock (_connLock)
        {
            if (_disposed) return false;

            if (_activeHandle is { IsInvalid: false, IsClosed: false } &&
                string.Equals(_activePath, target.Path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            CloseConnectionUnsafe();

            var handle = CreateFileW(
                target.Path,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                errorCode = Marshal.GetLastWin32Error();
                handle.Dispose();
                return false;
            }

            _activeHandle = handle;
            _activePath = target.Path;
            _activeMode = target.Mode;
            _readerCts = new CancellationTokenSource();

            var token = _readerCts.Token;
            var modeSnapshot = target.Mode;
            _readerThread = new Thread(() => ReaderLoop(handle, modeSnapshot, token))
            {
                IsBackground = true,
                Name = "YjxHidPushReader"
            };
            _readerThread.Start();
            return true;
        }
    }

    private void CloseConnection()
    {
        lock (_connLock)
        {
            CloseConnectionUnsafe();
        }
    }

    private void CloseConnectionUnsafe()
    {
        var cts = _readerCts;
        _readerCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        var handle = _activeHandle;
        _activeHandle = null;
        _activePath = null;
        _activeMode = ConnectionMode.Disconnected;

        if (handle is not null)
        {
            try { CancelIoEx(handle, IntPtr.Zero); } catch { }
            try { handle.Dispose(); } catch { }
        }

        lock (_pendingLock)
        {
            _pendingTcs?.TrySetCanceled();
            _pendingTcs = null;
            _pendingCmdByte = null;
        }
    }

    private void ReaderLoop(SafeFileHandle handle, ConnectionMode mode, CancellationToken ct)
    {
        var buffer = new byte[ReportLength];
        using var readEvt = new ManualResetEvent(false);
        WaitHandle[] waitHandles = [readEvt, ct.WaitHandle];

        while (!ct.IsCancellationRequested && !handle.IsInvalid && !handle.IsClosed)
        {
            readEvt.Reset();
            var overlapped = new NativeOverlapped { EventHandle = readEvt.SafeWaitHandle.DangerousGetHandle() };

            bool ok = ReadFile(handle, buffer, ReportLength, out uint bytesRead, ref overlapped);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_IO_PENDING)
                {
                    int signaled = WaitHandle.WaitAny(waitHandles);
                    if (signaled != 0)
                    {
                        try { CancelIoEx(handle, ref overlapped); } catch { }
                        break;
                    }

                    if (!GetOverlappedResult(handle, ref overlapped, out bytesRead, false))
                    {
                        int ovErr = Marshal.GetLastWin32Error();
                        if (ovErr == ERROR_DEVICE_NOT_CONNECTED || ovErr == ERROR_OPERATION_ABORTED || ovErr == ERROR_INVALID_HANDLE)
                            break;
                        continue;
                    }
                }
                else
                {
                    // Device disconnected or handle closed
                    break;
                }
            }

            if (bytesRead < 3)
                continue;

            var packet = new byte[bytesRead];
            Array.Copy(buffer, packet, bytesRead);
            HandleIncomingPacket(packet, mode);
        }

        // Clean up stale handle reference if loop exited due to unplug
        lock (_connLock)
        {
            if (ReferenceEquals(_activeHandle, handle))
            {
                CloseConnectionUnsafe();
            }
        }
    }

    private void HandleIncomingPacket(byte[] packet, ConnectionMode mode)
    {
        if (packet.Length < 3 || packet[1] != 0xAA)
            return;

        byte cmd = packet[2];

        // 1) Resolve any synchronous command waiting for this reply
        lock (_pendingLock)
        {
            if (_pendingTcs is not null && _pendingCmdByte == cmd)
            {
                var tcs = _pendingTcs;
                _pendingTcs = null;
                _pendingCmdByte = null;
                tcs.TrySetResult(packet);
                return;
            }
        }

        // 2) Handle unsolicited real-time push events from K-Snake X11 Pro
        if (cmd == 0xFA && packet.Length >= 12)
        {
            if (packet[9] == 0xD0)
            {
                int pct = packet[10];
                bool chg = packet[11] != 0;
                if (pct is >= 0 and <= 100)
                {
                    PushEventReceived?.Invoke(new HidPushEvent(
                        mode,
                        IsOnline: true,
                        BatteryPercent: pct,
                        IsCharging: chg,
                        LogLine: $"HID Push ({mode}): Bateria={pct}% | Carregando={chg}"));
                }
            }
            else
            {
                int dpiIdx = packet[10] - 1;
                int rateIdx = packet[11] - 1;
                int? dpi = (dpiIdx >= 0 && dpiIdx < _dpiSlots.Length) ? _dpiSlots[dpiIdx] : null;
                int? hz = (rateIdx >= 0 && rateIdx < PollingRatesHz.Length) ? PollingRatesHz[rateIdx] : null;

                if (dpi.HasValue) _lastDpi = dpi;
                if (hz.HasValue) _lastPollingRateHz = hz;

                if (dpi.HasValue || hz.HasValue)
                {
                    PushEventReceived?.Invoke(new HidPushEvent(
                        mode,
                        IsOnline: true,
                        Dpi: _lastDpi,
                        PollingRateHz: _lastPollingRateHz,
                        LogLine: $"HID Push ({mode}): DPI={_lastDpi?.ToString() ?? "?"} | {_lastPollingRateHz?.ToString() ?? "?"}Hz"));
                }
            }
        }
        else if (cmd == 0xED && packet.Length >= 10)
        {
            bool online = packet[9] == 0x02;
            PushEventReceived?.Invoke(new HidPushEvent(
                mode,
                IsOnline: online,
                LogLine: $"HID Push ({mode}): Mouse {(online ? "acordou/online" : "entrou em repouso/offline")}"));
        }
    }

    private async Task<byte[]?> SendCommandAsync(byte[] commandPrefix, byte expectedCmdByte, int timeoutMs, CancellationToken ct)
    {
        SafeFileHandle? handle;
        lock (_connLock)
        {
            handle = _activeHandle;
        }
        if (handle is null || handle.IsInvalid || handle.IsClosed)
            return null;

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pendingCmdByte = expectedCmdByte;
            _pendingTcs = tcs;
        }

        var outBuf = new byte[ReportLength];
        Array.Copy(commandPrefix, outBuf, Math.Min(commandPrefix.Length, outBuf.Length));

        if (!WriteOverlapped(handle, outBuf, 500))
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pendingTcs, tcs))
                {
                    _pendingTcs = null;
                    _pendingCmdByte = null;
                }
            }
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);

        try
        {
            return await tcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pendingTcs, tcs))
                {
                    _pendingTcs = null;
                    _pendingCmdByte = null;
                }
            }
            return null;
        }
    }

    private static bool WriteOverlapped(SafeFileHandle handle, byte[] buffer, int timeoutMs)
    {
        using var evt = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = evt.SafeWaitHandle.DangerousGetHandle() };

        bool ok = WriteFile(handle, buffer, (uint)buffer.Length, out uint written, ref overlapped);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_IO_PENDING)
                return false;

            if (!evt.WaitOne(timeoutMs))
            {
                CancelIoEx(handle, ref overlapped);
                return false;
            }

            if (!GetOverlappedResult(handle, ref overlapped, out written, false))
                return false;
        }

        return written == buffer.Length;
    }

    public void Dispose()
    {
        lock (_connLock)
        {
            if (_disposed) return;
            _disposed = true;
            CloseConnectionUnsafe();
        }
        _cmdLock.Dispose();
    }

    private sealed record TargetHidDevice(string Path, ConnectionMode Mode);

    private static List<TargetHidDevice> EnumerateTargetDevices()
    {
        var results = new List<TargetHidDevice>();
        HidD_GetHidGuid(out Guid hidGuid);

        IntPtr devInfo = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == INVALID_HANDLE_VALUE)
            return results;

        try
        {
            var ifaceData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };

            for (uint index = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, index, ref ifaceData); index++)
            {
                SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifaceData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
                if (requiredSize == 0)
                    continue;

                IntPtr detailBuf = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    int cbSize = IntPtr.Size == 8 ? 8 : 6;
                    Marshal.WriteInt32(detailBuf, cbSize);

                    if (!SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifaceData, detailBuf, requiredSize, out _, IntPtr.Zero))
                        continue;

                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4));
                    if (string.IsNullOrEmpty(path))
                        continue;

                    string lower = path.ToLowerInvariant();
                    if (!lower.Contains("pid_2255") || (!lower.Contains("vid_a8a5") && !lower.Contains("vid_a8a4")))
                        continue;

                    using var queryHandle = CreateFileW(
                        path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                    if (queryHandle.IsInvalid)
                        continue;

                    var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(queryHandle, ref attrs))
                        continue;

                    if (attrs.ProductID != ProductIdX11 || (attrs.VendorID != VendorIdWireless && attrs.VendorID != VendorIdWired))
                        continue;

                    if (!HidD_GetPreparsedData(queryHandle, out IntPtr preparsed))
                        continue;

                    try
                    {
                        if (HidP_GetCaps(preparsed, out HIDP_CAPS caps) == HIDP_STATUS_SUCCESS &&
                            caps.UsagePage == TargetUsagePage &&
                            caps.Usage == TargetUsage &&
                            caps.InputReportByteLength == ReportLength &&
                            caps.OutputReportByteLength == ReportLength)
                        {
                            var mode = attrs.VendorID == VendorIdWired
                                ? ConnectionMode.Wired
                                : ConnectionMode.Wireless24G;
                            results.Add(new TargetHidDevice(path, mode));
                        }
                    }
                    finally
                    {
                        HidD_FreePreparsedData(preparsed);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }

        return results;
    }

    #region Win32 P/Invoke

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997;
    private const int ERROR_OPERATION_ABORTED = 995;
    private const int ERROR_INVALID_HANDLE = 6;
    private const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle hFile,
        ref NativeOverlapped lpOverlapped,
        out uint lpNumberOfBytesTransferred,
        bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    #endregion
}
