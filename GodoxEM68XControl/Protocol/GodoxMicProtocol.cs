using System;
using System.Text;

namespace GodoxEM68XControl.Protocol;

/// <summary>
/// RGB Lighting Effect Modes (Constant.java: MODEL_SINGLE_ALWAYS_BRIGHT .. MODEL_SOUND_EFFECT).
/// </summary>
public enum RgbLightMode : byte
{
    Monochrome = 0xA0,      // 160 - Sabit Tek Renk (MODEL_SINGLE_ALWAYS_BRIGHT)
    Breathing = 0xA1,       // 161 - Nefes Alma (MODEL_SINGLE_BREATHE)
    Gradient = 0xA2,        // 162 - Renk Geçişli (MODEL_SINGLE_GRADIENT)
    RgbLoop = 0xA3,         // 163 - 7 Renk Döngüsü (MODEL_SEVEN_COLOR_CYCLE)
    SoundEffect = 0xA4      // 164 - Sese Duyarlı (MODEL_SOUND_EFFECT)
}

/// <summary>
/// RGB Color Presets (Constant.java: COLOR_RED .. COLOR_WHITE).
/// Note: In Gradient mode (0xA2), slot 7 (0xF6) renders as Orange (#FF7F00) in the official app.
/// </summary>
public enum RgbColorPreset : byte
{
    Red = 0xF0,             // 240 - #FF0404
    Magenta = 0xF1,         // 241 - #FF00FF
    Blue = 0xF2,            // 242 - #0000FF
    Cyan = 0xF3,            // 243 - #00FFFF
    Green = 0xF4,           // 244 - #00FF00
    Yellow = 0xF5,          // 245 - #FFFF00
    White = 0xF6            // 246 - #FFFFFF (or #FF7F00 Orange in Gradient mode)
}

/// <summary>
/// Top Sensor / Mute Control Mode (Constant.java: LIGHT_CONSTANT_ON .. TOUCH_HAND_MODE).
/// Packed into Data1 of Command 0xA7 (MODEL_VOICE).
/// </summary>
public enum ControlSensorMode : byte
{
    NoClose = 0xF0,         // 240 - Sensör Kapalı / Sürekli Açık (No Close)
    Touch = 0xF1,           // 241 - Dokunmatik Susturma (Touch)
    HandSweep = 0xF2,       // 242 - El Hareketi ile Susturma (Hand Sweep)
    AnyKey = 0xF3           // 243 - Dokunma + El Hareketi (Any Key)
}

/// <summary>
/// Microphone Polar / Pickup Pattern (Constant.java: HEART_MODE .. STEREO_MODE).
/// Packed into Data5 (4th payload byte) of Command 0xA7 (MODEL_VOICE).
/// </summary>
public enum PickupPattern : byte
{
    Cardioid = 0xF4,        // 244 - Kardioid / Ön Yönlü (HEART_MODE)
    Bidirectional = 0xF5,   // 245 - Çift Yönlü / Sekiz (EIGHT_MODE)
    Omnidirectional = 0xF6, // 246 - Tüm Yönlü / 360° (ALL_DIRECTION_MODE)
    Stereo = 0xF7           // 247 - Stereo (STEREO_MODE)
}

