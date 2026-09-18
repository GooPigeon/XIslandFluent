using System.Windows;
using XIslandFluent.Services;

namespace XIslandFluent;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticsLog.Write("DispatcherUnhandledException", args.Exception);
            try { MessageBox.Show(args.Exception.Message, "X 岛遇到问题", MessageBoxButton.OK, MessageBoxImage.Error); }
            catch { }
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticsLog.Write("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticsLog.Write("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }
}
