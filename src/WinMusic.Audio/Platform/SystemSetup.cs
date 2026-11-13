using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinMusic.Audio.Interop;
using WinMusic.Audio.Wasapi;

namespace WinMusic.Audio.Platform;

/// <summary>One-click fixes for Windows settings that would hurt music quality.</summary>
public static class SystemSetup
{
    public sealed record CableStatus(bool Installed, AudioDevice? Input, AudioDevice? Output, WaveFormatInfo? InputFormat, WaveFormatInfo? OutputFormat, bool CableIsDefaultPlayback);

    public static CableStatus GetCableStatus()
    {
        var input = Devices.FindCableInput();
        var output = Devices.FindCableOutput();
        WaveFormatInfo? inF = null, outF = null;
        try { if (input != null) inF = GetDeviceFormat(input.Id) ?? Devices.GetMixFormat(input.Id); } catch { }
        try { if (output != null) outF = GetDeviceFormat(output.Id) ?? Devices.GetMixFormat(output.Id); } catch { }
        bool cableDefault = input != null && Devices.DefaultId(DeviceFlow.Playback) == input.Id;
        return new CableStatus(input != null && output != null, input, output, inF, outF, cableDefault);
    }

    /// <summary>The format the device itself runs at (Sound control panel "Default Format").</summary>
    public static WaveFormatInfo? GetDeviceFormat(string deviceId) => Devices.Mta(() =>
    {
        var pc = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            if (pc.GetDeviceFormat(deviceId, 0, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
            var f = WaveFormatInfo.Parse(p);
            Marshal.FreeCoTaskMem(p);
            return f;
        }
        finally { Marshal.ReleaseComObject(pc); }
    });

    /// <summary>Sets a device to 48 kHz / 24-bit (what Discord, games and most voice apps use internally).</summary>
    public static void SetDeviceFormat48k(string deviceId, int channels = 2) => Devices.Mta(() =>
    {
        var pc = (IPolicyConfig)new PolicyConfigClient();
        IntPtr endpoint = WaveFormatInfo.Alloc(48000, channels, false, 24, 24);
        IntPtr mix = WaveFormatInfo.AllocFloat(48000, channels);
        try { Hr.Check(pc.SetDeviceFormat(deviceId, endpoint, mix), "SetDeviceFormat"); }
        finally
        {
            Marshal.FreeHGlobal(endpoint);
            Marshal.FreeHGlobal(mix);
            Marshal.ReleaseComObject(pc);
        }
    });

    public static void SetDefaultDevice(string deviceId) => Devices.Mta(() =>
    {
        var pc = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                Hr.Check(pc.SetDefaultEndpoint(deviceId, role), "SetDefaultEndpoint");
        }
        finally { Marshal.ReleaseComObject(pc); }
    });

    /// <summary>
    /// Windows lowers other apps' volume when it detects a call ("communications activity").
    /// That would lower the music your friends hear whenever Discord opens the mic. Turn it off.
    /// </summary>
    public static bool IsDuckingDisabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Multimedia\Audio");
        return k?.GetValue("UserDuckingPreference") is int v && v == 3;
    }

    public static void DisableDucking()
    {
        using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Multimedia\Audio");
        k.SetValue("UserDuckingPreference", 3, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Applies all optimisations. Returns human readable lines describing what was done.
    /// </summary>
    public static List<string> Optimize(string? preferredSpeakerId)
    {
        var log = new List<string>();
        var st = GetCableStatus();
        if (!st.Installed) { log.Add("VB-CABLE is not installed."); return log; }

        foreach (var dev in new[] { st.Input!, st.Output! })
        {
            try
            {
                var f = GetDeviceFormat(dev.Id);
                if (f == null || f.SampleRate != 48000 || (!f.IsFloat && f.ValidBits < 24))
                {
                    SetDeviceFormat48k(dev.Id, f?.Channels ?? 2);
                    log.Add($"Set {dev.Name} to 48 kHz / 24-bit.");
                }
                else log.Add($"{dev.Name} is already {f}.");
            }
            catch (Exception e) { log.Add($"Could not set the format of {dev.Name} ({e.Message}). Set it to 48000 Hz, 24 bit in Sound settings."); }
        }

        if (st.CableIsDefaultPlayback)
        {
            var speakers = Devices.List(DeviceFlow.Playback).Where(d => !d.IsVirtualCable).ToList();
            var target = speakers.FirstOrDefault(d => d.Id == preferredSpeakerId) ?? speakers.FirstOrDefault();
            if (target != null)
            {
                try { SetDefaultDevice(target.Id); log.Add($"Your default speakers were set to CABLE Input (so you'd hear nothing). Switched back to {target.Name}."); }
                catch (Exception e) { log.Add($"Could not change the default speakers: {e.Message}"); }
            }
        }

        if (!IsDuckingDisabled())
        {
            try { DisableDucking(); log.Add("Turned off Windows' automatic volume lowering during calls."); }
            catch (Exception e) { log.Add($"Could not change ducking setting: {e.Message}"); }
        }
        else log.Add("Windows call ducking is already off.");
        return log;
    }

    // ---- VB-CABLE installer helper ----

    private const string CablePage = "https://vb-audio.com/Cable/";
    private const string FallbackZip = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";

    /// <summary>
    /// Downloads the official VB-CABLE package from vb-audio.com and launches its setup (asks for admin).
    /// VB-CABLE is donationware by VB-Audio; it is not bundled with WinMusic.
    /// </summary>
    public static async Task<string> DownloadAndLaunchCableInstaller(IProgress<string>? progress = null)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinMusic/0.1");
        string url = FallbackZip;
        try
        {
            progress?.Report("Finding the latest VB-CABLE…");
            string html = await http.GetStringAsync(CablePage);
            var m = Regex.Matches(html, @"https://download\.vb-audio\.com/Download_CABLE/VBCABLE_Driver_Pack(\d+)\.zip")
                .OrderByDescending(x => int.Parse(x.Groups[1].Value)).FirstOrDefault();
            if (m != null) url = m.Value;
        }
        catch { }

        string dir = Path.Combine(Path.GetTempPath(), "WinMusic-VBCABLE-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string zip = Path.Combine(dir, "vbcable.zip");
        progress?.Report("Downloading VB-CABLE…");
        await using (var s = await http.GetStreamAsync(url))
        await using (var f = File.Create(zip))
            await s.CopyToAsync(f);
        ZipFile.ExtractToDirectory(zip, dir);
        string setup = Path.Combine(dir, Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe");
        if (!File.Exists(setup)) throw new FileNotFoundException("Setup not found in VB-CABLE package", setup);
        progress?.Report("Launching VB-CABLE setup (click 'Install Driver')…");
        var p = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WorkingDirectory = dir });
        if (p != null) await p.WaitForExitAsync();
        return dir;
    }
}
