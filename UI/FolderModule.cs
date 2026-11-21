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
    public class FolderModule : ModuleBase
    {
        private const int PrefetchLimit = 64;
        private sealed record FolderTile(FileSystemInfo File, Border View);
        private readonly string _directory;
        private readonly string _name;
        private readonly WrapPanel _wrap = new WrapPanel();
        private readonly DispatcherTimer _debounce;
        private const int ReloadDelayMs = 300;
        private long _lastFolderChange;
        private int _reloadQueued;
        private FileSystemWatcher _watcher;
        private FileSystemInfo[] _files, _displayedFiles, _preparingFiles;
        private FolderTile[] _tiles = Array.Empty<FolderTile>();
        private int _loadVersion;
        private volatile bool _disposed;

        public FolderModule(string name, TabStyle style) : base(style)
        {
            _name = name;
            _directory = MiniFolderCatalog.GetFolderPath(App.Settings.StorageRoot, name);
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
                ShellOpen.OpenFolder(_directory);
            };
            Pill.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowFolderMenu();
            };

            Panel.Width = 190 * Ui.Scale;
            Panel.Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = SystemParameters.PrimaryScreenHeight * 0.5,
                Content = _wrap
            };
            Panel.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowFolderMenu();
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

        protected override void OnFileDropSettled() => _ = ShowFilesAsync();

        private async Task LoadFilesAsync()
        {
            if (_directory == null || _disposed) return;
            int version = ++_loadVersion;
            bool watch = _watcher == null;
            var snapshot = await Task.Run(() => ReadFolder(watch));
            if (_disposed || version != _loadVersion)
            {
                snapshot.Watcher?.Dispose();
                return;
            }
            _watcher ??= snapshot.Watcher;
            _files = snapshot.Files ?? _files ?? Array.Empty<FileSystemInfo>();
            for (int i = 0; i < Math.Min(PrefetchLimit, _files.Length); i++)
                IconCache.LoadAsync(_files[i]);
            if (Open) await ShowFilesAsync();
        }

        private (FileSystemInfo[] Files, FileSystemWatcher Watcher) ReadFolder(bool watch)
        {
            FileSystemWatcher watcher = null;
            if (watch)
            {
                try
                {
                    watcher = new FileSystemWatcher(_directory)
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
                var files = new DirectoryInfo(_directory).GetFileSystemInfos()
                    .OrderBy(item => item is DirectoryInfo ? 0 : 1)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return (files, watcher);
            }
            catch (DirectoryNotFoundException)
            {
                watcher?.Dispose();
                return (Array.Empty<FileSystemInfo>(), null);
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
            bool CanApply() => !_disposed && Open && !FileDropInteractionActive && ReferenceEquals(_files, files);
            if (files == null || !CanApply() || ReferenceEquals(_displayedFiles, files)
                || ReferenceEquals(_preparingFiles, files)) return;

            _preparingFiles = files;
            try
            {
                var existing = _tiles.ToDictionary(tile => tile.File.FullName, StringComparer.Ordinal);
                var next = new FolderTile[files.Length];
                int prepared = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    var file = files[i];
                    if (existing.TryGetValue(file.FullName, out var tile) && file.GetType() == tile.File.GetType()
                        && file.LastWriteTimeUtc == tile.File.LastWriteTimeUtc)
                    {
                        next[i] = tile;
                        continue;
                    }
                    next[i] = new FolderTile(file, MakeItem(file));
                    if (++prepared % 8 == 0 && i + 1 < files.Length)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        if (!CanApply()) return;
                    }
                }

                // Keep the current tree on screen while preparing icons and commit the layout in one pass.
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

                int cols = Math.Min(4, Math.Max(1, files.Length));
                _wrap.Width = cols * 76 * Ui.Scale;
                Panel.Width = Math.Max(190, cols * 76 + 26) * Ui.Scale;
                _tiles = next;
                _displayedFiles = files;
            }
            finally { if (ReferenceEquals(_preparingFiles, files)) _preparingFiles = null; }
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
            var body = new StackPanel { Width = 68 * scale, Margin = new Thickness(3 * scale, 8 * scale, 3 * scale, 8 * scale) };
            body.Children.Add(image);
            body.Children.Add(label);
            var motion = new ScaleTransform(1, 1);
            var tile = new Border
            {
                CornerRadius = new CornerRadius(8 * scale), Background = Brushes.Transparent, Child = body,
                Cursor = Cursors.Hand, ToolTip = item.Name, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = motion
            };
            tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BackgroundProperty, "ThemePanelControl");
            tile.MouseLeave += (_, _) => { tile.Background = Brushes.Transparent; AnimateScale(motion, 1); };
            tile.MouseLeftButtonDown += (_, _) => AnimateScale(motion, 0.93);
            ShellFileActions.WireDrag(tile, () =>
            {
                AnimateScale(motion, 1);
                RunFileOperation(() => ShellFiles.Drag(FileOperationOwner, item.FullName));
            }, _ =>
            {
                AnimateScale(motion, 1);
                if (isDir) ShellOpen.OpenFolder(item.FullName);
                else ShellOpen.OpenPath(item.FullName);
            });
            tile.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var items = new System.Collections.Generic.List<FlyoutItem>
                {
                    new FlyoutItem("打开", () => ShellOpen.OpenPath(item.FullName)),
                    ItemVerb("复制", item.FullName, "copy"),
                    ItemVerb("剪切", item.FullName, "cut"),
                    PasteItem(isDir ? item.FullName : _directory),
                    RenameItem(item.FullName),
                    ItemVerb("删除", item.FullName, "delete", danger: true),
                    new FlyoutItem("在资源管理器中定位", () => ShellOpen.RevealItem(item.FullName))
                };
                TrackFlyout(FlyoutMenu.Show(items, displayName));
            };
            if (isDir)
                WireFileDrop(tile, item.FullName, RunFileOperation);
            return tile;
        }

        private void ShowFolderMenu()
        {
            if (_directory == null) return;
            TrackFlyout(FlyoutMenu.Show(new[]
            {
                PasteItem(_directory),
                RenameItem(_directory),
                ItemVerb("删除小文件夹", _directory, "delete", danger: true),
                new FlyoutItem("在资源管理器中打开", () => ShellOpen.OpenFolder(_directory))
            }, _name));
        }

        private FlyoutItem ItemVerb(string label, string path, string verb, bool danger = false)
            => new FlyoutItem(label, () => RunFileOperation(() => ShellFiles.Invoke(FileOperationOwner, path, verb)), danger);

        private FlyoutItem PasteItem(string path)
            => new FlyoutItem("粘贴", () => RunFileOperation(() => ShellFiles.Paste(FileOperationOwner, path)));

        private FlyoutItem RenameItem(string path)
            => new FlyoutItem("重命名", () => RunFileOperation(() => ShellFileActions.Rename(FileOperationOwner, path)));

        private static void AnimateScale(ScaleTransform transform, double value)
        {
            var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 125 : 0));
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        public override void Dispose()
        {
            _disposed = true;
            _debounce.Stop();
            _watcher?.Dispose();
            base.Dispose();
        }
    }
}
