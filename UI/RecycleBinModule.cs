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
using DesktopBar.Services;

namespace DesktopBar.UI
{
    public class RecycleBinModule : ModuleBase
    {
        private readonly ListBox _items;
        private readonly TextBlock _status;
        private readonly DispatcherTimer _refreshTimer, _filterTimer;
        private List<ShellFiles.RecycledItem> _snapshot = new();
        private List<ShellFiles.RecycledItem> _readItems;
        private CancellationTokenSource _viewRequest;
        private CancellationTokenSource _readRequest;
        private ShellFiles.RecycleTotals? _recycleTotals;
        private long _lastFullRead;
        private readonly HashSet<string> _selection = new(StringComparer.Ordinal);
        private readonly TextBox _filter;
        private readonly Button _restoreSelected, _selectMode, _dateFilter;
        private bool _selecting, _recentOnly;
        private bool _refreshing;
        private bool _refreshRequested;
        private bool _forceRefreshRequested;
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
                _ = FilterItemsAsync();
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
                _ = FilterItemsAsync();
            }, compact: true);
            DockPanel.SetDock(_dateFilter, Dock.Right);
            filters.Children.Add(_dateFilter);
            _filter = new TextBox { Height = 26 * Ui.Scale, MinHeight = 0, Padding = new Thickness(7, 1, 7, 1),
                ToolTip = "按名称、拼音首字母或原目录筛选" };
            _filter.SetResourceReference(StyleProperty, "DarkBox");
            _filter.SetResourceReference(BackgroundProperty, "ThemePanelHover");
            _filter.SetResourceReference(ForegroundProperty, "ThemePanelText");
            _filter.SetResourceReference(TextBox.CaretBrushProperty, "ThemePanelText");
            _filter.PreviewMouseLeftButtonDown += (_, _) => EnterPanelKeyboardMode(_filter);
            _filterTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
            _filterTimer.Tick += (_, _) =>
            {
                _filterTimer.Stop();
                _ = FilterItemsAsync();
            };
            _filter.TextChanged += (_, _) =>
            {
                _viewRequest?.Cancel();
                _filterTimer.Stop();
                _filterTimer.Start();
            };
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
            _items = PanelControls.VirtualizedFileList<RecycleRowHost>(SystemParameters.PrimaryScreenHeight * .5);
            body.Children.Add(_items);
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
            _ = FilterItemsAsync();
            _refreshTimer.Start();
            Refresh(force: true);
        }

        protected override void OnPanelHidden()
        {
            _refreshTimer.Stop();
            _filterTimer.Stop();
            _viewRequest?.Cancel();
            _readRequest?.Cancel();
            _refreshRequested = _forceRefreshRequested = false;
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
                if (Open) Refresh(force: true);
            }
        }

        protected override void OnFileDropSettled() => Refresh(force: true);

        private bool FileTransferActive => FileDropInteractionActive || _binOperations > 0;

        private bool CanApplySnapshot()
        {
            if (_disposed || !Open) return false;
            if (!FileTransferActive) return true;
            _refreshRequested = true;
            return false;
        }

        private async void Refresh(bool force = false)
        {
            if (!CanApplySnapshot())
            {
                if (Open && !_disposed) _forceRefreshRequested |= force;
                return;
            }
            if (_refreshing)
            {
                _refreshRequested = true;
                _forceRefreshRequested |= force;
                return;
            }
            force |= _forceRefreshRequested || _readItems == null || Environment.TickCount64 - _lastFullRead >= 12000;
            _refreshRequested = _forceRefreshRequested = false;
            _refreshing = true;
            using var read = new CancellationTokenSource();
            _readRequest = read;
            var previous = _recycleTotals;
            try
            {
                var completion = new TaskCompletionSource<(ShellFiles.RecycleTotals? Totals, List<ShellFiles.RecycledItem> Items)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = new Thread(() =>
                {
                    try
                    {
                        read.Token.ThrowIfCancellationRequested();
                        var totals = ShellFiles.ReadRecycleTotals();
                        var items = force || totals == null || totals != previous ? ShellFiles.ReadRecycleBin(read.Token) : null;
                        completion.SetResult((totals, items));
                    }
                    catch (OperationCanceledException) when (read.IsCancellationRequested) { completion.SetCanceled(read.Token); }
                    catch (Exception ex) { completion.SetException(ex); }
                }) { IsBackground = true, Name = "YukiDesk Recycle Bin" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                var (totals, entries) = await completion.Task;
                if (read.IsCancellationRequested || !CanApplySnapshot() || entries == null) return;
                _recycleTotals = totals;
                _lastFullRead = Environment.TickCount64;
                _readItems = entries;
                _ = FilterItemsAsync();
            }
            catch (OperationCanceledException) when (read.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!read.IsCancellationRequested && CanApplySnapshot())
                {
                    _recycleTotals = null;
                    App.Log("Windows 回收站读取失败: " + ex.Message);
                    _status.Text = "无法读取回收站";
                    _status.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                _refreshing = false;
                _readRequest = null;
                if (_refreshRequested) Refresh(_forceRefreshRequested);
            }
        }

        private async Task FilterItemsAsync()
        {
            if (_readItems == null || !CanApplySnapshot()) return;
            _viewRequest?.Cancel();
            using var request = new CancellationTokenSource();
            _viewRequest = request;
            var items = _readItems;
            string search = _filter.Text.Trim();
            bool recent = _recentOnly;
            bool newest = App.Settings.RecycleNewestFirst;
            var snapshot = _snapshot;
            try
            {
                var entries = await Task.Run(() =>
                {
                    var matches = FilterItems(items, search, recent, newest, request.Token);
                    return SameItems(matches, snapshot) ? snapshot : matches;
                }, request.Token);
                if (request.IsCancellationRequested || !CanApplySnapshot()) return;
                if (!ReferenceEquals(entries, snapshot)) PresentRecycleItems(entries);
                _status.Text = search.Length > 0 || recent ? "没有匹配的项目" : "回收站是空的";
                _status.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Log("Windows 回收站筛选失败: " + ex.Message);
                if (!request.IsCancellationRequested && CanApplySnapshot())
                {
                    _status.Text = "无法筛选回收站";
                    _status.Visibility = Visibility.Visible;
                }
            }
            finally { if (ReferenceEquals(_viewRequest, request)) _viewRequest = null; }
        }

        private static List<ShellFiles.RecycledItem> FilterItems(List<ShellFiles.RecycledItem> items, string search,
            bool recent, bool newest, CancellationToken cancellation)
        {
            DateTime cutoff = recent ? DateTime.UtcNow.AddDays(-7) : DateTime.MinValue;
            var words = search.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => (Text: word, Initials: PinyinInitials.ParseTerm(word))).ToArray();
            var matches = new List<(ShellFiles.RecycledItem Item, DateTime Deleted)>();
            foreach (var item in items)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!words.All(word => item.Name.Contains(word.Text, StringComparison.CurrentCultureIgnoreCase)
                    || item.OriginalFolder?.Contains(word.Text, StringComparison.CurrentCultureIgnoreCase) == true
                    || PinyinInitials.Matches(item.Name, word.Initials))) continue;
                DateTime date = item.DeletedAtUtc ?? DateTime.MinValue;
                if (date >= cutoff) matches.Add((item, date));
            }
            return matches.OrderByDescending(match => newest ? match.Deleted : DateTime.MinValue)
                .ThenBy(match => match.Item.Name, Ui.FileNameOrder).Select(match => match.Item).ToList();
        }

        private static bool SameItems(List<ShellFiles.RecycledItem> current, List<ShellFiles.RecycledItem> previous) =>
            current.Count == previous.Count && current.Zip(previous).All(pair => pair.First.Name == pair.Second.Name
                && pair.First.OriginalFolder == pair.Second.OriginalFolder && pair.First.DeletedAtUtc == pair.Second.DeletedAtUtc
                && pair.First.IsFolder == pair.Second.IsFolder && pair.First.Id.AsSpan().SequenceEqual(pair.Second.Id));

        private void PresentRecycleItems(List<ShellFiles.RecycledItem> entries)
        {
            byte[] anchor = null;
            var scroller = Ui.VisualDescendants<ScrollViewer>(_items).FirstOrDefault();
            if (_items.ItemsSource is RecycleGridRow[] previousRows
                && scroller != null
                && (int)scroller.VerticalOffset < previousRows.Length)
                anchor = previousRows[(int)scroller.VerticalOffset].Items[0].Id;

            int columns = Math.Min(4, Math.Max(1, entries.Count));
            SetPanelWidth(Math.Max(260, columns * 76 + 26) * Ui.Scale);
            var rows = entries.Chunk(columns).Select(items => new RecycleGridRow(items, BuildRow)).ToArray();
            _snapshot = entries;
            if (_selection.Count > 0) _selection.IntersectWith(entries.Select(RecycledKey));
            _items.ItemsSource = rows;
            if (anchor != null)
            {
                int rowIndex = Array.FindIndex(rows, row => row.Items.Any(item => item.Id.AsSpan().SequenceEqual(anchor)));
                scroller.ScrollToVerticalOffset(Math.Max(0, rowIndex));
            }
            UpdateSelection();
        }

        private FrameworkElement BuildRow(ShellFiles.RecycledItem[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var item in items) row.Children.Add(MakeItem(item));
            return row;
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
                ToolTip = string.Join(Environment.NewLine, new[] { item.Name, item.OriginalFolder, item.DeletedAtUtc?.ToLocalTime().ToString("G") }
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
            PaintSelection(tile);
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
            foreach (var row in Ui.VisualDescendants<RecycleRowHost>(_items))
                if (row.Content is StackPanel panel)
                    foreach (var tile in panel.Children.OfType<Border>()) PaintSelection(tile);
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
            _filterTimer.Stop();
            _viewRequest?.Cancel();
            _readRequest?.Cancel();
            base.Dispose();
        }
    }

    internal sealed record RecycleGridRow(ShellFiles.RecycledItem[] Items, Func<ShellFiles.RecycledItem[], FrameworkElement> Build);

    public sealed class RecycleRowHost : ContentControl
    {
        public RecycleRowHost()
        {
            DataContextChanged += (_, args) => Content = args.NewValue is RecycleGridRow row ? row.Build(row.Items) : null;
        }
    }
}
