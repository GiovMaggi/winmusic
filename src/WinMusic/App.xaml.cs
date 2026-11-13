using System.Windows;

namespace WinMusic;

public partial class App : Application
{
    /// <summary>--smoketest &lt;exeToTick&gt; &lt;report.md&gt; : automated GUI test used by CI.</summary>
    public static (string exe, string report)? SmokeTest;
    public static int SmokeExitCode = 1;
    /// <summary>Launched by Windows at login: start minimized and go live automatically.</summary>
    public static bool LaunchedAtLogin;

    protected override void OnStartup(StartupEventArgs e)
    {
        LaunchedAtLogin = e.Args.Contains(StartupManager.StartupArg);
        if (e.Args.Length >= 3 && e.Args[0] == "--smoketest")
        {
            SmokeTest = (e.Args[1], e.Args[2]);
            Settings.ReadOnly = true;
        }
        else if (e.Args.Length >= 2 && e.Args[0] == "--smoketest-nocable")
        {
            SmokeTest = ("", e.Args[1]);
            Settings.ReadOnly = true;
        }
        else if (e.Args.Length >= 3 && e.Args[0] == "--smoketest-startup")
        {
            // simulate a login launch: minimized + go live, with ToneApp.exe selected
            SmokeTest = ("@startup:" + e.Args[1], e.Args[2]);
            Settings.ReadOnly = true;
            LaunchedAtLogin = true;
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            if (SmokeTest != null)
            {
                System.IO.File.AppendAllText(SmokeTest.Value.report, $"\n- FAIL: unhandled {ex.Exception}\n");
                Shutdown(1);
                return;
            }
            MessageBox.Show(ex.Exception.Message, "WinMusic", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (SmokeTest != null) e.ApplicationExitCode = SmokeExitCode;
        base.OnExit(e);
    }
}
