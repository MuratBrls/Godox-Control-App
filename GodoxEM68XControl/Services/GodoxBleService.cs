using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GodoxEM68XControl.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace GodoxEM68XControl.Services;

public sealed record DiscoveredBleDevice(
    ulong BluetoothAddress,
    string AddressHex,
    string Name,
    short Rssi,
    DateTime LastSeen);

public sealed record ProtocolLogEntry(
    DateTime Timestamp,
    string Direction,
    string HexPayload,
    string Description,
    bool IsValid = true);

/// <summary>
/// Manages BLE discovery, GattSession (MaintainConnection = true), GATT service/characteristic
/// connection (0xFFE0 / 0xFFE1 / 0xFFE2), 100ms command queueing, and 0xA6 status polling
/// for the Godox EM68X microphone.
/// </summary>
public sealed class GodoxBleService : IDisposable
{
    private static readonly string LocalCacheFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GodoxEM68XStudio",
        "last_device.cfg");

    public static (ulong Address, string Name)? LoadCachedDevice()
    {
        try
        {
            if (System.IO.File.Exists(LocalCacheFilePath))
            {
                string content = System.IO.File.ReadAllText(LocalCacheFilePath).Trim();
                string[] parts = content.Split('|');
                if (parts.Length >= 1 &&
                    ulong.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out ulong addr) &&
                    addr != 0)
                {
                    string name = parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1])
                        ? parts[1]
                        : $"LK-{addr & 0xFFFF:X4}";
                    return (addr, name);
                }
            }
        }
        catch { }
        return null;
    }

    public static void SaveCachedDevice(ulong address, string name)
    {
        if (address == 0) return;
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(LocalCacheFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            System.IO.File.WriteAllText(LocalCacheFilePath, $"{address:X12}|{name}");
        }
        catch { }
    }

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattSession? _gattSession;
    private GattDeviceService? _qppService;
    private GattCharacteristic? _notifyChar;
    private GattCharacteristic? _writeChar;

    private readonly ConcurrentDictionary<ulong, DiscoveredBleDevice> _discovered = new();
    private readonly Channel<byte[]> _txChannel = Channel.CreateUnbounded<byte[]>();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private CancellationTokenSource? _connectionCts;
    private long _suppressPollUntilTicks;
    private bool _disposed;

    public event Action<DiscoveredBleDevice>? DeviceDiscovered;
    public event Action<bool, string>? ConnectionStateChanged;
    public event Action<GodoxMicState>? StateReceived;
    public event Action<ProtocolLogEntry>? PacketLogged;

    public bool IsConnected =>
        _device?.ConnectionStatus == BluetoothConnectionStatus.Connected &&
        _writeChar is not null;

    public string? ConnectedDeviceName { get; private set; }
    public string? ConnectedDeviceAddress { get; private set; }

    public IReadOnlyCollection<DiscoveredBleDevice> DiscoveredDevices => _discovered.Values.ToArray();

    public void StartScan()
    {
        if (_disposed) return;

        try
        {
            if (_watcher is null)
            {
                _watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active
                };
                _watcher.Received += OnAdvertisementReceived;
            }

            if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
            {
                _watcher.Start();
                Log("BLE", "--", "BLE taraması aktif ('LK*' mikrofon yayını dinleniyor)");
            }
        }
        catch (Exception ex)
        {
            Log("ERR", "--", $"BLE tarama başlatılamadı: {ex.Message}", false);
        }
    }

    public void StopScan()
    {
        try
        {
            if (_watcher is { Status: BluetoothLEAdvertisementWatcherStatus.Started })
            {
                _watcher.Stop();
            }
        }
        catch { }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        string localName = args.Advertisement.LocalName ?? string.Empty;

        if (string.IsNullOrWhiteSpace(localName))
        {
            foreach (var section in args.Advertisement.DataSections)
            {
                if (section.DataType is 0x08 or 0x09 && section.Data.Length > 0)
                {
                    byte[] bytes = section.Data.ToArray();
                    localName = System.Text.Encoding.UTF8.GetString(bytes).Trim('\0');
                    break;
                }
            }
        }

        var cached = LoadCachedDevice();
        bool hasQppUuid = args.Advertisement.ServiceUuids.Any(u => u == GodoxMicProtocol.QppServiceUuid);
        bool isKnownGodoxAddress = (cached.HasValue && args.BluetoothAddress == cached.Value.Address) ||
                                   _discovered.ContainsKey(args.BluetoothAddress);
        bool hasLkPrefix = localName.StartsWith(GodoxMicProtocol.DeviceNamePrefix, StringComparison.OrdinalIgnoreCase);

        if (!hasLkPrefix && !hasQppUuid && !isKnownGodoxAddress)
        {
            return;
        }

        _discovered.TryGetValue(args.BluetoothAddress, out var existing);
        string effectiveName = !string.IsNullOrWhiteSpace(localName)
            ? localName
            : (existing?.Name ?? (cached.HasValue && args.BluetoothAddress == cached.Value.Address ? cached.Value.Name : $"LK-{args.BluetoothAddress & 0xFFFF:X4}"));

        var dev = new DiscoveredBleDevice(
            args.BluetoothAddress,
            FormatMacAddress(args.BluetoothAddress),
            effectiveName,
            args.RawSignalStrengthInDBm,
            DateTime.Now);

        _discovered[args.BluetoothAddress] = dev;
        SaveCachedDevice(args.BluetoothAddress, effectiveName);
        DeviceDiscovered?.Invoke(dev);
    }

    /// <summary>
    /// Cycles the local Windows Bluetooth radio Off and On to clear any stuck BTHLE GATT handles,
    /// then connects to the Godox EM68X.
    /// </summary>
    public async Task<bool> ResetBluetoothAdapterAndConnectAsync(ulong? targetAddress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            StopScan();
            await DisconnectAsync();
            Log("BLE", "--", "Windows Bluetooth adaptörü sıfırlanıyor...");
            ConnectionStateChanged?.Invoke(false, "Bluetooth adaptörü sıfırlanıyor...");

            var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
            foreach (var radio in radios)
            {
                if (radio.Kind == Windows.Devices.Radios.RadioKind.Bluetooth)
                {
                    await radio.SetStateAsync(Windows.Devices.Radios.RadioState.Off);
                    await Task.Delay(1800, cancellationToken);
                    await radio.SetStateAsync(Windows.Devices.Radios.RadioState.On);
                    await Task.Delay(2000, cancellationToken);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log("ERR", "--", $"Bluetooth sıfırlama uyarısı: {ex.Message}", false);
        }

        return await ConnectAsync(targetAddress, cancellationToken);
    }

    /// <summary>
    /// Connects to the Godox EM68X over BLE using WinRT GattSession (MaintainConnection = true)
    /// and retries GATT service discovery just like bleak's WinRT backend.
    /// </summary>
    public async Task<bool> ConnectAsync(ulong? targetAddress = null, CancellationToken cancellationToken = default)
    {
        if (!await _connectLock.WaitAsync(0, cancellationToken))
            return IsConnected;

        try
        {
            await DisconnectInternalAsync();
            StartScan();

            DiscoveredBleDevice? target = null;
            var scanDeadline = DateTime.UtcNow.AddSeconds(3.0);
            while (DateTime.UtcNow < scanDeadline && !cancellationToken.IsCancellationRequested)
            {
                if (targetAddress.HasValue && _discovered.TryGetValue(targetAddress.Value, out target))
                    break;

                if (!targetAddress.HasValue)
                {
                    target = _discovered.Values
                        .OrderByDescending(d => d.LastSeen)
                        .FirstOrDefault(d => d.Name.StartsWith(GodoxMicProtocol.DeviceNamePrefix, StringComparison.OrdinalIgnoreCase));
                    if (target is not null)
                        break;
                }

                await Task.Delay(100, cancellationToken);
            }

            if (target is null)
            {
                var cached = LoadCachedDevice();
                ulong? fallbackAddr = targetAddress ?? cached?.Address;
                if (!fallbackAddr.HasValue || fallbackAddr.Value == 0)
                {
                    ConnectionStateChanged?.Invoke(false, "Godox EM68X ('LK*') BLE yayını aranıyor...");
                    StartScan();
                    return false;
                }

                string fallbackName = cached.HasValue && fallbackAddr.Value == cached.Value.Address
                    ? cached.Value.Name
                    : $"LK-{fallbackAddr.Value & 0xFFFF:X4}";
                target = new DiscoveredBleDevice(
                    fallbackAddr.Value,
                    FormatMacAddress(fallbackAddr.Value),
                    fallbackName,
                    -35,
                    DateTime.Now);
            }

            Log("BLE", target.AddressHex, $"{target.Name} ({target.AddressHex}) ile GATT oturumu başlatılıyor...");

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(target.BluetoothAddress);
            if (_device is null)
            {
                ConnectionStateChanged?.Invoke(false, "BluetoothLEDevice açılamadı. BLE yayını bekleniyor...");
                StartScan();
                return false;
            }

            _device.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;

            // Create and maintain GattSession (critical on Windows 10/11 WinRT BLE, matching bleak)
            _gattSession = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId);
            if (_gattSession.CanMaintainConnection)
            {
                _gattSession.MaintainConnection = true;
            }

            // Query all GATT services with retry on Unreachable (matching bleak _get_services)
            GattDeviceServicesResult? servicesResult = null;
            for (int attempt = 1; attempt <= 4 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                servicesResult = await _device.GetGattServicesAsync(
                    attempt == 1 ? BluetoothCacheMode.Cached : BluetoothCacheMode.Uncached);

                if (servicesResult.Status == GattCommunicationStatus.Success && servicesResult.Services.Count > 0)
                {
                    _qppService = servicesResult.Services.FirstOrDefault(s => s.Uuid == GodoxMicProtocol.QppServiceUuid);
                    if (_qppService is not null)
                        break;
                }

                await Task.Delay(450, cancellationToken);
            }

            if (_qppService is null)
            {
                var status = servicesResult?.Status ?? GattCommunicationStatus.Unreachable;
                Log("BLE", target.AddressHex, $"Mikrofon BLE yayınına ulaşılamadı ({status}). Telefon bağlantısını kapatın veya USB kablosunu çıkarıp takın.", false);
                ConnectionStateChanged?.Invoke(false, "Mikrofon BLE yayını bekleniyor (USB tak-çıkar veya telefon BT kapatın)");
                await DisconnectInternalAsync();
                StartScan();
                return false;
            }

            GattCharacteristicsResult? charsResult = null;
            for (int attempt = 1; attempt <= 3 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                charsResult = await _qppService.GetCharacteristicsAsync(
                    attempt == 1 ? BluetoothCacheMode.Cached : BluetoothCacheMode.Uncached);
                if (charsResult.Status == GattCommunicationStatus.Success && charsResult.Characteristics.Count > 0)
                    break;
                await Task.Delay(300, cancellationToken);
            }

            if (charsResult is null || charsResult.Status != GattCommunicationStatus.Success)
            {
                ConnectionStateChanged?.Invoke(false, $"GATT karakteristikleri okunamadı ({charsResult?.Status}).");
                await DisconnectInternalAsync();
                StartScan();
                return false;
            }

            foreach (var ch in charsResult.Characteristics)
            {
                if (ch.Uuid == GodoxMicProtocol.NotifyCharacteristicUuid)
                    _notifyChar = ch;
                else if (ch.Uuid == GodoxMicProtocol.WriteCharacteristicUuid)
                    _writeChar = ch;
            }

            if (_notifyChar is null || _writeChar is null)
            {
                ConnectionStateChanged?.Invoke(false, "0xFFE1 / 0xFFE2 karakteristikleri bulunamadı.");
                await DisconnectInternalAsync();
                StartScan();
                return false;
            }

            _notifyChar.ValueChanged += OnCharacteristicValueChanged;

            GattCommunicationStatus cccdStatus = GattCommunicationStatus.Unreachable;
            for (int attempt = 1; attempt <= 3 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                cccdStatus = await _notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (cccdStatus == GattCommunicationStatus.Success)
                    break;
                await Task.Delay(350, cancellationToken);
            }

            if (cccdStatus != GattCommunicationStatus.Success)
            {
                Log("BLE", target.AddressHex, $"Mikrofon BLE yanıt vermiyor ({cccdStatus}). Mikrofon başka bir cihaza (telefona) bağlı olabilir veya USB tak-çıkar gerekebilir.", false);
                ConnectionStateChanged?.Invoke(false, "BLE yanıt vermiyor (Telefon BT kapatın veya USB tak-çıkar yapın)");
                await DisconnectInternalAsync();
                StartScan();
                return false;
            }

            StopScan();

            ConnectedDeviceName = !string.IsNullOrWhiteSpace(_device.Name) && _device.Name.StartsWith("LK", StringComparison.OrdinalIgnoreCase)
                ? _device.Name
                : target.Name;
            ConnectedDeviceAddress = target.AddressHex;
            SaveCachedDevice(target.BluetoothAddress, ConnectedDeviceName);

            _connectionCts = new CancellationTokenSource();
            _ = Task.Run(() => RunTxWorkerAsync(_connectionCts.Token));
            _ = Task.Run(() => RunStatusPollingLoopAsync(_connectionCts.Token));

            ConnectionStateChanged?.Invoke(true, $"Bağlandı: {ConnectedDeviceName} ({ConnectedDeviceAddress})");
            Log("BLE", ConnectedDeviceAddress, $"Bağlantı başarılı ({ConnectedDeviceName}). İlk durum sorgusu (0xA6) gönderiliyor...");

            EnqueueCommand(GodoxMicProtocol.BuildQueryStatusPacket(), "İlk Durum Sorgusu (0xA6)");
            return true;
        }
        catch (Exception ex)
        {
            Log("ERR", "--", $"BLE bağlantı hatası: {ex.Message}", false);
            ConnectionStateChanged?.Invoke(false, $"Bağlantı hatası: {ex.Message}");
            await DisconnectInternalAsync();
            StartScan();
            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public void SuppressPollingFor(TimeSpan duration)
    {
        Interlocked.Exchange(ref _suppressPollUntilTicks, DateTime.UtcNow.Add(duration).Ticks);
    }

    public void RequestStatus()
    {
        EnqueueCommand(GodoxMicProtocol.BuildQueryStatusPacket(), "Durum Sorgusu (0xA6)");
    }

    public void SendAudioControl(
        ControlSensorMode controlMode,
        int micVolume,
        int monitorVolume,
        PickupPattern pickupPattern)
    {
        SuppressPollingFor(TimeSpan.FromMilliseconds(1400));
        byte[] packet = GodoxMicProtocol.BuildAudioControlPacket(controlMode, micVolume, monitorVolume, pickupPattern);
        EnqueueCommand(
            packet,
            $"Ses/Mod (0xA7): Mic={micVolume}%, Mon={monitorVolume}%, Kutup={pickupPattern}, Sensör={controlMode}");
    }

    public void SendLightPower(bool powerOn)
    {
        SuppressPollingFor(TimeSpan.FromMilliseconds(1200));
        byte[] packet = GodoxMicProtocol.BuildLightPowerPacket(powerOn);
        EnqueueCommand(packet, $"RGB Güç (0xA5): {(powerOn ? "AÇIK" : "KAPALI")}");
    }

    public void SendLightConfig(
        RgbLightMode mode,
        RgbColorPreset color,
        int brightness,
        int speed)
    {
        SuppressPollingFor(TimeSpan.FromMilliseconds(1200));
        byte[] packet = GodoxMicProtocol.BuildLightModePacket(mode, color, brightness, speed);
        EnqueueCommand(
            packet,
            $"RGB Ayarı (0x{(byte)mode:X2} {mode}): Renk={color}, Parlaklık={brightness}%, Hız={speed}%");
    }

    private void EnqueueCommand(byte[] packet, string description)
    {
        if (!IsConnected) return;
        if (packet.Length > 2 && packet[2] != GodoxMicProtocol.CmdQueryStatus)
        {
            Log("TX", GodoxMicProtocol.ToHexString(packet), description);
        }
        _txChannel.Writer.TryWrite(packet);
    }

    private async Task RunTxWorkerAsync(CancellationToken ct)
    {
        try
        {
            while (await _txChannel.Reader.WaitToReadAsync(ct))
            {
                while (_txChannel.Reader.TryRead(out byte[]? packet))
                {
                    while (_txChannel.Reader.TryPeek(out byte[]? next) &&
                           next.Length == packet.Length &&
                           next.Length >= 3 &&
                           next[2] == packet[2])
                    {
                        _txChannel.Reader.TryRead(out packet!);
                    }

                    var writeChar = _writeChar;
                    if (writeChar is null) break;

                    try
                    {
                        var buffer = packet.AsBuffer();
                        var option = (writeChar.CharacteristicProperties & GattCharacteristicProperties.Write) != 0
                            ? GattWriteOption.WriteWithResponse
                            : GattWriteOption.WriteWithoutResponse;

                        await writeChar.WriteValueAsync(buffer, option);
                    }
                    catch (Exception ex)
                    {
                        Log("ERR", GodoxMicProtocol.ToHexString(packet), $"GATT yazma hatası: {ex.Message}", false);
                    }

                    await Task.Delay(95, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunStatusPollingLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(600, ct);
            while (!ct.IsCancellationRequested && IsConnected)
            {
                long suppressUntil = Interlocked.Read(ref _suppressPollUntilTicks);
                if (DateTime.UtcNow.Ticks >= suppressUntil)
                {
                    _txChannel.Writer.TryWrite(GodoxMicProtocol.BuildQueryStatusPacket());
                }
                await Task.Delay(1500, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private string _lastStatusHex = string.Empty;

    private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            byte[] data = args.CharacteristicValue.ToArray();
            string hex = GodoxMicProtocol.ToHexString(data);

            bool hasState = GodoxMicProtocol.TryParseNotification(
                data,
                out GodoxMicState? state,
                out bool crcValid,
                out string summary);

            if (hasState && state is not null)
            {
                if (!string.Equals(hex, _lastStatusHex, StringComparison.Ordinal))
                {
                    _lastStatusHex = hex;
                    Log("RX", hex, summary, crcValid);
                }

                long suppressUntil = Interlocked.Read(ref _suppressPollUntilTicks);
                if (DateTime.UtcNow.Ticks >= suppressUntil)
                {
                    StateReceived?.Invoke(state);
                }
            }
            else
            {
                Log("RX", hex, summary, crcValid);
            }
        }
        catch (Exception ex)
        {
            Log("ERR", "--", $"RX ayrıştırma hatası: {ex.Message}", false);
        }
    }

    private void OnDeviceConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            Log("BLE", ConnectedDeviceAddress ?? "--", "Mikrofon BLE bağlantısı kesildi. Yeniden yayın aranıyor...");
            ConnectionStateChanged?.Invoke(false, "Bağlantı kesildi — BLE yayını aranıyor...");
            StartScan();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            await DisconnectInternalAsync();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task DisconnectInternalAsync()
    {
        try
        {
            _connectionCts?.Cancel();
            _connectionCts?.Dispose();
            _connectionCts = null;
        }
        catch { }

        if (_notifyChar is not null)
        {
            try
            {
                _notifyChar.ValueChanged -= OnCharacteristicValueChanged;
                if (_device?.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    await _notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
            }
            catch { }
            _notifyChar = null;
        }

        _writeChar = null;

        if (_qppService is not null)
        {
            try { _qppService.Dispose(); } catch { }
            _qppService = null;
        }

        if (_gattSession is not null)
        {
            try
            {
                _gattSession.MaintainConnection = false;
                _gattSession.Dispose();
            }
            catch { }
            _gattSession = null;
        }

        if (_device is not null)
        {
            try
            {
                _device.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                _device.Dispose();
            }
            catch { }
            _device = null;
        }

        ConnectedDeviceName = null;
        ConnectedDeviceAddress = null;
        _lastStatusHex = string.Empty;
    }

    private void Log(string direction, string hex, string description, bool isValid = true)
    {
        PacketLogged?.Invoke(new ProtocolLogEntry(DateTime.Now, direction, hex, description, isValid));
    }

    public static string FormatMacAddress(ulong address)
    {
        byte b5 = (byte)((address >> 40) & 0xFF);
        byte b4 = (byte)((address >> 32) & 0xFF);
        byte b3 = (byte)((address >> 24) & 0xFF);
        byte b2 = (byte)((address >> 16) & 0xFF);
        byte b1 = (byte)((address >> 8) & 0xFF);
        byte b0 = (byte)(address & 0xFF);
        return $"{b5:X2}:{b4:X2}:{b3:X2}:{b2:X2}:{b1:X2}:{b0:X2}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopScan();
        try
        {
            DisconnectInternalAsync().GetAwaiter().GetResult();
        }
        catch { }
    }
}
