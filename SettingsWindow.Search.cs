using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopBar.Services;
using DesktopBar.UI;

namespace DesktopBar
{
    public partial class SettingsWindow
    {
        private static readonly DependencyProperty SearchLabelProperty = DependencyProperty.RegisterAttached(
            "SearchLabel", typeof(string), typeof(SettingsWindow));
        private static readonly DependencyProperty SearchSectionProperty = DependencyProperty.RegisterAttached(
            "SearchSection", typeof(string), typeof(SettingsWindow));
        private static readonly DependencyProperty SearchAliasesProperty = DependencyProperty.RegisterAttached(
            "SearchAliases", typeof(string), typeof(SettingsWindow));
        // WPF can keep an inactive results view alive after the settings window closes.
        private sealed record SearchHit(string Page, string Label, string Context, WeakReference<FrameworkElement> Anchor)
        {
            public FrameworkElement Target => Anchor?.TryGetTarget(out var target) == true ? target : null;
        }

        private static readonly Dictionary<string, string> PageKeywords = new(StringComparer.Ordinal)
        {
            ["Appearance"] = "外观 材质 胶囊 液态玻璃 磨砂 雾面 壁纸 幻灯片 透明度 主题 配色 颜色 边框 缩放 定格",
            ["Time"] = "时间 时钟 日期 星期 秒数 农历 时区 倒计时 居中",
            ["Folders"] = "小文件夹 文件 目录 存储 创建 排序 重命名 资源管理器 直连 收件箱 置顶 拼音",
            ["Modules"] = "模块显示 顶栏布局 常用项 固定 排列 预设 性能 监测 GPU CPU 内存 网络速率 音乐 音量 频谱 回收站",
            ["General"] = "通用 开机自启 启动 登录 动画 悬停关闭 桌面空间 顶栏唤出 鼠标触顶 置顶 全局快捷键 截图 录屏 远程控制 备份",
            ["About"] = "关于 版本 软件更新 检查更新 下载 GitHub 开发者 协助者 头像 Akiyuki1130 OneCat2015"
        };

        private static string PageName(string page) => page switch
        {
            "Appearance" => "外观",
            "Time" => "时间",
            "Folders" => "小文件夹",
            "Modules" => "顶栏布局",
            "General" => "通用",
            "About" => "关于",
            _ => page
        };

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_pagesReady) return;
            string query = SettingsSearchBox.Text.Trim();
            bool searching = query.Length != 0;
            SearchPlaceholder.Visibility = SettingsSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearSettingsSearch.Visibility = SettingsSearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            NavigationItems.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            SettingsSearchResults.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
            var hits = searching ? FindSettings(query) : Array.Empty<SearchHit>();
            SettingsSearchResults.ItemsSource = hits;
            SettingsSearchResults.SelectedIndex = hits.Length > 0 ? 0 : -1;
            SearchEmpty.Visibility = searching && hits.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private SearchHit[] FindSettings(string query)
        {
            var terms = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => (Word: word, Initials: word.Length > 1 ? PinyinInitials.ParseTerm(word) : null)).ToArray();
            var candidates = new List<(SearchHit Hit, int Score)>();
            foreach (var view in PagesGrid.Children.OfType<ScrollViewer>())
            {
                if (view.Tag is not string page || view.Content is not FrameworkElement root) continue;
                EnsurePageBuilt(page);
                string pageName = PageName(page);
                bool hasAnchor = false;
                foreach (var (element, section) in SearchAnchors(root, null))
                {
                    string label = (string)element.GetValue(SearchLabelProperty);
                    string name = string.IsNullOrEmpty(section) || section == label ? pageName : pageName + " · " + section;
                    string searchable = label + " " + (string)element.GetValue(SearchAliasesProperty);
                    string combined = searchable + " " + name;
                    if (!terms.All(term => combined.Contains(term.Word, StringComparison.OrdinalIgnoreCase)
                        || PinyinInitials.Matches(label, term.Initials))) continue;
                    if (!terms.Any(term => searchable.Contains(term.Word, StringComparison.OrdinalIgnoreCase)
                        || PinyinInitials.Matches(label, term.Initials))) continue;
                    int score = label.Equals(query, StringComparison.OrdinalIgnoreCase) ? 100
                        : label.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 70
                        : label.Contains(query, StringComparison.OrdinalIgnoreCase) ? 60 : 45;
                    candidates.Add((new SearchHit(page, label, name, new WeakReference<FrameworkElement>(element)), score));
                    hasAnchor = true;
                }
                if (!hasAnchor && PageKeywords.TryGetValue(page, out var keywords) && terms.All(
                    term => keywords.Contains(term.Word, StringComparison.OrdinalIgnoreCase)
                        || PinyinInitials.Matches(pageName, term.Initials)))
                    candidates.Add((new SearchHit(page, pageName, "设置页面", null), pageName == query ? 100 : 20));
            }

