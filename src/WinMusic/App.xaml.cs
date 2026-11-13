using System.Windows;

namespace WinMusic;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "WinMusic", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
        base.OnStartup(e);
    }
}