/// <summary>
/// Represents the complete parsed state returned by the Godox EM68X over BLE (Command 0xA6).
/// </summary>
public sealed record GodoxMicState
{
    public bool IsEm68X { get; init; } = true;
    public RgbLightMode LightMode { get; init; } = RgbLightMode.Monochrome;
    public RgbColorPreset LightColor { get; init; } = RgbColorPreset.Magenta;
    public int Brightness { get; init; } = 100;           // 0..100
    public bool IsLightPowerOn { get; init; } = true;     // byte[6]: 0x00 = ON, 0x01 = OFF
    public int BreatheSpeed { get; init; } = 100;         // 1..100
    public int GradientSpeed { get; init; } = 100;        // 1..100
    public int ColorCycleSpeed { get; init; } = 50;       // 1..100
    public ControlSensorMode ControlMode { get; init; } = ControlSensorMode.HandSweep;
    public int MicVolume { get; init; } = 100;            // 0..100 (%)
    public int MonitorVolume { get; init; } = 64;         // 0..100 (%)
    public PickupPattern PickupMode { get; init; } = PickupPattern.Omnidirectional;
    public byte[] RawFrame { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Reverse-engineered Bluetooth Low Energy protocol implementation for Godox EM68X / EM68G
/// from com.linking.godoxmic (MicBlueDataInfo, MicSoundData, QppApi, ToolUtil, MainActivity).
/// </summary>
public static class GodoxMicProtocol
{
    public const string DeviceNamePrefix = "LK";
    public static readonly Guid QppServiceUuid = new("0000ffe0-0000-1000-8000-00805f9b34fb");
    public static readonly Guid NotifyCharacteristicUuid = new("0000ffe1-0000-1000-8000-00805f9b34fb");
    public static readonly Guid WriteCharacteristicUuid = new("0000ffe2-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CccdUuid = new("00002902-0000-1000-8000-00805f9b34fb");

    public const byte HeaderByte = 0x68;        // 104 (Constant.FIRST_CODE)
    public const byte CmdSwitchLight = 0xA5;    // 165 (Constant.MODEL_SWITCH_LIGHT)
    public const byte CmdQueryStatus = 0xA6;    // 166 (Constant.MODEL_RECEIVE)
    public const byte CmdVoiceControl = 0xA7;   // 167 (Constant.MODEL_VOICE)

    /// <summary>
    /// Computes CRC-16/MODBUS (init 0xFFFF, polynomial 0xA001) in little-endian byte order,
    /// matching com.linking.godoxmic.util.ToolUtil.getCrc16(byte[]).
    /// </summary>
    public static (byte Lo, byte Hi) ComputeCrc16(ReadOnlySpan<byte> payload)
    {
        int crc = 0xFFFF;
        for (int i = 0; i < payload.Length; i++)
        {
            crc = ((crc & 0xFF) ^ payload[i]) | (crc & 0xFF00);
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 1) > 0)
                {
                    crc = (crc >> 1) ^ 0xA001;
                }
                else
                {
                    crc >>= 1;
                }
            }
        }
        return ((byte)(crc & 0xFF), (byte)((crc >> 8) & 0xFF));
    }

    /// <summary>
    /// Builds an 8-byte command frame (MicBlueDataInfo.getMicBlueData):
    /// [0x68, 0x04, cmd, data1, data2, data3, crcLo, crcHi]
    /// </summary>
    public static byte[] BuildCommand4(byte cmd, byte data1, byte data2, byte data3 = 0x00)
    {
        Span<byte> body = stackalloc byte[4] { cmd, data1, data2, data3 };
        var (crcLo, crcHi) = ComputeCrc16(body);
        return new byte[8]
        {
            HeaderByte,
            0x04,
            cmd,
            data1,
            data2,
            data3,
            crcLo,
            crcHi
        };
    }

    /// <summary>
    /// Builds the 8-byte status polling frame (Cmd = 0xA6):
    /// 68 04 A6 00 00 00 22 AC
    /// </summary>
    public static byte[] BuildQueryStatusPacket()
        => BuildCommand4(CmdQueryStatus, 0x00, 0x00, 0x00);

    /// <summary>
    /// Builds the 8-byte RGB power switch frame (Cmd = 0xA5):
    /// data1 = 0x00 for Light ON (LIGHT_SWITCH_ON), 0x01 for Light OFF (LIGHT_SWITCH_OFF).
    /// </summary>
    public static byte[] BuildLightPowerPacket(bool powerOn)
        => BuildCommand4(CmdSwitchLight, powerOn ? (byte)0x00 : (byte)0x01, 0x00, 0x00);

    /// <summary>
    /// Builds the 8-byte RGB effect/color/brightness/speed frame (Cmd = 0xA0..0xA4).
    /// For Monochrome (0xA0) and SoundEffect (0xA4), speed is sent as 0x00.
    /// </summary>
    public static byte[] BuildLightModePacket(
        RgbLightMode mode,
        RgbColorPreset color,
        int brightness,
        int speed = 0)
    {
        byte clampedBrightness = (byte)Math.Clamp(brightness, 0, 100);
        byte speedByte = mode is RgbLightMode.Breathing or RgbLightMode.Gradient or RgbLightMode.RgbLoop
            ? (byte)Math.Clamp(speed, 1, 100)
            : (byte)0x00;

        return BuildCommand4((byte)mode, (byte)color, clampedBrightness, speedByte);
    }

    /// <summary>
    /// Builds the 9-byte audio &amp; pickup pattern control frame (MicSoundData.getMicBlueData, Cmd = 0xA7):
    /// [0x68, 0x05, 0xA7, controlMode, micVol, monitorVol, pickupPattern, crcLo, crcHi]
    /// Note: MicSoundData has a field data4 ("extraSound" / gain) in the APK, but it is omitted from
    /// the 9-byte frame and hidden in activity_main.xml.
    /// </summary>
    public static byte[] BuildAudioControlPacket(
        ControlSensorMode controlMode,
        int micVolume,
        int monitorVolume,
        PickupPattern pickupPattern)
    {
        byte micByte = (byte)Math.Clamp(micVolume, 0, 100);
        byte monByte = (byte)Math.Clamp(monitorVolume, 0, 100);
        Span<byte> body = stackalloc byte[5]
        {
            CmdVoiceControl,
            (byte)controlMode,
            micByte,
            monByte,
            (byte)pickupPattern
        };
        var (crcLo, crcHi) = ComputeCrc16(body);
        return new byte[9]
        {
            HeaderByte,
            0x05,
            body[0],
            body[1],
            body[2],
            body[3],
            body[4],
            crcLo,
            crcHi
        };
    }

