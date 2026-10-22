using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GodoxEM68XControl.Protocol;
using GodoxEM68XControl.Services;

namespace GodoxEM68XControl;

public partial class MainWindow : Window
{
    private readonly GodoxBleService _bleService = new();
    private readonly WindowsAudioService _audioService = new();
    private readonly DispatcherTimer _audioMeterTimer;
    private readonly DispatcherTimer _autoReconnectTimer;
    private readonly ObservableCollection<string> _logEntries = new();
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    private bool _isUiInitialized;
    private bool _isApplyingRemoteState;
    private bool _isConnecting;

    // Device state cache
    private ControlSensorMode _currentSensorMode = ControlSensorMode.HandSweep;
    private int _currentMicVolume = 100;
    private int _currentMonitorVolume = 64;
    private PickupPattern _currentPickupPattern = PickupPattern.Omnidirectional;

    private bool _isLightPowerOn = true;
    private RgbLightMode _currentLightMode = RgbLightMode.Monochrome;
    private RgbColorPreset _currentLightColor = RgbColorPreset.Magenta;
    private int _currentBrightness = 100;
    private int _breatheSpeed = 100;
    private int _gradientSpeed = 100;
    private int _cycleSpeed = 50;

    private sealed record DeviceComboItem(ulong Address, string DisplayText)
    {
        public override string ToString() => DisplayText;
    }

