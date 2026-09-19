using System.Collections;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using XIslandFluent;
using XIslandFluent.Models;
using XIslandFluent.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        VerifyCookieParsers();
        var exitCode = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var initialized = false;
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        var validating = false;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/XIsland;component/Themes/Fluent.xaml", UriKind.Absolute)
        });
        var window = new BrowserCookieWindow { ShowInTaskbar = false, WindowState = WindowState.Minimized, Opacity = 0 };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        var monitor = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        void Finish(int code, string message)
        {
            exitCode = code;
            timeout.Stop();
            monitor.Stop();
            Console.WriteLine(message);
            window.Close();
            app.Shutdown(exitCode);
        }
        timeout.Tick += (_, _) =>
        {
            var status = (window.FindName("BrowserStatusText") as System.Windows.Controls.TextBlock)?.Text ?? "状态不可用";
            var path = (window.FindName("Browser") as Microsoft.Web.WebView2.Wpf.WebView2)?.Source?.AbsolutePath ?? "页面不可用";
            Finish(1, initialized
                ? $"FAIL WebView2 cookie export timed out; names={cookies.Count}; page={path}; status={status}"
                : "FAIL WebView2 initialization timed out");
        };
        monitor.Tick += async (_, _) =>
        {
            var status = (window.FindName("BrowserStatusText") as System.Windows.Controls.TextBlock)?.Text ?? "";
            if (!validating && status.StartsWith("已自动获取并记录", StringComparison.Ordinal))
            {
                validating = true;
                monitor.Stop();
                var invalid = new List<string>();
                foreach (var (name, cookie) in cookies)
                {
                    try
                    {
                        using var api = new XIslandApiService(new AppSettings { Cookie = cookie });
                        await api.ValidateCookieAsync();
                    }
                    catch (Exception ex) { invalid.Add($"{name}({ex.Message})"); }
                }
                Finish(cookies.Count > 0 && invalid.Count == 0 ? 0 : 1,
                    invalid.Count == 0
                        ? $"PASS WebView2 exported cookies validated names={cookies.Count}; status={status}"
                        : $"FAIL exported cookies invalid names={string.Join(',', invalid)}; captured={cookies.Count}");
            }
            else if (status.StartsWith("没有读取到饼干列表", StringComparison.Ordinal))
                Finish(0, $"PASS WebView2 initialized; apply simulation skipped because user system is logged out; status={status}");
            else if (status.StartsWith("自动获取失败", StringComparison.Ordinal))
                Finish(1, $"FAIL {status}");
        };
        window.BrowserReady += async (_, _) =>
        {
            initialized = true;
            await Task.Delay(4000);
            if (window.FindName("AutoApplyCookiesButton") is System.Windows.Controls.Button button)
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        };
        window.CookieCaptured += (_, captured) =>
        {
            if (captured.Name is { Length: >= 3 and <= 8 } && captured.Name.All(char.IsAsciiLetterOrDigit))
                cookies[captured.Name] = captured.CookieHeader;
        };
        timeout.Start();
        monitor.Start();
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

        const string percentCredential = "%A4Oz%F7*g%FB%80%F8Ad%B7@%EBI%8C()%F5";
        var normalizeMethod = typeof(BrowserCookieWindow).GetMethod("NormalizeCookieHeader", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("NormalizeCookieHeader");
        var normalized = normalizeMethod.Invoke(null, [percentCredential])?.ToString();
        if (normalized != $"userhash={percentCredential}")
            throw new InvalidOperationException("用户系统饼干凭据被二次编码");
    }
}
