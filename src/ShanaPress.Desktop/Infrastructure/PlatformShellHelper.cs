using System.Diagnostics;
using Avalonia.Controls;
using ShanaPress.Core.Models;
using ShanaPress.Desktop.Views;

namespace ShanaPress.Desktop.Infrastructure;

public static class PlatformShellHelper
{
    public static void OpenFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { folderPath }, UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { folderPath }, UseShellExecute = false });
            }
        }
        catch
        {
            // Ignore desktop shell errors
        }
    }

    public static void RevealFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            if (File.Exists(filePath))
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
                    return;
                }
                if (OperatingSystem.IsMacOS())
                {
                    Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", filePath }, UseShellExecute = false });
                    return;
                }
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                OpenFolder(dir);
            }
        }
        catch
        {
            // Ignore desktop shell errors
        }
    }

    public static void OpenFileWithDefaultApp(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { filePath }, UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { filePath }, UseShellExecute = false });
            }
        }
        catch
        {
            // Ignore
        }
    }

    public static async Task ExecutePostEncodeAsync(PostEncodeAction action, string? targetFolder, Window? owner)
    {
        switch (action)
        {
            case PostEncodeAction.OpenOutputFolder:
                OpenFolder(targetFolder);
                break;

            case PostEncodeAction.PlayBeep:
                try
                {
                    if (OperatingSystem.IsMacOS())
                    {
                        Process.Start(new ProcessStartInfo("afplay") { ArgumentList = { "/System/Library/Sounds/Glass.aiff" }, CreateNoWindow = true });
                    }
                    else
                    {
                        Console.Beep();
                    }
                }
                catch { }
                break;

            case PostEncodeAction.ShutdownPc:
                if (owner is null) break;
                var dlg = new ShutdownCountdownWindow();
                var confirmed = await dlg.ShowDialog<bool>(owner);
                if (confirmed)
                {
                    TriggerOsShutdown();
                }
                break;
        }
    }

    private static void TriggerOsShutdown()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("osascript") { ArgumentList = { "-e", "tell app \"System Events\" to shut down" }, CreateNoWindow = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("systemctl") { ArgumentList = { "poweroff" }, CreateNoWindow = true });
            }
        }
        catch
        {
            // Shutdown may require elevated privileges on some Linux setups
        }
    }
}
