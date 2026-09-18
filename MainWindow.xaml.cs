using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using XIslandFluent.Models;
using XIslandFluent.Services;

namespace XIslandFluent;

public partial class MainWindow : Window
{
    private enum ViewMode { Timeline, Board, Feed, Thread, OfflineThread, Offline, Settings }
    private readonly SettingsService _settingsService = new();
    private readonly OfflineCacheService _offlineCache = new();
    private readonly ObservableCollection<ForumCategory> _forumCategories = [];
    private readonly ObservableCollection<TimelineInfo> _timelines = [];
    private AppSettings _settings;
    private XIslandApiService _api;
    private ViewMode _mode = ViewMode.Timeline;
    private ViewMode _returnMode = ViewMode.Timeline;
    private int _page = 1;
    private int _currentForumId;
    private int _currentTimelineId = 1;
    private int _currentThreadId;
    private string? _replyImagePath;
    private ForumInfo? _selectedForum;
    private bool _loaded;
    private bool _replyInProgress;
    private List<Post> _currentThreadPosts = [];
    private string _currentPageTitle = "时间线";

    public ObservableCollection<ForumCategory> ForumCategories => _forumCategories;

    public MainWindow()
    {
        _settings = _settingsService.Load();
        _api = new XIslandApiService(_settings);
        InitializeComponent();
        DataContext = this;
        Loaded += MainWindow_Loaded;
        SourceInitialized += (_, _) => ApplyWindowBackdrop();
        StateChanged += (_, _) => ApplyWindowBackdrop();
        Closing += (_, _) => _api.Dispose();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        LoadSettingsIntoControls();
        ApplyTheme(_settings.Theme);
        await RunBusyAsync("正在连接 X 岛…", async () =>
        {
            await _api.InitializeAsync();
            var forumTask = _api.GetForumsAsync();
            var timelineTask = _api.GetTimelinesAsync();
            await Task.WhenAll(forumTask, timelineTask);
            _forumCategories.Clear();
            foreach (var category in forumTask.Result) _forumCategories.Add(category);
            _timelines.Clear();
            foreach (var timeline in timelineTask.Result) _timelines.Add(timeline);
            TimelinePicker.ItemsSource = _timelines;
            TimelinePicker.SelectedItem = _timelines.FirstOrDefault();
        });
        await ShowTimelineAsync(resetPage: true);
    }

    private async Task ShowTimelineAsync(bool resetPage)
    {
        if (resetPage) _page = 1;
        _mode = ViewMode.Timeline;
        ShowPanel(ListViewPanel);
        BackButton.Visibility = Visibility.Collapsed;
        NewThreadButton.Visibility = Visibility.Visible;
        var selected = _timelines.FirstOrDefault(x => x.Id == _currentTimelineId) ?? _timelines.FirstOrDefault();
        if (selected is not null)
        {
            _currentTimelineId = selected.Id;
            if (!ReferenceEquals(TimelinePicker.SelectedItem, selected)) TimelinePicker.SelectedItem = selected;
            SetHeader(selected.ToString(), string.IsNullOrWhiteSpace(selected.NoticeHtml) ? "来自全岛的最新内容" : HtmlText.Clean(selected.NoticeHtml));
        }
        await LoadThreadListAsync(() => _api.GetTimelineAsync(_currentTimelineId, _page));
    }

    private async Task ShowBoardAsync(ForumInfo forum, bool resetPage)
    {
        if (resetPage) _page = 1;
        _selectedForum = forum;
        _currentForumId = forum.NumericId;
        _mode = ViewMode.Board;
        ShowPanel(ListViewPanel);
        BackButton.Visibility = Visibility.Visible;
        NewThreadButton.Visibility = Visibility.Visible;
        SetHeader(forum.Name, string.IsNullOrWhiteSpace(forum.CleanDescription) ? $"板块 #{forum.Id}" : forum.CleanDescription);
        await LoadThreadListAsync(() => _api.GetBoardAsync(_currentForumId, _page));
    }

