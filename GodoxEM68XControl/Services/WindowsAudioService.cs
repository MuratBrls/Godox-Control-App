using System;
using System.Runtime.InteropServices;

namespace GodoxEM68XControl.Services;

/// <summary>
/// Lightweight Windows Core Audio (MMDevice / IAudioEndpointVolume / IAudioMeterInformation)
/// helper to read live USB microphone peak levels and Windows USB audio endpoint volumes for EM68X.
/// </summary>
public sealed class WindowsAudioService : IDisposable
{
    private IMMDevice? _captureDevice;
    private IMMDevice? _renderDevice;
    private IAudioEndpointVolume? _captureVolume;
    private IAudioEndpointVolume? _renderVolume;
    private IAudioMeterInformation? _captureMeter;

    public bool HasUsbCaptureEndpoint => _captureVolume is not null;
    public bool HasUsbRenderEndpoint => _renderVolume is not null;
    public string CaptureEndpointName { get; private set; } = "Bulunamadı";
    public string RenderEndpointName { get; private set; } = "Bulunamadı";

    public void RefreshEndpoints()
    {
        ReleaseComObjects();

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType is null) return;

            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            Guid iidVolume = typeof(IAudioEndpointVolume).GUID;
            Guid iidMeter = typeof(IAudioMeterInformation).GUID;

            _captureDevice = FindDeviceByToken(enumerator, EDataFlow.eCapture, "EM68X", out string capName);
            if (_captureDevice is not null)
            {
                CaptureEndpointName = capName;
                _captureDevice.Activate(ref iidVolume, 0x17, IntPtr.Zero, out object volObj);
                _captureVolume = volObj as IAudioEndpointVolume;

                _captureDevice.Activate(ref iidMeter, 0x17, IntPtr.Zero, out object meterObj);
                _captureMeter = meterObj as IAudioMeterInformation;
            }
            else
            {
                CaptureEndpointName = "Bağlı Değil";
            }

            _renderDevice = FindDeviceByToken(enumerator, EDataFlow.eRender, "EM68X", out string renName);
            if (_renderDevice is not null)
            {
                RenderEndpointName = renName;
                _renderDevice.Activate(ref iidVolume, 0x17, IntPtr.Zero, out object volObj);
                _renderVolume = volObj as IAudioEndpointVolume;
            }
            else
            {
                RenderEndpointName = "Bağlı Değil";
            }

            Marshal.ReleaseComObject(enumerator);
        }
        catch
        {
            // Ignore COM audio enumeration errors on systems without audio devices
        }
    }

    public float GetCapturePeakLevel()
    {
        if (_captureMeter is null) return 0f;
        try
        {
            _captureMeter.GetPeakValue(out float peak);
            return Math.Clamp(peak, 0f, 1f);
        }
        catch
        {
            return 0f;
        }
    }

    public int? GetWindowsMicVolumePercent()
    {
        if (_captureVolume is null) return null;
        try
        {
            _captureVolume.GetMasterVolumeLevelScalar(out float level);
            return (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100f);
        }
        catch
        {
            return null;
        }
    }

    public void SetWindowsMicVolumePercent(int percent)
    {
        if (_captureVolume is null) return;
        try
        {
            float scalar = Math.Clamp(percent / 100f, 0f, 1f);
            Guid ctx = Guid.Empty;
            _captureVolume.SetMasterVolumeLevelScalar(scalar, ref ctx);
        }
        catch { }
    }

    public int? GetWindowsHeadphoneVolumePercent()
    {
        if (_renderVolume is null) return null;
        try
        {
            _renderVolume.GetMasterVolumeLevelScalar(out float level);
            return (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100f);
        }
        catch
        {
            return null;
        }
    }

    public void SetWindowsHeadphoneVolumePercent(int percent)
    {
        if (_renderVolume is null) return;
        try
        {
            float scalar = Math.Clamp(percent / 100f, 0f, 1f);
            Guid ctx = Guid.Empty;
            _renderVolume.SetMasterVolumeLevelScalar(scalar, ref ctx);
        }
        catch { }
    }

    private static IMMDevice? FindDeviceByToken(
        IMMDeviceEnumerator enumerator,
        EDataFlow flow,
        string token,
        out string matchedName)
    {
        matchedName = string.Empty;
        enumerator.EnumAudioEndpoints(flow, 0x00000001 /* DEVICE_STATE_ACTIVE */, out IMMDeviceCollection collection);
        if (collection is null) return null;

        collection.GetCount(out uint count);
        var pkeyFriendlyName = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        for (uint i = 0; i < count; i++)
        {
            collection.Item(i, out IMMDevice dev);
            if (dev is null) continue;

            string friendly = GetDeviceStringProperty(dev, pkeyFriendlyName);
            if (friendly.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                matchedName = friendly;
                Marshal.ReleaseComObject(collection);
                return dev;
            }

            Marshal.ReleaseComObject(dev);
        }

        Marshal.ReleaseComObject(collection);
        return null;
    }

    private static string GetDeviceStringProperty(IMMDevice device, PropertyKey key)
    {
        try
        {
            device.OpenPropertyStore(0 /* STGM_READ */, out IPropertyStore store);
            if (store is null) return string.Empty;
            store.GetValue(ref key, out PropVariant prop);
            string result = prop.GetString() ?? string.Empty;
            PropVariantClear(ref prop);
            Marshal.ReleaseComObject(store);
            return result;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ReleaseComObjects()
    {
        if (_captureMeter is not null) { try { Marshal.ReleaseComObject(_captureMeter); } catch { } _captureMeter = null; }
        if (_captureVolume is not null) { try { Marshal.ReleaseComObject(_captureVolume); } catch { } _captureVolume = null; }
        if (_renderVolume is not null) { try { Marshal.ReleaseComObject(_renderVolume); } catch { } _renderVolume = null; }
        if (_captureDevice is not null) { try { Marshal.ReleaseComObject(_captureDevice); } catch { } _captureDevice = null; }
        if (_renderDevice is not null) { try { Marshal.ReleaseComObject(_renderDevice); } catch { } _renderDevice = null; }
    }

    public void Dispose() => ReleaseComObjects();

    #region Core Audio COM Interop

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid fmtid, uint pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public int p2;

        public readonly string? GetString()
            => vt == 31 /* VT_LPWSTR */ && p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out uint pcDevices);
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        int OpenPropertyStore(uint stgmAccess, out IPropertyStore ppProperties);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PropertyKey pkey);
        int GetValue(ref PropertyKey key, out PropVariant pv);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        int GetPeakValue(out float pfPeak);
    }

    #endregion
}
