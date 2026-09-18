using System.Windows;
using System.Windows.Threading;
using XIslandFluent;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var exitCode = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/XIsland;component/Themes/Fluent.xaml", UriKind.Absolute)
        });
        var window = new BrowserCookieWindow { ShowInTaskbar = false, WindowState = WindowState.Minimized, Opacity = 0 };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            Console.WriteLine("FAIL WebView2 initialization timed out");
            window.Close();
            app.Shutdown(exitCode);
        };
        window.BrowserReady += (_, _) =>
        {
            exitCode = 0;
            timeout.Stop();
            Console.WriteLine("PASS WebView2 initialized");
            window.Close();
            app.Shutdown(exitCode);
        };
        timeout.Start();
        window.Show();
        app.Run();
        return exitCode;
    }
}
