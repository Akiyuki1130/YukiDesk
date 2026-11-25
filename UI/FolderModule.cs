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
        private const int MaxReloadDelayMs = 1200;
        private readonly object _reloadGate = new();
        private long _firstFolderChange, _lastFolderChange;
        private bool _reloadQueued;
        private FileSystemWatcher _watcher;
        private FileSystemInfo[] _files, _displayedFiles, _preparingFiles;
        private FolderTile[] _tiles = Array.Empty<FolderTile>();
        private FileSystemInfo[] _matchedFiles = Array.Empty<FileSystemInfo>();
        private CancellationTokenSource _filterRequest;
        private Task _viewTask = Task.CompletedTask;
        private int _loadVersion;
        private CancellationTokenSource _folderRead;
        private bool _readAgain;
        private volatile bool _catalogDemand;
        private volatile bool _disposed;
        internal string FolderName => _name;
        internal string FolderPath => _directory;
        internal bool IsLinkedFolder => _linked;

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

            _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _debounce.Tick += (_, _) => ProcessCatalogChanges(_loadVersion);
            SetCatalogDemand(App.ActiveSettings.ShowFolders);
        }

        protected override void OnPanelShown()
        {
            if (_watcher == null) _ = LoadFilesAsync();
            _ = ShowFilesAsync();
        }

        internal void SetCatalogDemand(bool active)
        {
            if (_disposed || active == _catalogDemand) return;
            _catalogDemand = active;
            if (active) _ = LoadFilesAsync();
            else InvalidateCatalog();
        }

        private void InvalidateCatalog()
        {
            lock (_reloadGate)
            {
                _loadVersion++;
                _reloadQueued = false;
            }
            _readAgain = false;
            _folderRead?.Cancel();
            _debounce.Stop();
            MiniFolderCatalog.RetireWatcher(_watcher);
            _watcher = null;
        }

        protected override bool KeepPanelOpenOnPointerExit => _panelPinned;
        protected override bool CaptureFullPanelEnvelope => false;
        // Preview replaces the file list within the same window.
        protected override Size GetPanelEnvelope(Size content) => new(Math.Max(content.Width, 336 * Ui.Scale),
            Math.Max(content.Height, SystemParameters.PrimaryScreenHeight * .42 + 176 * Ui.Scale));

        protected override void OnPanelHidden()
        {
            _textFilterDelay?.Stop();
            _filterKeyVersion++;
            _filterRequest?.Cancel();
            ClearSelection();
            CancelPreview();
            if (_panelPinned) SetPanelPinned(false);
        }

        protected override void OnFileDropSettled() => _ = ShowFilesAsync();

        private async Task LoadFilesAsync(bool contentsChanged = false)
        {
            if (_directory == null || _disposed || !_catalogDemand) return;
            if (_folderRead != null)
            {
                _readAgain |= contentsChanged || _folderRead.IsCancellationRequested;
                return;
            }
            string path = CurrentDirectory;
            int version = _loadVersion;
            using var read = new CancellationTokenSource();
            _folderRead = read;
            bool watch = _watcher == null;
            var previous = _files;
            bool wasAvailable = _available;
            try
            {
                var (files, watcher, unchanged) = await Task.Run(() =>
                {
                    var snapshot = ReadFolder(path, watch, version, read.Token);
                    bool same = previous != null && (snapshot.Files == null
                        ? !wasAvailable : wasAvailable && SameFolderContents(previous, snapshot.Files));
                    return (snapshot.Files, snapshot.Watcher, same);
                }, read.Token);
                if (_disposed || version != _loadVersion)
                {
                    MiniFolderCatalog.RetireWatcher(watcher);
                    return;
                }
                _watcher ??= watcher;
                if (files == null && _watcher != null)
                {
                    MiniFolderCatalog.RetireWatcher(_watcher);
                    _watcher = null;
                }
                if (!unchanged)
                {
                    UpdateFolderSnapshot(files);
                    for (int i = 0; i < Math.Min(PrefetchLimit, _files.Length); i++) IconCache.LoadAsync(_files[i]);
                }
                if (Open) _ = ShowFilesAsync();
            }
            catch (OperationCanceledException) when (read.IsCancellationRequested) { }
            finally
            {
                _folderRead = null;
                bool readAgain = _readAgain;
                _readAgain = false;
                if (readAgain) _ = LoadFilesAsync();
            }
        }

        private static bool SameFolderContents(FileSystemInfo[] first, FileSystemInfo[] second)
            => first.Length == second.Length && first.Zip(second).All(pair => SameFileMetadata(pair.First, pair.Second));

        private static bool SameFileMetadata(FileSystemInfo first, FileSystemInfo second)
        {
            if (first.Name != second.Name) return false;
            try
            {
                return first.Attributes == second.Attributes && first.LastWriteTimeUtc == second.LastWriteTimeUtc
                    && ReadFileSize(first) == ReadFileSize(second);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        private (FileSystemInfo[] Files, FileSystemWatcher Watcher) ReadFolder(string path, bool watch, int version, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            FileSystemWatcher watcher = null;
            if (watch)
            {
                try
                {
                    watcher = new FileSystemWatcher(path)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                            | NotifyFilters.Attributes | NotifyFilters.Size
                    };
                    watcher.Created += (_, _) => ScheduleReload(version);
                    watcher.Changed += (_, _) => ScheduleReload(version);
                    watcher.Deleted += (_, _) => ScheduleReload(version);
                    watcher.Renamed += (_, _) => ScheduleReload(version);
                    watcher.Error += (source, error) => OnWatcherError((FileSystemWatcher)source, error.GetException(), version);
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    MiniFolderCatalog.RetireWatcher(watcher);
                    watcher = null;
                }
            }
            try
            {
                return (ReadEntries()
                    .OrderBy(item => item is DirectoryInfo ? 0 : 1)
                    .ThenBy(item => item.Name, Ui.FileNameOrder)
                    .ToArray(), watcher);

                IEnumerable<FileSystemInfo> ReadEntries()
                {
                    cancellation.ThrowIfCancellationRequested();
                    foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        yield return entry;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                MiniFolderCatalog.RetireWatcher(watcher);
                throw;
            }
            catch (DirectoryNotFoundException)
            {
                MiniFolderCatalog.RetireWatcher(watcher);
                return (null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                MiniFolderCatalog.RetireWatcher(watcher);
                App.Log("读取小文件夹失败: " + ex.Message);
                return (null, null);
            }
        }

        private void OnWatcherError(FileSystemWatcher watcher, Exception error, int version)
        {
            if (error is InternalBufferOverflowException)
            {
                ScheduleReload(version);
                return;
            }
            if (_disposed || !_catalogDemand || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_disposed || !_catalogDemand || version != _loadVersion
                    || (_watcher != null && !ReferenceEquals(_watcher, watcher))) return;
                InvalidateCatalog();
                ScheduleReload(_loadVersion);
            }));
        }

        private void ScheduleReload(int version)
        {
            lock (_reloadGate)
            {
                if (_disposed || !_catalogDemand || version != _loadVersion || Dispatcher.HasShutdownStarted) return;
                _lastFolderChange = Environment.TickCount64;
                if (_reloadQueued) return;
                _firstFolderChange = _lastFolderChange;
                _reloadQueued = true;
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ProcessCatalogChanges(version)));
        }

        private void ProcessCatalogChanges(int version)
        {
            long remaining;
            lock (_reloadGate)
            {
                if (version != _loadVersion || !_reloadQueued) return;
                remaining = Math.Min(_firstFolderChange + MaxReloadDelayMs, _lastFolderChange + ReloadDelayMs)
                    - Environment.TickCount64;
                if (remaining <= 0) _reloadQueued = false;
            }
            _debounce.Stop();
            if (remaining <= 0) _ = LoadFilesAsync(contentsChanged: true);
            else
            {
                _debounce.Interval = TimeSpan.FromMilliseconds(remaining);
                _debounce.Start();
            }
        }

        private Task ShowFilesAsync()
        {
            var files = _files;
            int revision = _viewRevision;
            if (files == null || _textFilterDelay?.IsEnabled == true || _disposed || !Open || FileDropInteractionActive
                || (ReferenceEquals(_displayedFiles, files) && _displayedRevision == revision)) return Task.CompletedTask;
            if (ReferenceEquals(_preparingFiles, files) && _preparingRevision == revision
                && _filterRequest is { IsCancellationRequested: false }) return _viewTask;
            return _viewTask = BuildFolderViewAsync(files, revision);
        }

        private async Task BuildFolderViewAsync(FileSystemInfo[] files, int revision)
        {
            bool CanApply() => !_disposed && Open && !FileDropInteractionActive
                && ReferenceEquals(_files, files) && revision == _viewRevision;
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
                    if (existing.TryGetValue(file.FullName, out var tile) && SameFileMetadata(tile.File, file))
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
            image.Source = IconCache.GetFallback(isDir, item.Extension);
            image.Loaded += (_, _) => { if (!_disposed) IconCache.LoadAsync(item, image); };
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
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Apps || key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    if (!e.IsRepeat) ShowItemMenu(item, displayName, tile);
                    e.Handled = true;
                    return;
                }
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
                ShowItemMenu(item, displayName);
            };
            if (isDir)
                WireFileDrop(tile, item.FullName, RunFileOperation);
            return tile;
        }

        private void ShowItemMenu(FileSystemInfo item, string name, FrameworkElement keyboardAnchor = null)
        {
            if (_selection.Contains(item.FullName) && _selection.Count > 1)
            {
                OpenMenu(SelectionActions(), $"已选 {_selection.Count} 项");
                return;
            }
            bool isDir = item is DirectoryInfo;
            var items = new List<FlyoutItem>
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
            OpenMenu(items, name);

            void OpenMenu(IReadOnlyList<FlyoutItem> actions, string caption)
                => TrackFlyout(keyboardAnchor == null ? FlyoutMenu.Show(actions, caption)
                    : FlyoutMenu.ShowKeyboard(actions, caption, keyboardAnchor));
        }

        private void ShowFolderMenu(bool fromPanel = false)
        {
            if (fromPanel && !_listView.IsHitTestVisible) return;
            string path = fromPanel ? CurrentDirectory : _directory;
            if (path == null) return;
            var actions = new List<FlyoutItem>
            {
                new FlyoutItem("重新读取", () => _ = LoadFilesAsync(contentsChanged: true)),
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
            _textFilterDelay?.Stop();
            InvalidateCatalog();
            base.Dispose();
        }
    }
}