            return candidates.OrderByDescending(candidate => candidate.Score).Select(candidate => candidate.Hit)
                .DistinctBy(hit => (hit.Page, hit.Context, hit.Label)).Take(35).ToArray();
        }

        private static IEnumerable<(FrameworkElement Element, string Section)> SearchAnchors(FrameworkElement node, string section)
        {
            if (node.Visibility != Visibility.Visible) yield break;
            section = node.GetValue(SearchSectionProperty) as string ?? section;
            if (node.GetValue(SearchLabelProperty) is string) yield return (node, section);
            // A collapsed Expander still owns its content logically. Opening a result
            // makes it visible without adding entries for controls hidden by the current mode.
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<FrameworkElement>())
                foreach (var anchor in SearchAnchors(child, section)) yield return anchor;
        }

        private bool HandleSearchKeys(KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SettingsSearchBox.Focus();
                SettingsSearchBox.SelectAll();
                return true;
            }
            bool inSearch = SettingsSearchBox.IsKeyboardFocusWithin || SettingsSearchResults.IsKeyboardFocusWithin
                || ClearSettingsSearch.IsKeyboardFocusWithin;
            if (!inSearch) return false;

            if (e.Key == Key.Escape && SettingsSearchBox.Text.Length > 0)
            {
                ClearSearch_Click(this, e);
                return true;
            }
            if (e.Key == Key.Down && SettingsSearchBox.IsKeyboardFocusWithin
                && SettingsSearchResults.SelectedItem is SearchHit)
            {
                SettingsSearchResults.ScrollIntoView(SettingsSearchResults.SelectedItem);
                SettingsSearchResults.UpdateLayout();
                if (SettingsSearchResults.ItemContainerGenerator.ContainerFromIndex(SettingsSearchResults.SelectedIndex) is ListBoxItem row)
                    row.Focus();
                return true;
            }
            if (e.Key != Key.Enter || SettingsSearchResults.SelectedItem is not SearchHit hit) return false;
            OpenSearchHit(hit);
            return true;
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SettingsSearchBox.Clear();
            SettingsSearchBox.Focus();
        }

        private void SearchResult_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source
                || ItemsControl.ContainerFromElement(SettingsSearchResults, source) is not ListBoxItem { DataContext: SearchHit hit }) return;
            e.Handled = true;
            OpenSearchHit(hit);
        }

        private void OpenSearchHit(SearchHit hit)
        {
            NavigateTo(hit.Page);
            var view = FindPage(hit.Page);
            if (view == null) return;
            var target = hit.Target;
            if (target == null)
            {
                view.ScrollToTop();
                view.Focus();
                return;
            }
            for (var ancestor = LogicalTreeHelper.GetParent(target); ancestor != null && ancestor != view;
                 ancestor = LogicalTreeHelper.GetParent(ancestor))
                if (ancestor is Expander expander) expander.IsExpanded = true;

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_currentPage != hit.Page || !target.IsVisible) return;
                target.BringIntoView();
                if (target is Grid) target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                if (SystemParameters.ClientAreaAnimation)
                    target.BeginAnimation(OpacityProperty, new DoubleAnimation(.60, 1, TimeSpan.FromMilliseconds(265))
                    { FillBehavior = FillBehavior.Stop });
            }));
        }
    }
}
