using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.Models;

namespace DesktopBar.UI
{
    public class RecycleBinModule : ModuleBase
    {
        private readonly WrapPanel _items = new WrapPanel();
        private readonly TextBlock _status;
        private readonly DispatcherTimer _refreshTimer;
        private List<ShellFiles.RecycledItem> _snapshot = new();
        private readonly HashSet<string> _selection = new(StringComparer.Ordinal);
        private readonly TextBox _filter;
        private readonly Button _restoreSelected, _selectMode, _dateFilter;
        private bool _selecting, _recentOnly;
        private bool _refreshing;
        private bool _refreshRequested;
        private int _binOperations;
        private bool _disposed;

        public RecycleBinModule(TabStyle style) : base(style)
        {
            Pill.Width = 38 * Ui.Scale;
            Pill.Padding = new Thickness(0);
            Pill.Child = CenterIcon(MakeTrashGlyph(20.5));
            bool doubleClick = false;
            Pill.MouseLeftButtonDown += (_, e) => doubleClick = e.ClickCount == 2;
            Pill.MouseLeftButtonUp += (_, e) =>
            {
                if (!doubleClick)
                {
                    ShowPanel();
                    return;
                }
                doubleClick = false;
                e.Handled = true;
                ClosePanelImmediately();
                ShellOpen.OpenRecycleBin();
            };
            Pill.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowBinMenu();
            };
            Panel.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowBinMenu();
            };

