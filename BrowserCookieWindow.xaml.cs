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
    private bool _batchApplying;

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
        if (_batchApplying) return;
        if (!e.IsSuccess)
        {
            BrowserStatusText.Text = $"页面加载失败：{e.WebErrorStatus}";
            return;
        }
        var current = await ReadUserHashAsync();
        if (!_initialized)
        {
            _baselineCookie = current;
            _initialized = true;
            _cookieTimer.Start();
            BrowserStatusText.Text = "请在网页的饼干列表中点击“应用”";
            return;
        }
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, _baselineCookie, StringComparison.Ordinal))
        {
            _baselineCookie = current;
            var name = await ResolveCurrentCookieNameAsync(current);
            ApplyCookie(current, name, "检测到网页已应用饼干，客户端已记录并启用");
        }
        else BrowserStatusText.Text = "请在网页的饼干列表中点击“应用”";
    }

    private async void CookieTimer_Tick(object? sender, EventArgs e)
    {
        if (_captureInProgress || _batchApplying || !_initialized) return;
        var current = await ReadUserHashAsync();
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, _baselineCookie, StringComparison.Ordinal))
        {
            _baselineCookie = current;
            var name = await ResolveCurrentCookieNameAsync(current);
            ApplyCookie(current, name, "检测到网页已应用饼干，客户端已记录并启用");
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
        var name = await ResolveCurrentCookieNameAsync(current);
        ApplyCookie(current, name, "当前网页饼干已应用到客户端");
    }

    private async void AutoApplyCookies_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null || _batchApplying) return;
        AutoApplyCookiesButton.IsEnabled = false;
        _batchApplying = true;
        var original = await ReadUserHashAsync();
        string? originalId = null;
        string? lastApplied = null;
        CookieListRow? lastRow = null;
        var captured = 0;
        try
        {
            var rows = await LoadCookieRowsAsync();
            if (rows.Count == 0)
            {
                BrowserStatusText.Text = "没有读取到饼干列表，请先登录用户系统";
                return;
            }

            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                BrowserStatusText.Text = $"正在模拟应用 {row.Name}（{index + 1}/{rows.Count}）…";
                if (!await ClickApplyAndWaitAsync(row.Id)) continue;
                var current = await ReadUserHashAsync();
                if (string.IsNullOrWhiteSpace(current)) continue;
                if (!string.IsNullOrWhiteSpace(original) && NormalizeCredential(current) == NormalizeCredential(original)) originalId = row.Id;
                _cookieNames[NormalizeCredential(current)] = row.Name;
                CookieCaptured?.Invoke(this, new CookieCapturedEventArgs(NormalizeCookieHeader(current), row.Name, false));
                lastApplied = current;
                lastRow = row;
                captured++;
            }

            if (!string.IsNullOrWhiteSpace(originalId))
            {
                BrowserStatusText.Text = "正在恢复原先使用的饼干…";
                await ClickApplyAndWaitAsync(originalId);
                var restored = await ReadUserHashAsync();
                var restoredRow = rows.First(x => x.Id == originalId);
                if (!string.IsNullOrWhiteSpace(restored))
                {
                    _baselineCookie = restored;
                    _cookieNames[NormalizeCredential(restored)] = restoredRow.Name;
                    CookieCaptured?.Invoke(this, new CookieCapturedEventArgs(NormalizeCookieHeader(restored), restoredRow.Name, true));
                }
            }
            else if (!string.IsNullOrWhiteSpace(lastApplied) && lastRow is not null)
            {
                _baselineCookie = lastApplied;
                CookieCaptured?.Invoke(this, new CookieCapturedEventArgs(NormalizeCookieHeader(lastApplied), lastRow.Name, true));
            }
            BrowserStatusText.Text = $"已通过网页应用按钮记录 {captured} 个饼干";
        }
        catch (Exception ex)
        {
            BrowserStatusText.Text = $"自动应用失败：{ex.Message}";
        }
        finally
        {
            _batchApplying = false;
            AutoApplyCookiesButton.IsEnabled = true;
        }
    }

    private async Task<List<CookieListRow>> LoadCookieRowsAsync()
    {
        if (Browser.CoreWebView2 is null) return [];
        var sessionCookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.nmbxd1.com/");
        var cookieHeader = string.Join("; ", sessionCookies.Select(x => $"{x.Name}={x.Value}"));
        if (string.IsNullOrWhiteSpace(cookieHeader)) return [];
        using var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.nmbxd1.com/") };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "XIslandFluent/1.7.0");
        var html = await client.GetStringAsync("Member/User/Cookie/index.html");
        return ParseCookieRows(html);
    }

    private async Task<bool> ClickApplyAndWaitAsync(string id)
    {
        if (Browser.CoreWebView2 is null) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? _, CoreWebView2NavigationCompletedEventArgs args) => completion.TrySetResult(args.IsSuccess);
        Browser.CoreWebView2.NavigationCompleted += Completed;
        try
        {
            Browser.CoreWebView2.Navigate($"https://www.nmbxd1.com/Member/User/Cookie/switchTo/id/{Uri.EscapeDataString(id)}.html");
            var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            return finished == completion.Task && await completion.Task;
        }
        finally { Browser.CoreWebView2.NavigationCompleted -= Completed; }
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

    private async Task<string> ResolveCurrentCookieNameAsync(string current)
    {
        if (Browser.CoreWebView2 is null) return "待同步饼干";
        var normalizedCurrent = NormalizeCredential(current);
        if (_cookieNames.TryGetValue(normalizedCurrent, out var cachedName)) return cachedName;
        try
        {
            var sessionCookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.nmbxd1.com/");
            var cookieHeader = string.Join("; ", sessionCookies.Select(x => $"{x.Name}={x.Value}"));
            if (string.IsNullOrWhiteSpace(cookieHeader)) return "待同步饼干";
            using var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All };
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.nmbxd1.com/") };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "XIslandFluent/1.7.0");
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

            foreach (var item in cookies)
            {
                var value = item.Cookie;
                if (!string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(item.Name))
                    _cookieNames[NormalizeCredential(value)] = item.Name;
            }
            return _cookieNames.TryGetValue(normalizedCurrent, out var name) ? name : "待同步饼干";
        }
        catch
        {
            return "待同步饼干";
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