    public MainWindow()
    {
        InitializeComponent();
        _isUiInitialized = true;

        LstProtocolLog.ItemsSource = _logEntries;

        // Pre-populate known Godox EM68X address so the ComboBox is never empty
        var defaultItem = new DeviceComboItem(
            GodoxBleService.DefaultEm68XAddress,
            "LK8353-53B3 (84:B4:DB:E9:53:B3)");
        CmbDevices.Items.Add(defaultItem);
        CmbDevices.SelectedItem = defaultItem;

        _bleService.DeviceDiscovered += OnBleDeviceDiscovered;
        _bleService.ConnectionStateChanged += OnBleConnectionStateChanged;
        _bleService.StateReceived += OnBleStateReceived;
        _bleService.PacketLogged += OnBlePacketLogged;

        _audioMeterTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _audioMeterTimer.Tick += OnAudioMeterTick;

        _autoReconnectTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6)
        };
        _autoReconnectTimer.Tick += OnAutoReconnectTick;

        InitializeTrayIcon();

        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closed += MainWindow_Closed;
    }

    private void InitializeTrayIcon()
    {
        System.Drawing.Icon? icon = null;
        try
        {
            var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/godox_em68x.ico"));
            if (streamInfo?.Stream is not null)
            {
                using var bmp = new System.Drawing.Bitmap(streamInfo.Stream);
                icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }
        catch
        {
            // Fallback to associated process icon
        }

        if (icon is null && !string.IsNullOrEmpty(Environment.ProcessPath))
        {
            try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
        }

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("Godox EM68X Studio'yu Aç");
        openItem.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Çıkış");
        exitItem.Click += (_, _) => Dispatcher.Invoke(Close);
        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "Godox EM68X Studio",
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Dispatcher.Invoke(RestoreFromTray);
            }
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void MinimizeToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            MinimizeToTray();
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateUsbAudioStatus();
        _audioMeterTimer.Start();
        _autoReconnectTimer.Start();

        UpdateRgbControlsVisibilityAndPreview();
        await AutoConnectBleAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _audioMeterTimer.Stop();
        _autoReconnectTimer.Stop();
        _bleService.Dispose();
        _audioService.Dispose();
    }

    // ==================== CUSTOM WINDOW CHROME HANDLERS ====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BtnMaximize_Click(sender, e);
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        MinimizeToTray();
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ==================== USB AUDIO & BLE CONNECTION ====================

    private void UpdateUsbAudioStatus()
    {
        _audioService.RefreshEndpoints();
        if (_audioService.HasUsbCaptureEndpoint || _audioService.HasUsbRenderEndpoint)
        {
            UsbStatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            TxtUsbStatus.Text = "USB Ses: EM68X Aktif";
            TxtUsbStatus.ToolTip = $"Kayıt: {_audioService.CaptureEndpointName}\nÇıkış: {_audioService.RenderEndpointName}";

            if (!_bleService.IsConnected)
            {
                _isApplyingRemoteState = true;
                try
                {
                    if (_audioService.GetWindowsMicVolumePercent() is int winMic)
                    {
                        _currentMicVolume = winMic;
                        SliderMicVolume.Value = winMic;
                        TxtMicVolumeValue.Text = $"{winMic}%";
                    }
                    if (_audioService.GetWindowsHeadphoneVolumePercent() is int winMon)
                    {
                        _currentMonitorVolume = winMon;
                        SliderMonitorVolume.Value = winMon;
                        TxtMonitorVolumeValue.Text = $"{winMon}%";
                    }
                }
                finally
                {
                    _isApplyingRemoteState = false;
                }
            }
        }
        else
        {
            UsbStatusDot.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            TxtUsbStatus.Text = "USB Ses: Bağlı Değil";
        }
    }

    private void OnAudioMeterTick(object? sender, EventArgs e)
    {
        if (!_audioService.HasUsbCaptureEndpoint)
            return;

        float peak = _audioService.GetCapturePeakLevel();
        int peakPercent = (int)Math.Round(peak * 100f);
        BarMicPeak.Value = peakPercent;
        TxtMicPeak.Text = $"{peakPercent}%";

        BarMicPeak.Foreground = peakPercent switch
        {
            >= 85 => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            >= 60 => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            _ => new SolidColorBrush(Color.FromRgb(16, 185, 129))
        };
    }

    private async void OnAutoReconnectTick(object? sender, EventArgs e)
    {
        if (!_bleService.IsConnected && !_isConnecting)
        {
            await AutoConnectBleAsync();
        }
    }

    private async Task AutoConnectBleAsync()
    {
        if (_isConnecting) return;
        _isConnecting = true;
        BtnConnect.IsEnabled = false;
        BtnResetBle.IsEnabled = false;
        TxtBleStatus.Text = "BLE Bağlanıyor...";
        BleStatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));

        try
        {
            ulong? selectedAddress = (CmbDevices.SelectedItem as DeviceComboItem)?.Address;
            bool ok = await _bleService.ConnectAsync(selectedAddress);
            BannerBleHint.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _isConnecting = false;
            BtnConnect.IsEnabled = true;
            BtnResetBle.IsEnabled = true;
        }
    }

    private void OnBleDeviceDiscovered(DiscoveredBleDevice dev)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            string label = $"{dev.Name} ({dev.AddressHex}, {dev.Rssi} dBm)";
            var existing = CmbDevices.Items.OfType<DeviceComboItem>().FirstOrDefault(x => x.Address == dev.BluetoothAddress);
            if (existing is null)
            {
                var item = new DeviceComboItem(dev.BluetoothAddress, label);
                CmbDevices.Items.Add(item);
                CmbDevices.SelectedItem = item;
            }

            if (!_bleService.IsConnected && !_isConnecting)
            {
                await AutoConnectBleAsync();
            }
        });
    }

    private void OnBleConnectionStateChanged(bool connected, string statusMessage)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (connected)
            {
                BleStatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                TxtBleStatus.Text = $"BLE: {_bleService.ConnectedDeviceName} Bağlı";
                TxtBleStatus.ToolTip = statusMessage;
                BtnConnect.Content = "Bağlı";
                BannerBleHint.Visibility = Visibility.Collapsed;
            }
            else
            {
                BleStatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                TxtBleStatus.Text = "BLE Bekleniyor...";
                TxtBleStatus.ToolTip = statusMessage;
                BtnConnect.Content = "Bağlan";
            }
        });
    }

    private void OnBlePacketLogged(ProtocolLogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            string line = $"[{entry.Timestamp:HH:mm:ss}] [{entry.Direction}] {entry.HexPayload} — {entry.Description}";
            _logEntries.Add(line);
            while (_logEntries.Count > 200)
            {
                _logEntries.RemoveAt(0);
            }
            LstProtocolLog.ScrollIntoView(line);
        });
    }

    private void OnBleStateReceived(GodoxMicState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _isApplyingRemoteState = true;
            try
            {
                BannerBleHint.Visibility = Visibility.Collapsed;
                TxtLastRawState.Text = $"Canlı Durum (0xA6): {GodoxMicProtocol.ToHexString(state.RawFrame)}";

                // 1. Audio & Pickup State
                _currentSensorMode = state.ControlMode;
                _currentMicVolume = state.MicVolume;
                _currentMonitorVolume = state.MonitorVolume;
                _currentPickupPattern = state.PickupMode;

                SliderMicVolume.Value = state.MicVolume;
                TxtMicVolumeValue.Text = $"{state.MicVolume}%";

                SliderMonitorVolume.Value = state.MonitorVolume;
                TxtMonitorVolumeValue.Text = $"{state.MonitorVolume}%";

                ApplyPickupPatternToUi(state.PickupMode);
                ApplySensorModeToUi(state.ControlMode);

                // 2. RGB Lighting State
                _isLightPowerOn = state.IsLightPowerOn;
                _currentLightMode = state.LightMode;
                _currentLightColor = state.LightColor;
                _currentBrightness = state.Brightness;
                if (state.BreatheSpeed > 0) _breatheSpeed = state.BreatheSpeed;
                if (state.GradientSpeed > 0) _gradientSpeed = state.GradientSpeed;
                if (state.ColorCycleSpeed > 0) _cycleSpeed = state.ColorCycleSpeed;

                ChkRgbPower.IsChecked = state.IsLightPowerOn;
                ApplyRgbModeToUi(state.LightMode);
                ApplyRgbColorToUi(state.LightColor);

                SliderBrightness.Value = state.Brightness;
                TxtBrightnessValue.Text = $"{state.Brightness}%";

                int activeSpeed = GetActiveModeSpeed(state.LightMode);
                SliderSpeed.Value = Math.Clamp(activeSpeed, 1, 100);
                TxtSpeedValue.Text = $"{activeSpeed}%";

                UpdateRgbControlsVisibilityAndPreview();
            }
            finally
            {
                _isApplyingRemoteState = false;
            }
        });
    }

    private void ApplyPickupPatternToUi(PickupPattern pattern)
    {
        switch (pattern)
        {
            case PickupPattern.Cardioid:
                RbPickupCardioid.IsChecked = true;
                TxtActivePickupBadge.Text = "Kardioid";
                break;
            case PickupPattern.Bidirectional:
                RbPickupBidirectional.IsChecked = true;
                TxtActivePickupBadge.Text = "Çift Yönlü";
                break;
            case PickupPattern.Omnidirectional:
                RbPickupOmni.IsChecked = true;
                TxtActivePickupBadge.Text = "Tüm Yönlü";
                break;
            case PickupPattern.Stereo:
                RbPickupStereo.IsChecked = true;
                TxtActivePickupBadge.Text = "Stereo";
                break;
        }
    }

    private void ApplySensorModeToUi(ControlSensorMode mode)
    {
        switch (mode)
        {
            case ControlSensorMode.NoClose:
                RbCtrlNoClose.IsChecked = true;
                TxtActiveSensorBadge.Text = "Sensör Kapalı";
                break;
            case ControlSensorMode.Touch:
                RbCtrlTouch.IsChecked = true;
                TxtActiveSensorBadge.Text = "Dokunmatik";
                break;
            case ControlSensorMode.HandSweep:
                RbCtrlHandSweep.IsChecked = true;
                TxtActiveSensorBadge.Text = "El Hareketi";
                break;
            case ControlSensorMode.AnyKey:
                RbCtrlAnyKey.IsChecked = true;
                TxtActiveSensorBadge.Text = "Her İkisi";
                break;
        }
    }

    private void ApplyRgbModeToUi(RgbLightMode mode)
    {
        switch (mode)
        {
            case RgbLightMode.Monochrome: RbModeMonochrome.IsChecked = true; break;
            case RgbLightMode.Breathing: RbModeBreathing.IsChecked = true; break;
            case RgbLightMode.Gradient: RbModeGradient.IsChecked = true; break;
            case RgbLightMode.RgbLoop: RbModeLoop.IsChecked = true; break;
            case RgbLightMode.SoundEffect: RbModeSound.IsChecked = true; break;
        }
    }

    private void ApplyRgbColorToUi(RgbColorPreset color)
    {
        switch (color)
        {
            case RgbColorPreset.Red: RbColorRed.IsChecked = true; break;
            case RgbColorPreset.Magenta: RbColorMagenta.IsChecked = true; break;
            case RgbColorPreset.Blue: RbColorBlue.IsChecked = true; break;
            case RgbColorPreset.Cyan: RbColorCyan.IsChecked = true; break;
            case RgbColorPreset.Green: RbColorGreen.IsChecked = true; break;
            case RgbColorPreset.Yellow: RbColorYellow.IsChecked = true; break;
            case RgbColorPreset.White: RbColorWhite.IsChecked = true; break;
        }
    }

    private int GetActiveModeSpeed(RgbLightMode mode) => mode switch
    {
        RgbLightMode.Breathing => _breatheSpeed,
        RgbLightMode.Gradient => _gradientSpeed,
        RgbLightMode.RgbLoop => _cycleSpeed,
        _ => 100
    };

    private void SetActiveModeSpeed(RgbLightMode mode, int speed)
    {
        int clamped = Math.Clamp(speed, 1, 100);
        switch (mode)
        {
            case RgbLightMode.Breathing: _breatheSpeed = clamped; break;
            case RgbLightMode.Gradient: _gradientSpeed = clamped; break;
            case RgbLightMode.RgbLoop: _cycleSpeed = clamped; break;
        }
    }

    private void UpdateRgbControlsVisibilityAndPreview()
    {
        if (!_isUiInitialized) return;

        bool isGradient = _currentLightMode == RgbLightMode.Gradient;
        RbColorWhite.Background = new SolidColorBrush(isGradient
            ? Color.FromRgb(255, 136, 0)
            : Color.FromRgb(255, 255, 255));

        PanelColorSwatches.IsEnabled = _isLightPowerOn && _currentLightMode != RgbLightMode.RgbLoop;
        PanelColorSwatches.Opacity = PanelColorSwatches.IsEnabled ? 1.0 : 0.40;

        bool hasSpeed = _currentLightMode is RgbLightMode.Breathing or RgbLightMode.Gradient or RgbLightMode.RgbLoop;
        PanelSpeedControl.IsEnabled = _isLightPowerOn && hasSpeed;
        PanelSpeedControl.Opacity = PanelSpeedControl.IsEnabled ? 1.0 : 0.40;

        SliderBrightness.IsEnabled = _isLightPowerOn;

        var (colorName, previewColor) = (_currentLightColor, isGradient) switch
        {
            (RgbColorPreset.Red, _) => ("Kırmızı", Color.FromRgb(255, 42, 42)),
            (RgbColorPreset.Magenta, _) => ("Magenta", Color.FromRgb(240, 0, 255)),
            (RgbColorPreset.Blue, _) => ("Mavi", Color.FromRgb(37, 99, 235)),
            (RgbColorPreset.Cyan, _) => ("Camgöbeği", Color.FromRgb(0, 240, 255)),
            (RgbColorPreset.Green, _) => ("Yeşil", Color.FromRgb(16, 185, 129)),
            (RgbColorPreset.Yellow, _) => ("Sarı", Color.FromRgb(250, 204, 21)),
            (RgbColorPreset.White, true) => ("Turuncu (Geçişli Mod)", Color.FromRgb(255, 136, 0)),
            _ => ("Beyaz", Color.FromRgb(255, 255, 255))
        };

        TxtSelectedColorName.Text = _currentLightMode == RgbLightMode.RgbLoop
            ? "7 Renk Otomatik Döngü"
            : colorName;

        Color activeColor = _isLightPowerOn ? previewColor : Color.FromRgb(40, 48, 68);
        BrushRgbPreview.Color = activeColor;
        BrushRgbGlow.Color = activeColor;
    }

    private void SendCurrentAudioStateToHardware()
    {
        if (!_isUiInitialized || _isApplyingRemoteState) return;

        _bleService.SendAudioControl(
            _currentSensorMode,
            _currentMicVolume,
            _currentMonitorVolume,
            _currentPickupPattern);

        if (ChkSyncWindowsAudio.IsChecked == true)
        {
            _audioService.SetWindowsMicVolumePercent(_currentMicVolume);
            _audioService.SetWindowsHeadphoneVolumePercent(_currentMonitorVolume);
        }
    }

    private void SendCurrentRgbStateToHardware()
    {
        if (!_isUiInitialized || _isApplyingRemoteState) return;

        int speed = GetActiveModeSpeed(_currentLightMode);
        _bleService.SendLightConfig(
            _currentLightMode,
            _currentLightColor,
            _currentBrightness,
            speed);
    }

    // ==================== UI EVENT HANDLERS ====================

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        UpdateUsbAudioStatus();
        await AutoConnectBleAsync();
    }

    private async void BtnResetBle_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnecting) return;
        _isConnecting = true;
        BtnConnect.IsEnabled = false;
        BtnResetBle.IsEnabled = false;
        TxtBleStatus.Text = "BT Sıfırlanıyor...";
        BleStatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));

        try
        {
            ulong? selectedAddress = (CmbDevices.SelectedItem as DeviceComboItem)?.Address;
            bool ok = await _bleService.ResetBluetoothAdapterAndConnectAsync(selectedAddress);
            BannerBleHint.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _isConnecting = false;
            BtnConnect.IsEnabled = true;
            BtnResetBle.IsEnabled = true;
        }
    }

    private void BtnRefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        UpdateUsbAudioStatus();
        _bleService.RequestStatus();
    }

    private void AudioSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _bleService.SuppressPollingFor(TimeSpan.FromSeconds(2));
    }

    private void AudioSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        SendCurrentAudioStateToHardware();
    }

    private void SliderMicVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiInitialized) return;
        int val = (int)Math.Round(e.NewValue);
        _currentMicVolume = val;
        if (TxtMicVolumeValue is not null)
            TxtMicVolumeValue.Text = $"{val}%";

        if (!_isApplyingRemoteState)
        {
            SendCurrentAudioStateToHardware();
        }
    }

    private void SliderMonitorVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiInitialized) return;
        int val = (int)Math.Round(e.NewValue);
        _currentMonitorVolume = val;
        if (TxtMonitorVolumeValue is not null)
            TxtMonitorVolumeValue.Text = $"{val}%";

        if (!_isApplyingRemoteState)
        {
            SendCurrentAudioStateToHardware();
        }
    }

    private void BtnMicPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int preset))
        {
            SliderMicVolume.Value = preset;
        }
    }

    private void BtnMonPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int preset))
        {
            SliderMonitorVolume.Value = preset;
        }
    }

    private void PickupPattern_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingRemoteState || sender is not RadioButton rb) return;

        _currentPickupPattern = rb.Tag?.ToString() switch
        {
            "Cardioid" => PickupPattern.Cardioid,
            "Bidirectional" => PickupPattern.Bidirectional,
            "Omnidirectional" => PickupPattern.Omnidirectional,
            "Stereo" => PickupPattern.Stereo,
            _ => _currentPickupPattern
        };

        ApplyPickupPatternToUi(_currentPickupPattern);
        SendCurrentAudioStateToHardware();
    }

    private void SensorMode_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingRemoteState || sender is not RadioButton rb) return;

        _currentSensorMode = rb.Tag?.ToString() switch
        {
            "NoClose" => ControlSensorMode.NoClose,
            "Touch" => ControlSensorMode.Touch,
            "HandSweep" => ControlSensorMode.HandSweep,
            "AnyKey" => ControlSensorMode.AnyKey,
            _ => _currentSensorMode
        };

        ApplySensorModeToUi(_currentSensorMode);
        SendCurrentAudioStateToHardware();
    }

    private void ChkRgbPower_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingRemoteState) return;
        _isLightPowerOn = ChkRgbPower.IsChecked == true;
        UpdateRgbControlsVisibilityAndPreview();
        _bleService.SendLightPower(_isLightPowerOn);
    }

    private void RgbMode_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingRemoteState || sender is not RadioButton rb) return;

        _currentLightMode = rb.Tag?.ToString() switch
        {
            "Monochrome" => RgbLightMode.Monochrome,
            "Breathing" => RgbLightMode.Breathing,
            "Gradient" => RgbLightMode.Gradient,
            "RgbLoop" => RgbLightMode.RgbLoop,
            "SoundEffect" => RgbLightMode.SoundEffect,
            _ => _currentLightMode
        };

        _isApplyingRemoteState = true;
        try
        {
            int modeSpeed = GetActiveModeSpeed(_currentLightMode);
            SliderSpeed.Value = Math.Clamp(modeSpeed, 1, 100);
            TxtSpeedValue.Text = $"{modeSpeed}%";
        }
        finally
        {
            _isApplyingRemoteState = false;
        }

        UpdateRgbControlsVisibilityAndPreview();
        SendCurrentRgbStateToHardware();
    }

    private void RgbColor_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingRemoteState || sender is not RadioButton rb) return;

        _currentLightColor = rb.Tag?.ToString() switch
        {
            "Red" => RgbColorPreset.Red,
            "Magenta" => RgbColorPreset.Magenta,
            "Blue" => RgbColorPreset.Blue,
            "Cyan" => RgbColorPreset.Cyan,
            "Green" => RgbColorPreset.Green,
            "Yellow" => RgbColorPreset.Yellow,
            "White" => RgbColorPreset.White,
            _ => _currentLightColor
        };

        UpdateRgbControlsVisibilityAndPreview();
        SendCurrentRgbStateToHardware();
    }

    private void RgbSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _bleService.SuppressPollingFor(TimeSpan.FromSeconds(2));
    }

    private void RgbSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        SendCurrentRgbStateToHardware();
    }

    private void SliderBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiInitialized) return;
        int val = (int)Math.Round(e.NewValue);
        _currentBrightness = val;
        if (TxtBrightnessValue is not null)
            TxtBrightnessValue.Text = $"{val}%";

        if (!_isApplyingRemoteState)
        {
            SendCurrentRgbStateToHardware();
        }
    }

    private void SliderSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiInitialized) return;
        int val = (int)Math.Round(e.NewValue);
        SetActiveModeSpeed(_currentLightMode, val);
        if (TxtSpeedValue is not null)
            TxtSpeedValue.Text = $"{val}%";

        if (!_isApplyingRemoteState)
        {
            SendCurrentRgbStateToHardware();
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logEntries.Clear();
    }
}