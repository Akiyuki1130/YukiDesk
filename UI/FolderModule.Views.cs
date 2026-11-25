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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.Models;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    public partial class FolderModule
    {
        private TextBox _filter;
        private DispatcherTimer _textFilterDelay;
        private Button _sortButton, _viewButton;
        private TextBlock _folderStatus;
        private StackPanel _folderBrowser;
        private Border _preview;
        private Image _previewImage;
        private TextBlock _previewLabel;
        private Button _previewPrevious, _previewNext;
        private int _viewRevision, _preparingRevision, _displayedRevision = -1, _previewRevision, _filterKeyVersion;
        private int _previewIndex;
        private CancellationTokenSource _previewRead;
        private int _visibleLimit = 144;
        private Button _showMore;
        private FileSystemInfo[] _previewItems;
        private bool _available, _hasSnapshot;

        private FolderPreferences Preferences
        {
            get
            {
                var views = App.Settings.FolderViews;
                var key = CurrentDirectory ?? _name;
                if (!views.TryGetValue(key, out var view)) views[key] = view = new FolderPreferences();
                return view;
            }
        }

        private bool IsInbox => string.Equals(App.Settings.InboxFolder, CurrentDirectory, StringComparison.OrdinalIgnoreCase);

        internal void RefreshInboxRole(string previousInbox)
        {
            if (IsInbox != string.Equals(previousInbox, CurrentDirectory, StringComparison.OrdinalIgnoreCase)) RefreshView();
        }

        private FrameworkElement BuildFolderContent()
        {
            var browser = _folderBrowser = new StackPanel();
            var tools = new DockPanel { Margin = new Thickness(3, 2, 3, 5), LastChildFill = true };
            _sortButton = PanelControls.GlassButton("排序", () =>
            {
                LeaveKeyboardMode();
                ShowSortMenu();
            }, compact: true);
            DockPanel.SetDock(_sortButton, Dock.Right);
            tools.Children.Add(_sortButton);
            _viewButton = PanelControls.GlassButton(Preferences.ListView ? "网格" : "列表", () =>
            {
                LeaveKeyboardMode();
                Preferences.ListView = !Preferences.ListView;
                App.Save();
                RefreshView();
            }, compact: true);
            DockPanel.SetDock(_viewButton, Dock.Right);
            tools.Children.Add(_viewButton);
            _filter = new TextBox { MinWidth = 70, Height = 26 * Ui.Scale, MinHeight = 0,
                Padding = new Thickness(8 * Ui.Scale, 2 * Ui.Scale, 8 * Ui.Scale, 2 * Ui.Scale), FontSize = 12 * Ui.Scale,
                ToolTip = "输入文件名或拼音首字母，例如 fp pdf" };
            _filter.SetResourceReference(TextBox.StyleProperty, "DarkBox");
            _filter.SetResourceReference(TextBox.BackgroundProperty, "ThemePanelHover");
            _filter.SetResourceReference(TextBox.BorderBrushProperty, "ThemePanelBorder");
            _filter.SetResourceReference(TextBox.ForegroundProperty, "ThemePanelText");
            _filter.SetResourceReference(TextBox.CaretBrushProperty, "ThemePanelText");
            System.Windows.Automation.AutomationProperties.SetName(_filter, "筛选文件名");
            _filter.PreviewMouseLeftButtonDown += (_, _) => EnterPanelKeyboardMode(_filter);
            _filter.TextChanged += (_, _) => OnTextFilterChanged();
            _filter.PreviewKeyDown += (_, e) =>
            {
                if (e.Handled || e.Key is not (Key.Enter or Key.Down) || Keyboard.Modifiers != ModifierKeys.None) return;
                e.Handled = true;
                if (!e.IsRepeat) _ = UseFirstFilterMatchAsync(e.Key);
            };
            tools.Children.Add(_filter);
            browser.Children.Add(tools);
            browser.Children.Add(BuildFolderLocation());
            _folderStatus = PanelControls.Text("正在读取…");
            _folderStatus.Margin = new Thickness(4 * Ui.Scale, 0, 4 * Ui.Scale, 0);
            _folderStatus.TextTrimming = TextTrimming.CharacterEllipsis;
            _folderStatus.VerticalAlignment = VerticalAlignment.Center;
            _gridScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = SystemParameters.PrimaryScreenHeight * 0.42,
                Content = _wrap
            };
            browser.Children.Add(BuildSelectionToolbar());
            browser.Children.Add(_gridScroll);
            browser.Children.Add(BuildVirtualList());
            SetFolderViewMode();
            _showMore = PanelControls.Button("显示更多", () => { _visibleLimit += 144; RefreshView(); });
            _showMore.Visibility = Visibility.Collapsed;
            browser.Children.Add(_showMore);
            _previewLabel = PanelControls.Text("");
            _previewLabel.Focusable = true;
            _previewLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            _previewImage = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center };
            _previewImage.SizeChanged += (_, args) =>
            {
                double radius = Math.Min(9 * Ui.Scale, Math.Min(args.NewSize.Width, args.NewSize.Height) / 2);
                _previewImage.Clip = new RectangleGeometry(new Rect(args.NewSize), radius, radius);
            };
            var previewContent = new StackPanel();
            previewContent.Children.Add(_previewLabel);
            var imageArea = new Grid { Height = Math.Min(225 * Ui.Scale, SystemParameters.WorkArea.Height * .40),
                Margin = new Thickness(0, 6 * Ui.Scale, 0, 6 * Ui.Scale) };
            imageArea.Children.Add(_previewImage);
            previewContent.Children.Add(imageArea);
            var previewActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            _previewPrevious = PanelControls.GlassButton("上一项", () => MovePreview(-1), compact: true);
            _previewNext = PanelControls.GlassButton("下一项", () => MovePreview(1), compact: true);
            previewActions.Children.Add(_previewPrevious);
            previewActions.Children.Add(_previewNext);
            var closePreview = PanelControls.GlassButton("关闭预览", CancelPreview, compact: true);
            closePreview.ToolTip = "关闭预览（空格或 Esc）";
            previewActions.Children.Add(closePreview);
            previewContent.Children.Add(previewActions);
            _preview = new Border { Child = previewContent, Margin = new Thickness(8), Visibility = Visibility.Collapsed };
            var root = new Grid();
            root.Children.Add(browser);
            root.Children.Add(_preview);
            PanelWindow.PreviewKeyDown += (_, e) =>
            {
                if (HandleFlyoutKey(e) || HandleSelectionKey(e)) return;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                bool previewing = _preview.Visibility == Visibility.Visible;
                if (e.Key == Key.Escape || previewing && e.Key == Key.Space)
                {
                    if (previewing) CancelPreview();
                    else if (_filter.Text.Length != 0) _filter.Clear();
                    else HidePanel();
                    e.Handled = true;
                }
                else if (previewing && (e.Key == Key.Left || e.Key == Key.Right))
                {
                    MovePreview(e.Key == Key.Left ? -1 : 1);
                    e.Handled = true;
                }
                else if (!previewing && (key == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt
                    || key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && !_filter.IsKeyboardFocusWithin))
                {
                    e.Handled = NavigateUp();
                }
                else if ((e.Key == Key.OemQuestion || e.Key == Key.Divide) && !_filter.IsKeyboardFocusWithin)
                {
                    EnterPanelKeyboardMode(_filter);
                    e.Handled = true;
                }
            };
            PanelWindow.Deactivated += (_, _) =>
            {
                if (!PanelWindow.Focusable) return;
                LeaveKeyboardMode();
                if (Open && !_panelPinned && !PanelWindow.IsMouseOver && !Root.IsMouseOver) HidePanel();
            };
            return root;
        }

        private void ShowSortMenu()
        {
            var sort = Preferences.Sort;
            var choices = new List<(string Key, string Name)>
                { ("name", "名称"), ("modified", "最近修改"), ("size", "大小（大到小）"), ("type", "类型") };
            if (IsInbox) choices.Insert(0, ("arrived", "最近拖入或新增"));
            TrackFlyout(FlyoutMenu.Show(choices.Select(item => new FlyoutItem(
                (sort == item.Key ? "✓  " : "") + item.Name, () =>
                {
                    Preferences.Sort = item.Key;
                    App.Save();
                    RefreshView();
                })).ToArray(), "排序方式", _sortButton));
        }

        private void UpdateFolderSnapshot(FileSystemInfo[] files)
        {
            if (_available != (files != null)) _viewRevision++;
            _available = files != null;
            if (files == null)
            {
                _files = Array.Empty<FileSystemInfo>();
                _hasSnapshot = false;
                return;
            }
            if (IsInbox && _hasSnapshot)
            {
                var before = _files.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var arrivals = Preferences.Arrivals;
                bool changed = false;
                foreach (var file in files)
                    if (!before.Contains(file.Name))
                    {
                        arrivals[file.Name] = DateTime.UtcNow;
                        changed = true;
                    }
                if (changed)
                {
                    Preferences.Arrivals = arrivals.OrderByDescending(pair => pair.Value).Take(100)
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                    App.Save();
                }
            }
            _hasSnapshot = true;
            _files = files;
        }

        private Func<FileSystemInfo[], CancellationToken, FileSystemInfo[]> BuildFolderQuery()
        {
            var view = Preferences;
            var words = _filter.Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => (Text: word, Initials: PinyinInitials.ParseTerm(word))).ToArray();
            var pinned = view.Pinned.ToHashSet(StringComparer.OrdinalIgnoreCase);
            string sort = view.Sort;
            var arrivals = sort == "arrived" && IsInbox ? new Dictionary<string, DateTime>(view.Arrivals) : null;
            int kind = _fileKind;
            DateTime cutoff = ModifiedCutoff();
            return (files, cancellation) =>
            {
                var groups = files.Where(item =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!MatchesCategory(item, kind, cutoff)) return false;
                    return words.All(word => item.Name.Contains(word.Text, StringComparison.CurrentCultureIgnoreCase)
                        || PinyinInitials.Matches(item.Name, word.Initials));
                }).OrderByDescending(item => pinned.Contains(item.Name));
                var sorted = sort switch
                {
                    "modified" => groups.ThenByDescending(item => item.LastWriteTimeUtc),
                    "size" => groups.ThenByDescending(ReadFileSize),
                    "type" => groups.ThenBy(item => item is DirectoryInfo ? "" : item.Extension, StringComparer.OrdinalIgnoreCase),
                    "arrived" when arrivals != null => groups.ThenByDescending(item => arrivals.GetValueOrDefault(item.Name)),
                    _ => groups.ThenBy(item => item is DirectoryInfo ? 0 : 1)
                };
                return sorted.ThenBy(item => item.Name, Ui.FileNameOrder).ToArray();
            };
        }

        private bool IsPinned(string name) => Preferences.Pinned.Contains(name, StringComparer.OrdinalIgnoreCase);

        private void TogglePin(string name)
        {
            if (IsPinned(name)) Preferences.Pinned.RemoveAll(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
            else Preferences.Pinned.Add(name);
            App.Save();
            RefreshView();
        }

        private void RefreshView()
        {
            _textFilterDelay?.Stop();
            _viewRevision++;
            _ = ShowFilesAsync();
        }

        private async Task UseFirstFilterMatchAsync(Key key)
        {
            _textFilterDelay?.Stop();
            int keyVersion = ++_filterKeyVersion;
            int revision = _viewRevision;
            await ShowFilesAsync();
            if (keyVersion != _filterKeyVersion || revision != _viewRevision || _displayedRevision != revision
                || !ReferenceEquals(_displayedFiles, _files) || !Open || !_filter.IsKeyboardFocusWithin
                || _matchedFiles.FirstOrDefault() is not FileSystemInfo first) return;
            if (key == Key.Enter) OpenInside(first);
            else FocusFirstMatch();
        }

        private void OnTextFilterChanged()
        {
            _visibleLimit = 144;
            if (!Open || _files is not { Length: > 512 })
            {
                RefreshView();
                return;
            }

            _viewRevision++;
            _filterRequest?.Cancel();
            if (_textFilterDelay == null)
            {
                _textFilterDelay = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(45)
                };
                _textFilterDelay.Tick += (_, _) =>
                {
                    _textFilterDelay.Stop();
                    _ = ShowFilesAsync();
                };
            }
            _textFilterDelay.Stop();
            _textFilterDelay.Start();
        }

        private void ShowFolderStatus(int count)
        {
            if (_available) UpdateFolderCount(count);
            else
            {
                _folderStatus.Text = "目录暂不可用";
                _folderStatus.ToolTip = "目录暂不可用，重新展开可重试";
            }
            _showMore.Visibility = !Preferences.ListView && count > _visibleLimit ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateFolderCount(int count)
        {
            string selected = _selection.Count == 0 ? "" : $" · 已选 {_selection.Count}";
            _folderStatus.Text = $"{count} 项{selected}";
            _folderStatus.ToolTip = count == 0 ? (_filter.Text.Length > 0 || HasFileFilter ? "没有匹配的项目" : "文件夹为空")
                : (IsInbox ? "收件箱 · " : "") + _folderStatus.Text;
        }

        private FrameworkElement BuildFileTile(FileSystemInfo item, Image image, TextBlock label, string name, double scale)
        {
            bool list = Preferences.ListView;
            var body = new StackPanel { Width = (list ? 300 : 68) * scale,
                Orientation = list ? Orientation.Horizontal : Orientation.Vertical,
                Margin = new Thickness(3 * scale, (list ? 4 : 8) * scale, 3 * scale, (list ? 4 : 8) * scale) };
            if (IsPinned(item.Name)) label.Text = "★ " + name;
            body.Children.Add(image);
            if (!list)
            {
                body.Children.Add(label);
                return body;
            }
            image.Width = image.Height = 26 * scale;
            var details = new StackPanel { Width = 261 * scale, Margin = new Thickness(8, 0, 0, 0) };
            label.TextAlignment = TextAlignment.Left;
            label.Margin = new Thickness(0);
            details.Children.Add(label);
            long bytes = ReadFileSize(item);
            string date = item.LastWriteTime.ToString("g");
            var caption = PanelControls.Text(bytes >= 0 ? date + " · " + FormatFileSize(bytes) : date);
            caption.TextTrimming = TextTrimming.CharacterEllipsis;
            caption.Opacity = 0.72;
            details.Children.Add(caption);
            body.Children.Add(details);
            return body;
        }

        private static long ReadFileSize(FileSystemInfo item)
        {
            if (item is not FileInfo file) return -1;
            try { return file.Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return -1; }
        }

        private static string FormatFileSize(long bytes)
        {
            const double mib = 1024 * 1024;
            return bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
                < 1024L * 1024 * 1024 => $"{bytes / mib:0.#} MB",
                _ => $"{bytes / (mib * 1024):0.#} GB"
            };
        }

        private void PreviewFile(FileSystemInfo file)
        {
            bool images = FileImage.IsImage(file.Extension);
            _previewItems = _matchedFiles.Where(item => item is FileInfo && (!images || FileImage.IsImage(item.Extension))).ToArray();
            _previewIndex = Array.FindIndex(_previewItems, item => item.FullName == file.FullName);
            if (_previewIndex < 0) return;
            _folderBrowser.Visibility = Visibility.Collapsed;
            _preview.Visibility = Visibility.Visible;
            _previewPrevious.IsEnabled = _previewNext.IsEnabled = _previewItems.Length > 1;
            EnterPanelKeyboardMode(_previewLabel);
            _ = LoadPreviewAsync(file.FullName);
        }

        private void MovePreview(int direction)
        {
            if (_previewItems == null || _previewItems.Length <= 1) return;
            _previewIndex = (_previewIndex + direction + _previewItems.Length) % _previewItems.Length;
            _ = LoadPreviewAsync(_previewItems[_previewIndex].FullName);
        }

        private async Task LoadPreviewAsync(string path)
        {
            int request = ++_previewRevision;
            _previewRead?.Cancel();
            using var read = new CancellationTokenSource();
            _previewRead = read;
            _previewImage.BeginAnimation(OpacityProperty, null);
            string name = Path.GetFileName(path);
            _previewLabel.ToolTip = name;
            _previewLabel.Text = name + " · 正在读取…";
            try
            {
                var image = await FilePreview.ReadAsync(path, read.Token);
                if (request != _previewRevision || _disposed) return;
                _previewImage.Source = image;
                if (image != null && SystemParameters.ClientAreaAnimation)
                    _previewImage.BeginAnimation(OpacityProperty, new DoubleAnimation(.72, 1, TimeSpan.FromMilliseconds(125))
                    { FillBehavior = FillBehavior.Stop });
                _previewLabel.Text = image != null ? name : name + " · 无法预览此文件";
            }
            finally { if (ReferenceEquals(_previewRead, read)) _previewRead = null; }
        }

        private void CancelPreview()
        {
            if (_preview.Visibility != Visibility.Visible) return;
            _previewRevision++;
            _previewRead?.Cancel();
            _previewItems = null;
            _previewImage.BeginAnimation(OpacityProperty, null);
            _previewImage.Source = null;
            if (_previewLabel.IsKeyboardFocusWithin) LeaveKeyboardMode();
            _preview.Visibility = Visibility.Collapsed;
            _folderBrowser.Visibility = Visibility.Visible;
        }
    }
}
