using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DesktopBar.UI
{
    public partial class FolderModule
    {
        private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
        private string _selectionAnchor;
        private bool _selectionMode;
        private Button _selectButton;
        private WrapPanel _batchActions;
        private ScrollViewer _gridScroll;
        private ListBox _listView;

        private FrameworkElement BuildSelectionToolbar()
        {
            var row = new StackPanel();
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            _selectButton = PanelControls.GlassButton("多选", () =>
            {
                if (_selectionMode) ClearSelection();
                else
                {
                    _selectionMode = true;
                    UpdateSelection();
                }
            }, compact: true);
            _selectButton.HorizontalAlignment = HorizontalAlignment.Left;
            controls.Children.Add(_selectButton);
            controls.Children.Add(BuildPinButton());
            controls.Children.Add(BuildFileFilter());
            var summary = new DockPanel { Margin = new Thickness(0, 2 * Ui.Scale, 0, 4 * Ui.Scale) };
            DockPanel.SetDock(controls, Dock.Right);
            summary.Children.Add(controls);
            summary.Children.Add(_folderStatus);
            row.Children.Add(summary);
            _batchActions = new WrapPanel { Margin = new Thickness(3, 3, 3, 4), Visibility = Visibility.Collapsed };
            foreach (var item in SelectionActions())
                _batchActions.Children.Add(PanelControls.GlassButton(item.Text, item.OnClick, compact: true));
            row.Children.Add(_batchActions);
            return row;
        }

        private FrameworkElement BuildVirtualList()
        {
            _listView = PanelControls.VirtualizedFileList<FolderRowHost>(SystemParameters.PrimaryScreenHeight * .42);
            return _listView;
        }

        private void SetFolderViewMode()
        {
            bool list = Preferences.ListView;
            _viewButton.Content = list ? "网格" : "列表";
            _gridScroll.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
            _listView.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowVirtualRows(FileSystemInfo[] matches)
        {
            var scroll = Ui.VisualDescendants<ScrollViewer>(_listView).FirstOrDefault();
            double offset = scroll?.VerticalOffset ?? 0;
            var previous = _listView.ItemsSource as FolderRow[];
            string anchor = previous != null && offset >= 0 && offset < previous.Length
                ? previous[(int)offset].File.FullName : null;
            var rows = matches.Select(file => new FolderRow(file, () => MakeItem(file))).ToArray();
            _listView.ItemsSource = rows;
            if (anchor != null && Array.Find(rows, row => row.File.FullName == anchor) is FolderRow target)
                _listView.ScrollIntoView(target);
            if (_displayedRevision != _viewRevision) EaseInResults(_listView);
        }

        private void FocusFirstMatch()
        {
            if (Preferences.ListView && _listView.Items.Count > 0)
            {
                if (!_listView.IsHitTestVisible) return;
                var first = _listView.Items[0];
                int revision = _viewRevision;
                int keyVersion = _filterKeyVersion;
                _listView.ScrollIntoView(first);
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusRow(allowRetry: true)));

                void FocusRow(bool allowRetry)
                {
                    if (!Open || !_filter.IsKeyboardFocusWithin || !_listView.IsHitTestVisible
                        || revision != _viewRevision || keyVersion != _filterKeyVersion
                        || !ReferenceEquals(_displayedFiles, _files)) return;
                    if (_listView.Items.Count > 0 && ReferenceEquals(_listView.Items[0], first)
                        && _listView.ItemContainerGenerator.ContainerFromItem(first) is ListBoxItem row)
                    {
                        var tile = Ui.VisualDescendants<Border>(row).FirstOrDefault(item => item.Focusable);
                        if (tile?.Focus() == true) return;
                    }
                    if (!allowRetry) return;
                    // A virtualized item can still be waiting for its row to be realized after replacing ItemsSource.
                    _listView.UpdateLayout();
                    Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => FocusRow(allowRetry: false)));
                }
            }
            else if (_gridScroll.IsHitTestVisible && _tiles.FirstOrDefault() is FolderTile tile) tile.View.Focus();
        }

        private bool SelectItem(FileSystemInfo item, int clicks)
        {
            var modifiers = Keyboard.Modifiers;
            if (!_selectionMode && (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) return false;
            if (clicks > 1 && modifiers == ModifierKeys.None) return false;
            _selectionMode = true;
            if ((modifiers & ModifierKeys.Shift) != 0 && _selectionAnchor != null)
            {
                int start = Array.FindIndex(_matchedFiles, file => _selection.Comparer.Equals(_selectionAnchor, file.FullName));
                int end = Array.FindIndex(_matchedFiles, file => _selection.Comparer.Equals(item.FullName, file.FullName));
                if (start >= 0 && end >= 0)
                {
                    if ((modifiers & ModifierKeys.Control) == 0) _selection.Clear();
                    for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++) _selection.Add(_matchedFiles[i].FullName);
                    UpdateSelection();
                    return true;
                }
            }
            if (!_selection.Add(item.FullName)) _selection.Remove(item.FullName);
            _selectionAnchor = item.FullName;
            UpdateSelection();
            return true;
        }

        private bool HandleSelectionKey(KeyEventArgs args)
        {
            if (_filter.IsKeyboardFocusWithin || _preview.Visibility == Visibility.Visible) return false;
            if (args.Key == Key.Escape && _selectionMode) ClearSelection();
            else if (Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.A)
            {
                _selectionMode = true;
                foreach (var file in _matchedFiles) _selection.Add(file.FullName);
                UpdateSelection();
            }
            else if (_selection.Count > 0 && Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.C) RunSelection("copy");
            else if (_selection.Count > 0 && Keyboard.Modifiers == ModifierKeys.Control && args.Key == Key.X) RunSelection("cut");
            else if (_selection.Count > 0 && Keyboard.Modifiers == ModifierKeys.None && args.Key == Key.Delete) RunSelection("delete");
            else return false;
            args.Handled = true;
            return true;
        }

        private FlyoutItem[] SelectionActions() => new[]
        {
            new FlyoutItem("复制", () => RunSelection("copy")),
            new FlyoutItem("剪切", () => RunSelection("cut")),
            new FlyoutItem("删除", () => RunSelection("delete"), danger: true)
        };

        private void RunSelection(string verb)
        {
            var paths = _selection.ToArray();
            if (paths.Length == 0) return;
            RunFileOperation(() => Interop.ShellFiles.Invoke(FileOperationOwner, paths, verb));
        }

        private string[] SelectedPaths(string item)
            => _selection.Contains(item) ? _selection.ToArray() : new[] { item };

        private void ReconcileSelection(FileSystemInfo[] matches)
        {
            if (_selection.Count == 0) return;
            var available = matches.Select(item => item.FullName).ToHashSet(_selection.Comparer);
            _selection.IntersectWith(available);
            UpdateSelection();
        }

        private void ClearSelection()
        {
            _selectionMode = false;
            _selectionAnchor = null;
            _selection.Clear();
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            _selectButton.Content = _selectionMode ? "退出多选" : "多选";
            if (_available) UpdateFolderCount(_matchedFiles.Length);
            _batchActions.Visibility = _selection.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var tile in _tiles) PaintTile(tile.View);
            foreach (var tile in Ui.VisualDescendants<FolderRowHost>(_listView).Select(row => row.Content).OfType<Border>()) PaintTile(tile);
        }

        private void PaintTile(Border tile)
        {
            if (tile.Tag is string path && _selection.Contains(path))
            {
                tile.SetResourceReference(Border.BackgroundProperty, "ThemePanelSelected");
                tile.SetResourceReference(Border.BorderBrushProperty, "SettingsAccent");
            }
            else
            {
                if (tile.IsMouseOver || tile.IsKeyboardFocusWithin)
                    tile.SetResourceReference(Border.BackgroundProperty, "ThemePanelControl");
                else tile.Background = Brushes.Transparent;
                tile.BorderBrush = Brushes.Transparent;
            }
            tile.BorderThickness = new Thickness(1);
        }

        private static void EaseInResults(FrameworkElement content)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(.84, 1, TimeSpan.FromMilliseconds(115))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
        }

    }

    internal sealed record FolderRow(FileSystemInfo File, Func<Border> Create);

    public sealed class FolderRowHost : ContentControl
    {
        public FolderRowHost()
        {
            DataContextChanged += (_, args) => Content = (args.NewValue as FolderRow)?.Create();
        }
    }
}
