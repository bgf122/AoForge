using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AoForge.Services;

namespace AoForge;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        // After a self-update the previous exe is left as AoForge.exe.old until its process exits.
        Task.Run(() => SelfUpdater.CleanupOld(SelfUpdater.ExePath));
        base.OnStartup(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "AoForge", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
