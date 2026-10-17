using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
    private readonly ObservableCollection<Post> _visibleListPosts = [];
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
    private bool _suppressPoFilter;
    private bool _isLoadingNextThreadPage;
    private bool _isLoadingNextListPage;
    private bool _listHasMore = true;
    private int _highestLoadedListPage = 1;
    private bool _threadHasMore = true;
    private int _highestLoadedThreadPage = 1;
    private int _currentThreadLastPage = 1;
    private List<Post> _currentThreadPosts = [];
    private readonly List<Post> _allThreadPosts = [];
    private readonly ObservableCollection<Post> _visibleThreadPosts = [];
    private readonly Dictionary<int, Post> _referenceCache = [];
    private readonly List<ReferenceVisual> _referenceVisuals = [];
    private readonly DispatcherTimer _arrowUpdateTimer;
    private bool _arrowUpdatePending;
    private bool _suppressCookieSlotSelection;
    private string? _editingCookieSlotId;
    private string _currentPageTitle = "时间线";

    private static readonly Regex ReferenceRegex = new(@">>No\.(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Color[] ReferenceColors =
    [
        Color.FromRgb(22, 163, 74), Color.FromRgb(37, 99, 235), Color.FromRgb(147, 51, 234),
        Color.FromRgb(234, 88, 12), Color.FromRgb(15, 118, 110), Color.FromRgb(219, 39, 119)
    ];
    private static readonly SolidColorBrush[] ReferenceBrushes = ReferenceColors.Select(CreateReferenceBrush).ToArray();
    private static readonly double[] ReferenceLaneXs = [3d, 8d, 13d, 18d, 23d];
    private static readonly double[] ReferenceTargetOffsets = [-8d, -4d, 0d, 4d, 8d];

    private sealed class ReferenceVisual(RichTextBox owner, Hyperlink link, int sourceId, int targetId, int colorIndex)
    {
        public RichTextBox Owner { get; } = owner;
        public Hyperlink Link { get; } = link;
        public int SourceId { get; } = sourceId;
        public int TargetId { get; } = targetId;
        public int ColorIndex { get; } = colorIndex;
        public System.Windows.Shapes.Path? Path { get; set; }
        public System.Windows.Shapes.Polygon? ArrowHead { get; set; }
        public Border? Highlight { get; set; }
        public bool ArrowVisible { get; set; }
        public bool HighlightVisible { get; set; }
        public bool IsHovered { get; set; }
    }

    public ObservableCollection<ForumCategory> ForumCategories => _forumCategories;

    public MainWindow()
    {
        _settings = _settingsService.Load();
        _settings.NormalizeCookieSlots();
        _api = new XIslandApiService(_settings);
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Round(workArea.Width * 0.6));
        Height = Math.Max(MinHeight, Math.Round(workArea.Height * 0.6));
        DataContext = this;
        _arrowUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _arrowUpdateTimer.Tick += (_, _) =>
        {
            if (!_arrowUpdatePending)
            {
                _arrowUpdateTimer.Stop();
                return;
            }
            _arrowUpdatePending = false;
            UpdateReferenceArrows();
        };
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
            _visibleListPosts.Clear();
            foreach (var post in posts)
            {
                post.ShowPageDivider = false;
                post.PageDividerText = "";
                _visibleListPosts.Add(post);
            }
            ThreadItems.ItemsSource = _visibleListPosts;
            _highestLoadedListPage = _page;
            _listHasMore = posts.Count > 0;
            PageNumberText.Text = $"第 {_page} 页";
            if (posts.Count == 0) ShowTransientStatus("这一页暂时没有内容");
        });
    }

    private async Task LoadNextBoardPageAsync()
    {
        if (_mode != ViewMode.Board || _selectedForum is null || _isLoadingNextListPage || !_listHasMore) return;
        _isLoadingNextListPage = true;
        ListNextPageLoading.Visibility = Visibility.Visible;
        try
        {
            var nextPage = _highestLoadedListPage + 1;
            var posts = await _api.GetBoardAsync(_currentForumId, nextPage);
            var existingIds = _visibleListPosts.Select(x => x.Id).ToHashSet();
            var additions = posts.Where(x => existingIds.Add(x.Id)).ToList();
            if (additions.Count == 0)
            {
                _listHasMore = false;
                return;
            }

            PrepareImages(additions);
            additions[0].ShowPageDivider = true;
            additions[0].PageDividerText = $"第 {nextPage} 页";
            foreach (var post in additions) _visibleListPosts.Add(post);
            _highestLoadedListPage = nextPage;
            _listHasMore = posts.Count > 0;
            PageContextText.Text = $"{_selectedForum.Name} · 已加载至第 {nextPage} 页";
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"Load next board page failed forum={_currentForumId}", ex);
            ShowTransientStatus($"下一页加载失败：{ex.Message}");
        }
        finally
        {
            ListNextPageLoading.Visibility = Visibility.Collapsed;
            _isLoadingNextListPage = false;
        }
    }

    private async Task OpenThreadAsync(int threadId, int page = 1)
    {
        var isNewThread = threadId != _currentThreadId || _mode != ViewMode.Thread;
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
        _allThreadPosts.Clear();
        _visibleThreadPosts.Clear();
        _referenceVisuals.Clear();
        _referenceCache.Clear();
        ReferenceArrowCanvas.Children.Clear();
        _highestLoadedThreadPage = page;
        _threadHasMore = true;
        if (isNewThread)
        {
            _suppressPoFilter = true;
            OnlyPoCheck.IsChecked = false;
            _suppressPoFilter = false;
        }
        ReplyComposer.Visibility = Visibility.Collapsed;
        ThreadLoadingOverlay.Visibility = Visibility.Visible;
        try
        {
            await RunBusyAsync("正在加载串…", async () =>
            {
                var thread = await _api.GetThreadAsync(threadId, _page, false);
                var posts = new List<Post> { thread };
                posts.AddRange(thread.Replies ?? []);
                MarkPo(posts, thread);
                PrepareImages(posts);
                _allThreadPosts.AddRange(posts);
                _currentThreadPosts = _allThreadPosts;
                ApplyThreadFilter();
                ReplyItems.ItemsSource = _visibleThreadPosts;
                PageContextText.Text = $"{thread.ReplyCount} 条回复 · 第 {_page} 页";
                PageNumberText.Text = $"第 {_page} 页";
                _currentThreadLastPage = Math.Max(1, (thread.ReplyCount + 19) / 20);
                _threadHasMore = (thread.Replies?.Count ?? 0) > 0;
            });
        }
        finally
        {
            ThreadLoadingOverlay.Visibility = Visibility.Collapsed;
            SetThreadOnlineState(true);
        }
    }

    private static void MarkPo(IEnumerable<Post> posts, Post root)
    {
        foreach (var post in posts)
            post.IsPo = ReferenceEquals(post, root) || (!string.IsNullOrWhiteSpace(root.UserHash) && post.UserHash == root.UserHash);
    }

    private void ApplyThreadFilter()
    {
        ResetReferenceVisuals();
        _visibleThreadPosts.Clear();
        var onlyPo = OnlyPoCheck.IsChecked == true;
        foreach (var post in _allThreadPosts)
            if (!onlyPo || post.IsPo) _visibleThreadPosts.Add(post);
        ScheduleArrowUpdate();
    }

    private async Task LoadNextThreadPageAsync()
    {
        if (_mode != ViewMode.Thread || _isLoadingNextThreadPage || !_threadHasMore) return;
        _isLoadingNextThreadPage = true;
        NextPageLoading.Visibility = Visibility.Visible;
        try
        {
            var nextPage = _highestLoadedThreadPage + 1;
            var thread = await _api.GetThreadAsync(_currentThreadId, nextPage, false);
            var existingIds = _allThreadPosts.Select(x => x.Id).ToHashSet();
            var additions = (thread.Replies ?? []).Where(x => existingIds.Add(x.Id)).ToList();
            if (additions.Count == 0)
            {
                _threadHasMore = false;
                return;
            }

            MarkPo(additions, _allThreadPosts[0]);
            PrepareImages(additions);
            additions[0].ShowPageDivider = true;
            additions[0].PageDividerText = $"第 {nextPage} 页";
            foreach (var post in additions)
            {
                _allThreadPosts.Add(post);
                if (OnlyPoCheck.IsChecked != true || post.IsPo) _visibleThreadPosts.Add(post);
            }
            _highestLoadedThreadPage = nextPage;
            _currentThreadPosts = _allThreadPosts;
            PageContextText.Text = $"{thread.ReplyCount} 条回复 · 已加载至第 {nextPage} 页";
            _threadHasMore = additions.Count > 0;
            ScheduleArrowUpdate();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"Load next thread page failed thread={_currentThreadId}", ex);
            ShowTransientStatus($"下一页加载失败：{ex.Message}");
        }
        finally
        {
            NextPageLoading.Visibility = Visibility.Collapsed;
            _isLoadingNextThreadPage = false;
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

    private async void TimelineItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_loaded || sender is not ComboBoxItem { DataContext: TimelineInfo timeline }) return;
        e.Handled = true;
        TimelinePicker.SelectedItem = timeline;
        TimelinePicker.IsDropDownOpen = false;
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

    private async void FirstPage_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != ViewMode.Thread || _page == 1) return;
        await OpenThreadAsync(_currentThreadId, 1);
    }

    private async void LastPage_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != ViewMode.Thread || _page == _currentThreadLastPage) return;
        await OpenThreadAsync(_currentThreadId, _currentThreadLastPage);
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

    private void OnlyPoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppressPoFilter && _mode == ViewMode.Thread) ApplyThreadFilter();
    }

    private async void ReplyScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        ScheduleArrowUpdate();
        // Layout changes (especially images finishing download) also raise ScrollChanged.
        // Only a real downward scroll by the user may request another page.
        if (e.VerticalChange <= 0) return;
        if (e.ExtentHeight <= 0 || e.ViewportHeight <= 0) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - Math.Max(90, e.ViewportHeight * 0.12))
            await LoadNextThreadPageAsync();
    }

    private async void ThreadListScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || e.ExtentHeight <= 0 || e.ViewportHeight <= 0) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - Math.Max(90, e.ViewportHeight * 0.12))
            await LoadNextBoardPageAsync();
    }

    private void PostContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox { Tag: Post post } richTextBox) return;
        foreach (var stale in _referenceVisuals.Where(x => ReferenceEquals(x.Owner, richTextBox)).ToList())
            RemoveReferenceVisual(stale);
        var paragraph = new Paragraph { Margin = new Thickness(0), FontSize = 16, LineHeight = 22 };
        richTextBox.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
        var content = post.CleanContent;
        var offset = 0;
        foreach (Match match in ReferenceRegex.Matches(content))
        {
            if (match.Index > offset) paragraph.Inlines.Add(new Run(content[offset..match.Index]));
            if (!int.TryParse(match.Groups[1].Value, out var targetId)) continue;
            var link = new Hyperlink(new Run(match.Value))
            {
                Foreground = new SolidColorBrush(Color.FromRgb(196, 43, 28)),
                TextDecorations = null,
                Cursor = Cursors.Hand,
                ToolTip = new ToolTip { Content = "正在加载引用内容…", Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse }
            };
            ToolTipService.SetInitialShowDelay(link, 250);
            ToolTipService.SetShowDuration(link, 12000);
            link.Click += ReferenceLink_Click;
            link.MouseEnter += ReferenceLink_MouseEnter;
            link.MouseLeave += ReferenceLink_MouseLeave;
            link.Tag = targetId;
            paragraph.Inlines.Add(link);
            var colorIndex = _referenceVisuals.Count % ReferenceColors.Length;
            _referenceVisuals.Add(new ReferenceVisual(richTextBox, link, post.Id, targetId, colorIndex));
            offset = match.Index + match.Length;
        }
        if (offset < content.Length) paragraph.Inlines.Add(new Run(content[offset..]));
        ScheduleArrowUpdate();
    }

    private async void ReferenceLink_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Hyperlink { Tag: int targetId } link || link.ToolTip is not ToolTip tip) return;
        var visual = _referenceVisuals.FirstOrDefault(x => ReferenceEquals(x.Link, link));
        if (visual is not null) visual.IsHovered = true;
        ScheduleArrowUpdate();
        try
        {
            var post = _allThreadPosts.FirstOrDefault(x => x.Id == targetId);
            if (post is null && !_referenceCache.TryGetValue(targetId, out post))
            {
                post = await _api.GetReferenceAsync(targetId);
                _referenceCache[targetId] = post;
            }
            var content = post.CleanContent;
            if (content.Length > 260) content = content[..260] + "…";
            tip.Content = $"No.{post.Id} · {post.Author}\n{post.Time}\n\n{content}";
        }
        catch (Exception ex)
        {
            tip.Content = $"引用内容加载失败：{ex.Message}";
        }
        ScheduleArrowUpdate();
    }

    private void ReferenceLink_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Hyperlink { ToolTip: ToolTip tip } link)
        {
            tip.IsOpen = false;
            var visual = _referenceVisuals.FirstOrDefault(x => ReferenceEquals(x.Link, link));
            if (visual is not null) visual.IsHovered = false;
        }
        ScheduleArrowUpdate();
    }

    private async void ReferenceLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { Tag: int targetId }) return;
        e.Handled = true;
        var target = _visibleThreadPosts.FirstOrDefault(x => x.Id == targetId);
        if (target is null)
        {
            try
            {
                ShowTransientStatus($"正在定位 No.{targetId}…");
                await _api.GetThreadAsync(targetId, 1, false);
                await OpenThreadAsync(targetId, 1);
            }
            catch (Exception)
            {
                ShowTransientStatus($"No.{targetId} 是其他串的回复；接口未提供其所属主串，无法直接定位");
            }
            return;
        }
        if (ReplyItems.ItemContainerGenerator.ContainerFromItem(target) is FrameworkElement element)
        {
            element.BringIntoView();
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(320)));
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScheduleArrowUpdate));
        }
    }

    private void ScheduleArrowUpdate()
    {
        if (!_settings.ReferenceVisualEffects)
        {
            _arrowUpdatePending = false;
            _arrowUpdateTimer.Stop();
            ReferenceArrowCanvas.Visibility = Visibility.Collapsed;
            return;
        }
        ReferenceArrowCanvas.Visibility = Visibility.Visible;
        _arrowUpdatePending = true;
        if (!_arrowUpdateTimer.IsEnabled) _arrowUpdateTimer.Start();
    }

    private void UpdateReferenceArrows()
    {
        if (!_settings.ReferenceVisualEffects || _mode != ViewMode.Thread || ReferenceArrowCanvas.ActualHeight <= 0)
        {
            foreach (var reference in _referenceVisuals)
            {
                SetArrowVisible(reference, false);
                SetHighlightVisible(reference, false);
            }
            return;
        }

        var height = ReferenceArrowCanvas.ActualHeight;
        var maxDistance = Math.Max(height * 2.1, 600);
        var topEdgeSlots = new int[ReferenceLaneXs.Length];
        var bottomEdgeSlots = new int[ReferenceLaneXs.Length];
        var laneSegments = Enumerable.Range(0, ReferenceLaneXs.Length).Select(_ => new List<(double Start, double End)>()).ToArray();
        var colorSegments = Enumerable.Range(0, ReferenceColors.Length).Select(_ => new List<(double Start, double End)>()).ToArray();
        foreach (var reference in _referenceVisuals)
        {
            var target = _visibleThreadPosts.FirstOrDefault(x => x.Id == reference.TargetId);
            if (target is null || ReplyItems.ItemContainerGenerator.ContainerFromItem(target) is not FrameworkElement targetElement ||
                FindPostCard(targetElement, target.Id) is not Border targetCard)
            {
                SetArrowVisible(reference, false);
                SetHighlightVisible(reference, false);
                continue;
            }
            try
            {
                var ownerTransform = reference.Owner.TransformToVisual(ReferenceArrowCanvas);
                var sourceRect = reference.Link.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var sourceInOwner = sourceRect.IsEmpty
                    ? new Point(4, reference.Owner.ActualHeight / 2)
                    : new Point(sourceRect.Left, sourceRect.Top + sourceRect.Height / 2);
                var sourcePoint = ownerTransform.Transform(sourceInOwner);
                var targetTopLeft = targetCard.TransformToVisual(ReferenceArrowCanvas).Transform(new Point(0, 0));
                var targetBaseY = targetTopLeft.Y + Math.Min(28, targetCard.ActualHeight / 2);
                var sourceOnScreen = sourcePoint.Y >= 0 && sourcePoint.Y <= height;
                var targetOnScreen = targetTopLeft.Y + targetCard.ActualHeight >= 0 && targetTopLeft.Y <= height;
                var crossesViewport = (sourcePoint.Y < 0 && targetBaseY > height) || (sourcePoint.Y > height && targetBaseY < 0);
                var shouldShow = crossesViewport || ((sourceOnScreen || targetOnScreen) && Math.Abs(sourcePoint.Y - targetBaseY) <= maxDistance);
                if (shouldShow)
                {
                    var lane = ChooseReferenceLane(reference, sourcePoint.Y, targetBaseY, height, laneSegments);
                    var color = ChooseReferenceColor(reference, sourcePoint.Y, targetBaseY, height, colorSegments);
                    var targetY = targetBaseY + ReferenceTargetOffsets[lane];
                    targetY = Math.Clamp(targetY, targetTopLeft.Y + 10, targetTopLeft.Y + Math.Max(10, targetCard.ActualHeight - 10));
                    var edgeSlot = 0;
                    if (!targetOnScreen)
                    {
                        edgeSlot = targetY < 0 ? topEdgeSlots[lane]++ : bottomEdgeSlots[lane]++;
                    }
                    UpdateArrowGeometry(reference, sourcePoint.Y, targetY, sourceOnScreen, targetOnScreen, height, edgeSlot, lane, color);
                    SetArrowVisible(reference, true);
                }
                else SetArrowVisible(reference, false);

                UpdateReferenceHighlight(reference, targetElement, targetOnScreen);
            }
            catch (InvalidOperationException)
            {
                SetArrowVisible(reference, false);
                SetHighlightVisible(reference, false);
            }
            catch (ArgumentException)
            {
                SetArrowVisible(reference, false);
                SetHighlightVisible(reference, false);
            }
        }
    }

    private static int ChooseReferenceLane(ReferenceVisual reference, double sourceY, double targetY, double height, List<(double Start, double End)>[] lanes)
    {
        const double clearance = 3d;
        var start = Math.Clamp(Math.Min(sourceY, targetY), 0, height);
        var end = Math.Clamp(Math.Max(sourceY, targetY), 0, height);
        var preferred = reference.ColorIndex % lanes.Length;
        var order = Enumerable.Range(0, lanes.Length).OrderBy(i => Math.Min(Math.Abs(i - preferred), lanes.Length - Math.Abs(i - preferred)));
        foreach (var lane in order)
        {
            if (lanes[lane].All(x => end + clearance < x.Start || start - clearance > x.End))
            {
                lanes[lane].Add((start, end));
                return lane;
            }
        }
        var leastOverlapLane = Enumerable.Range(0, lanes.Length)
            .OrderBy(i => lanes[i].Sum(x => Math.Max(0, Math.Min(end, x.End) - Math.Max(start, x.Start) + clearance)))
            .ThenBy(i => Math.Min(Math.Abs(i - preferred), lanes.Length - Math.Abs(i - preferred)))
            .First();
        lanes[leastOverlapLane].Add((start, end));
        return leastOverlapLane;
    }

    private static int ChooseReferenceColor(ReferenceVisual reference, double sourceY, double targetY, double height, List<(double Start, double End)>[] colors)
    {
        const double clearance = 3d;
        var start = Math.Clamp(Math.Min(sourceY, targetY), 0, height);
        var end = Math.Clamp(Math.Max(sourceY, targetY), 0, height);
        var preferred = reference.ColorIndex % colors.Length;
        var order = Enumerable.Range(0, colors.Length).OrderBy(i => Math.Min(Math.Abs(i - preferred), colors.Length - Math.Abs(i - preferred)));
        foreach (var color in order)
        {
            if (colors[color].All(x => end + clearance < x.Start || start - clearance > x.End))
            {
                colors[color].Add((start, end));
                return color;
            }
        }
        var leastOverlapColor = Enumerable.Range(0, colors.Length)
            .OrderBy(i => colors[i].Sum(x => Math.Max(0, Math.Min(end, x.End) - Math.Max(start, x.Start) + clearance)))
            .ThenBy(i => Math.Min(Math.Abs(i - preferred), colors.Length - Math.Abs(i - preferred)))
            .First();
        colors[leastOverlapColor].Add((start, end));
        return leastOverlapColor;
    }

    private void UpdateArrowGeometry(ReferenceVisual reference, double sourceY, double targetY, bool sourceOnScreen, bool targetOnScreen, double height, int edgeSlot, int lane, int colorIndex)
    {
        EnsureArrow(reference);
        var brush = ReferenceBrushes[colorIndex];
        reference.Path!.Stroke = brush;
        reference.ArrowHead!.Fill = brush;
        // 卡片从 X=28 开始；箭头尖端统一深入卡片 10px，所有轨道以同一横坐标收尾。
        const double endpointX = 38;
        const double arrowBaseX = 30;
        var laneX = ReferenceLaneXs[lane];
        var geometry = new PathGeometry();
        PathFigure figure;
        PointCollection arrowPoints;

        if (sourceOnScreen && targetOnScreen)
        {
            var direction = targetY < sourceY ? -1d : 1d;
            var span = Math.Abs(targetY - sourceY);
            var radius = Math.Min(18, Math.Max(2, span / 3));
            radius = Math.Min(radius, span / 2);
            figure = new PathFigure { StartPoint = new Point(endpointX, sourceY), IsClosed = false };
            figure.Segments.Add(new BezierSegment(
                new Point(endpointX - 6, sourceY), new Point(laneX, sourceY + direction * radius / 2), new Point(laneX, sourceY + direction * radius), true));
            figure.Segments.Add(new LineSegment(new Point(laneX, targetY - direction * radius), true));
            figure.Segments.Add(new BezierSegment(
                new Point(laneX, targetY - direction * radius / 2), new Point(arrowBaseX - 2, targetY), new Point(arrowBaseX, targetY), true));
            arrowPoints = RightArrow(endpointX, targetY);
        }
        else if (sourceOnScreen)
        {
            var edgeInset = edgeSlot * 7d;
            var edgeY = targetY < 0 ? 1d + edgeInset : height - 1 - edgeInset;
            var direction = targetY < 0 ? -1d : 1d;
            var arrowBaseY = edgeY - direction * 8;
            var radius = Math.Min(10, Math.Abs(sourceY - arrowBaseY) / 2);
            figure = new PathFigure { StartPoint = new Point(endpointX, sourceY), IsClosed = false };
            figure.Segments.Add(new BezierSegment(
                new Point(endpointX - 5, sourceY), new Point(laneX, sourceY + direction * radius / 2), new Point(laneX, sourceY + direction * radius), true));
            figure.Segments.Add(new LineSegment(new Point(laneX, arrowBaseY), true));
            arrowPoints = VerticalArrow(laneX, edgeY, direction);
        }
        else if (targetOnScreen)
        {
            var edgeY = sourceY < 0 ? 1d : height - 1;
            var direction = sourceY < 0 ? 1d : -1d;
            var radius = Math.Min(18, Math.Abs(targetY - edgeY) / 2);
            figure = new PathFigure { StartPoint = new Point(laneX, edgeY), IsClosed = false };
            figure.Segments.Add(new LineSegment(new Point(laneX, targetY - direction * radius), true));
            figure.Segments.Add(new BezierSegment(
                new Point(laneX, targetY - direction * radius / 2), new Point(arrowBaseX - 2, targetY), new Point(arrowBaseX, targetY), true));
            arrowPoints = RightArrow(endpointX, targetY);
        }
        else
        {
            var sourceEdgeY = sourceY < 0 ? 1d : height - 1;
            var edgeInset = edgeSlot * 7d;
            var targetEdgeY = targetY < 0 ? 1d + edgeInset : height - 1 - edgeInset;
            var direction = targetY < 0 ? -1d : 1d;
            var arrowBaseY = targetEdgeY - direction * 8;
            figure = new PathFigure { StartPoint = new Point(laneX, sourceEdgeY), IsClosed = false };
            figure.Segments.Add(new LineSegment(new Point(laneX, arrowBaseY), true));
            arrowPoints = VerticalArrow(laneX, targetEdgeY, direction);
        }
        geometry.Figures.Add(figure);
        reference.Path!.Data = geometry;
        reference.ArrowHead!.Points = arrowPoints;
    }

    private static PointCollection RightArrow(double x, double y) => [new Point(x, y), new Point(x - 8, y - 5), new Point(x - 8, y + 5)];

    private static PointCollection VerticalArrow(double x, double y, double direction) => direction < 0
        ? [new Point(x, y), new Point(x - 5, y + 8), new Point(x + 5, y + 8)]
        : [new Point(x, y), new Point(x - 5, y - 8), new Point(x + 5, y - 8)];

    private void EnsureArrow(ReferenceVisual reference)
    {
        if (reference.Path is not null) return;
        var brush = ReferenceBrushes[reference.ColorIndex];
        reference.Path = new System.Windows.Shapes.Path
        {
            Stroke = brush, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Flat, StrokeLineJoin = PenLineJoin.Round,
            Opacity = 0, Visibility = Visibility.Collapsed
        };
        reference.ArrowHead = new System.Windows.Shapes.Polygon { Fill = brush, Opacity = 0, Visibility = Visibility.Collapsed };
        Panel.SetZIndex(reference.Path, 2);
        Panel.SetZIndex(reference.ArrowHead, 2);
        ReferenceArrowCanvas.Children.Add(reference.Path);
        ReferenceArrowCanvas.Children.Add(reference.ArrowHead);
    }

    private static SolidColorBrush CreateReferenceBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void SetArrowVisible(ReferenceVisual reference, bool visible)
    {
        if (reference.ArrowVisible == visible) return;
        reference.ArrowVisible = visible;
        if (visible) EnsureArrow(reference);
        if (reference.Path is null || reference.ArrowHead is null) return;
        if (visible)
        {
            reference.Path.Visibility = Visibility.Visible;
            reference.ArrowHead.Visibility = Visibility.Visible;
            reference.Path.BeginAnimation(OpacityProperty, new DoubleAnimation(reference.Path.Opacity, 0.9, TimeSpan.FromMilliseconds(140)));
            reference.ArrowHead.BeginAnimation(OpacityProperty, new DoubleAnimation(reference.ArrowHead.Opacity, 0.9, TimeSpan.FromMilliseconds(140)));
        }
        else
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fade.Completed += (_, _) =>
            {
                if (!reference.ArrowVisible)
                {
                    reference.Path.Visibility = Visibility.Collapsed;
                    reference.ArrowHead.Visibility = Visibility.Collapsed;
                }
            };
            reference.Path.BeginAnimation(OpacityProperty, fade);
            reference.ArrowHead.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
        }
    }

    private void UpdateReferenceHighlight(ReferenceVisual reference, FrameworkElement targetElement, bool targetOnScreen)
    {
        var visible = reference.IsHovered && targetOnScreen;
        if (visible) EnsureHighlight(reference, targetElement);
        SetHighlightVisible(reference, visible);
    }

    private static void EnsureHighlight(ReferenceVisual reference, FrameworkElement targetElement)
    {
        if (reference.Highlight is not null) return;
        reference.Highlight = FindHighlightBorder(targetElement);
    }

    private void SetHighlightVisible(ReferenceVisual reference, bool visible)
    {
        if (reference.HighlightVisible == visible) return;
        reference.HighlightVisible = visible;
        if (reference.Highlight is null) return;
        if (visible)
        {
            reference.Highlight.Visibility = Visibility.Visible;
            reference.Highlight.BeginAnimation(OpacityProperty, new DoubleAnimation(reference.Highlight.Opacity, 1, TimeSpan.FromMilliseconds(120)));
        }
        else
        {
            var highlight = reference.Highlight;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fade.Completed += (_, _) =>
            {
                if (!reference.HighlightVisible && ReferenceEquals(reference.Highlight, highlight))
                    highlight.Visibility = Visibility.Collapsed;
            };
            highlight.BeginAnimation(OpacityProperty, fade);
        }
    }

    private static Border? FindPostCard(DependencyObject root, int postId)
    {
        if (root is Border { Tag: int id } card && id == postId) return card;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var match = FindPostCard(VisualTreeHelper.GetChild(root, i), postId);
            if (match is not null) return match;
        }
        return null;
    }

    private static Border? FindHighlightBorder(DependencyObject root)
    {
        if (root is Border { Uid: "ReferenceHighlight" } highlight) return highlight;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var match = FindHighlightBorder(VisualTreeHelper.GetChild(root, i));
            if (match is not null) return match;
        }
        return null;
    }

    private void RemoveReferenceVisual(ReferenceVisual reference)
    {
        if (reference.Path is not null) ReferenceArrowCanvas.Children.Remove(reference.Path);
        if (reference.ArrowHead is not null) ReferenceArrowCanvas.Children.Remove(reference.ArrowHead);
        ResetHighlight(reference);
        _referenceVisuals.Remove(reference);
    }

    private void ResetReferenceVisuals()
    {
        foreach (var reference in _referenceVisuals) ResetHighlight(reference);
        _referenceVisuals.Clear();
        ReferenceArrowCanvas.Children.Clear();
    }

    private static void ResetHighlight(ReferenceVisual reference)
    {
        if (reference.Highlight is null) return;
        reference.Highlight.BeginAnimation(OpacityProperty, null);
        reference.Highlight.Opacity = 0;
        reference.Highlight.Visibility = Visibility.Collapsed;
        reference.Highlight = null;
        reference.HighlightVisible = false;
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
        _allThreadPosts.Clear();
        _allThreadPosts.AddRange(package.Posts);
        if (package.Posts.Count > 0) MarkPo(package.Posts, package.Posts[0]);
        _referenceVisuals.Clear();
        ReferenceArrowCanvas.Children.Clear();
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
        SaveCookieEditorToSlot();
        _settings.Theme = (ThemePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";
        _settings.WatermarkImages = WatermarkCheck.IsChecked == true;
        _settings.ReferenceVisualEffects = ReferenceEffectsCheck.IsChecked == true;
        if (string.IsNullOrWhiteSpace(_settings.SubscriptionId)) _settings.SubscriptionId = Guid.NewGuid().ToString();
        _settingsService.Save(_settings);
        _api.UpdateSettings(_settings);
        ApplyTheme(_settings.Theme);
        if (_settings.ReferenceVisualEffects) ScheduleArrowUpdate();
        else
        {
            _arrowUpdateTimer.Stop();
            _arrowUpdatePending = false;
            ReferenceArrowCanvas.Visibility = Visibility.Collapsed;
        }
        LoadSettingsIntoControls();
        ShowTransientStatus("设置已保存到本机");
    }

    private void LoadSettingsIntoControls()
    {
        if (SubscriptionIdBox is null) return;
        _settings.NormalizeCookieSlots();
        SubscriptionIdBox.Text = _settings.SubscriptionId;
        RefreshCookieSlotPicker(_settings.ActiveCookieSlotId);
        var activeCookieName = _settings.CookieSlots.FirstOrDefault(x => x.Id == _settings.ActiveCookieSlotId)?.Name
            ?? AppSettings.CookieName(_settings.Cookie);
        CookieStatusText.Text = string.IsNullOrWhiteSpace(_settings.Cookie)
            ? "尚未获取"
            : $"当前：{activeCookieName} · 共 {_settings.CookieSlots.Count} 个槽位";
        WatermarkCheck.IsChecked = _settings.WatermarkImages;
        ReferenceEffectsCheck.IsChecked = _settings.ReferenceVisualEffects;
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
        browser.CookieCaptured += async (_, captured) =>
        {
            var slot = _settings.RememberCookie(captured.CookieHeader, captured.Name, captured.Activate);
            if (slot is null) return;
            _settingsService.Save(_settings);
            _api.UpdateSettings(_settings);
            LoadSettingsIntoControls();
            if (captured.Activate)
            {
                CookieStatusText.Text = $"已记录并启用 {slot.Name} · 共 {_settings.CookieSlots.Count} 个槽位";
                ShowTransientStatus($"已从用户系统记录并启用饼干 {slot.Name}");
                await RefreshCurrentAsync();
            }
        };
        browser.ShowDialog();
    }

    private void CookieSlotPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCookieSlotSelection || CookieSlotPicker.SelectedItem is not CookieSlot selected) return;
        SaveCookieEditorToSlot();
        ActivateCookieSlot(selected);
        _settingsService.Save(_settings);
        _api.UpdateSettings(_settings);
        CookieBox.Password = selected.Cookie;
        _editingCookieSlotId = selected.Id;
        CookieStatusText.Text = string.IsNullOrWhiteSpace(selected.Cookie) ? "该槽位尚未设置" : $"当前：{selected.Name}";
    }

    private void AddCookieSlot_Click(object sender, RoutedEventArgs e)
    {
        SaveCookieEditorToSlot();
        var slot = new CookieSlot();
        _settings.CookieSlots.Add(slot);
        ActivateCookieSlot(slot);
        _settingsService.Save(_settings);
        RefreshCookieSlotPicker(slot.Id);
        CookieStatusText.Text = "新槽位已建立，粘贴饼干后保存即可";
    }

    private void DeleteCookieSlot_Click(object sender, RoutedEventArgs e)
    {
        if (CookieSlotPicker.SelectedItem is not CookieSlot selected) return;
        _settings.CookieSlots.RemoveAll(x => x.Id == selected.Id);
        var next = _settings.CookieSlots.FirstOrDefault();
        if (next is not null) ActivateCookieSlot(next);
        else
        {
            _settings.ActiveCookieSlotId = "";
            _settings.Cookie = "";
        }
        _settingsService.Save(_settings);
        _api.UpdateSettings(_settings);
        RefreshCookieSlotPicker(next?.Id);
        CookieStatusText.Text = next is null ? "尚未获取" : $"当前：{next.Name}";
    }

    private async void ValidateCookie_Click(object sender, RoutedEventArgs e)
    {
        SaveCookieEditorToSlot();
        _settings.NormalizeCookieSlots();
        _settingsService.Save(_settings);
        _api.UpdateSettings(_settings);
        if (string.IsNullOrWhiteSpace(_settings.Cookie))
        {
            CookieStatusText.Text = "当前槽位没有饼干";
            return;
        }
        ValidateCookieButton.IsEnabled = false;
        var activeName = _settings.CookieSlots.FirstOrDefault(x => x.Id == _settings.ActiveCookieSlotId)?.Name
            ?? AppSettings.CookieName(_settings.Cookie);
        CookieStatusText.Text = $"正在通过值班室校验 {activeName}…";
        try
        {
            await _api.ValidateCookieAsync();
            CookieStatusText.Text = $"✓ {activeName} 有效";
        }
        catch (ApiException ex) when (ex.Message.Contains("饼干") || ex.Message.Contains("登入"))
        {
            CookieStatusText.Text = $"✕ {activeName} 无效或已失效";
        }
        catch (Exception ex)
        {
            CookieStatusText.Text = $"校验失败：{ex.Message}";
        }
        finally { ValidateCookieButton.IsEnabled = true; }
    }

    private void SaveCookieEditorToSlot()
    {
        if (string.IsNullOrWhiteSpace(_editingCookieSlotId))
        {
            var remembered = _settings.RememberCookie(CookieBox.Password);
            _editingCookieSlotId = remembered?.Id;
            return;
        }
        var slot = _settings.CookieSlots.FirstOrDefault(x => x.Id == _editingCookieSlotId);
        if (slot is null) return;
        var editedCookie = CookieBox.Password.Trim();
        if (!string.Equals(slot.Cookie, editedCookie, StringComparison.Ordinal)) slot.Name = AppSettings.CookieName(editedCookie);
        slot.Cookie = editedCookie;
        slot.LastUsedAt = DateTimeOffset.Now;
        if (_settings.ActiveCookieSlotId == slot.Id) _settings.Cookie = slot.Cookie;
    }

    private void ActivateCookieSlot(CookieSlot slot)
    {
        _settings.ActiveCookieSlotId = slot.Id;
        _settings.Cookie = slot.Cookie;
        slot.LastUsedAt = DateTimeOffset.Now;
    }

    private void RefreshCookieSlotPicker(string? selectedId)
    {
        _suppressCookieSlotSelection = true;
        CookieSlotPicker.ItemsSource = null;
        CookieSlotPicker.ItemsSource = _settings.CookieSlots;
        var selected = _settings.CookieSlots.FirstOrDefault(x => x.Id == selectedId) ?? _settings.CookieSlots.FirstOrDefault();
        CookieSlotPicker.SelectedItem = selected;
        CookieBox.Password = selected?.Cookie ?? "";
        _editingCookieSlotId = selected?.Id;
        _suppressCookieSlotSelection = false;
    }

    private void ApplyTheme(string theme)
    {
        var useDark = theme == "Dark" || (theme == "System" && IsSystemDarkMode());
        SetThemeColor("TextColor", "TextBrush", useDark ? "#FFFFFF" : "#1A1A1A");
        SetThemeColor("SubtleTextColor", "SubtleTextBrush", useDark ? "#CFCFCF" : "#616161");
        SetThemeColor("SurfaceColor", "SurfaceBrush", useDark ? "#202020" : "#F9F9F9");
        SetThemeColor("CardColor", "CardBrush", useDark ? "#2B2B2B" : "#FDFDFD");
        SetThemeColor("LayerColor", "LayerBrush", useDark ? "#272727" : "#F3F3F3");
        SetThemeColor("StrokeColor", "StrokeBrush", useDark ? "#28FFFFFF" : "#16000000");
        SetThemeColor("HoverColor", "HoverBrush", useDark ? "#12FFFFFF" : "#0A000000");
        SetThemeColor("PressedColor", "PressedBrush", useDark ? "#18FFFFFF" : "#0F000000");
        ApplyWindowBackdrop();
    }

    private static void SetThemeColor(string colorKey, string brushKey, string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        var resources = Application.Current.Resources;
        resources[colorKey] = color;
        resources[brushKey] = new SolidColorBrush(color);
        foreach (var dictionary in resources.MergedDictionaries.Where(x => x.Contains(colorKey) || x.Contains(brushKey)))
        {
            dictionary[colorKey] = color;
            dictionary[brushKey] = new SolidColorBrush(color);
        }
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