    private async Task ShowFeedAsync(bool resetPage)
    {
        if (resetPage) _page = 1;
        _mode = ViewMode.Feed;
        ShowPanel(ListViewPanel);
        BackButton.Visibility = Visibility.Collapsed;
        NewThreadButton.Visibility = Visibility.Visible;
        SetHeader("我的订阅", "使用本机订阅标识同步的串");
        await LoadThreadListAsync(async () => (await _api.GetFeedAsync(_settings.SubscriptionId, _page)).Cast<Post>().ToList());
    }

    private async Task LoadThreadListAsync(Func<Task<List<Post>>> loader)
    {
        await RunBusyAsync("正在加载内容…", async () =>
        {
            var posts = await loader();
            PrepareImages(posts);
            ThreadItems.ItemsSource = posts;
            PageNumberText.Text = $"第 {_page} 页";
            if (posts.Count == 0) ShowTransientStatus("这一页暂时没有内容");
        });
    }

    private async Task OpenThreadAsync(int threadId, int page = 1)
    {
        if (_mode != ViewMode.Thread) _returnMode = _mode;
        _currentThreadId = threadId;
        _page = page;
        _mode = ViewMode.Thread;
        ShowPanel(ThreadPanel);
        BackButton.Visibility = Visibility.Visible;
        NewThreadButton.Visibility = Visibility.Visible;
        SetHeader($"No.{threadId}", "串内容与回复");
        ReplyItems.ItemsSource = null;
        _currentThreadPosts = [];
        ReplyComposer.Visibility = Visibility.Collapsed;
        ThreadLoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            await RunBusyAsync("正在加载串…", async () =>
            {
                var thread = await _api.GetThreadAsync(threadId, _page, OnlyPoCheck.IsChecked == true);
                var posts = new List<Post> { thread };
                posts.AddRange(thread.Replies ?? []);
                PrepareImages(posts);
                _currentThreadPosts = posts;
                ReplyItems.ItemsSource = posts;
                PageContextText.Text = $"{thread.ReplyCount} 条回复 · 第 {_page} 页";
                PageNumberText.Text = $"第 {_page} 页";
            });
        }
        finally
        {
            ThreadLoadingOverlay.Visibility = Visibility.Collapsed;
            SetThreadOnlineState(true);
        }
    }

    private void PrepareImages(IEnumerable<Post> posts)
    {
        var pending = new Stack<Post>(posts.Reverse());
        var visited = new HashSet<Post>(ReferenceEqualityComparer.Instance);
        while (pending.Count > 0)
        {
            var post = pending.Pop();
            if (!visited.Add(post)) continue;
            post.ImageUrl = _api.GetImageUrl(post);
            post.ThumbnailUrl = _api.GetImageUrl(post, true);
            if (post.Replies is null) continue;
            foreach (var reply in post.Replies) pending.Push(reply);
        }
    }

    private async Task RunBusyAsync(string text, Func<Task> action)
    {
        StatusText.Text = text;
        StatusBar.Visibility = Visibility.Visible;
        BusyIndicator.Visibility = Visibility.Visible;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"RunBusyAsync failed: {text}", ex);
            StatusText.Text = ex.Message;
            BusyIndicator.Visibility = Visibility.Collapsed;
            return;
        }
        StatusBar.Visibility = Visibility.Collapsed;
    }

    private async Task<T?> RunBusyAsync<T>(string text, Func<Task<T>> action)
    {
        T? result = default;
        await RunBusyAsync(text, async () =>
        {
            result = await action();
        });
        return result;
    }

    private async void FeedNav_Click(object sender, RoutedEventArgs e) => await ShowFeedAsync(true);

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        _mode = ViewMode.Settings;
        ShowPanel(SettingsPanel);
        BackButton.Visibility = Visibility.Collapsed;
        NewThreadButton.Visibility = Visibility.Collapsed;
        SetHeader("设置", "管理身份、订阅同步和外观");
        LoadSettingsIntoControls();
    }

    private void OfflineNav_Click(object sender, RoutedEventArgs e) => ShowOfflineLibrary();

    private void ShowOfflineLibrary()
    {
        _mode = ViewMode.Offline;
        ShowPanel(OfflinePanel);
        BackButton.Visibility = Visibility.Collapsed;
        NewThreadButton.Visibility = Visibility.Collapsed;
        SetHeader("离线缓存", "可离线阅读，也可导入或导出缓存包");
        RefreshOfflineList();
    }

    private void ShowPanel(UIElement selected)
    {
        foreach (var panel in new UIElement[] { ListViewPanel, ThreadPanel, OfflinePanel, SettingsPanel })
            panel.Visibility = ReferenceEquals(panel, selected) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetHeader(string title, string subtitle)
    {
        _currentPageTitle = title;
        Title = title == "时间线" ? "X 岛" : $"X 岛 · {title}";
        PageContextText.Text = subtitle.Replace("\r", " ").Replace("\n", " · ");
    }

    private async void ThreadCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: int id }) await OpenThreadAsync(id);
    }

    private async void ForumTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        DependencyObject? current = source;
        while (current is not null && current is not TreeViewItem) current = VisualTreeHelper.GetParent(current);
        if (current is not TreeViewItem { DataContext: ForumInfo { NumericId: > 0 } forum }) return;
        e.Handled = true;
        _selectedForum = forum;
        await ShowBoardAsync(forum, true);
    }

    private async void TimelinePicker_DropDownClosed(object sender, EventArgs e)
    {
        if (!_loaded || TimelinePicker.SelectedItem is not TimelineInfo timeline) return;
        _currentTimelineId = timeline.Id;
        await ShowTimelineAsync(true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshCurrentAsync();

    private async Task RefreshCurrentAsync()
    {
        switch (_mode)
        {
            case ViewMode.Timeline: await ShowTimelineAsync(false); break;
            case ViewMode.Board when _selectedForum is not null: await ShowBoardAsync(_selectedForum, false); break;
            case ViewMode.Feed: await ShowFeedAsync(false); break;
            case ViewMode.Thread: await OpenThreadAsync(_currentThreadId, _page); break;
        }
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 1) return;
        _page--;
        await RefreshCurrentAsync();
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        await RefreshCurrentAsync();
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == ViewMode.Thread)
        {
            switch (_returnMode)
            {
                case ViewMode.Board when _selectedForum is not null: await ShowBoardAsync(_selectedForum, false); break;
                case ViewMode.Feed: await ShowFeedAsync(false); break;
                default: await ShowTimelineAsync(false); break;
            }
        }
        else if (_mode == ViewMode.OfflineThread) ShowOfflineLibrary();
        else await ShowTimelineAsync(false);
    }

    private async void OnlyPoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_mode == ViewMode.Thread) await OpenThreadAsync(_currentThreadId, 1);
    }

    private void ChooseReplyImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择图片", Filter = "图片|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp|所有文件|*.*" };
        if (picker.ShowDialog(this) == true)
        {
            _replyImagePath = picker.FileName;
            ShowTransientStatus($"已选择图片：{System.IO.Path.GetFileName(_replyImagePath)}");
        }
    }

    private async void SendReply_Click(object sender, RoutedEventArgs e)
    {
        if (_replyInProgress) return;
        if (string.IsNullOrWhiteSpace(ReplyBox.Text) && string.IsNullOrWhiteSpace(_replyImagePath))
        {
            ShowTransientStatus("回复内容和图片不能同时为空");
            return;
        }
        _replyInProgress = true;
        SendReplyButton.IsEnabled = false;
        var content = ReplyBox.Text.Trim();
        try
        {
            DiagnosticsLog.Write($"Reply begin thread={_currentThreadId} contentLength={content.Length} hasImage={!string.IsNullOrWhiteSpace(_replyImagePath)}");
            var result = await RunBusyAsync("正在发送回复…", () => _api.ReplyAsync(_currentThreadId, content, _replyImagePath, _settings.WatermarkImages));
            if (result is null) return;
            DiagnosticsLog.Write($"Reply accepted thread={_currentThreadId}; refreshing page={_page}");
            ReplyBox.Clear();
            _replyImagePath = null;
            ShowTransientStatus(string.IsNullOrWhiteSpace(result) ? "回复已发送" : result);
            await OpenThreadAsync(_currentThreadId, _page);
            DiagnosticsLog.Write($"Reply refresh complete thread={_currentThreadId}");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"Reply UI failed thread={_currentThreadId}", ex);
            ShowTransientStatus($"回复失败：{ex.Message}");
        }
        finally
        {
            _replyInProgress = false;
            SendReplyButton.IsEnabled = true;
        }
    }

    private void Quote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Post post })
        {
            ReplyBox.Text = $">>No.{post.Id}\n{ReplyBox.Text}";
            ReplyBox.Focus();
            ReplyBox.CaretIndex = ReplyBox.Text.Length;
        }
    }

    private async void Reference_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;
        var post = await RunBusyAsync("正在查看引用…", () => _api.GetReferenceAsync(id));
        if (post is not null) MessageBox.Show(this, $"No.{post.Id} · {post.Author}\n{post.Time}\n\n{post.CleanContent}", "引用内容", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void SubscribeCurrent_Click(object sender, RoutedEventArgs e)
    {
        var result = await RunBusyAsync("正在添加订阅…", () => _api.AddFeedAsync(_settings.SubscriptionId, _currentThreadId));
        if (result is not null) ShowTransientStatus(string.IsNullOrWhiteSpace(result) ? "订阅成功" : result);
    }

    private async void UnsubscribeCurrent_Click(object sender, RoutedEventArgs e)
    {
        var result = await RunBusyAsync("正在取消订阅…", () => _api.DeleteFeedAsync(_settings.SubscriptionId, _currentThreadId));
        if (result is not null) ShowTransientStatus(string.IsNullOrWhiteSpace(result) ? "已取消订阅" : result);
    }

    private async void NewThread_Click(object sender, RoutedEventArgs e)
    {
        var forums = _forumCategories.SelectMany(x => x.Forums).Where(x => x.NumericId > 0).ToList();
        var dialog = new ComposerWindow(forums, _selectedForum, _settings.WatermarkImages) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedForum is null) return;
        var result = await RunBusyAsync("正在发表新串…", () => _api.CreateThreadAsync(dialog.SelectedForum.NumericId, dialog.PostTitle, dialog.PostContent, dialog.ImagePath, dialog.AddWatermark));
        if (result is not null)
        {
            ShowTransientStatus(string.IsNullOrWhiteSpace(result) ? "发表成功" : result);
            await ShowBoardAsync(dialog.SelectedForum, true);
        }
    }

    private async void QuickOpenBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var raw = QuickOpenBox.Text.Trim().Replace("No.", "", StringComparison.OrdinalIgnoreCase).Replace(">>", "");
        if (int.TryParse(raw, out var id) && id > 0) await OpenThreadAsync(id);
        else ShowTransientStatus("请输入有效串号，例如 50000002");
    }

    private void QuickOpenBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (QuickOpenHint is not null)
            QuickOpenHint.Visibility = string.IsNullOrEmpty(QuickOpenBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        var search = new SearchWindow { Owner = this };
        search.ThreadSelected += async (_, id) =>
        {
            search.Close();
            await OpenThreadAsync(id);
        };
        search.ShowDialog();
    }

    private async void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Post post } || string.IsNullOrWhiteSpace(post.ImageUrl)) return;
        var extension = string.IsNullOrWhiteSpace(post.ImageExtension) ? ".jpg" : post.ImageExtension;
        if (!extension.StartsWith('.')) extension = "." + extension;
        var picker = new SaveFileDialog
        {
            Title = "保存图片",
            FileName = $"No.{post.Id}{extension}",
            Filter = $"图片 (*{extension})|*{extension}|所有文件|*.*"
        };
        if (picker.ShowDialog(this) != true) return;
        await RunBusyAsync("正在保存图片…", async () =>
        {
            var bytes = await _api.DownloadImageAsync(post.ImageUrl);
            await File.WriteAllBytesAsync(picker.FileName, bytes);
            ShowTransientStatus($"图片已保存到 {picker.FileName}");
        });
    }

    private async void Image_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: Post post } || string.IsNullOrWhiteSpace(post.ImageUrl)) return;
        try
        {
            var extension = string.IsNullOrWhiteSpace(post.ImageExtension) ? ".jpg" : post.ImageExtension;
            if (!extension.StartsWith('.')) extension = "." + extension;
            if (extension.Length > 8 || extension.Any(x => !char.IsLetterOrDigit(x) && x != '.')) extension = ".jpg";
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIslandFluent", "ImageViewer");
            Directory.CreateDirectory(folder);
            var imagePath = Path.Combine(folder, $"No.{post.Id}{extension.ToLowerInvariant()}");
            if (!File.Exists(imagePath))
            {
                var bytes = await _api.DownloadImageAsync(post.ImageUrl);
                await File.WriteAllBytesAsync(imagePath, bytes);
            }
            Process.Start(new ProcessStartInfo(imagePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"Open image failed post={post.Id}", ex);
            ShowTransientStatus($"无法打开图片：{ex.Message}");
        }
    }

    private async void CacheCurrentThread_Click(object sender, RoutedEventArgs e)
    {
        if (_currentThreadId <= 0 || _currentThreadPosts.Count == 0) return;
        await RunBusyAsync("正在缓存串与图片…", async () =>
        {
            await _offlineCache.SaveAsync(_currentThreadId, _currentPageTitle, _currentThreadPosts, url => _api.DownloadImageAsync(url));
            ShowTransientStatus($"No.{_currentThreadId} 已保存到离线缓存");
        });
    }

    private void RefreshOffline_Click(object sender, RoutedEventArgs e) => RefreshOfflineList();

    private void RefreshOfflineList()
    {
        OfflineItems.ItemsSource = _offlineCache.List();
        if (OfflineItems.Items.Count > 0) OfflineItems.SelectedIndex = 0;
    }

    private async void ImportOffline_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "导入串缓存", Filter = "X 岛离线串|*.xthread|所有文件|*.*" };
        if (picker.ShowDialog(this) != true) return;
        await RunBusyAsync("正在导入离线缓存…", async () =>
        {
            var info = await _offlineCache.ImportAsync(picker.FileName);
            RefreshOfflineList();
            ShowTransientStatus($"已导入 No.{info.ThreadId}");
        });
    }

    private void ExportOffline_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineItems.SelectedItem is not OfflineThreadInfo info)
        {
            ShowTransientStatus("请先选择一个离线串");
            return;
        }
        var picker = new SaveFileDialog { Title = "导出串缓存", FileName = $"No.{info.ThreadId}.xthread", Filter = "X 岛离线串|*.xthread" };
        if (picker.ShowDialog(this) != true) return;
        File.Copy(info.FilePath, picker.FileName, true);
        ShowTransientStatus($"缓存包已导出到 {picker.FileName}");
    }

    private async void OpenOffline_Click(object sender, RoutedEventArgs e)
    {
        if (OfflineItems.SelectedItem is not OfflineThreadInfo info)
        {
            ShowTransientStatus("请先选择一个离线串");
            return;
        }
        await OpenOfflineThreadAsync(info);
    }

    private async Task OpenOfflineThreadAsync(OfflineThreadInfo info)
    {
        var package = await RunBusyAsync("正在打开离线串…", () => _offlineCache.LoadAsync(info.FilePath));
        if (package is null) return;
        _currentThreadId = package.ThreadId;
        _currentThreadPosts = package.Posts;
        _page = 1;
        _mode = ViewMode.OfflineThread;
        ShowPanel(ThreadPanel);
        BackButton.Visibility = Visibility.Visible;
        NewThreadButton.Visibility = Visibility.Collapsed;
        SetHeader(string.IsNullOrWhiteSpace(package.Title) ? $"No.{package.ThreadId}" : package.Title, $"离线缓存 · {package.CachedAt.LocalDateTime:yyyy-MM-dd HH:mm}");
        ReplyItems.ItemsSource = package.Posts;
        PageNumberText.Text = "离线";
        SetThreadOnlineState(false);
    }

    private void SetThreadOnlineState(bool online)
    {
        SubscribeButton.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        UnsubscribeButton.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        CacheThreadButton.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        ReplyComposer.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        OnlyPoCheck.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        OfflineModeText.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.SubscriptionId = SubscriptionIdBox.Text.Trim();
        _settings.Cookie = CookieBox.Password.Trim();
        _settings.Theme = (ThemePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";
        _settings.WatermarkImages = WatermarkCheck.IsChecked == true;
        if (string.IsNullOrWhiteSpace(_settings.SubscriptionId)) _settings.SubscriptionId = Guid.NewGuid().ToString();
        _settingsService.Save(_settings);
        _api.UpdateSettings(_settings);
        ApplyTheme(_settings.Theme);
        LoadSettingsIntoControls();
        ShowTransientStatus("设置已保存到本机");
    }

    private void LoadSettingsIntoControls()
    {
        if (SubscriptionIdBox is null) return;
        SubscriptionIdBox.Text = _settings.SubscriptionId;
        CookieBox.Password = _settings.Cookie;
        CookieStatusText.Text = string.IsNullOrWhiteSpace(_settings.Cookie) ? "尚未获取" : "已保存饼干";
        WatermarkCheck.IsChecked = _settings.WatermarkImages;
        ThemePicker.SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
    }

    private void ShowTransientStatus(string message)
    {
        StatusText.Text = message;
        BusyIndicator.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Visible;
    }

    private void ImportCookie_Click(object sender, RoutedEventArgs e)
    {
        var browser = new BrowserCookieWindow { Owner = this };
        browser.CookieCaptured += async (_, cookieHeader) =>
        {
            _settings.Cookie = cookieHeader;
            _settingsService.Save(_settings);
            _api.UpdateSettings(_settings);
            CookieBox.Password = cookieHeader;
            CookieStatusText.Text = "饼干已自动更新";
            ShowTransientStatus("已从用户系统更新饼干，后续请求将立即使用新饼干");
            await RefreshCurrentAsync();
        };
        browser.ShowDialog();
    }

    private void ApplyTheme(string theme)
    {
        var useDark = theme == "Dark" || (theme == "System" && IsSystemDarkMode());
        var resources = Application.Current.Resources;
        resources["TextColor"] = (Color)ColorConverter.ConvertFromString(useDark ? "#FFFFFF" : "#1A1A1A");
        resources["SubtleTextColor"] = (Color)ColorConverter.ConvertFromString(useDark ? "#CFCFCF" : "#616161");
        resources["SurfaceColor"] = (Color)ColorConverter.ConvertFromString(useDark ? "#202020" : "#F9F9F9");
        resources["CardColor"] = (Color)ColorConverter.ConvertFromString(useDark ? "#2B2B2B" : "#FDFDFD");
        resources["LayerColor"] = (Color)ColorConverter.ConvertFromString(useDark ? "#272727" : "#F3F3F3");
        resources["StrokeColor"] = (Color)ColorConverter.ConvertFromString(useDark ? "#28FFFFFF" : "#16000000");
        resources["HoverColor"] = (Color)ColorConverter.ConvertFromString(useDark ? "#12FFFFFF" : "#0A000000");
        resources["PressedColor"] = (Color)ColorConverter.ConvertFromString(useDark ? "#18FFFFFF" : "#0F000000");
        ApplyWindowBackdrop();
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    private void ApplyWindowBackdrop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = _settings.Theme == "Dark" || (_settings.Theme == "System" && IsSystemDarkMode());
        var darkValue = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref darkValue, sizeof(int));
        var transitionsDisabled = 0;
        DwmSetWindowAttribute(handle, 3, ref transitionsDisabled, sizeof(int));
        var backdrop = 2; // DWMSBT_MAINWINDOW (Mica)
        DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        var corner = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