    /// <summary>
    /// Parses an incoming BLE notification from characteristic 0xFFE1.
    /// </summary>
    public static bool TryParseNotification(
        byte[]? data,
        out GodoxMicState? state,
        out bool isCrcValid,
        out string summary)
    {
        state = null;
        isCrcValid = false;

        if (data is null || data.Length < 4)
        {
            summary = "Geçersiz veya çok kısa paket";
            return false;
        }

        if (data[0] != HeaderByte)
        {
            summary = $"Bilinmeyen başlık (0x{data[0]:X2})";
            return false;
        }

        int payloadLen = data[1];
        if (data.Length >= 2 + payloadLen + 2 && payloadLen > 0)
        {
            ReadOnlySpan<byte> body = data.AsSpan(2, payloadLen);
            var (expectedLo, expectedHi) = ComputeCrc16(body);
            byte actualLo = data[2 + payloadLen];
            byte actualHi = data[2 + payloadLen + 1];
            isCrcValid = (expectedLo == actualLo && expectedHi == actualHi);
        }

        byte cmd = data[2];
        if (cmd == CmdQueryStatus && data.Length >= 9)
        {
            bool isEm68X = data.Length >= 14;
            RgbLightMode mode = Enum.IsDefined(typeof(RgbLightMode), data[3])
                ? (RgbLightMode)data[3]
                : RgbLightMode.Monochrome;
            RgbColorPreset color = Enum.IsDefined(typeof(RgbColorPreset), data[4])
                ? (RgbColorPreset)data[4]
                : RgbColorPreset.Red;
            int brightness = Math.Clamp((int)data[5], 0, 100);
            bool lightPowerOn = data[6] == 0x00;

            int breatheSpeed = 100;
            int gradientSpeed = 100;
            int cycleSpeed = 50;
            ControlSensorMode ctrlMode = ControlSensorMode.HandSweep;
            int micVol = 100;
            int monVol = 64;
            PickupPattern pickup = PickupPattern.Cardioid;

            if (isEm68X)
            {
                breatheSpeed = Math.Clamp((int)data[7], 0, 100);
                gradientSpeed = Math.Clamp((int)data[8], 0, 100);
                cycleSpeed = Math.Clamp((int)data[9], 0, 100);
                if (Enum.IsDefined(typeof(ControlSensorMode), data[10]))
                {
                    ctrlMode = (ControlSensorMode)data[10];
                }
                micVol = Math.Clamp((int)data[11], 0, 100);
                monVol = Math.Clamp((int)data[12], 0, 100);
                if (Enum.IsDefined(typeof(PickupPattern), data[13]))
                {
                    pickup = (PickupPattern)data[13];
                }
            }

            state = new GodoxMicState
            {
                IsEm68X = isEm68X,
                LightMode = mode,
                LightColor = color,
                Brightness = brightness,
                IsLightPowerOn = lightPowerOn,
                BreatheSpeed = breatheSpeed,
                GradientSpeed = gradientSpeed,
                ColorCycleSpeed = cycleSpeed,
                ControlMode = ctrlMode,
                MicVolume = micVol,
                MonitorVolume = monVol,
                PickupMode = pickup,
                RawFrame = (byte[])data.Clone()
            };

            summary = $"Durum (0xA6): Mic={micVol}%, Mon={monVol}%, Mod={pickup}, RGB={mode}/{color} ({brightness}%, {(lightPowerOn ? "Açık" : "Kapalı")})";
            return true;
        }

        summary = cmd switch
        {
            CmdVoiceControl when data.Length >= 7 =>
                $"Ses Komutu ACK (0xA7): Sensör=0x{data[3]:X2}, Mic={data[4]}%, Mon={data[5]}%, Kutup=0x{data[6]:X2}",
            CmdSwitchLight when data.Length >= 4 =>
                $"RGB Güç ACK (0xA5): {(data[3] == 0 ? "Açık" : "Kapalı")}",
            >= 0xA0 and <= 0xA4 when data.Length >= 6 =>
                $"RGB Efekt ACK (0x{cmd:X2}): Renk=0x{data[3]:X2}, Parlaklık={data[4]}%, Hız={data[5]}%",
            _ => $"Paket (Cmd=0x{cmd:X2}, {data.Length} bayt)"
        };
        return false;
    }

    public static string ToHexString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        var sb = new StringBuilder(bytes.Length * 3 - 1);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
