using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinMusic.Audio.Interop;
using WinMusic.Audio.Wasapi;

namespace WinMusic.Audio.Platform;

/// <summary>One-click fixes for Windows settings that would hurt music quality.</summary>
public static class SystemSetup
{
    public sealed record CableStatus(bool Installed, AudioDevice? Input, AudioDevice? Output, WaveFormatInfo? InputFormat, WaveFormatInfo? OutputFormat, bool CableIsDefaultPlayback, bool CableVolumeNot100 = false);

    public static CableStatus GetCableStatus()
    {
        var input = Devices.FindCableInput();
        var output = Devices.FindCableOutput();
        WaveFormatInfo? inF = null, outF = null;
        try { if (input != null) inF = GetDeviceFormat(input.Id) ?? Devices.GetMixFormat(input.Id); } catch { }
        try { if (output != null) outF = GetDeviceFormat(output.Id) ?? Devices.GetMixFormat(output.Id); } catch { }
        bool cableDefault = input != null && Devices.DefaultId(DeviceFlow.Playback) == input.Id;
        bool volIssue = false;
        foreach (var d in new[] { input, output })
        {
            if (d == null) continue;
            try { var (v, m) = Devices.GetEndpointVolume(d.Id); if (v < 0.999f || m) volIssue = true; } catch { }
        }
        return new CableStatus(input != null && output != null, input, output, inF, outF, cableDefault, volIssue);
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

        foreach (var dev in new[] { st.Input!, st.Output! })
        {
            try
            {
                var (vol, muted) = Devices.GetEndpointVolume(dev.Id);
                if (vol < 0.999f || muted)
                {
                    Devices.SetEndpointVolume(dev.Id, 1f, false);
                    log.Add($"Set the Windows volume of {dev.Name} to 100% (was {vol * 100:F0}%{(muted ? ", muted" : "")}).");
                }
            }
            catch (Exception e) { log.Add($"Could not check the volume of {dev.Name}: {e.Message}"); }
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

    private const string CableSha256 = "bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90";
    private static readonly string[] OnlineMirrors =
    {
        "https://github.com/GiovMaggi/winmusic/raw/main/third_party/vbcable/VBCABLE_Driver_Pack46.zip",
        "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip",
    };

    /// <summary>
    /// Gets the official VB-CABLE package. Order: the copy embedded in WinMusic itself (works offline,
    /// forever) → a VBCABLE_Driver_Pack*.zip next to WinMusic.exe → online mirrors. Returns the zip path.
    /// </summary>
    public static async Task<string> GetCablePackage(string dir, IProgress<string>? progress = null)
    {
        string zip = Path.Combine(dir, "VBCABLE_Driver_Pack.zip");

        using (var res = typeof(SystemSetup).Assembly.GetManifestResourceStream("VBCABLE_Driver_Pack.zip"))
        {
            if (res != null)
            {
                progress?.Report("Using the VB-CABLE package built into WinMusic (offline).");
                await using var f = File.Create(zip);
                await res.CopyToAsync(f);
                return zip;
            }
        }

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        var local = Directory.EnumerateFiles(exeDir, "VBCABLE_Driver_Pack*.zip").OrderDescending().FirstOrDefault();
        if (local != null)
        {
            progress?.Report($"Using {Path.GetFileName(local)} next to WinMusic.");
            File.Copy(local, zip, true);
            return zip;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinMusic/1.0");
        foreach (var url in OnlineMirrors)
        {
            try
            {
                progress?.Report($"Downloading VB-CABLE from {new Uri(url).Host}…");
                var bytes = await http.GetByteArrayAsync(url);
                if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() != CableSha256)
                    throw new InvalidDataException("checksum mismatch");
                await File.WriteAllBytesAsync(zip, bytes);
                return zip;
            }
            catch (Exception e) { progress?.Report($"  failed: {e.Message}"); }
        }
        throw new InvalidOperationException("No VB-CABLE package available.");
    }

    /// <summary>
    /// Extracts the official, unmodified VB-CABLE package and launches VB-Audio's own setup (asks for admin).
    /// VB-CABLE is donationware by VB-Audio Software (https://vb-audio.com/Cable/).
    /// </summary>
    public static async Task<string> DownloadAndLaunchCableInstaller(IProgress<string>? progress = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinMusic-VBCABLE-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string zip = await GetCablePackage(dir, progress);
        ZipFile.ExtractToDirectory(zip, dir, true);
        string setup = Path.Combine(dir, Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe");
        if (!File.Exists(setup)) throw new FileNotFoundException("Setup not found in VB-CABLE package", setup);
        progress?.Report("Launching VB-CABLE setup — click “Install Driver”, then restart your PC.");
        var p = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WorkingDirectory = dir });
        if (p != null) await p.WaitForExitAsync();
        return dir;
    }
}
