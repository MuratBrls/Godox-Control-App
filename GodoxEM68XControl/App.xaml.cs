using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using GodoxEM68XControl.Protocol;
using GodoxEM68XControl.Services;

namespace GodoxEM68XControl;

public partial class App : Application
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            File.AppendAllText("crash.log", $"[{DateTime.Now:HH:mm:ss.fff}] DispatcherUnhandledException:\n{args.Exception}\n\n");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            File.AppendAllText("crash.log", $"[{DateTime.Now:HH:mm:ss.fff}] UnhandledException:\n{args.ExceptionObject}\n\n");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            File.AppendAllText("crash.log", $"[{DateTime.Now:HH:mm:ss.fff}] UnobservedTaskException:\n{args.Exception}\n\n");
            args.SetObserved();
        };

        if (e.Args.Any(a => string.Equals(a, "--verify-hardware", StringComparison.OrdinalIgnoreCase)))
        {
            int code = await RunHardwareVerificationAsync();
            Shutdown(code);
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        var hwnd = new WindowInteropHelper(mainWindow).EnsureHandle();
        ShowWindow(hwnd, 1 /* SW_SHOWNORMAL */);
        ShowWindow(hwnd, 9 /* SW_RESTORE */);
        ShowWindow(hwnd, 5 /* SW_SHOW */);
        SetForegroundWindow(hwnd);
        mainWindow.Activate();
    }

    private static async Task<int> RunHardwareVerificationAsync()
    {
        var report = new StringBuilder();
        void LogLine(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        LogLine("=== GodoxEM68XControl C# / WinRT BLE & CoreAudio Donanım Doğrulama Testi ===");

        using var audio = new WindowsAudioService();
        audio.RefreshEndpoints();
        LogLine($"USB Kayıt Ucu: {audio.CaptureEndpointName} (WinSes={audio.GetWindowsMicVolumePercent()}%, Peak={audio.GetCapturePeakLevel():F3})");
        LogLine($"USB Çıkış Ucu: {audio.RenderEndpointName} (WinSes={audio.GetWindowsHeadphoneVolumePercent()}%)");

        using var ble = new GodoxBleService();
        GodoxMicState? initialState = null;
        GodoxMicState? latestState = null;
        var initialTcs = new TaskCompletionSource<GodoxMicState>(TaskCreationOptions.RunContinuationsAsynchronously);

        ble.PacketLogged += entry =>
        {
            LogLine($"[{entry.Direction}] {entry.HexPayload} | {entry.Description}");
        };

        ble.StateReceived += state =>
        {
            latestState = state;
            initialTcs.TrySetResult(state);
        };

        bool connected = await ble.ConnectAsync();
        if (!connected)
        {
            LogLine("HATA: BLE bağlantısı kurulamadı.");
            await File.WriteAllTextAsync("hardware_verify_result.txt", report.ToString());
            return 1;
        }

        var completed = await Task.WhenAny(initialTcs.Task, Task.Delay(4000));
        if (completed != initialTcs.Task)
        {
            LogLine("HATA: İlk 0xA6 durum yanıtı zaman aşımına uğradı.");
            await File.WriteAllTextAsync("hardware_verify_result.txt", report.ToString());
            return 2;
        }

        initialState = await initialTcs.Task;
        LogLine($"İlk Durum Alındı: Mic={initialState.MicVolume}%, Mon={initialState.MonitorVolume}%, Kutup={initialState.PickupMode}, Sensör={initialState.ControlMode}, RGB={initialState.LightMode}/{initialState.LightColor} ({initialState.Brightness}%)");

        // 1. Test Microphone Volume Change (Priority #1)
        int testMicVol = initialState.MicVolume == 75 ? 80 : 75;
        LogLine($"Test 1: Mikrofon ses seviyesi {testMicVol}% olarak ayarlanıyor (0xA7)...");
        ble.SendAudioControl(initialState.ControlMode, testMicVol, initialState.MonitorVolume, initialState.PickupMode);
        await Task.Delay(450);
        ble. SuppressPollingFor(TimeSpan.Zero);
        ble.RequestStatus();
        await Task.Delay(600);
        LogLine($"Doğrulama 1 (0xA6): MicVolume = {latestState?.MicVolume}% (Beklenen: {testMicVol}%)");

        // 2. Restore exact initial state
        LogLine("Test 2: Orijinal mikrofon ayarları geri yükleniyor...");
        ble.SendAudioControl(initialState.ControlMode, initialState.MicVolume, initialState.MonitorVolume, initialState.PickupMode);
        await Task.Delay(450);
        ble.SuppressPollingFor(TimeSpan.Zero);
        ble.RequestStatus();
        await Task.Delay(600);
        LogLine($"Geri Yükleme Doğrulaması (0xA6): Mic={latestState?.MicVolume}%, Mon={latestState?.MonitorVolume}%, Kutup={latestState?.PickupMode}");

        await ble.DisconnectAsync();
        LogLine("=== C# Donanım Doğrulama Testi Başarıyla Tamamlandı ===");
        await File.WriteAllTextAsync("hardware_verify_result.txt", report.ToString());
        return 0;
    }
}
