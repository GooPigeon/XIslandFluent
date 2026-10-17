using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XIslandFluent;

internal static class Program
{
    private const int ThreadId = 69424935;

    [STAThread]
    private static int Main()
    {
        VerifyCookieSlots();
        var exitCode = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/XIsland;component/Themes/Fluent.xaml", UriKind.Absolute)
        });
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000, Opacity = 0 };
        var applyTheme = typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("ApplyTheme");
        applyTheme.Invoke(window, ["Dark"]);
        var darkCard = Application.Current.Resources["CardBrush"] as SolidColorBrush;
        var darkSurface = Application.Current.Resources["SurfaceBrush"] as SolidColorBrush;
        if (darkCard?.Color != (Color)ColorConverter.ConvertFromString("#2B2B2B") ||
            darkSurface?.Color != (Color)ColorConverter.ConvertFromString("#202020"))
            throw new InvalidOperationException("深色主题没有更新应用内容画刷");
        var expectedWidth = Math.Max(window.MinWidth, Math.Round(SystemParameters.WorkArea.Width * 0.6));
        var expectedHeight = Math.Max(window.MinHeight, Math.Round(SystemParameters.WorkArea.Height * 0.6));
        if (Math.Abs(window.Width - expectedWidth) > 0.01 || Math.Abs(window.Height - expectedHeight) > 0.01)
            throw new InvalidOperationException("主窗口默认尺寸未按工作区的 60% 计算");
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
                applyTheme.Invoke(window, ["Dark"]);
                window.UpdateLayout();
                await Task.Delay(5000);
                if (window.Background is not SolidColorBrush background || background.Color != (Color)ColorConverter.ConvertFromString("#202020"))
                    throw new InvalidOperationException("深色主题没有应用到主窗口内容区");
                var testForum = window.ForumCategories.SelectMany(x => x.Forums).FirstOrDefault(x => x.NumericId == 4)
                    ?? window.ForumCategories.SelectMany(x => x.Forums).First(x => x.NumericId > 0);
                var showBoard = typeof(MainWindow).GetMethod("ShowBoardAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("ShowBoardAsync");
                await ((Task?)showBoard.Invoke(window, [testForum, true]) ?? throw new InvalidOperationException("ShowBoardAsync did not return Task"));
                var threadItems = (ItemsControl)(window.FindName("ThreadItems") ?? throw new InvalidOperationException("ThreadItems not found"));
                var boardFirstPageCount = threadItems.Items.Count;
                if (boardFirstPageCount == 0) throw new InvalidOperationException("板块第一页为空，无法验证无缝续页");
                await Task.Delay(800);
                if (threadItems.Items.Count != boardFirstPageCount)
                    throw new InvalidOperationException("板块在用户未滚动时自动加载了下一页");
                var loadNextBoard = typeof(MainWindow).GetMethod("LoadNextBoardPageAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("LoadNextBoardPageAsync");
                await ((Task?)loadNextBoard.Invoke(window, null) ?? throw new InvalidOperationException("LoadNextBoardPageAsync did not return Task"));
                if (threadItems.Items.Count <= boardFirstPageCount)
                    throw new InvalidOperationException("板块无缝续页没有追加新串");
                if (!threadItems.Items.Cast<XIslandFluent.Models.Post>().Any(x => x.ShowPageDivider && x.PageDividerText == "第 2 页"))
                    throw new InvalidOperationException("板块续页缺少细分页线");
                var listPanel = (Grid)(window.FindName("ListViewPanel") ?? throw new InvalidOperationException("ListViewPanel not found"));
                var listButtons = Descendants<Button>(listPanel).Select(x => x.Content?.ToString()).ToList();
                if (!listButtons.Contains("上一页") || !listButtons.Contains("下一页"))
                    throw new InvalidOperationException("板块原有分页按钮被删除");
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
                window.UpdateLayout();
                await Task.Delay(250);
                await Task.Delay(800);
                if (replies.Items.Count != firstPageCount)
                    throw new InvalidOperationException("进入长串后在用户未滚动时自动连续加载了后续页面");
                var searchButton = (Button)(window.FindName("SearchButton") ?? throw new InvalidOperationException("SearchButton not found"));
                var searchLabel = Descendants<TextBlock>(searchButton).First(x => x.Text == "搜索全岛");
                if (searchLabel.Foreground is not SolidColorBrush searchBrush || searchBrush.Color != Colors.White)
                    throw new InvalidOperationException("强调色按钮文字对比度不正确");
                _ = window.FindName("CookieSlotPicker") ?? throw new InvalidOperationException("CookieSlotPicker not found");
                _ = window.FindName("ValidateCookieButton") ?? throw new InvalidOperationException("ValidateCookieButton not found");
                var loadedIds = replies.Items.Cast<XIslandFluent.Models.Post>().Select(x => x.Id).ToHashSet();
                var richTextPosts = Descendants<RichTextBox>(replies).ToList();
                if (richTextPosts.Count == 0 || richTextPosts.Any(x => !x.IsReadOnly || !x.IsDocumentEnabled))
                    throw new InvalidOperationException("串内正文没有使用可选择复制的只读富文本");
                var selectablePost = richTextPosts.First();
                selectablePost.SelectAll();
                if (string.IsNullOrWhiteSpace(selectablePost.Selection.Text))
                    throw new InvalidOperationException("串内正文无法被选择复制");
                selectablePost.Selection.Select(selectablePost.Document.ContentStart, selectablePost.Document.ContentStart);
                var localReference = richTextPosts
                    .SelectMany(Hyperlinks)
                    .FirstOrDefault(x => x.Tag is int id && loadedIds.Contains(id))
                    ?? throw new InvalidOperationException("没有生成可测试的本页引用链接");
                var updateArrows = typeof(MainWindow).GetMethod("UpdateReferenceArrows", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("UpdateReferenceArrows");
                richTextPosts.First(x => Hyperlinks(x).Contains(localReference)).BringIntoView();
                window.UpdateLayout();
                updateArrows.Invoke(window, null);
                var arrowCanvas = (Canvas)(window.FindName("ReferenceArrowCanvas") ?? throw new InvalidOperationException("ReferenceArrowCanvas not found"));
                if (arrowCanvas.Children.Count == 0)
                    throw new InvalidOperationException("引用箭头没有生成");
                var referenceVisual = FindRenderedReferenceVisual(window);
                localReference = referenceVisual.GetType().GetProperty("Link")?.GetValue(referenceVisual) as Hyperlink
                    ?? throw new InvalidOperationException("已渲染引用缺少链接");
                var visiblePaths = arrowCanvas.Children.OfType<System.Windows.Shapes.Path>()
                    .Where(x => x.Visibility == Visibility.Visible && x.Stroke is SolidColorBrush)
                    .ToList();
                var orderedLanes = visiblePaths
                    .Select(x => ((PathGeometry)x.Data).Figures[0].Segments.OfType<LineSegment>().First().Point.X)
                    .Distinct().OrderBy(x => x).ToList();
                if (orderedLanes.Count > 5)
                    throw new InvalidOperationException("引用箭头使用了超过五条紧凑轨道");
                if (orderedLanes.Count > 1 && orderedLanes[^1] - orderedLanes[0] > 20.1)
                    throw new InvalidOperationException("引用轨道超出预留区域");
                if (orderedLanes.Any(x => Math.Abs((x - 3) / 5 - Math.Round((x - 3) / 5)) > 0.01))
                    throw new InvalidOperationException("引用箭头没有落在紧凑轨道上");
                var cardArrowHeads = arrowCanvas.Children.OfType<System.Windows.Shapes.Polygon>()
                    .Where(x => x.Visibility == Visibility.Visible && x.Points.Count == 3 && Math.Abs(x.Points.Max(p => p.X) - x.Points.Min(p => p.X) - 8) < 0.01)
                    .ToList();
                if (cardArrowHeads.Count == 0 || cardArrowHeads.Any(x => Math.Abs(x.Points.Max(p => p.X) - 38) > 0.01))
                    throw new InvalidOperationException("卡片引用箭头没有统一深入卡片的横坐标");
                for (var i = 0; i < visiblePaths.Count; i++)
                for (var j = i + 1; j < visiblePaths.Count; j++)
                {
                    var first = ((PathGeometry)visiblePaths[i].Data).Bounds;
                    var second = ((PathGeometry)visiblePaths[j].Data).Bounds;
                    var verticalOverlap = Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top);
                    var firstColor = ((SolidColorBrush)visiblePaths[i].Stroke).Color;
                    var secondColor = ((SolidColorBrush)visiblePaths[j].Stroke).Color;
                    if (verticalOverlap > 6 && firstColor == secondColor)
                        throw new InvalidOperationException("同一屏幕区间内的引用箭头出现重复颜色");
                }

                var poPost = replies.Items.Cast<XIslandFluent.Models.Post>().First(x => x.IsPo);
                var poContainer = (FrameworkElement)(replies.ItemContainerGenerator.ContainerFromItem(poPost) ?? throw new InvalidOperationException("PO 容器缺失"));
                var poCard = Descendants<Border>(poContainer).First(x => x.Tag is int id && id == poPost.Id);
                var poLabel = Descendants<TextBlock>(poCard).First(x => x.Text == "PO");
                var poNumber = Descendants<TextBlock>(poCard).First(x => x.Text == $"No.{poPost.Id}");
                var poCenter = poLabel.TransformToAncestor(poCard).Transform(new Point(0, poLabel.ActualHeight / 2)).Y;
                var numberCenter = poNumber.TransformToAncestor(poCard).Transform(new Point(0, poNumber.ActualHeight / 2)).Y;
                if (Math.Abs(poCenter - numberCenter) > 2)
                    throw new InvalidOperationException("PO 标记与串号未垂直对齐");
                var arrowPath = referenceVisual.GetType().GetProperty("Path")?.GetValue(referenceVisual) as System.Windows.Shapes.Path
                    ?? throw new InvalidOperationException("引用箭头路径缺失");
                var childCount = arrowCanvas.Children.Count;
                updateArrows.Invoke(window, null);
                if (!arrowCanvas.Children.Contains(arrowPath) || arrowCanvas.Children.Count != childCount)
                    throw new InvalidOperationException("滚动刷新时重新创建了引用图形");

                var mouseEnter = typeof(MainWindow).GetMethod("ReferenceLink_MouseEnter", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("ReferenceLink_MouseEnter");
                var hoverTargetId = (int)(localReference.Tag ?? throw new InvalidOperationException("引用目标编号缺失"));
                var hoverTarget = replies.Items.Cast<XIslandFluent.Models.Post>().First(x => x.Id == hoverTargetId);
                ((FrameworkElement)(replies.ItemContainerGenerator.ContainerFromItem(hoverTarget)
                    ?? throw new InvalidOperationException("引用目标容器缺失"))).BringIntoView();
                window.UpdateLayout();
                mouseEnter.Invoke(window, [localReference, new MouseEventArgs(Mouse.PrimaryDevice, 0)]);
                updateArrows.Invoke(window, null);
                var targetHighlight = Descendants<Border>(replies).FirstOrDefault(x => x.Uid == "ReferenceHighlight" && x.Visibility == Visibility.Visible);
                if (targetHighlight is null)
                    throw new InvalidOperationException("悬停引用时没有高亮屏幕内目标");
                var highlightParent = VisualTreeHelper.GetParent(targetHighlight) as Visual
                    ?? throw new InvalidOperationException("高亮层缺少视觉父级");
                var highlightPosition = targetHighlight.TransformToAncestor(highlightParent).Transform(new Point(0, 0));
                var replyScrollForHighlight = (ScrollViewer)(window.FindName("ReplyScrollViewer") ?? throw new InvalidOperationException("ReplyScrollViewer not found"));
                replyScrollForHighlight.ScrollToVerticalOffset(12);
                window.UpdateLayout();
                var scrolledHighlightPosition = targetHighlight.TransformToAncestor(highlightParent).Transform(new Point(0, 0));
                if (highlightPosition != scrolledHighlightPosition)
                    throw new InvalidOperationException("滚动时引用目标高亮相对卡片发生错位");
                replyScrollForHighlight.ScrollToTop();
                window.UpdateLayout();
                var tooltip = (ToolTip)(localReference.ToolTip ?? throw new InvalidOperationException("引用悬浮窗缺失"));
                tooltip.IsOpen = true;
                var mouseLeave = typeof(MainWindow).GetMethod("ReferenceLink_MouseLeave", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("ReferenceLink_MouseLeave");
                mouseLeave.Invoke(window, [localReference, new MouseEventArgs(Mouse.PrimaryDevice, 0)]);
                if (tooltip.IsOpen) throw new InvalidOperationException("引用悬浮窗离开后仍未关闭");
                updateArrows.Invoke(window, null);
                await Task.Delay(240);
                if (Descendants<Border>(replies).Any(x => x.Uid == "ReferenceHighlight" && x.Visibility == Visibility.Visible && x.Opacity > 0.01))
                    throw new InvalidOperationException("引用目标高亮没有渐出消失");

                var replyScroll = (ScrollViewer)(window.FindName("ReplyScrollViewer") ?? throw new InvalidOperationException("ReplyScrollViewer not found"));
                var sourceOwner = Descendants<RichTextBox>(replies).First(x => Hyperlinks(x).Contains(localReference));
                var sourceParagraph = sourceOwner.Document.Blocks.OfType<Paragraph>().First();
                if (Math.Abs(sourceParagraph.FontSize - 16) > 0.01 || Math.Abs(sourceParagraph.LineHeight - 22) > 0.01)
                    throw new InvalidOperationException("串内正文排版未匹配网页版 16px/22px");
                var sourceRect = localReference.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var sourceY = sourceOwner.TransformToVisual(arrowCanvas).Transform(new Point(sourceRect.Left, sourceRect.Top + sourceRect.Height / 2)).Y;
                var targetId = (int)(localReference.Tag ?? throw new InvalidOperationException("引用目标编号缺失"));
                var targetPost = replies.Items.Cast<XIslandFluent.Models.Post>().First(x => x.Id == targetId);
                var targetContainer = (FrameworkElement)(replies.ItemContainerGenerator.ContainerFromItem(targetPost) ?? throw new InvalidOperationException("引用目标容器缺失"));
                var targetCard = Descendants<Border>(targetContainer).First(x => x.Tag is int id && id == targetId);
                var replyTemplateRoot = Descendants<StackPanel>(targetContainer).First(x => x.Margin.Left > 0);
                if (Math.Abs(replyTemplateRoot.Margin.Left - replyTemplateRoot.Margin.Right) > 0.01)
                    throw new InvalidOperationException("串内卡片左右留白不对称");
                var targetTop = targetCard.TransformToVisual(arrowCanvas).Transform(new Point(0, 0)).Y;
                double? crossOffset = null;
                if (sourceY > targetTop + targetCard.ActualHeight + replyScroll.ViewportHeight + 4)
                {
                    var minimum = targetTop + targetCard.ActualHeight + 2;
                    var maximum = sourceY - replyScroll.ViewportHeight - 2;
                    crossOffset = (minimum + maximum) / 2;
                }
                else if (targetTop > sourceY + replyScroll.ViewportHeight + 4)
                {
                    var minimum = sourceY + 2;
                    var maximum = targetTop - replyScroll.ViewportHeight - 2;
                    crossOffset = (minimum + maximum) / 2;
                }
                if (crossOffset is not null)
                {
                    replyScroll.ScrollToVerticalOffset(Math.Max(0, crossOffset.Value));
                    window.UpdateLayout();
                    updateArrows.Invoke(window, null);
                    var crossesVisible = (bool)(referenceVisual.GetType().GetProperty("ArrowVisible")?.GetValue(referenceVisual) ?? false);
                    if (!crossesVisible) throw new InvalidOperationException("引用与目标分处屏幕上下外侧时没有显示箭头");
                    var crossingGeometry = arrowPath.Data as PathGeometry ?? throw new InvalidOperationException("跨屏箭头几何缺失");
                    if (crossingGeometry.Figures[0].Segments.Count != 1 || crossingGeometry.Figures[0].Segments[0] is not LineSegment)
                        throw new InvalidOperationException("跨屏箭头不是贯穿视口的直线");
                }
                var edgeOffset = targetTop < sourceY
                    ? targetTop + targetCard.ActualHeight + 4
                    : targetTop - replyScroll.ViewportHeight - 4;
                replyScroll.ScrollToVerticalOffset(Math.Max(0, edgeOffset));
                window.UpdateLayout();
                updateArrows.Invoke(window, null);
                var edgeGeometry = arrowPath.Data as PathGeometry ?? throw new InvalidOperationException("屏幕边缘箭头几何缺失");
                var edgeLine = edgeGeometry.Figures[0].Segments.LastOrDefault() as LineSegment
                    ?? throw new InvalidOperationException("屏外目标没有使用纯竖直线段");
                var arrowHead = referenceVisual.GetType().GetProperty("ArrowHead")?.GetValue(referenceVisual) as System.Windows.Shapes.Polygon
                    ?? throw new InvalidOperationException("屏幕边缘箭头缺失");
                var arrowTipY = arrowHead.Points.Min(x => x.Y);
                if (edgeLine.Point.Y <= arrowTipY + 2)
                    throw new InvalidOperationException("屏幕边缘线段越过了箭头三角");
                replyScroll.ScrollToVerticalOffset(sourceY + 80);
                window.UpdateLayout();
                updateArrows.Invoke(window, null);
                var arrowVisible = (bool)(referenceVisual.GetType().GetProperty("ArrowVisible")?.GetValue(referenceVisual) ?? true);
                if (arrowVisible) throw new InvalidOperationException("引用与目标均在屏幕外时仍显示箭头");
                replyScroll.ScrollToTop();
                window.UpdateLayout();
                updateArrows.Invoke(window, null);
                _ = window.FindName("FirstThreadPageButton") ?? throw new InvalidOperationException("串内首页按钮缺失");
                _ = window.FindName("LastThreadPageButton") ?? throw new InvalidOperationException("串内末页按钮缺失");
                var lastPageField = typeof(MainWindow).GetField("_currentThreadLastPage", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException("_currentThreadLastPage");
                var expectedLastPage = Math.Max(1, (replies.Items.Cast<XIslandFluent.Models.Post>().First().ReplyCount + 19) / 20);
                if ((int)(lastPageField.GetValue(window) ?? 0) != expectedLastPage)
                    throw new InvalidOperationException("串内末页页码计算不正确");
                var loadNext = typeof(MainWindow).GetMethod("LoadNextThreadPageAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("LoadNextThreadPageAsync");
                await ((Task?)loadNext.Invoke(window, null) ?? throw new InvalidOperationException("LoadNextThreadPageAsync did not return Task"));
                for (var i = 0; i < 40 && replies.Items.Count <= firstPageCount; i++) await Task.Delay(100);
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
                var referenceClick = typeof(MainWindow).GetMethod("ReferenceLink_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("ReferenceLink_Click");
                referenceClick.Invoke(window, [new Hyperlink { Tag = 69466323 }, new RoutedEventArgs(Hyperlink.ClickEvent)]);
                for (var i = 0; i < 80 && (!window.Title.Contains("69466323") || overlay.Visibility != Visibility.Collapsed); i++)
                    await Task.Delay(250);
                if (!window.Title.Contains("69466323"))
                    throw new InvalidOperationException("指向其他主串的引用没有打开目标串");
                if (onlyPo.IsChecked == true)
                    throw new InvalidOperationException("只看 PO 状态被带到另一串");
                exitCode = 0;
                Console.WriteLine($"PASS navigation board={testForum.NumericId} boardAppended={threadItems.Items.Count - boardFirstPageCount} thread={ThreadId} firstPage={firstPageCount} appended={appendedCount} poReset=true");
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

    private static void VerifyCookieSlots()
    {
        var settings = new XIslandFluent.Models.AppSettings { Cookie = "userhash=BzKMePb" };
        settings.NormalizeCookieSlots();
        if (settings.CookieSlots.Count != 1 || settings.CookieSlots[0].Name != "待同步饼干")
            throw new InvalidOperationException("旧版私密凭据被错误地显示为饼干名称");
        settings.RememberCookie("userhash=BzKMePb", "BzKMePb");
        if (settings.CookieSlots.Count != 1)
            throw new InvalidOperationException("相同饼干被重复记录");
        settings.RememberCookie("userhash=AbCd123");
        if (settings.CookieSlots.Count != 2 || settings.Cookie != "userhash=AbCd123")
            throw new InvalidOperationException("不同饼干没有记录并切换为当前槽位");
        settings.RememberCookie("userhash=Slot003");
        settings.RememberCookie("userhash=Slot004");
        settings.RememberCookie("userhash=Slot005");
        settings.RememberCookie("userhash=Slot006");
        if (settings.CookieSlots.Count != 6)
            throw new InvalidOperationException("自动获取的不同饼干没有全部保留");
        var encoded = new XIslandFluent.Models.AppSettings();
        encoded.RememberCookie("userhash=%A4Oz%F7*g%FB%80%F8Ad%B7@%EBI%8C()%F5", "BzKMePb");
        encoded.NormalizeCookieSlots();
        if (encoded.CookieSlots.Single().Name != "BzKMePb")
            throw new InvalidOperationException("用户系统导出的饼干名称没有保留");
        var unsynced = new XIslandFluent.Models.AppSettings();
        unsynced.RememberCookie("userhash=%A4Oz%F7*g%FB%80%F8Ad%B7@%EBI%8C()%F5");
        if (unsynced.CookieSlots.Single().Name != "待同步饼干")
            throw new InvalidOperationException("编码凭据被错误地当作饼干名称");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static IEnumerable<Hyperlink> Hyperlinks(RichTextBox richTextBox) =>
        richTextBox.Document.Blocks.OfType<Paragraph>().SelectMany(x => x.Inlines.OfType<Hyperlink>());

    private static object FindReferenceVisual(MainWindow window, Hyperlink link)
    {
        var field = typeof(MainWindow).GetField("_referenceVisuals", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("_referenceVisuals");
        var visuals = (System.Collections.IEnumerable)(field.GetValue(window) ?? throw new InvalidOperationException("引用视觉列表缺失"));
        return visuals.Cast<object>().First(x => ReferenceEquals(x.GetType().GetProperty("Link")?.GetValue(x), link));
    }

    private static object FindRenderedReferenceVisual(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_referenceVisuals", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("_referenceVisuals");
        var visuals = (System.Collections.IEnumerable)(field.GetValue(window) ?? throw new InvalidOperationException("引用视觉列表缺失"));
        return visuals.Cast<object>().First(x => x.GetType().GetProperty("Path")?.GetValue(x) is System.Windows.Shapes.Path);
    }
}

