# Godox EM68X Studio (Windows)

Modern, native Windows desktop controller for the **Godox EM68X USB/Bluetooth Esports Microphone**.

Developed by reverse-engineering the Godox Mic Android BLE communication protocol.

---

## Features

- **Microphone & Headphone Volume**: Precise hardware slider control (0–100%) with quick presets.
- **Windows USB Audio Synchronization**: Bidirectional sync between Godox mic hardware and Windows CoreAudio endpoints with live audio peak meter.
- **Polar Pickup Pattern**: Switch between Cardioid, Bidirectional, Omnidirectional, and Stereo pickup modes.
- **Top Mute Sensor Modes**: Configure touch sensor behavior (Disabled, Touch Only, Hand Sweep / Gesture, Touch + Sweep).
- **RGB Lighting Controls**:
  - Monochrome, Breathing, Gradient, 7-Color Auto Loop, and Sound Effect reactive modes.
  - Color palette selection (Red, Magenta, Blue, Cyan, Green, Yellow, White / Gradient Orange).
  - Brightness and animation speed adjustments.
  - Power toggle switch and live color preview badge.
- **System Tray (Gizli Simgeler) Support**: Minimizes seamlessly to the Windows notification tray to keep the taskbar clean while maintaining active background connection and polling.
- **Start with Windows & Auto-100% Gain**: Launches silently in the system tray on Windows boot (configurable from the Controls page or tray menu) and automatically sets the microphone gain to 100% on startup.
- **Auto-Reconnect & BLE Reset**: Automatic reconnection on signal drop and built-in Bluetooth adapter reset tool.

---

## Technical Stack

- **C# / .NET 8 (Windows Desktop SDK)**
- **WPF (Windows Presentation Foundation)** with custom dark studio theme and frameless `WindowChrome`
- **Windows.Devices.Bluetooth (WinRT BLE API)**
- **Windows Core Audio COM Interop (IMMDeviceEnumerator / IAudioEndpointVolume / IAudioMeterInformation)**

---

## Protocol Overview

- **Service UUID**: `0000FFE0-0000-1000-8000-00805F9B34FB`
- **Notify Characteristic**: `0000FFE1-0000-1000-8000-00805F9B34FB`
- **Write Characteristic**: `0000FFE2-0000-1000-8000-00805F9B34FB`
- **Header**: `0x68`
- **Packet Structure**:
  - `0x68 [Length] [Command] [Payload...] [CRC16_Lo] [CRC16_Hi]` (CRC-16/MODBUS over `Command + Payload`)
  - Device State Query / Notification (`0xA6`)
  - Audio & Sensor Control (`0xA7`)
  - RGB Light Power (`0xA5`)
  - RGB Lighting Modes (`0xA0` – `0xA4`)
