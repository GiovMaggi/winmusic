using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinMusic.Audio.Engine;
using WinMusic.Audio.Platform;
using WinMusic.Audio.Wasapi;

namespace WinMusic;

public sealed class Settings
{
    public List<string> Apps { get; set; } = new();
    public string? MicDeviceId { get; set; }          // null = automatic, "" = none
    public bool MicMuted { get; set; }
    public double MusicGainDb { get; set; }
    public double MicGainDb { get; set; }
    public bool AutoStart { get; set; }
    public bool ShowAllApps { get; set; }

    private static string PathName => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinMusic", "settings.json");

    public static Settings Load()
    {
        if (ReadOnly) return new();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathName)) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Set by the automated GUI test so it never touches real user settings.</summary>
    public static bool ReadOnly;

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public sealed class AppItem : INotifyPropertyChanged
{
    private bool _checked, _playing, _running = true;
    private readonly Action<AppItem> _onToggle;
    public AppItem(string exe, string name, ImageSource? icon, Action<AppItem> onToggle)
    { ExeName = exe; DisplayName = name; Icon = icon; _onToggle = onToggle; }

    public string ExeName { get; }
    public string DisplayName { get; set; }
    public ImageSource? Icon { get; set; }
    public bool IsChecked { get => _checked; set { if (_checked == value) return; _checked = value; Notify(nameof(IsChecked)); _onToggle(this); } }
    public void SetCheckedSilently(bool v) { _checked = v; Notify(nameof(IsChecked)); }
    public bool IsPlaying { get => _playing; set { _playing = value; Notify(nameof(StateText)); Notify(nameof(StateBrush)); } }
    public bool IsRunning { get => _running; set { _running = value; Notify(nameof(StateText)); Notify(nameof(StateBrush)); } }
    public string StateText => !IsRunning ? "not running" : IsPlaying ? "♪ playing" : "";
    public Brush StateBrush => !IsRunning ? (Brush)Application.Current.Resources["Muted"] : (Brush)Application.Current.Resources["Good"];

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly AudioEngine _engine = new();
    private readonly ObservableCollection<AppItem> _apps = new();
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _slowTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading = true;
    private bool _refreshingApps;
    private float _musicLevel, _micLevel, _outLevel;
    private readonly List<string> _log = new();
    private SystemSetup.CableStatus? _cable;

    private sealed record MicChoice(string? Id, string Label) { public override string ToString() => Label; }

    public MainWindow()
    {
        InitializeComponent();
        AppList.ItemsSource = _apps;
        _engine.Log += m => Dispatcher.BeginInvoke(() => AddLog(m));
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _slowTimer.Tick += async (_, _) => { await RefreshApps(); if (!_engine.IsRunning) await RefreshCable(); UpdateStatus(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MusicSlider.Value = _settings.MusicGainDb;
        MicSlider.Value = _settings.MicGainDb;
        MicMute.IsChecked = _settings.MicMuted;
        AutoStart.IsChecked = _settings.AutoStart;
        try { LaunchAtLogin.IsChecked = StartupManager.IsEnabled(); } catch { }
        if (App.LaunchedAtLogin) WindowState = WindowState.Minimized;
        ShowAllApps.IsChecked = _settings.ShowAllApps;
        UpdateGainLabels();

        if (!ProcessLoopbackSupported())
            AddLog("This version of Windows is too old for per-app capture (needs Windows 10 version 2004 or newer).");

        await RefreshCable();
        await RefreshMics();
        await RefreshApps();
        _loading = false;
        _meterTimer.Start();
        _slowTimer.Start();

        if (App.SmokeTest is { exe: "" } nc) { RunNoCableSmokeTest(nc.report); return; }
        if (App.SmokeTest is { } su && su.exe.StartsWith("@startup:")) { _ = RunStartupSmokeTest(su.exe["@startup:".Length..], su.report); return; }
        if (App.SmokeTest != null) { _ = RunSmokeTest(App.SmokeTest.Value.exe, App.SmokeTest.Value.report); return; }
        if (App.LaunchedAtLogin) { _ = GoLiveAfterLogin(); return; }
        if (_settings.AutoStart && _cable?.Installed == true) StartEngine();
    }

    /// <summary>
    /// Right after Windows starts, audio devices can take a while to appear. Keep trying quietly
    /// (no error pop-ups) for up to 3 minutes until the virtual cable is there, then go live.
    /// </summary>
    private async Task GoLiveAfterLogin()
    {
        for (int attempt = 0; attempt < 60 && !_engine.IsRunning; attempt++)
        {
            await RefreshCable();
            if (_cable?.Installed == true)
            {
                StartEngine(quiet: true);
                if (_engine.IsRunning) { AddLog("Started automatically at login."); return; }
            }
            await Task.Delay(3000);
        }
        if (!_engine.IsRunning) AddLog("Could not go live automatically (virtual cable not found). Press Start.");
    }

    private void OnLaunchAtLogin(object sender, RoutedEventArgs e)
    {
        if (_loading || App.SmokeTest != null && !_allowStartupChangeInTest) return;
        try
        {
            if (LaunchAtLogin.IsChecked == true) { StartupManager.Enable(); AddLog($"WinMusic will open when Windows starts ({StartupManager.InstalledExe})."); }
            else { StartupManager.Disable(); AddLog("WinMusic will no longer open when Windows starts."); }
        }
        catch (Exception ex)
        {
            AddLog("Could not change startup setting: " + ex.Message);
            _loading = true; LaunchAtLogin.IsChecked = StartupManager.IsEnabled(); _loading = false;
        }
    }

    private bool _allowStartupChangeInTest;

    /// <summary>CI: simulates a login launch and checks the startup checkbox really registers/unregisters.</summary>
    private async Task RunStartupSmokeTest(string exe, string reportPath)
    {
        var log = new List<string> { "# WinMusic GUI test: launch at Windows login", "" };
        bool ok = true;
        void Check(bool c, string what) { log.Add($"- {(c ? "PASS" : "FAIL")}: {what}"); ok &= c; }
        try
        {
            Check(WindowState == WindowState.Minimized, "opens minimized");
            _settings.Apps = new() { exe };
            _settings.MicDeviceId = "";
            await GoLiveAfterLogin();
            Check(_engine.IsRunning && (string)LiveText.Text == "LIVE", "went live by itself");
            for (int i = 0; i < 20 && !_engine.GetStatus().Any(s => s.Kind == SourceKind.App && s.Playing); i++) await Task.Delay(500);
            Check(_engine.GetStatus().Any(s => s.Kind == SourceKind.App && s.Playing), $"captures {exe} without any clicks");

            _allowStartupChangeInTest = true;
            LaunchAtLogin.IsChecked = true;
            var cmd = StartupManager.RegisteredCommand();
            log.Add($"- registered: `{cmd}`");
            Check(cmd != null && cmd.Contains(StartupManager.InstalledExe) && cmd.Contains(StartupManager.StartupArg), "checkbox registers WinMusic to run at login");
            Check(File.Exists(StartupManager.InstalledExe) && new FileInfo(StartupManager.InstalledExe).Length == new FileInfo(Environment.ProcessPath!).Length,
                "copied itself to a permanent folder (safe to delete the downloaded file)");
            Check(StartupManager.IsEnabled(), "reports enabled");
            LaunchAtLogin.IsChecked = false;
            Check(StartupManager.RegisteredCommand() == null && !StartupManager.IsEnabled(), "unchecking removes it again");
            StopEngine();
        }
        catch (Exception e) { log.Add($"- FAIL: exception {e}"); ok = false; }
        log.Add("");
        log.Add(ok ? "**GUI (startup) RESULT: ALL PASSED**" : "**GUI (startup) RESULT: FAILURES**");
        File.WriteAllText(reportPath, string.Join("\n", log));
        App.SmokeExitCode = ok ? 0 : 1;
        Close();
    }

    /// <summary>First launch on a PC without VB-CABLE: must not crash, must offer the installer.</summary>
    private void RunNoCableSmokeTest(string reportPath)
    {
        var log = new List<string> { "# WinMusic GUI test: first launch without VB-CABLE", "" };
        bool ok = true;
        void Check(bool c, string what) { log.Add($"- {(c ? "PASS" : "FAIL")}: {what}"); ok &= c; }
        Check(_cable?.Installed == false, "detects that VB-CABLE is missing");
        Check(InstallCableBtn.Visibility == Visibility.Visible && InstallCableBtn.IsEnabled, "shows the Install VB-CABLE button");
        Check(!StartBtn.IsEnabled, "Start is disabled until the cable exists");
        Check(CableText.Text.Contains("not installed"), $"explains the problem (\"{CableText.Text}\")");
        log.Add("");
        log.Add(ok ? "**GUI (no cable) RESULT: ALL PASSED**" : "**GUI (no cable) RESULT: FAILURES**");
        File.WriteAllText(reportPath, string.Join("\n", log));
        App.SmokeExitCode = ok ? 0 : 1;
        Close();
    }

    /// <summary>
    /// Automated end-to-end test of the real GUI (run in CI): tick an app in the list, press Start,
    /// and verify its sound comes out of the virtual mic.
    /// </summary>
    private async Task RunSmokeTest(string exe, string reportPath)
    {
        var log = new List<string> { "# WinMusic GUI end-to-end test", "" };
        bool ok = true;
        void Check(bool c, string what) { log.Add($"- {(c ? "PASS" : "FAIL")}: {what}"); ok &= c; }
        try
        {
            Check(_cable?.Installed == true, "GUI detected VB-CABLE");
            AppItem? item = null;
            for (int i = 0; i < 20 && item == null; i++)
            {
                await RefreshApps();
                item = _apps.FirstOrDefault(a => a.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase));
                if (item == null) await Task.Delay(500);
            }
            Check(item != null, $"app list shows {exe}");
            log.Add($"- app list: {string.Join(", ", _apps.Select(a => $"{a.DisplayName} [{a.StateText}]"))}");
            MicCombo.SelectedItem = ((IEnumerable<MicChoice>)MicCombo.ItemsSource).First(c => c.Id == "");
            if (item != null) item.IsChecked = true;            // same as clicking the checkbox
            StartBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); // press Start
            Check(_engine.IsRunning, "engine started from the Start button");
            Check((string)LiveText.Text == "LIVE", "UI shows LIVE");

            // let the drift correction lock in (6 s measure + 3 s ramp), then measure WinMusic's own output
            float maxMusic = 0;
            for (int i = 0; i < 60; i++) { await Task.Delay(200); maxMusic = Math.Max(maxMusic, _musicLevel); }
            int rate = _engine.OutputFormat!.SampleRate;
            var rec = new List<float>();
            var tap = new List<float>();
            var cap = CaptureStream.ForProcess(Environment.ProcessId, rate, 2, (d, ch, t, silent) => { lock (rec) for (int i = 0; i < d.Length; i += ch) rec.Add(d[i]); });
            await Task.Run(() => cap.Start());
            await Task.Delay(500);
            _engine.OutputTap = d => { lock (tap) for (int i = 0; i < d.Length; i += 2) tap.Add(d[i]); };
            await Task.Delay(20000);
            _engine.OutputTap = null;
            await Task.Delay(500);
            cap.Dispose();
            UpdateStatus();
            log.Add($"- status:\n```\n{StatusText.Text}\n```");
            foreach (var st in _engine.GetStatus()) log.Add($"- source detail: {st}");
            Check(_engine.GetStatus().Any(s => s.Kind == SourceKind.App && s.Playing && s.Underruns == 0), "app source is live with no underruns");
            Check(maxMusic > 0.1f, $"music meter moving (peak {20 * Math.Log10(Math.Max(maxMusic, 1e-9)):F1} dBFS)");
            float[] arr; lock (tap) arr = tap.ToArray();
            var rep = WinMusic.Core.Analysis.SineAnalyzer.Analyze(arr, rate, 1000);
            log.Add($"- WinMusic output, exactly what is sent to the cable (20 s): {rep}");
            float[] lb; lock (rec) lb = rec.ToArray();
            log.Add($"- same output seen through Windows process loopback (21 s, incl. capture start/stop): {WinMusic.Core.Analysis.SineAnalyzer.Analyze(lb, rate, 1000)}");
            Check(rep.Blocks >= 190 && rep.GlitchBlocks == 0 && rep.SilentBlocks == 0, "clean, continuous audio for 20 s");
            Check(rep.MedianThdNDb < -110, $"THD+N below -110 dB ({rep.MedianThdNDb:F1} dB)");
            Check(Math.Abs(20 * Math.Log10(Math.Max(rep.Amplitude, 1e-9) / 0.5)) < 0.1, "level unchanged at 0 dB");

            StartBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); // press Stop
            Check(!_engine.IsRunning && (string)LiveText.Text == "OFF", "Stop button stops the engine");
        }
        catch (Exception e) { log.Add($"- FAIL: exception {e}"); ok = false; }
        log.Add("");
        log.Add(ok ? "**GUI RESULT: ALL PASSED**" : "**GUI RESULT: FAILURES**");
        File.WriteAllText(reportPath, string.Join("\n", log));
        App.SmokeExitCode = ok ? 0 : 1;
        Close();
    }

    private static bool ProcessLoopbackSupported() => Environment.OSVersion.Version.Build >= 19041;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _settings.AutoStart = AutoStart.IsChecked == true;
        _settings.Save();
        _engine.Dispose();
    }

    // ---------------- cable ----------------

    private async Task RefreshCable()
    {
        try { _cable = await Task.Run(SystemSetup.GetCableStatus); }
        catch (Exception ex) { AddLog(ex.Message); return; }
        var c = _cable;
        if (!c.Installed)
        {
            CableDot.Fill = (Brush)FindResource("Bad");
            CableText.Text = "VB-CABLE is not installed";
            CableDetail.Text = "WinMusic sends your music into a free virtual cable (VB-CABLE by VB-Audio, donationware). Click Install, press “Install Driver”, then restart your PC.";
            InstallCableBtn.Visibility = Visibility.Visible;
            OptimizeBtn.IsEnabled = false;
            StartBtn.IsEnabled = false;
            return;
        }
        InstallCableBtn.Visibility = Visibility.Collapsed;
        OptimizeBtn.IsEnabled = true;
        StartBtn.IsEnabled = true;
        bool good48 = c.InputFormat?.SampleRate == 48000 && c.OutputFormat?.SampleRate == 48000;
        bool ducking = SystemSetup.IsDuckingDisabled();
        bool ok = good48 && !c.CableIsDefaultPlayback && ducking && !c.CableVolumeNot100;
        CableDot.Fill = (Brush)FindResource(ok ? "Good" : "Warn");
        CableText.Text = ok ? "Ready — select “CABLE Output” as your mic in Discord" : "Installed — click “Optimize Windows for music”";
        var issues = new List<string>();
        if (!good48) issues.Add("cable is not at 48 kHz");
        if (c.CableIsDefaultPlayback) issues.Add("your default speakers are set to the cable (you'd hear nothing)");
        if (!ducking) issues.Add("Windows lowers other apps' volume during calls");
        if (c.CableVolumeNot100) issues.Add("the cable's Windows volume is not 100% (makes your music quieter for friends)");
        CableDetail.Text = $"Cable format: {c.InputFormat?.ToString() ?? "?"}" + (issues.Count > 0 ? "\nTo fix: " + string.Join("; ", issues) : "");
    }

    private async void OnInstallCable(object sender, RoutedEventArgs e)
    {
        InstallCableBtn.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(AddLog);
            await SystemSetup.DownloadAndLaunchCableInstaller(progress);
            await RefreshCable();
            if (_cable?.Installed != true)
                MessageBox.Show(this, "When the VB-CABLE setup says it's done, restart your PC, then open WinMusic again.", "WinMusic");
            else
                await Optimize();
        }
        catch (Exception ex)
        {
            AddLog("Install failed: " + ex.Message);
            MessageBox.Show(this, "Couldn't install automatically. Download VB-CABLE from https://vb-audio.com/Cable/ , unzip it, right-click VBCABLE_Setup_x64.exe → Run as administrator.\n\n" + ex.Message, "WinMusic");
        }
        finally { InstallCableBtn.IsEnabled = true; }
    }

    private async void OnOptimize(object sender, RoutedEventArgs e) => await Optimize();

    private async Task Optimize()
    {
        OptimizeBtn.IsEnabled = false;
        bool wasRunning = _engine.IsRunning;
        try
        {
            if (wasRunning) StopEngine();
            var lines = await Task.Run(() => SystemSetup.Optimize(null));
            foreach (var l in lines) AddLog(l);
            await RefreshCable();
        }
        catch (Exception ex) { AddLog("Optimize failed: " + ex.Message); }
        finally
        {
            OptimizeBtn.IsEnabled = true;
            if (wasRunning) StartEngine();
        }
    }

    // ---------------- apps ----------------

    private async void OnRefreshApps(object sender, RoutedEventArgs e) => await RefreshApps();

    private async void OnShowAllChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ShowAllApps = ShowAllApps.IsChecked == true;
        await RefreshApps();
    }

    private async Task RefreshApps()
    {
        if (_refreshingApps) return;
        _refreshingApps = true;
        try
        {
            bool all = ShowAllApps.IsChecked == true;
            var list = await Task.Run(() => Apps.List(all));
            var wanted = new HashSet<string>(_settings.Apps, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var a in list)
            {
                seen.Add(a.ExeName);
                var item = _apps.FirstOrDefault(i => i.ExeName.Equals(a.ExeName, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    item = new AppItem(a.ExeName, a.DisplayName, GetIcon(a.ExePath), OnAppToggled);
                    item.SetCheckedSilently(wanted.Contains(a.ExeName));
                    _apps.Add(item);
                }
                item.IsRunning = true;
                item.IsPlaying = a.IsPlaying;
                if (item.Icon == null && a.ExePath != null) item.Icon = GetIcon(a.ExePath);
            }
            // keep ticked apps visible even when they're closed
            foreach (var exe in wanted.Where(w => !seen.Contains(w)))
            {
                var item = _apps.FirstOrDefault(i => i.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    item = new AppItem(exe, System.IO.Path.GetFileNameWithoutExtension(exe), null, OnAppToggled);
                    item.SetCheckedSilently(true);
                    _apps.Add(item);
                }
                item.IsRunning = false;
                item.IsPlaying = false;
            }
            foreach (var gone in _apps.Where(i => !seen.Contains(i.ExeName) && !wanted.Contains(i.ExeName)).ToList())
                _apps.Remove(gone);

            NoAppsText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { AddLog("Could not list apps: " + ex.Message); }
        finally { _refreshingApps = false; }
    }

    private void OnAppToggled(AppItem item)
    {
        _settings.Apps.RemoveAll(a => a.Equals(item.ExeName, StringComparison.OrdinalIgnoreCase));
        if (item.IsChecked) _settings.Apps.Add(item.ExeName);
        _settings.Save();
        _engine.SetApps(_settings.Apps);
    }

    // ---------------- mic ----------------

    private async Task RefreshMics()
    {
        var mics = await Task.Run(() => Devices.List(DeviceFlow.Recording).Where(d => !d.IsVirtualCable).ToList());
        var choices = new List<MicChoice> { new(null, "Automatic (Windows default mic)"), new("", "No microphone (music only)") };
        choices.AddRange(mics.Select(m => new MicChoice(m.Id, m.Name)));
        MicCombo.ItemsSource = choices;
        MicCombo.SelectedItem = choices.FirstOrDefault(c => c.Id == _settings.MicDeviceId) ?? choices[0];
    }

    private void OnMicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MicCombo.SelectedItem is not MicChoice c) return;
        if (!_loading) { _settings.MicDeviceId = c.Id; _settings.Save(); }
        _engine.SetMicDevice(c.Id);
    }

    private void OnMicMute(object sender, RoutedEventArgs e)
    {
        _settings.MicMuted = MicMute.IsChecked == true;
        _engine.SetMicMuted(_settings.MicMuted);
    }

    // ---------------- gains ----------------

    private void OnMusicGain(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _settings.MusicGainDb = MusicSlider.Value;
        _engine.SetMusicGainDb(MusicSlider.Value);
        UpdateGainLabels();
    }

    private void OnMicGain(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _settings.MicGainDb = MicSlider.Value;
        _engine.SetMicGainDb(MicSlider.Value);
        UpdateGainLabels();
    }

    private void OnResetMusic(object sender, System.Windows.Input.MouseButtonEventArgs e) => MusicSlider.Value = 0;
    private void OnResetMic(object sender, System.Windows.Input.MouseButtonEventArgs e) => MicSlider.Value = 0;

    private void UpdateGainLabels()
    {
        if (MusicDb == null || MicDb == null) return;
        MusicDb.Text = MusicSlider.Value == 0 ? "0 dB (original, untouched)" : $"{MusicSlider.Value:+0.0;-0.0} dB";
        MicDb.Text = $"{MicSlider.Value:+0.0;-0.0;0} dB";
    }

    // ---------------- start/stop ----------------

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning) StopEngine(); else StartEngine();
    }

    private void StartEngine(bool quiet = false)
    {
        try
        {
            _engine.SetApps(_settings.Apps);
            _engine.SetMicDevice(_settings.MicDeviceId);
            _engine.SetMicMuted(_settings.MicMuted);
            _engine.SetMusicGainDb(_settings.MusicGainDb);
            _engine.SetMicGainDb(_settings.MicGainDb);
            _engine.Start();
            if (_settings.Apps.Count == 0) AddLog("Tip: tick at least one app under Music sources.");
        }
        catch (Exception ex)
        {
            AddLog("Start failed: " + ex.Message);
            if (!quiet) MessageBox.Show(this, ex.Message, "WinMusic", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateRunState();
    }

    private void StopEngine()
    {
        _engine.Stop();
        UpdateRunState();
    }

    private void UpdateRunState()
    {
        bool on = _engine.IsRunning;
        StartBtn.Content = on ? "■  Stop" : "▶  Start";
        StartBtn.Background = (Brush)FindResource(on ? "Bad" : "Accent");
        LiveText.Text = on ? "LIVE" : "OFF";
        LiveDot.Fill = (Brush)FindResource(on ? "Good" : "Muted");
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!_engine.IsRunning) { StatusText.Text = "Stopped."; return; }
        var lines = new List<string> { $"→ {_engine.OutputDeviceName}  ·  {_engine.OutputFormat}  ·  32-bit float mix" };
        foreach (var s in _engine.GetStatus())
        {
            string kind = s.Kind == SourceKind.Mic ? "mic " : "app ";
            string state = s.Error != null ? $"error: {s.Error}" : s.Playing ? "live" : "waiting for sound";
            lines.Add($"{kind} {s.Name,-18} {state,-17} buffer {s.BufferedMs,5:F1} ms  clock {s.CorrectionPpm,7:+0.0;-0.0} ppm");
        }
        if (_settings.Apps.Count > 0 && !_engine.GetStatus().Any(s => s.Kind == SourceKind.App))
            lines.Add("Waiting for your selected apps to start…");
        StatusText.Text = string.Join("\n", lines);
    }

    // ---------------- meters ----------------

    private void UpdateMeters()
    {
        if (!_engine.IsRunning)
        {
            MusicMeter.Width = MicMeter.Width = OutMeter.Width = 0;
            return;
        }
        var (music, mic, output, lim) = _engine.ReadAndResetMeters();
        _musicLevel = Smooth(_musicLevel, music);
        _micLevel = Smooth(_micLevel, mic);
        _outLevel = Smooth(_outLevel, output);
        SetMeter(MusicMeter, _musicLevel);
        SetMeter(MicMeter, _micLevel);
        SetMeter(OutMeter, _outLevel);
        double gr = -20 * Math.Log10(Math.Max(lim, 1e-6));
        LimiterText.Text = gr > 0.05 ? $"Limiter −{gr:F1} dB (turn something down)" : "Limiter idle · no clipping";
        LimiterText.Foreground = (Brush)FindResource(gr > 0.05 ? "Warn" : "Muted");
    }

    private static float Smooth(float prev, float peak) => peak >= prev ? peak : prev * 0.85f + peak * 0.15f;

    private static void SetMeter(FrameworkElement bar, float peak)
    {
        double db = 20 * Math.Log10(Math.Max(peak, 1e-6));
        double frac = Math.Clamp((db + 60) / 60, 0, 1);
        if (bar.Parent is FrameworkElement track) bar.Width = track.ActualWidth * frac;
    }

    // ---------------- misc ----------------

    private void AddLog(string message)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (_log.Count > 6) _log.RemoveAt(0);
        LogText.Text = string.Join("\n", _log);
        UpdateStatus();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private ImageSource? GetIcon(string? path)
    {
        if (path == null) return null;
        if (_iconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            var info = new SHFILEINFO();
            SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 /* SHGFI_ICON */);
            if (info.hIcon != IntPtr.Zero)
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                img = bs;
                DestroyIcon(info.hIcon);
            }
        }
        catch { }
        _iconCache[path] = img;
        return img;
    }
}
