using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private int _lastSyncedCount;

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
        BrowserStatusText.Text = _lastSyncedCount > 0
            ? $"已自动获取 {_lastSyncedCount} 个饼干；以后新增后可再次自动获取"
            : "请先登录用户系统，登录后会自动获取全部饼干";
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

    private async void SyncAllCookies_Click(object sender, RoutedEventArgs e)
    {
        SyncAllCookiesButton.IsEnabled = false;
        BrowserStatusText.Text = "正在从用户系统自动获取全部饼干…";
        try
        {
            var current = await ReadUserHashAsync();
            var matched = await SyncUserCookiesAsync(current, false);
            BrowserStatusText.Text = _lastSyncedCount > 0
                ? $"已自动获取并记录 {_lastSyncedCount} 个不同饼干"
                : "尚未读取到饼干列表，请先登录用户系统后重试";
            if (matched && !string.IsNullOrWhiteSpace(current)) _baselineCookie = current;
        }
        finally { SyncAllCookiesButton.IsEnabled = true; }
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
            var sessionCookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.nmbxd1.com/");
            var cookieHeader = string.Join("; ", sessionCookies.Select(x => $"{x.Name}={x.Value}"));
            if (string.IsNullOrWhiteSpace(cookieHeader)) return false;
            using var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All };
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.nmbxd1.com/") };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "XIslandFluent/1.6.2");
            var indexHtml = await client.GetStringAsync("Member/User/Cookie/index.html");
            var rows = ParseCookieRows(indexHtml);
            var exported = await Task.WhenAll(rows.Select(async row =>
            {
                try
                {
                    var html = await client.GetStringAsync($"Member/User/Cookie/export/id/{Uri.EscapeDataString(row.Id)}.html");
                    var credential = ParseExportedCredential(html);
                    return string.IsNullOrWhiteSpace(credential) ? null : new ExportedCookie(credential, row.Name);
                }
                catch { return null; }
            }));
            var cookies = exported.OfType<ExportedCookie>().ToList();
            if (cookies.Count == 0) return false;
            _lastSyncedCount = cookies.Count;

            foreach (var item in cookies)
            {
                var value = item.Cookie;
                if (!string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(item.Name))
                    _cookieNames[NormalizeCredential(value)] = item.Name;
            }

            var normalizedCurrent = NormalizeCredential(current);
            var selected = cookies.FirstOrDefault(x => string.Equals(NormalizeCredential(x.Cookie), normalizedCurrent, StringComparison.Ordinal));
            var remaining = cookies.Where(x => !ReferenceEquals(x, selected));
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

    private static List<CookieListRow> ParseCookieRows(string html)
    {
        var tbody = Regex.Match(html, @"<tbody\b[^>]*>(?<body>.*?)</tbody>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!tbody.Success) return [];
        var result = new List<CookieListRow>();
        foreach (Match row in Regex.Matches(tbody.Groups["body"].Value, @"<tr\b[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var cells = Regex.Matches(row.Groups["row"].Value, @"<td\b[^>]*>(?<cell>.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (cells.Count < 4) continue;
            var id = PlainText(cells[1].Groups["cell"].Value);
            var name = PlainText(cells[2].Groups["cell"].Value);
            if (id.All(char.IsDigit) && name is { Length: >= 3 and <= 8 } && name.All(char.IsAsciiLetterOrDigit))
                result.Add(new CookieListRow(id, name));
        }
        return result;
    }

    private static string? ParseExportedCredential(string html)
    {
        var source = Regex.Match(html, @"\bsrc\s*=\s*['""](?<src>[^'""]*text=[^'""]+)['""]", RegexOptions.IgnoreCase).Groups["src"].Value;
        source = WebUtility.HtmlDecode(source);
        var text = Regex.Match(source, @"[?&]text=(?<text>[^&]+)", RegexOptions.IgnoreCase).Groups["text"].Value;
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(text)));
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("cookie", out var value) ? value.GetString() : null;
        }
        catch { return null; }
    }

    private static string PlainText(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Replace("\u00a0", " ").Trim();

    private string CookieNameFor(string value) => _cookieNames.TryGetValue(NormalizeCredential(value), out var name) ? name : "待同步饼干";

    private static string NormalizeCookieHeader(string cookie)
    {
        var credential = UserHashValue(cookie) ?? cookie.Trim();
        return $"userhash={Uri.EscapeDataString(NormalizeCredential(credential))}";
    }

    private static string NormalizeCredential(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return Uri.UnescapeDataString(value.Trim()); }
        catch { return value.Trim(); }
    }

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
        CookieCaptured?.Invoke(this, new CookieCapturedEventArgs(NormalizeCookieHeader(value), name, true));
        BrowserStatusText.Text = $"{status}：{name}";
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed record CookieCapturedEventArgs(string CookieHeader, string Name, bool Activate);

internal sealed record ExportedCookie(string Cookie, string Name);

internal sealed record CookieListRow(string Id, string Name);
