using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopBar.Models;
using DesktopBar.UI;
using Microsoft.Win32;

namespace DesktopBar
{
    public partial class SettingsWindow
    {
        private FrameworkElement BuildEdgeExclusionsCard()
        {
            var card = Card("触顶例外", "这些应用在前台时，鼠标触顶保持安静。快捷键仍可唤出。", out var body);
            var list = new ListBox { Height = 80 };
            list.SetResourceReference(StyleProperty, "DarkList");
            body.Children.Add(list);
            void Refresh() => list.ItemsSource = S.EdgeExcludedApps.ToArray();
            var actions = new WrapPanel();
            actions.Children.Add(PanelControls.Button("添加应用…", () =>
            {
                var picker = new OpenFileDialog { Title = "选择应用程序", Filter = "Windows 应用|*.exe" };
                if (picker.ShowDialog(this) != true) return;
                string name = Path.GetFileName(picker.FileName);
                if (S.EdgeExcludedApps.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
                S.EdgeExcludedApps.Add(name);
                App.Save();
                Refresh();
            }));
            actions.Children.Add(PanelControls.Button("移除", () =>
            {
                if (list.SelectedItem is not string name) return;
                S.EdgeExcludedApps.Remove(name);
                App.Save();
                Refresh();
            }));
            body.Children.Add(actions);
            Refresh();
            return card;
        }

        private FrameworkElement BuildFolderShortcutsCard()
        {
            var card = Card("小文件夹直达", "为常用目录选择快捷键。顶栏被盖住时会先唤出再打开。", out var body);
            var pickers = new ComboBox[3];
            var status = MakeHint("");
            bool refreshing = false;
            for (int slot = 0; slot < pickers.Length; slot++)
            {
                int index = slot;
                var picker = new ComboBox { Style = (Style)FindResource("DarkCombo"), Width = 250, DisplayMemberPath = nameof(ShortcutFolder.Label) };
                pickers[slot] = picker;
                picker.SelectionChanged += (_, _) =>
                {
                    if (refreshing || picker.SelectedItem is not ShortcutFolder folder) return;
                    if (App.Bar?.SetFolderShortcut(index, folder.Path != null) != true)
                    {
                        status.Text = "该快捷键已被占用。";
                        Refresh();
                        return;
                    }
                    S.FolderShortcuts[index] = folder.Path;
                    App.Save();
                    status.Text = "";
                };
                body.Children.Add(Row($"Ctrl + Alt + {slot + 1}", picker));
            }
            body.Children.Add(status);
            DesktopActions.Changed += Refresh;
            var bar = App.Bar;
            if (bar != null) bar.FolderCatalogChanged += Refresh;
            Closed += (_, _) =>
            {
                DesktopActions.Changed -= Refresh;
                if (bar != null) bar.FolderCatalogChanged -= Refresh;
            };
            Refresh();
            return card;

            void Refresh()
            {
                refreshing = true;
                var folders = S.Folders.Select(name => Services.MiniFolderCatalog.GetFolderPath(S.StorageRoot, name))
                    .Concat(S.LinkedFolders).Where(path => path != null).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => new ShortcutFolder(DesktopActions.Title(path), path)).ToList();
                folders.Insert(0, new ShortcutFolder("关闭", null));
                for (int slot = 0; slot < pickers.Length; slot++)
                {
                    pickers[slot].ItemsSource = folders;
                    var wanted = S.FolderShortcuts[slot];
                    pickers[slot].SelectedItem = folders.FirstOrDefault(folder => string.Equals(folder.Path, wanted, StringComparison.OrdinalIgnoreCase))
                        ?? folders[0];
                }
                refreshing = false;
            }
        }

        private sealed record ShortcutFolder(string Label, string Path);

