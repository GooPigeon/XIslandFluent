using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XIslandFluent;

internal static class Program
{
    private const int ThreadId = 69424935;

    [STAThread]
    private static int Main()
    {
        var exitCode = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/XIsland;component/Themes/Fluent.xaml", UriKind.Absolute)
        });
        var window = new MainWindow { ShowInTaskbar = false, WindowState = WindowState.Minimized, Opacity = 0 };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            Console.WriteLine("FAIL navigation workflow timed out");
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
                var task = (Task?)openThread.Invoke(window, [ThreadId, 1]) ?? throw new InvalidOperationException("OpenThreadAsync did not return Task");
                var replies = (ItemsControl)(window.FindName("ReplyItems") ?? throw new InvalidOperationException("ReplyItems not found"));
                var overlay = (Border)(window.FindName("ThreadLoadingOverlay") ?? throw new InvalidOperationException("ThreadLoadingOverlay not found"));
                if (replies.ItemsSource is not null || overlay.Visibility != Visibility.Visible)
                    throw new InvalidOperationException("旧串未清除或加载层未显示");
                await task;
                if (overlay.Visibility != Visibility.Collapsed || replies.Items.Count == 0)
                    throw new InvalidOperationException("新串加载完成状态不正确");
                var firstPageCount = replies.Items.Count;
                var loadNext = typeof(MainWindow).GetMethod("LoadNextThreadPageAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("LoadNextThreadPageAsync");
                await ((Task?)loadNext.Invoke(window, null) ?? throw new InvalidOperationException("LoadNextThreadPageAsync did not return Task"));
                if (replies.Items.Count <= firstPageCount)
                    throw new InvalidOperationException("滚动续页没有追加新回复");
                var dividerFound = replies.Items.Cast<XIslandFluent.Models.Post>().Any(x => x.ShowPageDivider);
                if (!dividerFound) throw new InvalidOperationException("续页分隔线标记缺失");

                var onlyPo = (CheckBox)(window.FindName("OnlyPoCheck") ?? throw new InvalidOperationException("OnlyPoCheck not found"));
                onlyPo.IsChecked = true;
                if (replies.Items.Cast<XIslandFluent.Models.Post>().Any(x => !x.IsPo))
                    throw new InvalidOperationException("只看 PO 未进行本地筛选");
                onlyPo.IsChecked = false;
                if (replies.Items.Count <= firstPageCount)
                    throw new InvalidOperationException("取消只看 PO 后未恢复已加载回复");
                var appendedCount = replies.Items.Count;
                onlyPo.IsChecked = true;
                await ((Task?)openThread.Invoke(window, [69466323, 1]) ?? throw new InvalidOperationException("second OpenThreadAsync did not return Task"));
                if (onlyPo.IsChecked == true)
                    throw new InvalidOperationException("只看 PO 状态被带到另一串");
                exitCode = 0;
                Console.WriteLine($"PASS navigation thread={ThreadId} firstPage={firstPageCount} appended={appendedCount} poReset=true");
            }
            catch (Exception ex) { Console.WriteLine($"FAIL {ex}"); }
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
