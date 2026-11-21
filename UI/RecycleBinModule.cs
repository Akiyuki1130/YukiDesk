using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        private bool _refreshing;
        private bool _refreshRequested;
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
            var clear = new Button
            {
                Content = "清空回收站",
                Style = TryFindResource("BtnTheme") as Style,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            clear.Click += (_, _) => Empty();
            var body = new StackPanel();
            body.Children.Add(clear);
            body.Children.Add(_status);
            body.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = SystemParameters.PrimaryScreenHeight * 0.5,
                Content = _items
            });
            Panel.Width = 190 * Ui.Scale;
            Panel.Child = body;
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += (_, _) => Refresh();
            foreach (var target in new FrameworkElement[] { Pill, PanelWindow })
                WireFileDrop(target, ShellFiles.RecycleBin, RunBinOperation);
        }

        protected override bool HoverOpensPanel => false;

        protected override void OnPanelShown()
        {
            _refreshTimer.Start();
            Refresh();
        }

        protected override void OnPanelHidden() => _refreshTimer.Stop();

        private void Empty() => RunBinOperation(() => ShellFiles.EmptyRecycleBin(FileOperationOwner));

        private void ShowBinMenu() => TrackFlyout(FlyoutMenu.Show(new[]
        {
            new FlyoutItem("清空回收站", Empty, danger: true),
            new FlyoutItem("在资源管理器中打开", ShellOpen.OpenRecycleBin)
        }));

        private void RunBinOperation(Action action)
        {
            RunFileOperation(action);
            if (Open) Refresh();
        }

        private async void Refresh()
        {
            if (_disposed || !Open) return;
            if (_refreshing)
            {
                _refreshRequested = true;
                return;
            }
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
                if (_disposed || !Open) return;
                entries = entries.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                var status = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (entries.Count == _snapshot.Count && entries.Zip(_snapshot,
                        (current, previous) => current.Id.AsSpan().SequenceEqual(previous.Id)).All(same => same))
                {
                    _status.Text = "回收站是空的";
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
                        if (_disposed || !Open) return;
                    }
                }
                int cols = Math.Min(4, Math.Max(1, entries.Count));
                _items.Width = cols * 76 * Ui.Scale;
                Panel.Width = Math.Max(190, cols * 76 + 26) * Ui.Scale;
                _items.Children.Clear();
                foreach (var view in views) _items.Children.Add(view);
                _snapshot = entries;
                _status.Text = "回收站是空的";
                _status.Visibility = status;
            }
            catch (Exception ex)
            {
                App.Log("Windows 回收站读取失败: " + ex.Message);
                _status.Text = "无法读取回收站";
                _status.Visibility = Visibility.Visible;
            }
            finally
            {
                _refreshing = false;
                if (_refreshRequested)
                {
                    _refreshRequested = false;
                    Refresh();
                }
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
            tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "ThemePanelControl");
            tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
            void Restore() => RunBinOperation(() => ShellFiles.Invoke(FileOperationOwner, item, "undelete"));
            ShellFileActions.WireDrag(tile,
                () => RunBinOperation(() => ShellFiles.Drag(FileOperationOwner, item)),
                clicks => { if (clicks == 2) Restore(); });
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

        public override void Dispose()
        {
            _disposed = true;
            _refreshTimer.Stop();
            base.Dispose();
        }
    }
}