        private FrameworkElement BuildFavoritesCard()
        {
            var card = Card("常用项", "从资源管理器拖入文件、快捷方式或文件夹。也可以固定网址。", out var body);
            body.Children.Add(ToggleRow("显示「常用」胶囊", S.ShowFavorites, value =>
            {
                S.ShowFavorites = value;
                DesktopActions.SaveLinks();
            }));
            var list = new ListBox { Height = 125, AllowDrop = true };
            list.SetResourceReference(StyleProperty, "DarkList");
            body.Children.Add(list);
            void Refresh()
            {
                var selected = (list.SelectedItem as LinkRow)?.Link;
                list.ItemsSource = S.DesktopLinks.Select(link => new LinkRow(link)).ToArray();
                list.SelectedItem = list.Items.OfType<LinkRow>().FirstOrDefault(row => ReferenceEquals(row.Link, selected));
            }
            list.DisplayMemberPath = nameof(LinkRow.Label);
            list.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
                e.Handled = true;
            };
            list.Drop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var path in files) DesktopActions.Add(path);
                e.Handled = true;
            };
            var actions = new WrapPanel();
            actions.Children.Add(PanelControls.Button("添加文件…", () =>
            {
                var picker = new OpenFileDialog { Title = "选择文件或快捷方式", Multiselect = true };
                if (picker.ShowDialog(this) == true)
                    foreach (var path in picker.FileNames) DesktopActions.Add(path);
            }));
            actions.Children.Add(PanelControls.Button("添加网址…", () =>
            {
                string address = Prompt.Show("添加网址", "输入 HTTP 或 HTTPS 网址", "")?.Trim();
                if (string.IsNullOrEmpty(address)) return;
                if (DesktopActions.IsWeb(address)) DesktopActions.Add(address);
                else ConfirmDialog.Notice("网址无效", "请输入完整的 HTTP 或 HTTPS 网址。");
            }));
            actions.Children.Add(PanelControls.Button("固定到顶栏／取消", () =>
            {
                if (list.SelectedItem is not LinkRow row) return;
                row.Link.OnBar = !row.Link.OnBar;
                DesktopActions.SaveLinks();
            }));
            actions.Children.Add(PanelControls.Button("改名…", () =>
            {
                if (list.SelectedItem is not LinkRow row) return;
                string title = Prompt.Show("常用项名称", "显示名称", row.Link.Title);
                if (string.IsNullOrWhiteSpace(title)) return;
                row.Link.Title = title.Trim();
                DesktopActions.SaveLinks();
            }));
            actions.Children.Add(PanelControls.Button("移除常用项", () =>
            {
                if (list.SelectedItem is not LinkRow row) return;
                S.DesktopLinks.Remove(row.Link);
                DesktopActions.SaveLinks();
            }));
            body.Children.Add(actions);
            body.Children.Add(ToggleRow("记录从 YukiDesk 打开的项目", S.TrackRecentDesktopTargets,
                value => { S.TrackRecentDesktopTargets = value; App.Save(); }));
            body.Children.Add(PanelControls.Button("清空最近打开", () => { S.RecentDesktopTargets.Clear(); App.Save(); }));
            Refresh();
            DesktopActions.Changed += Refresh;
            Closed += (_, _) => DesktopActions.Changed -= Refresh;
            return card;
        }

        private sealed record LinkRow(DesktopLink Link)
        {
            public string Label => (Link.OnBar ? "固定 · " : "") + (Link.Title ?? DesktopActions.Title(Link.Target)) + "   " + Link.Target;
        }

        private FrameworkElement BuildLayoutCard()
        {
            var card = Card("模块位置和顺序", "拖动模块到左、中、右。空间不够时，次要模块收进顶栏的「⋯」。", out var body);
            var columns = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            var names = new[] { "左侧", "中间", "右侧" };
            var lists = new ListBox[3];
            var previewStrip = new Border();
            Point dragStart = default;
            LayoutRow dragRow = null;
            bool isDragging = false;
            bool refreshing = false;
            BarPlacement selected = null;
            var rules = BarLayoutPanel.Rules.ToDictionary(rule => rule.Id);
            var layout = BarLayoutPanel.Placements();
            Button moveLeft = null, moveRight = null, moveUp = null, moveDown = null, priority = null;
            for (int zone = 0; zone < lists.Length; zone++)
            {
                int currentZone = zone;
                var column = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
                column.Children.Add(MakeHint(names[zone]));
                lists[zone] = new ListBox { Height = 176, AllowDrop = true };
                var list = lists[zone];
                list.SetResourceReference(StyleProperty, "DarkList");
                list.DisplayMemberPath = nameof(LayoutRow.Title);
                list.SelectionChanged += (_, _) =>
                {
                    if (refreshing || list.SelectedItem is not LayoutRow row) return;
                    selected = row.Placement;
                    foreach (var other in lists)
                        if (other != null && !ReferenceEquals(other, list)) other.UnselectAll();
                    UpdateActions();
                };
                list.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    dragStart = e.GetPosition(list);
                    dragRow = (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem)?.Content as LayoutRow;
                };
                list.PreviewMouseLeftButtonUp += (_, _) => dragRow = null;
                list.PreviewMouseMove += (_, e) =>
                {
                    if (isDragging || e.LeftButton != MouseButtonState.Pressed || dragRow is not LayoutRow row) return;
                    Point now = e.GetPosition(list);
                    if (Math.Abs(now.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                        && Math.Abs(now.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    isDragging = true;
                    try { DragDrop.DoDragDrop(list, new DataObject(typeof(LayoutRow), row), DragDropEffects.Move); }
                    finally { isDragging = false; dragRow = null; }
                };
                list.DragOver += (_, e) =>
                {
                    e.Effects = e.Data.GetDataPresent(typeof(LayoutRow)) ? DragDropEffects.Move : DragDropEffects.None;
                    e.Handled = true;
                };
                list.Drop += (_, e) =>
                {
                    if (e.Data.GetData(typeof(LayoutRow)) is not LayoutRow row) return;
                    var target = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
                    var neighbor = (target?.Content as LayoutRow)?.Placement;
                    if (ReferenceEquals(row.Placement, neighbor)) return;
                    layout.Remove(row.Placement);
                    row.Placement.Zone = currentZone;
                    int pos = neighbor == null ? layout.Count : layout.IndexOf(neighbor);
                    if (target != null && pos >= 0 && e.GetPosition(target).Y >= target.ActualHeight / 2) pos++;
                    layout.Insert(pos < 0 ? layout.Count : pos, row.Placement);
                    selected = row.Placement;
                    Commit();
                    e.Handled = true;
                };
                column.Children.Add(list);
                columns.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(column, zone);
                columns.Children.Add(column);
            }
            body.Children.Add(columns);
            var preview = new Border { Height = 32, Margin = new Thickness(0, 7, 6, 0),
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(3) };
            preview.SetResourceReference(Border.BackgroundProperty, "SettingsControlBackground");
            preview.SetResourceReference(Border.BorderBrushProperty, "SettingsDivider");
            preview.Child = previewStrip;
            body.Children.Add(preview);
            var actions = new WrapPanel();
            actions.Children.Add(moveLeft = PanelControls.Button("左移", () => MoveZone(-1)));
            actions.Children.Add(moveRight = PanelControls.Button("右移", () => MoveZone(1)));
            actions.Children.Add(moveUp = PanelControls.Button("上移", () => MoveOrder(-1)));
            actions.Children.Add(moveDown = PanelControls.Button("下移", () => MoveOrder(1)));
            actions.Children.Add(priority = PanelControls.Button("优先保留", () =>
            {
                if (selected == null) return;
                selected.KeepVisible = !selected.KeepVisible;
                Commit();
            }));
            actions.Children.Add(PanelControls.Button("恢复默认", () =>
            {
                S.BarLayout.Clear();
                layout = BarLayoutPanel.Placements();
                selected = null;
                App.Save();
                App.Bar?.RefreshBarLayout();
                Render();
            }));
            body.Children.Add(actions);
            Render();
            return card;

            void Render()
            {
                if (App.Bar?.Content is Visual live)
                    previewStrip.Background = new VisualBrush(live) { Stretch = Stretch.Uniform };
                refreshing = true;
                for (int zone = 0; zone < lists.Length; zone++)
                {
                    var rows = layout.Where(item => item.Zone == zone).Select(item => new LayoutRow(item,
                        (item.KeepVisible ? "★ " : "") + rules[item.Id].Title)).ToArray();
                    lists[zone].ItemsSource = rows;
                    lists[zone].SelectedItem = rows.FirstOrDefault(row => ReferenceEquals(row.Placement, selected));
                }
                refreshing = false;
                UpdateActions();
            }
            void UpdateActions()
            {
                if (priority == null) return;
                moveLeft.IsEnabled = selected?.Zone > 0;
                moveRight.IsEnabled = selected?.Zone < 2;
                var group = selected != null ? layout.Where(item => item.Zone == selected.Zone).ToList() : new List<BarPlacement>();
                int index = group.IndexOf(selected);
                moveUp.IsEnabled = index > 0;
                moveDown.IsEnabled = index >= 0 && index + 1 < group.Count;
                priority.IsEnabled = selected != null;
                priority.Content = selected?.KeepVisible == true ? "取消优先" : "优先保留";
            }
            void Commit()
            {
                S.BarLayout = layout.ToList();
                App.Save();
                App.Bar?.RefreshBarLayout();
                Render();
            }
            void MoveZone(int direction)
            {
                if (selected == null || selected.Zone + direction is < 0 or > 2) return;
                selected.Zone += direction;
                Commit();
            }
            void MoveOrder(int direction)
            {
                if (selected == null) return;
                int current = layout.IndexOf(selected);
                var group = layout.Where(item => item.Zone == selected.Zone).ToList();
                int target = group.IndexOf(selected) + direction;
                if (target < 0 || target >= group.Count) return;
                int other = layout.IndexOf(group[target]);
                (layout[current], layout[other]) = (layout[other], layout[current]);
                Commit();
            }
        }

        private sealed record LayoutRow(BarPlacement Placement, string Title);

        private FrameworkElement BuildLinkedFoldersCard()
        {
            var card = Card("直连文件夹", "把已有目录挂到顶栏，也可以指定一个目录作为临时收件箱。", out var body);
            var list = new ListBox { Height = 118 };
            list.SetResourceReference(StyleProperty, "DarkList");
            body.Children.Add(list);
            var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            body.Children.Add(actions);
            var inbox = MakeHint("");
            body.Children.Add(inbox);
            void Refresh()
            {
                var selected = list.SelectedItem as string;
                list.ItemsSource = S.LinkedFolders.ToArray();
                list.SelectedItem = selected;
                inbox.Text = string.IsNullOrEmpty(S.InboxFolder) ? "收件箱未设置" : "收件箱 · " + S.InboxFolder;
            }
            void Pick(bool makeInbox)
            {
                var picker = new OpenFolderDialog { Title = makeInbox ? "选一个目录作为收件箱" : "选择要挂到顶栏的目录" };
                if (picker.ShowDialog(this) != true) return;
                var path = Path.TrimEndingDirectorySeparator(picker.FolderName);
                if (makeInbox)
                {
                    S.InboxFolder = path;
                    if (!S.FolderViews.TryGetValue(path, out var view)) S.FolderViews[path] = view = new Models.FolderPreferences();
                    view.Sort = "arrived";
                    App.Save();
                }
                if (!DesktopActions.LinkFolder(path) && makeInbox) App.Bar?.ApplyFolderOrder();
                Refresh();
                list.SelectedItem = path;
            }
            actions.Children.Add(PanelControls.Button("连接文件夹…", () => Pick(false)));
            actions.Children.Add(PanelControls.Button("设置收件箱…", () => Pick(true)));
            actions.Children.Add(PanelControls.Button("加入 Windows「发送到」", DesktopActions.AddInboxToSendTo));
            var remove = PanelControls.Button("移除入口", () =>
            {
                if (list.SelectedItem is string target) DesktopActions.UnlinkFolder(target);
            });
            remove.IsEnabled = false;
            list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItem is string;
            actions.Children.Add(remove);
            actions.Children.Add(PanelControls.Button("重新绑定…", () =>
            {
                if (list.SelectedItem is not string previous) return;
                var picker = new OpenFolderDialog { Title = "选择新的目标文件夹" };
                if (picker.ShowDialog(this) != true) return;
                if (!DesktopActions.RelinkFolder(previous, picker.FolderName))
                    ConfirmDialog.Notice("无法更换", "选择的目录已经挂在顶栏上。");
            }));
            actions.Children.Add(PanelControls.Button("清除收件箱", () =>
            {
                S.InboxFolder = "";
                App.Save();
                App.Bar?.ApplyFolderOrder();
                Refresh();
            }));
            Refresh();
            DesktopActions.Changed += Refresh;
            Closed += (_, _) => DesktopActions.Changed -= Refresh;
            return card;
        }
    }
}
