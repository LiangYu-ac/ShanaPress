using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using ShanaPress.App.Views;
using ShanaPress.Core.Models;

namespace ShanaPress.App.Infrastructure;

public static class PostEncodeActionHelper
{
    public static void Execute(PostEncodeAction action, string? targetFolder, Window owner)
    {
        switch (action)
        {
            case PostEncodeAction.OpenOutputFolder:
                OpenFolder(targetFolder);
                break;

            case PostEncodeAction.PlayBeep:
                SystemSounds.Exclamation.Play();
                break;

            case PostEncodeAction.ShutdownPc:
                SystemSounds.Exclamation.Play();
                var dlg = new ShutdownCountdownWindow { Owner = owner };
                if (dlg.ShowDialog() == true)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "shutdown.exe",
                        Arguments = "/s /t 0",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                break;
        }
    }

    public static void OpenFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folderPath}\"",
            UseShellExecute = true
        });
    }

    public static void RevealFileInExplorer(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        if (File.Exists(filePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            });
            return;
        }

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            OpenFolder(dir);
        }
    }
}
