using System.IO;
using Microsoft.Win32;

namespace WinMusic;

/// <summary>
/// "Open WinMusic when Windows starts". Copies WinMusic.exe to a permanent folder
/// (%LOCALAPPDATA%\WinMusic) so moving/deleting the downloaded file never breaks startup,
/// and registers it in the current user's Run key (no admin needed).
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinMusic";
    public const string StartupArg = "--startup";

    public static string InstalledExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMusic", "WinMusic.exe");

    public static string? RegisteredCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) as string;
    }

    public static bool IsEnabled()
    {
        var cmd = RegisteredCommand();
        return cmd != null && File.Exists(InstalledExe) && cmd.Contains(InstalledExe, StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable()
    {
        var self = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InstalledExe)!);
            // copy to a temp name first, then replace, so a failed copy never leaves a broken exe
            var tmp = InstalledExe + ".new";
            File.Copy(self, tmp, true);
            if (File.Exists(InstalledExe))
            {
                try { File.Replace(tmp, InstalledExe, null); }
                catch (IOException) { /* the installed copy is running right now; keep it */ File.Delete(tmp); }
            }
            else File.Move(tmp, InstalledExe);
        }
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.SetValue(ValueName, $"\"{InstalledExe}\" {StartupArg}");
    }

    public static void Disable()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
