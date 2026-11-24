using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.Models;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    public partial class FolderModule : ModuleBase
    {
        private const int PrefetchLimit = 64;
        private sealed record FolderTile(FileSystemInfo File, Border View);
        private readonly string _directory;
        private readonly string _name;
        private readonly bool _linked;
        private readonly WrapPanel _wrap = new WrapPanel();
        private readonly DispatcherTimer _debounce;
        private const int ReloadDelayMs = 300;
        private long _lastFolderChange;
        private int _reloadQueued;
        private FileSystemWatcher _watcher;
        private FileSystemInfo[] _files, _displayedFiles, _preparingFiles;
        private FolderTile[] _tiles = Array.Empty<FolderTile>();
        private FileSystemInfo[] _matchedFiles = Array.Empty<FileSystemInfo>();
        private CancellationTokenSource _filterRequest;
        private int _loadVersion;
        private volatile bool _disposed;
        internal string FolderName => _name;
        internal string FolderPath => _directory;

        public FolderModule(string name, TabStyle style, string linkedPath = null) : base(style)
        {
            _name = name;
            _linked = linkedPath != null;
            _directory = linkedPath ?? MiniFolderCatalog.GetFolderPath(App.Settings.StorageRoot, name);
            var badge = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            badge.Children.Add(MakeFolderGlyph(18));
            var title = MakePillText(name);
            title.Margin = new Thickness(8 * Ui.Scale, 0, 0, 0);
            badge.Children.Add(title);
            Pill.Child = badge;
            Pill.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                e.Handled = true;
                if (_available) DesktopActions.Open(_directory);
                else ShowPanel();
            };
            Pill.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowFolderMenu();
            };

            Panel.Width = (Preferences.ListView ? 336 : 290) * Ui.Scale;
            Panel.Child = BuildFolderContent();
            Panel.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowFolderMenu(fromPanel: true);
            };
            foreach (var target in new FrameworkElement[] { Pill, PanelWindow })
                WireFileDrop(target, _directory, RunFileOperation);

            _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ReloadDelayMs)
            };
            _debounce.Tick += (_, _) =>
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastFolderChange) < ReloadDelayMs) return;
                _debounce.Stop();
                Interlocked.Exchange(ref _reloadQueued, 0);
                _ = LoadFilesAsync();
            };
            _ = LoadFilesAsync();
        }

        protected override void OnPanelShown()
        {
            if (_files != null && _watcher == null) _ = LoadFilesAsync();
            _ = ShowFilesAsync();
        }

        protected override bool KeepPanelOpenOnPointerExit => _panelPinned;
        protected override bool CaptureFullPanelEnvelope => false;
        protected override Size GetPanelEnvelope(Size content) => new(Math.Max(content.Width, 336 * Ui.Scale),
            Math.Max(content.Height, SystemParameters.PrimaryScreenHeight * .42 + 176 * Ui.Scale + 265));

        protected override void OnPanelHidden()
        {
            _filterRequest?.Cancel();
            ClearSelection();
            CancelPreview();
            if (_panelPinned) SetPanelPinned(false);
        }

        protected override void OnFileDropSettled() => _ = ShowFilesAsync();

        private async Task LoadFilesAsync()
        {
            if (_directory == null || _disposed) return;
            int version = ++_loadVersion;
            bool watch = _watcher == null;
            string path = CurrentDirectory;
            var snapshot = await Task.Run(() => ReadFolder(path, watch));
            if (_disposed || version != _loadVersion)
            {
                snapshot.Watcher?.Dispose();
                return;
            }
            _watcher ??= snapshot.Watcher;
            UpdateFolderSnapshot(snapshot.Files);
            for (int i = 0; i < Math.Min(PrefetchLimit, _files.Length); i++)
                IconCache.LoadAsync(_files[i]);
            if (Open) await ShowFilesAsync();
        }

        private (FileSystemInfo[] Files, FileSystemWatcher Watcher) ReadFolder(string path, bool watch)
        {
            FileSystemWatcher watcher = null;
            if (watch)
            {
                try
                {
                    watcher = new FileSystemWatcher(path)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                    };
                    watcher.Created += (_, _) => ScheduleReload();
                    watcher.Changed += (_, _) => ScheduleReload();
                    watcher.Deleted += (_, _) => ScheduleReload();
                    watcher.Renamed += (_, _) => ScheduleReload();
                    watcher.Error += (source, error) => OnWatcherError((FileSystemWatcher)source, error.GetException());
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    watcher?.Dispose();
                    watcher = null;
                }
            }
            try
            {
                var files = new DirectoryInfo(path).GetFileSystemInfos()
                    .OrderBy(item => item is DirectoryInfo ? 0 : 1)
                    .ThenBy(item => item.Name, Ui.FileNameOrder)
                    .ToArray();
                return (files, watcher);
            }
            catch (DirectoryNotFoundException)
            {
                watcher?.Dispose();
                return (null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                watcher?.Dispose();
                App.Log("读取小文件夹失败: " + ex.Message);
                return (null, null);
            }
        }

        private void OnWatcherError(FileSystemWatcher watcher, Exception error)
        {
            if (error is InternalBufferOverflowException)
            {
                ScheduleReload();
                return;
            }
            if (_disposed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_disposed || (_watcher != null && !ReferenceEquals(_watcher, watcher))) return;
                watcher.Dispose();
                _watcher = null;
                _loadVersion++;
                ScheduleReload();
            }));
        }

        private void ScheduleReload()
        {
            if (_disposed || Dispatcher.HasShutdownStarted) return;
            Interlocked.Exchange(ref _lastFolderChange, Environment.TickCount64);
            if (Interlocked.Exchange(ref _reloadQueued, 1) != 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_disposed) _debounce.Start();
            }));
        }

        private async Task ShowFilesAsync()
        {
            var files = _files;
            int revision = _viewRevision;
            bool CanApply() => !_disposed && Open && !FileDropInteractionActive
                && ReferenceEquals(_files, files) && revision == _viewRevision;
            if (files == null || !CanApply() || (ReferenceEquals(_displayedFiles, files) && _displayedRevision == revision)
                || (ReferenceEquals(_preparingFiles, files) && _preparingRevision == revision
                    && _filterRequest is { IsCancellationRequested: false })) return;

            _preparingFiles = files;
            _preparingRevision = revision;
            _filterRequest?.Cancel();
            using var request = new CancellationTokenSource();
            _filterRequest = request;
            try
            {
                var query = BuildFolderQuery();
                var matches = await Task.Run(() => query(files, request.Token), request.Token);
                if (request.IsCancellationRequested || !CanApply()) return;
                if (Preferences.ListView)
                {
                    CommitFolderView(matches);
                    SetPanelWidth(336 * Ui.Scale, animate: _displayedFiles != null);
                    ShowVirtualRows(matches);
                    _displayedRevision = revision;
                    _displayedFiles = files;
                    ShowFolderStatus(matches.Length);
                    return;
                }
                var items = matches.Take(_visibleLimit).ToArray();
                var existing = _displayedRevision == revision
                    ? _tiles.ToDictionary(tile => tile.File.FullName, StringComparer.Ordinal)
                    : new Dictionary<string, FolderTile>();
                var next = new FolderTile[items.Length];
                int prepared = 0;
                for (int i = 0; i < items.Length; i++)
                {
                    var file = items[i];
                    if (existing.TryGetValue(file.FullName, out var tile) && file.GetType() == tile.File.GetType()
                        && file.LastWriteTimeUtc == tile.File.LastWriteTimeUtc)
                    {
                        next[i] = tile;
                        continue;
                    }
                    next[i] = new FolderTile(file, MakeItem(file));
                    if (++prepared % 8 == 0 && i + 1 < items.Length)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        if (request.IsCancellationRequested || !CanApply()) return;
                    }
                }

                // Keep the current tree on screen while preparing icons and commit the layout in one pass.
                CommitFolderView(matches);
                var views = next.Select(tile => tile.View).ToHashSet();
                for (int i = _wrap.Children.Count - 1; i >= 0; i--)
                    if (_wrap.Children[i] is Border view && !views.Contains(view)) _wrap.Children.RemoveAt(i);
                for (int i = 0; i < next.Length; i++)
                {
                    var view = next[i].View;
                    if (i < _wrap.Children.Count && ReferenceEquals(_wrap.Children[i], view)) continue;
                    if (ReferenceEquals(view.Parent, _wrap)) _wrap.Children.Remove(view);
                    _wrap.Children.Insert(i, view);
                }

                int cols = Math.Min(4, Math.Max(3, items.Length));
                _wrap.Width = cols * 76 * Ui.Scale;
                SetPanelWidth(Math.Max(290, _wrap.Width / Ui.Scale + 26) * Ui.Scale, animate: _displayedFiles != null);
                if (_displayedRevision != revision) EaseInResults(_wrap);
                _tiles = next;
                _displayedRevision = revision;
                _displayedFiles = files;
                ShowFolderStatus(matches.Length);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_filterRequest, request))
                {
                    _filterRequest = null;
                    _preparingFiles = null;
                }
            }
        }

        private void CommitFolderView(FileSystemInfo[] matches)
        {
            bool navigated = !_listView.IsHitTestVisible;
            _matchedFiles = matches;
            _listView.IsHitTestVisible = _gridScroll.IsHitTestVisible = true;
            ReconcileSelection(matches);
            SetFolderViewMode();
            UpdateFolderLocation();
            if (navigated) WireFileDrop(PanelWindow, _available ? CurrentDirectory : null, RunFileOperation);
        }

        private Border MakeItem(FileSystemInfo item)
        {
            bool isDir = item is DirectoryInfo;
            string displayName = !isDir && (item.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                || item.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                ? Path.GetFileNameWithoutExtension(item.Name) : item.Name;
            double scale = Ui.Scale;
            var image = new Image { Width = 40 * scale, Height = 40 * scale, Stretch = Stretch.Uniform };
            IconCache.LoadAsync(item, icon => { if (!_disposed) image.Source = icon; });
            image.Source ??= IconCache.GetFallback(isDir, item.Extension);
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var label = new TextBlock
            {
                Text = displayName, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                FontSize = 12 * scale, MaxHeight = 28 * scale, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4 * scale, 0, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            var body = BuildFileTile(item, image, label, displayName, scale);
            var motion = new ScaleTransform(1, 1);
            var tile = new Border
            {
                CornerRadius = new CornerRadius(8 * scale), Background = Brushes.Transparent, Child = body,
                Cursor = Cursors.Hand, ToolTip = item.Name, Focusable = true,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = motion
            };
            System.Windows.Automation.AutomationProperties.SetName(tile, displayName);
            tile.Tag = item.FullName;
            tile.GotKeyboardFocus += (_, _) => PaintTile(tile);
            tile.LostKeyboardFocus += (_, _) => PaintTile(tile);
            tile.PreviewKeyDown += (_, e) =>
            {
                if (!tile.IsHitTestVisible) return;
                if (e.Key == Key.Space && !isDir) { PreviewFile(item); e.Handled = true; }
                if (e.Key == Key.Enter) { OpenInside(item); e.Handled = true; }
            };
            tile.MouseEnter += (_, _) => PaintTile(tile);
            tile.MouseLeave += (_, _) => { PaintTile(tile); AnimateScale(motion, 1); };
            PaintTile(tile);
            tile.MouseLeftButtonDown += (_, _) => AnimateScale(motion, 0.93);
            ShellFileActions.WireDrag(tile, () =>
            {
                AnimateScale(motion, 1);
                var selection = SelectedPaths(item.FullName);
                RunFileOperation(() => ShellFiles.Drag(FileOperationOwner, selection));
            }, clicks =>
            {
                AnimateScale(motion, 1);
                if (SelectItem(item, clicks)) EnterPanelKeyboardMode(tile);
                else OpenInside(item);
            });
            tile.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_selection.Contains(item.FullName) && _selection.Count > 1)
                {
                    TrackFlyout(FlyoutMenu.Show(SelectionActions(), $"已选 {_selection.Count} 项"));
                    return;
                }
                var items = new System.Collections.Generic.List<FlyoutItem>
                {
                    new FlyoutItem("打开", () => DesktopActions.Open(item.FullName)),
                    new FlyoutItem(IsPinned(item.Name) ? "取消置顶" : "置顶", () => TogglePin(item.Name)),
                    new FlyoutItem("固定到常用项", () => DesktopActions.Add(item.FullName)),
                    ItemVerb("复制", item.FullName, "copy"),
                    ItemVerb("剪切", item.FullName, "cut"),
                    PasteItem(isDir ? item.FullName : CurrentDirectory),
                    RenameItem(item.FullName),
                    ItemVerb("删除", item.FullName, "delete", danger: true),
                    new FlyoutItem("在资源管理器中定位", () => ShellOpen.RevealItem(item.FullName))
                };
                if (!isDir) items.Insert(1, new FlyoutItem("快速预览", () => PreviewFile(item)));
                TrackFlyout(FlyoutMenu.Show(items, displayName));
            };
            if (isDir)
                WireFileDrop(tile, item.FullName, RunFileOperation);
            return tile;
        }

        private void ShowFolderMenu(bool fromPanel = false)
        {
            if (fromPanel && !_listView.IsHitTestVisible) return;
            string path = fromPanel ? CurrentDirectory : _directory;
            if (path == null) return;
            var actions = new List<FlyoutItem>
            {
                new FlyoutItem("重新读取", () => _ = LoadFilesAsync()),
                new FlyoutItem("固定到常用项", () => DesktopActions.Add(path)),
                new FlyoutItem("在资源管理器中打开", () => ShellOpen.OpenFolder(path))
            };
            if (_available) actions.Insert(0, PasteItem(path));
            if (_linked && path == _directory)
                actions.Add(new FlyoutItem("移除顶栏入口", () => DesktopActions.UnlinkFolder(_directory)));
            else
            {
                actions.Add(RenameItem(path));
                actions.Add(ItemVerb("删除文件夹", path, "delete", danger: true));
            }
            TrackFlyout(FlyoutMenu.Show(actions, _name));
        }

        private FlyoutItem ItemVerb(string label, string path, string verb, bool danger = false)
            => new FlyoutItem(label, () => RunFileOperation(() => ShellFiles.Invoke(FileOperationOwner, path, verb)), danger);

        private FlyoutItem PasteItem(string path)
            => new FlyoutItem("粘贴", () => RunFileOperation(() => ShellFiles.Paste(FileOperationOwner, path)));

        private FlyoutItem RenameItem(string path)
            => new FlyoutItem("重命名", () => RunFileOperation(() => ShellFileActions.Rename(FileOperationOwner, path)));

        private static void AnimateScale(ScaleTransform transform, double value)
        {
            var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 110 : 0);
            var animation = new DoubleAnimation(transform.ScaleX, value, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
            };
            transform.ScaleX = transform.ScaleY = value;
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        public override void Dispose()
        {
            _disposed = true;
            _filterRequest?.Cancel();
            _debounce.Stop();
            _watcher?.Dispose();
            base.Dispose();
        }
    }
}
