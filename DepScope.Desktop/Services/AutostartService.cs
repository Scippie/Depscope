using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace DepScope.Desktop.Services;

public static class AutostartService
{
    private const string AppName = "DepScope";
    private const string MacPlistId = "com.depscope.app";

    public static void SetAutostart(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            SetWindowsAutostart(enabled);
        }
        else if (OperatingSystem.IsLinux())
        {
            SetLinuxAutostart(enabled);
        }
        else if (OperatingSystem.IsMacOS())
        {
            SetMacAutostart(enabled);
        }
        // Other OS: noop
    }

    private static string? GetExePath()
    {
        return Environment.ProcessPath
               ?? Process.GetCurrentProcess().MainModule?.FileName;
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsAutostart(bool enabled)
    {
        var exePath = GetExePath();
        if (exePath is null)
            return;

        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

        if (key is null)
            return;

        if (enabled)
        {
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    private static void SetLinuxAutostart(bool enabled)
    {
        var exePath = GetExePath();
        if (exePath is null)
            return;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        var dir = Path.Combine(home, ".config", "autostart");
        Directory.CreateDirectory(dir);

        var desktopFile = Path.Combine(dir, "DepScope.desktop");

        if (!enabled)
        {
            if (File.Exists(desktopFile))
                File.Delete(desktopFile);

            return;
        }

        var content = $@"[Desktop Entry]
Type=Application
Exec=""{exePath}""
Hidden=false
NoDisplay=false
X-GNOME-Autostart-enabled=true
Name=DepScope
Comment=Dependency monitor
";

        File.WriteAllText(desktopFile, content);
    }

    private static void SetMacAutostart(bool enabled)
    {
        var exePath = GetExePath();
        if (exePath is null)
            return;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        var dir = Path.Combine(home, "Library", "LaunchAgents");
        Directory.CreateDirectory(dir);

        var plistPath = Path.Combine(dir, $"{MacPlistId}.plist");

        if (!enabled)
        {
            if (File.Exists(plistPath))
                File.Delete(plistPath);
            return;
        }

        var plist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key><string>{MacPlistId}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{exePath}</string>
    </array>
    <key>RunAtLoad</key><true/>
</dict>
</plist>
";
        File.WriteAllText(plistPath, plist);
    }
}

