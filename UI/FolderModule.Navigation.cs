using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DesktopBar.UI
{
    public partial class FolderModule
    {
        private static readonly (string Name, Func<FileSystemInfo, bool> Matches)[] FileKinds =
        {
            ("全部类型", _ => true),
            ("文件夹", item => item is DirectoryInfo),
            ("图片", item => item is FileInfo && FileImage.IsImage(item.Extension)),
            ByExtension("视频", ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".flv", ".mts", ".mpeg", ".mpg", ".ts", ".3gp"),
            ByExtension("音频", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".amr", ".mid"),
            ByExtension("文档", ".doc", ".docx", ".docm", ".rtf", ".odt", ".wps", ".pages", ".epub", ".chm"),
            ByExtension("表格", ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv", ".tsv", ".ods", ".et", ".numbers"),
            ByExtension("演示文稿", ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".odp", ".dps", ".key"),
            ByExtension("PDF", ".pdf"),
            ByExtension("文本与代码", ".txt", ".md", ".log", ".ini", ".json", ".xml", ".yml", ".yaml", ".toml", ".html",
                ".css", ".js", ".jsx", ".ts", ".tsx", ".py", ".cs", ".java", ".c", ".cpp", ".h", ".rs", ".go", ".sql"),
            ByExtension("压缩包", ".zip", ".7z", ".rar", ".tar", ".gz", ".xz", ".bz2", ".zst", ".cab", ".tgz"),
            ByExtension("程序与安装包", ".exe", ".msi", ".msix", ".appx", ".appxbundle", ".msixbundle", ".bat", ".cmd", ".ps1", ".iso"),
            ByExtension("快捷方式", ".lnk", ".url", ".website")
        };
        private static readonly string[] ModifiedPeriods = { "不限时间", "今天", "最近 7 天", "最近 30 天" };
        private string _insideDirectory;
        private DockPanel _locationBar;
        private TextBlock _location;
        private Button _locationButton;
        private Button _pinPanelButton, _fileFilterButton;
        private bool _panelPinned;
        private int _fileKind, _modifiedPeriod;

        private static (string Name, Func<FileSystemInfo, bool> Matches) ByExtension(string name, params string[] extensions)
            => (name, item => item is FileInfo && extensions.Contains(item.Extension, StringComparer.OrdinalIgnoreCase));

        private bool HasFileFilter => _fileKind != 0 || _modifiedPeriod != 0;

        private DateTime ModifiedCutoff() => _modifiedPeriod switch
        {
            1 => DateTime.Today.ToUniversalTime(),
            2 => DateTime.UtcNow.AddDays(-7),
            3 => DateTime.UtcNow.AddDays(-30),
            _ => DateTime.MinValue
        };

        private string CurrentDirectory => _insideDirectory ?? _directory;

        private FrameworkElement BuildFolderLocation()
        {
            _locationBar = new DockPanel { Visibility = Visibility.Collapsed };
            var up = PanelControls.GlassButton("上一级", () => NavigateUp(), compact: true);
            up.ToolTip = "上一级（Alt+↑）";
            DockPanel.SetDock(up, Dock.Left);
            _locationBar.Children.Add(up);
            _location = PanelControls.Text("");
            _location.TextTrimming = TextTrimming.CharacterEllipsis;
            _locationButton = PanelControls.GlassButton("", ShowAncestorMenu, compact: true);
            _locationButton.HorizontalContentAlignment = HorizontalAlignment.Left;
            _locationButton.Content = _location;
            _locationBar.Children.Add(_locationButton);
            return _locationBar;
        }

        private bool NavigateUp()
        {
            if (_insideDirectory == null || !_listView.IsHitTestVisible) return false;
            NavigateTo(Path.GetDirectoryName(_insideDirectory));
            return true;
        }

        private void ShowAncestorMenu()
        {
            if (_insideDirectory == null || !_listView.IsHitTestVisible) return;
            var ancestors = new System.Collections.Generic.List<FlyoutItem>();
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_directory));
            for (string path = Path.GetDirectoryName(_insideDirectory); path != null; path = Path.GetDirectoryName(path))
            {
                bool atRoot = string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
                string destination = path;
                ancestors.Add(new FlyoutItem(atRoot ? _name : Path.GetFileName(path), () => NavigateTo(destination)));
                if (atRoot) break;
            }
            ancestors.Reverse();
            TrackFlyout(FlyoutMenu.Show(ancestors, "返回目录", _locationButton));
        }

        private void OpenInside(FileSystemInfo item)
        {
            if (item is DirectoryInfo) NavigateTo(item.FullName);
            else DesktopActions.Open(item.FullName);
        }

        private void NavigateTo(string path)
        {
            if (string.IsNullOrEmpty(path) || _directory == null
                || string.Equals(path, CurrentDirectory, StringComparison.OrdinalIgnoreCase)) return;
            string relative = Path.GetRelativePath(_directory, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
            _insideDirectory = relative == "." ? null : path;
            _watcher?.Dispose();
            _watcher = null;
            _debounce.Stop();
            System.Threading.Interlocked.Exchange(ref _reloadQueued, 0);
            _filterRequest?.Cancel();
            _files = null;
            _matchedFiles = Array.Empty<FileSystemInfo>();
            _available = false;
            _hasSnapshot = false;
            if (_listView.IsKeyboardFocusWithin || _gridScroll.IsKeyboardFocusWithin) LeaveKeyboardMode();
            _listView.IsHitTestVisible = _gridScroll.IsHitTestVisible = false;
            _folderStatus.Text = "正在读取…";
            _folderStatus.ToolTip = null;
            _filter.Clear();
            ClearSelection();
            CancelPreview();
            WireFileDrop(PanelWindow, null, RunFileOperation);
            _ = LoadFilesAsync();
        }

        private void UpdateFolderLocation()
        {
            _locationBar.Visibility = _insideDirectory == null ? Visibility.Collapsed : Visibility.Visible;
            if (_insideDirectory == null) return;
            string relative = Path.GetRelativePath(_directory, _insideDirectory);
            _location.Text = _name + " › " + relative.Replace(Path.DirectorySeparatorChar.ToString(), " › ");
            _locationButton.ToolTip = "返回上级目录\n" + _insideDirectory;
        }

        private Button BuildPinButton()
        {
            _pinPanelButton = PanelControls.GlassButton("钉住", () => SetPanelPinned(!_panelPinned), compact: true);
            return _pinPanelButton;
        }

        private void SetPanelPinned(bool pinned)
        {
            _panelPinned = pinned;
            _pinPanelButton.Content = pinned ? "取消钉住" : "钉住";
        }

        private Button BuildFileFilter()
        {
            _fileFilterButton = PanelControls.GlassButton("筛选", ShowFileFilters, compact: true);
            return _fileFilterButton;
        }

        private void ShowFileFilters()
        {
            var kinds = FileKinds.Select((category, kind) => new FlyoutItem(
                (_fileKind == kind ? "✓  " : "") + category.Name, () =>
            {
                _fileKind = kind;
                ApplyFileFilter();
            }, group: "文件类型"));
            var periods = ModifiedPeriods.Select((name, period) => new FlyoutItem(
                (_modifiedPeriod == period ? "✓  " : "") + name, () =>
            {
                _modifiedPeriod = period;
                ApplyFileFilter();
            }, group: "修改时间"));
            TrackFlyout(FlyoutMenu.Show(kinds.Concat(periods).ToArray(), anchor: _fileFilterButton));
        }

        private void ApplyFileFilter()
        {
            _fileFilterButton.Content = HasFileFilter ? "筛选 ✓" : "筛选";
            _fileFilterButton.ToolTip = HasFileFilter
                ? $"{FileKinds[_fileKind].Name} · {ModifiedPeriods[_modifiedPeriod]}"
                : null;
            _visibleLimit = 144;
            RefreshView();
        }

        private static bool MatchesCategory(FileSystemInfo item, int kind, DateTime cutoff)
            => FileKinds[kind].Matches(item) && item.LastWriteTimeUtc >= cutoff;
    }
}
