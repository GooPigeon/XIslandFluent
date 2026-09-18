using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace XIslandFluent;

public partial class SearchWindow : Window
{
    private const string SearchUrl = "https://nmb-search.166666666.xyz/";
    private static readonly Regex ThreadPath = new(@"/(?:t|thread)/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public event EventHandler<int>? ThreadSelected;

    public SearchWindow()
    {
        InitializeComponent();
        Loaded += SearchWindow_Loaded;
    }

    private async void SearchWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIslandFluent", "SearchWebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.NavigationStarting += Browser_NavigationStarting;
            Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
            Browser.CoreWebView2.NavigationCompleted += (_, args) => StatusText.Text = args.IsSuccess ? "选择一个搜索结果即可在客户端打开" : $"加载失败：{args.WebErrorStatus}";
            Browser.Source = new Uri(SearchUrl);
        }
        catch (Exception ex) { StatusText.Text = $"搜索窗口初始化失败：{ex.Message}"; }
    }

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!TryGetThreadId(e.Uri, out var id)) return;
        e.Cancel = true;
        ThreadSelected?.Invoke(this, id);
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (TryGetThreadId(e.Uri, out var id))
        {
            e.Handled = true;
            ThreadSelected?.Invoke(this, id);
        }
    }

    private static bool TryGetThreadId(string? raw, out int id)
    {
        id = 0;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        var match = ThreadPath.Match(uri.AbsolutePath);
        if (match.Success && int.TryParse(match.Groups["id"].Value, out id)) return true;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || (parts[0] != "id" && parts[0] != "tid")) continue;
            if (int.TryParse(Uri.UnescapeDataString(parts[1]), out id) && id > 0) return true;
        }
        return false;
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
