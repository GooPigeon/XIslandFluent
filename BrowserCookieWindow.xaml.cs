using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using XIslandFluent.Models;

namespace XIslandFluent;

public partial class BrowserCookieWindow : Window
{
    private const string ForumUrl = "https://www.nmbxd1.com/Member";
    private readonly DispatcherTimer _cookieTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _baselineCookie;
    private bool _initialized;
    private bool _captureInProgress;

    private readonly Dictionary<string, string> _cookieNames = new(StringComparer.Ordinal);

    public event EventHandler<CookieCapturedEventArgs>? CookieCaptured;
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
        BrowserStatusText.Text = "正在同步用户系统中的饼干名称…";
        var current = await ReadUserHashAsync();
        var changed = !_initialized || !string.Equals(current, _baselineCookie, StringComparison.Ordinal);
        _baselineCookie = current;
        var syncedCurrent = await SyncUserCookiesAsync(current, changed);
        if (!_initialized)
        {
            _initialized = true;
            _cookieTimer.Start();
        }
        if (changed && !syncedCurrent && !string.IsNullOrWhiteSpace(current))
            ApplyCookie(current, CookieNameFor(current), "已读取并记录用户系统当前饼干");
        BrowserStatusText.Text = "请在用户系统选择饼干；应用后客户端会自动同步";
    }

    private async void CookieTimer_Tick(object? sender, EventArgs e)
    {
        if (_captureInProgress || !_initialized) return;
        var current = await ReadUserHashAsync();
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, _baselineCookie, StringComparison.Ordinal))
        {
            _baselineCookie = current;
            if (!await SyncUserCookiesAsync(current, true))
                ApplyCookie(current, CookieNameFor(current), "检测到用户系统已应用新饼干，客户端已自动刷新");
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
        if (!await SyncUserCookiesAsync(current, true))
            ApplyCookie(current, CookieNameFor(current), "当前饼干已应用到客户端");
        else BrowserStatusText.Text = $"当前饼干已应用到客户端：{CookieNameFor(current)}";
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

    private async Task<bool> SyncUserCookiesAsync(string? current, bool activateCurrent)
    {
        if (Browser.CoreWebView2 is null) return false;
        try
        {
            var rawResult = await Browser.CoreWebView2.ExecuteScriptAsync("""
                (async () => {
                    const response = await fetch('/Member/User/Cookie/index.html', { credentials: 'include', cache: 'no-store' });
                    const html = await response.text();
                    const doc = new DOMParser().parseFromString(html, 'text/html');
                    const rows = [...doc.querySelectorAll('tbody tr')];
                    const results = [];
                    for (const row of rows) {
                        const cells = row.querySelectorAll('td');
                        if (cells.length < 3) continue;
                        const id = cells[1].textContent.trim();
                        const rowName = cells[2].textContent.trim();
                        if (!/^\d+$/.test(id)) continue;
                        try {
                            const exportResponse = await fetch(`/Member/User/Cookie/export/id/${id}.html`, { credentials: 'include', cache: 'no-store' });
                            const exportHtml = await exportResponse.text();
                            const exportDoc = new DOMParser().parseFromString(exportHtml, 'text/html');
                            const image = [...exportDoc.querySelectorAll('[src]')].find(x => /[?&]text=/.test(x.getAttribute('src') || ''));
                            const match = (image?.getAttribute('src') || '').match(/[?&]text=([^&]+)/);
                            if (!match) continue;
                            const encoded = decodeURIComponent(match[1]);
                            const bytes = Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
                            const data = JSON.parse(new TextDecoder().decode(bytes));
                            if (data.cookie) results.push({ cookie: String(data.cookie), name: String(data.name || rowName) });
                        } catch (_) { }
                    }
                    return JSON.stringify(results);
                })()
                """);
            var resultJson = JsonSerializer.Deserialize<string>(rawResult);
            var cookies = string.IsNullOrWhiteSpace(resultJson)
                ? []
                : JsonSerializer.Deserialize<List<ExportedCookie>>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            if (cookies.Count == 0) return false;

            foreach (var item in cookies)
            {
                var value = UserHashValue(item.Cookie);
                if (!string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(item.Name))
                    _cookieNames[value] = item.Name;
            }

            var selected = cookies.FirstOrDefault(x => string.Equals(UserHashValue(x.Cookie), current, StringComparison.Ordinal));
            var remaining = cookies.Where(x => !ReferenceEquals(x, selected)).Take(AppSettings.MaxCookieSlots - (selected is null ? 0 : 1));
            foreach (var item in remaining)
                CookieCaptured?.Invoke(this, new CookieCapturedEventArgs(NormalizeCookieHeader(item.Cookie), item.Name, false));
            if (selected is not null)
                CookieCaptured?.Invoke(this, new CookieCapturedEventArgs(NormalizeCookieHeader(selected.Cookie), selected.Name, activateCurrent));
            return selected is not null;
        }
        catch
        {
            // 登录页或页面结构临时不可用时仍可读取当前 userhash，稍后导航会再次同步名称。
            return false;
        }
    }

    private string CookieNameFor(string value) => _cookieNames.TryGetValue(value, out var name) ? name : "待同步饼干";

    private static string NormalizeCookieHeader(string cookie) => cookie.Contains('=') ? cookie.Trim() : $"userhash={cookie.Trim()}";

    private static string? UserHashValue(string cookie)
    {
        foreach (var part in cookie.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && part[..separator].Equals("userhash", StringComparison.OrdinalIgnoreCase))
                return part[(separator + 1)..];
        }
        return cookie.Contains('=') ? null : cookie.Trim();
    }

    private void ApplyCookie(string value, string name, string status)
    {
        CookieCaptured?.Invoke(this, new CookieCapturedEventArgs($"userhash={value}", name, true));
        BrowserStatusText.Text = $"{status}：{name}";
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed record CookieCapturedEventArgs(string CookieHeader, string Name, bool Activate);

internal sealed record ExportedCookie(string Cookie, string Name);
