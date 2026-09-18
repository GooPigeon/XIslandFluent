using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XIslandFluent;

internal static class Program
{
    private const int ThreadId = 69466323;

    [STAThread]
    private static int Main()
    {
        if (Environment.GetEnvironmentVariable("XISLAND_REPLY_SMOKE_CONFIRM") != $"{ThreadId}:test")
        {
            Console.WriteLine("SKIP: explicit reply-smoke confirmation is missing");
            return 2;
        }

        var exitCode = 1;
        var completed = false;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/XIsland;component/Themes/Fluent.xaml", UriKind.Absolute)
        });
        var window = new MainWindow { ShowInTaskbar = false, WindowState = WindowState.Minimized, Opacity = 0 };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            Console.WriteLine("FAIL: reply UI workflow timed out");
            window.Close();
            app.Shutdown(exitCode);
        };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Delay(5000);
                var openThread = typeof(MainWindow).GetMethod("OpenThreadAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("OpenThreadAsync");
                await ((Task?)openThread.Invoke(window, [ThreadId, 1]) ?? throw new InvalidOperationException("OpenThreadAsync did not return Task"));

                var replyBox = (TextBox)(window.FindName("ReplyBox") ?? throw new InvalidOperationException("ReplyBox not found"));
                var sendButton = (Button)(window.FindName("SendReplyButton") ?? throw new InvalidOperationException("SendReplyButton not found"));
                replyBox.Text = "test";
                var sendReply = typeof(MainWindow).GetMethod("SendReply_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("SendReply_Click");
                sendReply.Invoke(window, [sendButton, new RoutedEventArgs()]);

                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(400);
                    if (sendButton.IsEnabled && string.IsNullOrEmpty(replyBox.Text))
                    {
                        completed = true;
                        break;
                    }
                }
                if (!completed) throw new TimeoutException("Reply event did not complete");
                await Task.Delay(1500);
                exitCode = 0;
                Console.WriteLine("PASS: reply submitted and thread refreshed without process crash");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: {ex}");
            }
            finally
            {
                timeout.Stop();
                window.Close();
                app.Shutdown(exitCode);
            }
        };
        timeout.Start();
        window.Show();
        app.Run();
        return exitCode;
    }
}