            _status = MakeText("正在读取回收站…", -1);
            _status.HorizontalAlignment = HorizontalAlignment.Center;
            _status.Opacity = 0.72;
            _status.Margin = new Thickness(0, 12 * Ui.Scale, 0, 12 * Ui.Scale);
            var clear = PanelControls.GlassButton("清空回收站", Empty);
            clear.HorizontalContentAlignment = HorizontalAlignment.Center;
            var body = new StackPanel();
            var actions = new DockPanel();
            var sort = PanelControls.GlassButton(App.Settings.RecycleNewestFirst ? "最近删除" : "名称", () =>
            {
                App.Settings.RecycleNewestFirst = !App.Settings.RecycleNewestFirst;
                App.Save();
                Refresh();
            });
            sort.Margin = new Thickness(6 * Ui.Scale, 4 * Ui.Scale, 0, 0);
            sort.Click += (_, _) => sort.Content = App.Settings.RecycleNewestFirst ? "最近删除" : "名称";
            DockPanel.SetDock(sort, Dock.Right);
            actions.Children.Add(sort);
            actions.Children.Add(clear);
            body.Children.Add(actions);
            var filters = new DockPanel { Margin = new Thickness(0, 5, 0, 4), LastChildFill = true };
            _dateFilter = PanelControls.GlassButton("全部日期", () =>
            {
                _recentOnly = !_recentOnly;
                _dateFilter.Content = _recentOnly ? "近七天" : "全部日期";
                Refresh();
            }, compact: true);
            DockPanel.SetDock(_dateFilter, Dock.Right);
            filters.Children.Add(_dateFilter);
            _filter = new TextBox { Height = 26 * Ui.Scale, MinHeight = 0, Padding = new Thickness(7, 1, 7, 1),
                ToolTip = "按名称或原目录筛选" };
            _filter.SetResourceReference(StyleProperty, "DarkBox");
            _filter.SetResourceReference(BackgroundProperty, "ThemePanelHover");
            _filter.SetResourceReference(ForegroundProperty, "ThemePanelText");
            _filter.SetResourceReference(TextBox.CaretBrushProperty, "ThemePanelText");
            _filter.PreviewMouseLeftButtonDown += (_, _) => EnterPanelKeyboardMode(_filter);
            _filter.TextChanged += (_, _) => Refresh();
            filters.Children.Add(_filter);
            body.Children.Add(filters);
            var selectionActions = new WrapPanel();
            _selectMode = PanelControls.GlassButton("多选", () =>
            {
                _selecting = !_selecting;
                if (!_selecting) _selection.Clear();
                UpdateSelection();
            }, compact: true);
            _restoreSelected = PanelControls.GlassButton("还原已选", RestoreSelection, compact: true);
            _restoreSelected.Visibility = Visibility.Collapsed;
            selectionActions.Children.Add(_selectMode);
            selectionActions.Children.Add(_restoreSelected);
            body.Children.Add(selectionActions);
            body.Children.Add(_status);
            body.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = SystemParameters.PrimaryScreenHeight * 0.5,
                Content = _items
            });
            Panel.Width = 260 * Ui.Scale;
            Panel.Child = body;
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += (_, _) => Refresh();
            foreach (var target in new FrameworkElement[] { Pill, PanelWindow })
                WireFileDrop(target, ShellFiles.RecycleBin, RunBinOperation);
        }

        protected override bool HoverOpensPanel => false;
        protected override bool CaptureFullPanelEnvelope => false;
        protected override Size GetPanelEnvelope(Size content) => new(Math.Max(content.Width, 330 * Ui.Scale),
            Math.Max(content.Height, SystemParameters.PrimaryScreenHeight * .5 + 180 * Ui.Scale));

        protected override void OnPanelShown()
        {
            _refreshTimer.Start();
            Refresh();
        }

        protected override void OnPanelHidden()
        {
            _refreshTimer.Stop();
            _selection.Clear();
            _selecting = false;
            UpdateSelection();
        }

        private void Empty() => RunBinOperation(() => ShellFiles.EmptyRecycleBin(FileOperationOwner));

        private void ShowBinMenu() => TrackFlyout(FlyoutMenu.Show(new[]
        {
            new FlyoutItem("清空回收站", Empty, danger: true),
            new FlyoutItem("在资源管理器中打开", ShellOpen.OpenRecycleBin)
        }));

        private void RunBinOperation(Action action)
        {
            _binOperations++;
            try { RunFileOperation(action); }
            finally
            {
                _binOperations--;
                if (Open) Refresh();
            }
        }

        protected override void OnFileDropSettled() => Refresh();

        private bool FileTransferActive => FileDropInteractionActive || _binOperations > 0;

        private bool CanApplySnapshot()
        {
            if (_disposed || !Open) return false;
            if (!FileTransferActive) return true;
            _refreshRequested = true;
            return false;
        }

        private async void Refresh()
        {
            if (!CanApplySnapshot()) return;
            if (_refreshing)
            {
                _refreshRequested = true;
                return;
            }
            _refreshRequested = false;
            _refreshing = true;
            try
            {
                var completion = new TaskCompletionSource<List<ShellFiles.RecycledItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = new Thread(() =>
                {
                    try { completion.SetResult(ShellFiles.ReadRecycleBin()); }
                    catch (Exception ex) { completion.SetException(ex); }
                }) { IsBackground = true, Name = "YukiDesk Recycle Bin" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                var entries = await completion.Task;
                if (!CanApplySnapshot()) return;
                string search = _filter.Text.Trim();
                DateTime cutoff = _recentOnly ? DateTime.Now.AddDays(-7) : DateTime.MinValue;
                entries = entries.Where(item =>
                    (search.Length == 0 || item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                        || item.OriginalFolder?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true)
                    && (!_recentOnly || (DateTime.TryParse(item.DeletedAt, out var deleted) && deleted >= cutoff)))
                    .OrderByDescending(item => App.Settings.RecycleNewestFirst
                        && DateTime.TryParse(item.DeletedAt, out var date) ? date : DateTime.MinValue)
                    .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                var status = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (entries.Count == _snapshot.Count && entries.Zip(_snapshot,
                        (current, previous) => current.Id.AsSpan().SequenceEqual(previous.Id)).All(same => same))
                {
                    _status.Text = search.Length > 0 || _recentOnly ? "没有匹配的项目" : "回收站是空的";
                    _status.Visibility = status;
                    return;
                }

                var views = new FrameworkElement[entries.Count];
                for (int i = 0; i < entries.Count; i++)
                {
                    views[i] = MakeItem(entries[i]);
                    if (i % 12 == 11 && i + 1 < entries.Count)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        if (!CanApplySnapshot()) return;
                    }
                }
                int cols = Math.Min(4, Math.Max(1, entries.Count));
                _items.Width = cols * 76 * Ui.Scale;
                SetPanelWidth(Math.Max(260, cols * 76 + 26) * Ui.Scale);
                _items.Children.Clear();
                foreach (var view in views) _items.Children.Add(view);
                _snapshot = entries;
                _selection.IntersectWith(entries.Select(RecycledKey));
                UpdateSelection();
                _status.Text = search.Length > 0 || _recentOnly ? "没有匹配的项目" : "回收站是空的";
                _status.Visibility = status;
            }
            catch (Exception ex)
            {
                App.Log("Windows 回收站读取失败: " + ex.Message);
                if (CanApplySnapshot())
                {
                    _status.Text = "无法读取回收站";
                    _status.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                _refreshing = false;
                if (_refreshRequested) Refresh();
            }
        }

        private Border MakeItem(ShellFiles.RecycledItem item)
        {
            double scale = Ui.Scale;
            var icon = new Image
            {
                Width = 34 * scale, Height = 34 * scale,
                Source = IconCache.GetFallback(item.IsFolder, System.IO.Path.GetExtension(item.Name))
            };
            var label = new TextBlock
            {
                Text = item.Name, FontSize = 12 * scale, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 30 * scale, Margin = new Thickness(0, 4 * scale, 0, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            var body = new StackPanel { Width = 68 * scale, Margin = new Thickness(4 * scale, 8 * scale, 4 * scale, 8 * scale) };
            body.Children.Add(icon);
            body.Children.Add(label);
            var tile = new Border
            {
                CornerRadius = new CornerRadius(8 * scale), Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = string.Join(Environment.NewLine, new[] { item.Name, item.OriginalFolder, item.DeletedAt }
                    .Where(value => !string.IsNullOrEmpty(value))),
                Child = body
            };
            string key = RecycledKey(item);
            tile.Tag = key;
            tile.MouseEnter += (_, _) => PaintSelection(tile);
            tile.MouseLeave += (_, _) => PaintSelection(tile);
            void Restore() => RunBinOperation(() => ShellFiles.Invoke(FileOperationOwner, item, "undelete"));
            ShellFileActions.WireDrag(tile,
                () => RunBinOperation(() => ShellFiles.Drag(FileOperationOwner, item)),
                clicks =>
                {
                    if (_selecting || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                    {
                        _selecting = true;
                        if (!_selection.Add(key)) _selection.Remove(key);
                        UpdateSelection();
                    }
                    else if (clicks == 2) Restore();
                });
            tile.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                TrackFlyout(FlyoutMenu.Show(new[]
                {
                    new FlyoutItem("还原", Restore),
                    new FlyoutItem("复制", () => RunFileOperation(() => ShellFiles.Invoke(FileOperationOwner, item, "copy"))),
                    new FlyoutItem("永久删除", () => RunBinOperation(() => ShellFiles.Invoke(FileOperationOwner, item, "delete")), danger: true)
                }, item.Name));
            };
            return tile;
        }

        private static string RecycledKey(ShellFiles.RecycledItem item) => Convert.ToBase64String(item.Id);

        private void RestoreSelection()
        {
            var items = _snapshot.Where(item => _selection.Contains(RecycledKey(item))).ToArray();
            if (items.Length > 0) RunBinOperation(() => ShellFiles.Invoke(FileOperationOwner, items, "undelete"));
        }

        private void UpdateSelection()
        {
            _selectMode.Content = _selecting ? $"已选 {_selection.Count} 项 · 退出" : "多选";
            _restoreSelected.Visibility = _selection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var tile in _items.Children.OfType<Border>()) PaintSelection(tile);
        }

        private void PaintSelection(Border tile)
        {
            if (tile.Tag is string id && _selection.Contains(id))
                tile.SetResourceReference(Border.BackgroundProperty, "ThemePanelSelected");
            else if (tile.IsMouseOver) tile.SetResourceReference(Border.BackgroundProperty, "ThemePanelControl");
            else tile.Background = Brushes.Transparent;
        }

        public override void Dispose()
        {
            _disposed = true;
            _refreshTimer.Stop();
            base.Dispose();
        }
    }
}
