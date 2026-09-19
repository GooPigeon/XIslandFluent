using System.Collections;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using XIslandFluent;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        VerifyCookieParsers();
        var exitCode = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var initialized = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/XIsland;component/Themes/Fluent.xaml", UriKind.Absolute)
        });
        var window = new BrowserCookieWindow { ShowInTaskbar = false, WindowState = WindowState.Minimized, Opacity = 0 };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            exitCode = initialized ? 0 : 1;
            var status = (window.FindName("BrowserStatusText") as System.Windows.Controls.TextBlock)?.Text ?? "状态不可用";
            var path = (window.FindName("Browser") as Microsoft.Web.WebView2.Wpf.WebView2)?.Source?.AbsolutePath ?? "页面不可用";
            Console.WriteLine(initialized
                ? $"PASS WebView2 initialized; automatic cookie sync names={names.Count}; page={path}; status={status}"
                : "FAIL WebView2 initialization timed out");
            window.Close();
            app.Shutdown(exitCode);
        };
        window.BrowserReady += (_, _) =>
        {
            initialized = true;
        };
        window.CookieCaptured += (_, captured) =>
        {
            if (captured.Name is { Length: >= 3 and <= 8 } && captured.Name.All(char.IsAsciiLetterOrDigit))
                names.Add(captured.Name);
        };
        timeout.Start();
        window.Show();
        app.Run();
        return exitCode;
    }

    private static void VerifyCookieParsers()
    {
        var rowsMethod = typeof(BrowserCookieWindow).GetMethod("ParseCookieRows", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("ParseCookieRows");
        const string listHtml = "<table><tbody><tr><td>x</td><td>42</td><td><a>BzKMePb</a></td><td>说明</td></tr></tbody></table>";
        var rows = ((IEnumerable)(rowsMethod.Invoke(null, [listHtml]) ?? throw new InvalidOperationException("列表解析为空"))).Cast<object>().ToList();
        if (rows.Count != 1 || rows[0].GetType().GetProperty("Name")?.GetValue(rows[0])?.ToString() != "BzKMePb")
            throw new InvalidOperationException("用户系统饼干名称解析失败");

        const string credential = "private+cookie/value";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"cookie\":\"{credential}\"}}"));
        var exportHtml = $"<img src=\"/qr?size=200&amp;text={Uri.EscapeDataString(payload)}\">";
        var exportMethod = typeof(BrowserCookieWindow).GetMethod("ParseExportedCredential", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("ParseExportedCredential");
        var parsed = exportMethod.Invoke(null, [exportHtml])?.ToString();
        if (parsed != credential) throw new InvalidOperationException("用户系统饼干导出解析失败");
    }
}
