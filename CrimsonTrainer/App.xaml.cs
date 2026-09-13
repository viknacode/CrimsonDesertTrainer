using System.Windows;
using System.Windows.Threading;

namespace CrimsonTrainer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    // Safety net: show what went wrong instead of silently disappearing.
    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Crimson Desert Trainer", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
