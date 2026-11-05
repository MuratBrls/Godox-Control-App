using System;
using Microsoft.Win32;

namespace GodoxEM68XControl.Services;

/// <summary>
/// Manages "start with Windows" (HKCU Run key) and remembers the user's preference
/// so the app does not re-enable autostart after the user turned it off.
/// </summary>
public static class StartupManager
{
    public const string TrayArgument = "--tray";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "GodoxEM68XStudio";
    private const string SettingsKeyPath = @"Software\GodoxEM68XStudio";
    private const string AutoStartSettingName = "AutoStart";

    /// <summary>User preference (defaults to enabled).</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                return key?.GetValue(AutoStartSettingName) is not int value || value != 0;
            }
            catch
            {
                return true;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
            key.SetValue(AutoStartSettingName, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }

        ApplyRunEntry(enabled);
    }

    /// <summary>
    /// Called on each launch: keeps the Run entry in sync with the preference
    /// and refreshes the exe path if the folder was moved.
    /// </summary>
    public static void SyncOnLaunch() => ApplyRunEntry(IsEnabled);

    private static void ApplyRunEntry(bool enabled)
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (runKey is null) return;

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    runKey.SetValue(RunValueName, $"\"{exePath}\" {TrayArgument}");
                }
            }
            else
            {
                runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }
}
