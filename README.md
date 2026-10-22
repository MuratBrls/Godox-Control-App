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
- **Auto-Reconnect & BLE Reset**: Automatic reconnection on signal drop and built-in Bluetooth adapter reset tool.

---

## Technical Stack

- **C# / .NET 8 (Windows Desktop SDK)**
- **WPF (Windows Presentation Foundation)** with custom dark studio theme and frameless `WindowChrome`
- **Windows.Devices.Bluetooth (WinRT BLE API)**
- **NAudio (CoreAudioApi / WASAPI)**

---

## Protocol Overview

- **Service UUID**: `0000FFF0-0000-1000-8000-00805F9B34FB`
- **Write Characteristic**: `0000FFF2-0000-1000-8000-00805F9B34FB`
- **Notify Characteristic**: `0000FFF1-0000-1000-8000-00805F9B34FB`
- **Header**: `0x55 0xAA`
- **Packet Structure**:
  - `0x55 0xAA [Length] [Command] [Payload...] [CRC8]`
  - Audio State (`0x26` / State `0xA6`)
  - RGB Light Config (`0x23` / State `0xA3`)
  - Light Power (`0x24` / State `0xA4`)
