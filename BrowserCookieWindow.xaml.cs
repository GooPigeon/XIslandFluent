using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace XIslandFluent;

public partial class BrowserCookieWindow : Window
{
    private const string ForumUrl = "https://www.nmbxd1.com/Member";
    private readonly DispatcherTimer _cookieTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _baselineCookie;
    private bool _initialized;
    private bool _captureInProgress;

    public event EventHandler<string>? CookieCaptured;
    public event EventHandler? BrowserReady;

    public BrowserCookieWindow()
    {
        InitializeComponent();
        Loaded += BrowserCookieWindow_Loaded;
        Closed += (_, _) => _cookieTimer.Stop();
        _cookieTimer.Tick += CookieTimer_Tick;
    }

    private async void BrowserCookieWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIslandFluent", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            BrowserReady?.Invoke(this, EventArgs.Empty);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            Browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            Browser.CoreWebView2.NavigationCompleted += Browser_NavigationCompleted;
            Browser.Source = new Uri(ForumUrl);
        }
        catch (Exception ex)
        {
            BrowserStatusText.Text = $"浏览器初始化失败：{ex.Message}";
        }
    }

    private async void Browser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            BrowserStatusText.Text = $"页面加载失败：{e.WebErrorStatus}";
            return;
        }
        BrowserStatusText.Text = "请进入用户系统，领取或选择饼干后点击“应用”";
        if (!_initialized)
        {
            _baselineCookie = await ReadUserHashAsync();
            _initialized = true;
            if (!string.IsNullOrWhiteSpace(_baselineCookie))
                ApplyCookie(_baselineCookie, "已读取并记录用户系统当前饼干");
            _cookieTimer.Start();
        }
    }

    private async void CookieTimer_Tick(object? sender, EventArgs e)
    {
        if (_captureInProgress || !_initialized) return;
        var current = await ReadUserHashAsync();
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, _baselineCookie, StringComparison.Ordinal))
        {
            _baselineCookie = current;
            ApplyCookie(current, "检测到用户系统已应用新饼干，客户端已自动刷新");
        }
    }

    private async void UseCurrentCookie_Click(object sender, RoutedEventArgs e)
    {
        var current = await ReadUserHashAsync();
        if (string.IsNullOrWhiteSpace(current))
        {
            BrowserStatusText.Text = "当前页面没有检测到 userhash，请先在用户系统应用饼干";
            return;
        }
        _baselineCookie = current;
        ApplyCookie(current, "当前饼干已应用到客户端");
    }

    private async Task<string?> ReadUserHashAsync()
    {
        if (Browser.CoreWebView2 is null) return null;
        try
        {
            _captureInProgress = true;
            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.nmbxd1.com/");
            return cookies.LastOrDefault(x => string.Equals(x.Name, "userhash", StringComparison.OrdinalIgnoreCase))?.Value;
        }
        finally { _captureInProgress = false; }
    }

    private void ApplyCookie(string value, string status)
    {
        CookieCaptured?.Invoke(this, $"userhash={value}");
        BrowserStatusText.Text = $"{status}：{value}";
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
