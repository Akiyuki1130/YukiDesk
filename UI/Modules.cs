using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Threading;
using FilePath = System.IO.Path;
using DesktopBar.Interop;
using DesktopBar.Models;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    public class SettingsModule : ModuleBase
    {
        public SettingsModule(TabStyle st) : base(st)
        {
            Pill.Width = 38 * Ui.Scale;
            Pill.Padding = new Thickness(0);
            // 与文件夹图标同级的雪花：六臂双层分叉，24 网格等比缩放。
            Pill.Child = CenterIcon(MakeSnowflakeGlyph(21));
            Pill.MouseLeftButtonUp += (s, e) => App.ShowSettings();
        }
        protected override bool HasPanel => false;
    }

    public class PowerModule : ModuleBase
    {
        public PowerModule(TabStyle st) : base(st)
        {
            Pill.Width = 38 * Ui.Scale;
            Pill.Padding = new Thickness(0);
            Pill.Child = CenterIcon(MakePowerGlyph(20.5));
            Pill.MouseLeftButtonUp += (s, e) => ShowPanel();
        }
        protected override bool HoverOpensPanel => false;

        protected override void OnPanelShown()
        {
            var body = new StackPanel { Width = 190 * Ui.Scale };
            body.Children.Add(MakePanelTitle("电源"));
            AddPowerButton(body, "关机", "将关闭 Windows", NativeMethods.Shutdown, true, true);
            AddPowerButton(body, "重启", "重新启动 Windows", NativeMethods.Restart, true, true);
            AddPowerButton(body, "睡眠", "进入睡眠模式", NativeMethods.Sleep, true, false);
            AddPowerButton(body, "锁定", "锁定当前会话", NativeMethods.Lock, false, false);
            AddPowerButton(body, "息屏", "关闭显示器，移动鼠标可唤醒", NativeMethods.DisplayOff, false, false);
            Panel.Child = body;
        }

        private void AddPowerButton(StackPanel body, string title, string sub, Func<bool> action,
            bool confirm, bool danger)
        {
            // 半透明玻璃底（同右键菜单行），不再是实心黑色。
            var b = new Button { Content = title, Style = (Style)Application.Current.FindResource("BtnGlass"), HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            b.ToolTip = sub;
            b.Click += (s, e) =>
            {
                bool allowed = !confirm || (danger
                    ? ConfirmDialog.ShowDanger(title, "确定要" + title + "吗？", title)
                    : ConfirmDialog.Show(title, "确定要" + title + "吗？", title));
                if (!allowed) return;
                if (!action()) ConfirmDialog.Notice(title + "失败", title + "操作未能完成。");
            };
            body.Children.Add(b);
        }
    }

    // 此电脑:单击打开资源管理器"此电脑"页,右键菜单提供"属性"(跳转系统设置的"关于"页)。
    public class ThisPcModule : ModuleBase
    {
        private const string ThisPcShellPath = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
        private const string AboutSettingsUri = "ms-settings:about";

        public ThisPcModule(TabStyle st) : base(st)
        {
            Pill.Width = 38 * Ui.Scale;
            Pill.Padding = new Thickness(0);
            Pill.Child = CenterIcon(MakeThisPcGlyph(20.5));
            Pill.MouseLeftButtonUp += (s, e) => NativeMethods.ShellExecuteOpen(ThisPcShellPath);
            Pill.MouseRightButtonUp += (s, e) =>
            {
                TrackFlyout(FlyoutMenu.Show(new List<FlyoutItem>
                {
                    new FlyoutItem("属性", () => NativeMethods.ShellExecuteOpen(AboutSettingsUri))
                }));
                e.Handled = true;
            };
        }
        protected override bool HoverOpensPanel => false;
        protected override bool HasPanel => false;
    }

    public class ClockModule : ModuleBase
    {
        private static readonly string[] WeekNames = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
        private readonly TextBlock _pillText, _pTime, _pWeek, _pDate, _pLunar, _pFest;
        private readonly DispatcherTimer _timer;
        public ClockModule(TabStyle st) : base(st)
        {
            _pillText = MakeText(""); Pill.Child = _pillText;
            // 下拉内容整体居中:文字居中对齐,并设最小宽度留出左右呼吸感。
            var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 170 * Ui.Scale };
            _pTime = MakeText("", 12); _pWeek = MakeText(""); _pDate = MakeText(""); _pLunar = MakeText(""); _pFest = MakeText("");
            foreach (var tb in new[] { _pTime, _pWeek, _pDate, _pLunar, _pFest }) tb.HorizontalAlignment = HorizontalAlignment.Center;
            _pTime.Margin = new Thickness(4, 2, 4, 6); _pWeek.Margin = new Thickness(4, 2, 4, 2); _pDate.Margin = new Thickness(4, 2, 4, 2); _pLunar.Margin = new Thickness(4, 2, 4, 2);
            _pFest.Margin = new Thickness(4, 0, 4, 4);
            _pFest.FontSize = Math.Max(9, 12 * Ui.Scale);
            body.Children.Add(_pTime); body.Children.Add(_pWeek); body.Children.Add(_pDate); body.Children.Add(_pLunar); body.Children.Add(_pFest); Panel.Child = body;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }; _timer.Tick += (s, e) => Refresh(); _timer.Start(); Refresh();
            // 模块隐藏(或整条顶栏隐藏)时停表,不再空转刷新 5 个 TextBlock。
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { Refresh(); _timer.Start(); }
                else _timer.Stop();
            };
        }
        protected override bool CenterPanelOnPill => true;
        private void Refresh()
        {
            var now = DateTime.Now;
            var parts = new List<string> { now.ToString(App.Settings.ShowSeconds ? "HH:mm:ss" : "HH:mm") };
            if (App.Settings.ShowDate) parts.Add(now.ToString("M月d日"));
            if (App.Settings.ShowWeekday) parts.Add("周" + "日一二三四五六"[(int)now.DayOfWeek]);
            string pill = string.Join("  ", parts);
            // 只在文字真的变了才赋值:分层窗口每次被弄脏都会整条重呈,同值赋值是纯浪费。
            if (_pillText.Text != pill) _pillText.Text = pill;
            // 面板没打开时不刷面板那 5 个 TextBlock(打开时 OnPanelShown 会补一次)。
            if (Panel == null || !Panel.IsVisible) return;
            string time = now.ToString("HH:mm:ss"), week = WeekNames[(int)now.DayOfWeek], date = now.ToString("yyyy年M月d日"), lunar = Lunar.ToLunarString(now);
            if (_pTime.Text != time) _pTime.Text = time;
            if (_pWeek.Text != week) _pWeek.Text = week;
            if (_pDate.Text != date) _pDate.Text = date;
            if (_pLunar.Text != lunar) _pLunar.Text = lunar;
            string fest = Festivals.GetTodayLabel(now);
            string festText = fest.Length > 0 ? "今 " + fest : "";
            if (_pFest.Text != festText) _pFest.Text = festText;
            var vis = fest.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_pFest.Visibility != vis) _pFest.Visibility = vis;
        }

        protected override void OnPanelShown()
        {
            base.OnPanelShown();
            Refresh();
        }
        public override void Dispose() { _timer.Stop(); base.Dispose(); }
    }

    public class MusicModule : ModuleBase
    {
        private readonly Image _smallImg, _bigImg;
        private readonly MarqueeText _pillMarquee, _panelMarquee;
        private readonly TextBlock _tArtist, _playGlyph, _placeholderGlyph;
        private string _mediaKey = "";
        private long _lastMediaGeneration;
        private string _lastTitle = "暂无音乐";
        public MusicModule(TabStyle st) : base(st)
        {
            var coverHost = new Grid { Width = 26 * Ui.Scale, Height = 26 * Ui.Scale };
            _placeholderGlyph = Glyph("♫", 14);
            _placeholderGlyph.Width = 26 * Ui.Scale;
            _placeholderGlyph.Height = 26 * Ui.Scale;
            _placeholderGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            _placeholderGlyph.VerticalAlignment = VerticalAlignment.Center;
            coverHost.Children.Add(_placeholderGlyph);
            _smallImg = new Image { Width = 26 * Ui.Scale, Height = 26 * Ui.Scale, Stretch = Stretch.UniformToFill }; coverHost.Children.Add(_smallImg);
            var coverBorder = new Border { Width = 26 * Ui.Scale, Height = 26 * Ui.Scale, CornerRadius = new CornerRadius(7 * Ui.Scale), Child = coverHost, VerticalAlignment = VerticalAlignment.Center }; coverBorder.Clip = new RectangleGeometry(new Rect(0, 0, 26 * Ui.Scale, 26 * Ui.Scale), 7 * Ui.Scale, 7 * Ui.Scale);
            // 歌名超宽时在药丸内滚动；未超宽时药丸收缩到内容宽度。
            _pillMarquee = new MarqueeText(MakeText("暂无音乐"), 150 * Ui.Scale, 20 * Ui.Scale, hug: true);
            _pillMarquee.Margin = new Thickness(8 * Ui.Scale, 0, 0, 0);
            var pillSp = new StackPanel { Orientation = Orientation.Horizontal }; pillSp.Children.Add(coverBorder); pillSp.Children.Add(_pillMarquee); Pill.Child = pillSp;

            _bigImg = new Image { Width = 96 * Ui.Scale, Height = 96 * Ui.Scale, Stretch = Stretch.UniformToFill };
            var bigCover = new Border
            {
                Width = 96 * Ui.Scale,
                Height = 96 * Ui.Scale,
                CornerRadius = new CornerRadius(14 * Ui.Scale),
                Child = _bigImg,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            bigCover.SetResourceReference(Border.BackgroundProperty, "ThemePanelSubtle");
            bigCover.Clip = new RectangleGeometry(new Rect(0, 0, 96 * Ui.Scale, 96 * Ui.Scale), 14 * Ui.Scale, 14 * Ui.Scale);
            // 展开页歌名同样滚动；超宽前保持居中。宽度 = 面板 300 - 内边距 24 - 标题左右边距 8。
            _panelMarquee = new MarqueeText(MakeText("暂无音乐", 2), 268 * Ui.Scale, 24 * Ui.Scale, hug: false);
            _pillMarquee.SetText("暂无音乐");
            _panelMarquee.SetText("暂无音乐");
            _tArtist = MakeText(""); _tArtist.TextAlignment = TextAlignment.Center;
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14 * Ui.Scale, 0, 0) }; btnRow.Children.Add(ShellButton(Glyph("⏮", 19), () => NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PREV))); _playGlyph = Glyph("▶", 19); btnRow.Children.Add(ShellButton(_playGlyph, () => NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PLAY_PAUSE))); btnRow.Children.Add(ShellButton(Glyph("⏭", 19), () => NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_NEXT)));
            var body = new StackPanel { Width = 300 * Ui.Scale }; body.Children.Add(bigCover); _panelMarquee.Margin = new Thickness(4 * Ui.Scale, 12 * Ui.Scale, 4 * Ui.Scale, 0); body.Children.Add(_panelMarquee); _tArtist.Margin = new Thickness(4 * Ui.Scale, 4 * Ui.Scale, 4 * Ui.Scale, 0); body.Children.Add(_tArtist); body.Children.Add(btnRow); Panel.Child = body;
        }

        public void Update(MediaInfo info)
        {
            if (info == null || info.Generation < _lastMediaGeneration) return;
            _lastMediaGeneration = info.Generation;

            string key = (info.TrackKey ?? "") + "|" + (info.ThumbnailKey ?? "");
            if (!string.Equals(key, _mediaKey, StringComparison.Ordinal))
            {
                _mediaKey = key;
                _smallImg.Source = null;
                _bigImg.Source = null;
            }

            string title = string.IsNullOrEmpty(info.Title) ? "暂无音乐" : info.Title;
            if (!string.Equals(title, _lastTitle, StringComparison.Ordinal))
            {
                _lastTitle = title;
                _pillMarquee.SetText(title);
                _panelMarquee.SetText(title);
            }

            _tArtist.Text = info.Artist ?? "";
            _smallImg.Source = info.Cover;
            _bigImg.Source = info.Cover;
            _playGlyph.Text = info.Playing ? "⏸" : "▶"; NudgeGlyph(_playGlyph, info.Playing ? "⏸" : "▶");
        }
        // Segoe UI Symbol 媒体符号的行盒下留白偏大，墨迹在圆形按键里整体偏下；
        // 按实测（48px 按钮内 19px 字形）逐字形向上做光学补偿。
        private static readonly Dictionary<string, double> GlyphNudge = new Dictionary<string, double>
        {
            ["⏮"] = -3.0,
            ["⏭"] = -2.5,
            ["⏸"] = -2.5,
            ["▶"] = -1.5
        };

        private TextBlock Glyph(string g, double size = 19)
        {
            var t = MakeIconGlyph(g, size, "Segoe UI Symbol");
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            NudgeGlyph(t, g);
            return t;
        }

        private void NudgeGlyph(TextBlock t, string g)
        {
            if (GlyphNudge.TryGetValue(g, out double dy))
                t.RenderTransform = new TranslateTransform(0, dy * Ui.Scale);
            else
                t.RenderTransform = null;
        }
        private Border ShellButton(FrameworkElement content, Action click)
        {
            var b = new Border
            {
                Width = 48 * Ui.Scale,
                Height = 48 * Ui.Scale,
                CornerRadius = new CornerRadius(24 * Ui.Scale),
                Cursor = Cursors.Hand,
                Child = content,
                Margin = new Thickness(5 * Ui.Scale, 0, 5 * Ui.Scale, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            b.SetResourceReference(Border.BackgroundProperty, "ThemePanelControl");
            b.MouseEnter += (s, e) => b.SetResourceReference(Border.BackgroundProperty, "ThemePanelControlHover");
            b.MouseLeave += (s, e) => b.SetResourceReference(Border.BackgroundProperty, "ThemePanelControl");
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }
    }

    public class VolumeModule : ModuleBase
    {
        private readonly VolumeService _volume = new VolumeService();
        private readonly FrameworkElement _summary;
        private readonly System.Windows.Shapes.Path _speakerWave1, _speakerWave2;
        private readonly TextBlock _value;
        private readonly Slider _slider;
        private DispatcherTimer _poll;
        private DispatcherTimer _writeTimer;
        private float? _pendingVolume;
        private DateTime _lastUserAdjustUtc;
        private bool _syncing;
        private bool _muted;
        private bool _volumeAvailable;
        private bool _volumeWritesEnabled;
        private bool _disposed;
        public VolumeModule(TabStyle st) : base(st)
        {
            Pill.Width = 38 * Ui.Scale;
            Pill.MinWidth = Pill.MaxWidth = 38 * Ui.Scale;
            Pill.Padding = new Thickness(0);
            Pill.HorizontalAlignment = HorizontalAlignment.Left;
            _summary = CenterIcon(MakeSpeakerGlyph(out _speakerWave1, out _speakerWave2, 23));
            Pill.Child = _summary;
            // 读取启动值只用于显示；初始化期间禁止任何回写系统音量。
            _syncing = true;
            float? initialVolumeRead = _volume.GetVolume();
            bool initialVolumeAvailable = initialVolumeRead.HasValue;
            _volumeAvailable = initialVolumeAvailable;
            float initialVolume = initialVolumeRead ?? 0;
            _muted = _volume.GetMute() ?? false;
            _syncing = false;
            var body = new StackPanel { Width = 230 * Ui.Scale };
            body.Children.Add(MakePanelTitle("音量"));
            body.Children.Add(_slider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = initialVolume,
                Style = (Style)Application.Current.FindResource("SliderDark"),
                // 量程只有 0..1，而 RangeBase.LargeChange 默认就是 1.0——正好等于整个量程。
                // SliderDark 模板把整条轨道填成了 IncreaseLarge/DecreaseLarge 两个 RepeatButton，
                // 于是点一次轨道就被 ±1.0 直接顶到 100% 或 0%（拖动滑块走 Thumb.DragDelta，不受影响，
                // 这就是"能拖不能点"的原因）。开 IsMoveToPointEnabled 让点击直接跳到鼠标位置，
                // 再把 LargeChange/SmallChange 缩到 2%/1%，键盘 PageUp/PageDown/方向键才可用。
                IsMoveToPointEnabled = true,
                LargeChange = 0.02,
                SmallChange = 0.01
            });
            _slider.SetResourceReference(Control.BackgroundProperty, "ThemePanelControl");
            body.Children.Add(_value = MakeText(initialVolumeAvailable ? Math.Round(initialVolume * 100) + "%" : "读取失败"));
            _slider.IsEnabled = initialVolumeAvailable;
            _value.HorizontalAlignment = HorizontalAlignment.Center;
            Panel.Child = body;
            // 写音量经 120ms 合并:一次拖动只落一次系统写,不逐刻度跨进程写。
            _writeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _writeTimer.Tick += (s, e) =>
            {
                _writeTimer.Stop();
                if (_pendingVolume.HasValue && _volumeAvailable && _volumeWritesEnabled && IsLoaded)
                {
                    _volume.SyncDefaultIfChanged();
                    _volume.SetVolume(_pendingVolume.Value);
                }
                _pendingVolume = null;
            };
            _slider.ValueChanged += (s, e) =>
            {
                _value.Text = Math.Round(e.NewValue * 100) + "%";
                UpdateIcon(e.NewValue, _muted);
                if (!_syncing && _volumeAvailable && _volumeWritesEnabled && IsLoaded)
                {
                    _pendingVolume = (float)e.NewValue;
                    _lastUserAdjustUtc = DateTime.UtcNow;
                    _writeTimer.Stop();
                    _writeTimer.Start();
                }
            };
            Pill.MouseLeftButtonUp += (s, e) =>
            {
                _volumeWritesEnabled = true;
                _muted = !_muted;
                if (_volumeAvailable && _volumeWritesEnabled && IsLoaded)
                {
                    // 静音写入前先跟随默认设备,否则设备切换后 800ms 内会把静音写到旧端点。
                    _volume.SyncDefaultIfChanged();
                    _volume.SetMute(_muted);
                }
                UpdateIcon(_slider.Value, _muted);
            };
            UpdateIcon(initialVolume, _muted);
            // 启动阶段只读系统音量；必须先发生明确的用户操作才允许写回。
            Unloaded += (s, e) => _volumeWritesEnabled = false;
            _slider.PreviewMouseLeftButtonDown += (s, e) => _volumeWritesEnabled = true;
            _slider.PreviewKeyDown += (s, e) => _volumeWritesEnabled = true;
            _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _poll.Tick += (s, e) =>
            {
                // 每个轮询周期都跟随默认设备;只在面板打开时轮询,一条 COM 查询开销可忽略。
                _volume.SyncDefaultIfChanged();
                float? current = _volume.GetVolume();
                bool? muted = _volume.GetMute();
                if (current.HasValue)
                {
                    _volumeAvailable = true;
                    _slider.IsEnabled = true;
                    UpdateIcon(current.Value, muted ?? _muted);
                    // 用户正在调整(1.2 秒内)时不要把滑块拽回设备值,避免拖动中跳变。
                    if ((DateTime.UtcNow - _lastUserAdjustUtc).TotalMilliseconds > 1200)
                        SyncControls(current.Value);
                }
                else
                {
                    _volumeAvailable = false;
                    _slider.IsEnabled = false;
                    _value.Text = "读取失败";
                }
                if (muted.HasValue) _muted = muted.Value;
            };
            Pill.MouseWheel += (s, e) => { _volumeWritesEnabled = true; Adjust(e.Delta); };
            Panel.MouseWheel += (s, e) => { _volumeWritesEnabled = true; Adjust(e.Delta); };
        }

        protected override void OnPanelShown()
        {
            _volume.SyncDefaultIfChanged();
            SyncFromDevice();
            _poll.Start();
        }

        protected override void OnPanelHidden() => _poll.Stop();

        private void SyncControls(float value)
        {
            _syncing = true;
            _slider.Value = value;
            _value.Text = Math.Round(value * 100) + "%";
            _syncing = false;
        }

        private void SyncFromDevice()
        {
            float? value = _volume.GetVolume();
            bool? muted = _volume.GetMute();
            if (muted.HasValue) _muted = muted.Value;
            if (value.HasValue)
            {
                _volumeAvailable = true;
                _slider.IsEnabled = true;
                SyncControls(value.Value);
                UpdateIcon(value.Value, _muted);
            }
            else
            {
                _volumeAvailable = false;
                _slider.IsEnabled = false;
                _value.Text = "读取失败";
            }
        }
        private void UpdateIcon(double volume, bool muted)
        {
            _speakerWave1.Opacity = muted || volume < 0.01 ? 0 : 1;
            _speakerWave2.Opacity = muted || volume < 0.34 ? 0 : 1;
        }
        private void Adjust(int delta)
        {
            _lastUserAdjustUtc = DateTime.UtcNow;
            _slider.Value = Math.Max(0, Math.Min(1, _slider.Value + (delta > 0 ? 0.04 : -0.04)));
        }
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _poll.Stop();
            _writeTimer?.Stop();
            _volume.Dispose();
            base.Dispose();
        }
    }

    public class FolderModule : ModuleBase
    {
        private readonly string _name; private readonly WrapPanel _wrap = new WrapPanel(); private readonly ScrollViewer _scroll; private FileSystemWatcher _watcher; private readonly DispatcherTimer _debounce;
        private bool _disposed;
        private bool _filesDirty = true;
        private int _fileLoadVersion;

        private sealed class MoveRequest
        {
            public string Source;
            public string ReplaceTarget;
            public bool CopyInstead;
        }
        private readonly List<DispatcherTimer> _itemOpenTimers = new List<DispatcherTimer>();
        private string Dir
        {
            get
            {
                if (!IsSafeLeafName(_name)) return string.Empty;
                try
                {
                    string root = FilePath.GetFullPath(App.Settings.StorageRoot);
                    string candidate = FilePath.GetFullPath(FilePath.Combine(root, _name));
                    string relative = FilePath.GetRelativePath(root, candidate);
                    if (relative == "." || FilePath.IsPathRooted(relative)
                        || relative.StartsWith(".." + FilePath.DirectorySeparatorChar, StringComparison.Ordinal)
                        || relative.StartsWith(".." + FilePath.AltDirectorySeparatorChar, StringComparison.Ordinal)) return string.Empty;
                    return string.Equals(relative, _name, StringComparison.OrdinalIgnoreCase) ? candidate : string.Empty;
                }
                catch { return string.Empty; }
            }
        }

        private static bool IsSafeLeafName(string name) => SettingsService.IsValidFolderName(name);
        public FolderModule(string name, TabStyle st) : base(st)
        {
            _name = name;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(MakeFolderGlyph(18));
            var tb = MakeText(name);
            tb.Margin = new Thickness(8 * Ui.Scale, 0, 0, 0);
            sp.Children.Add(tb);
            Pill.Child = sp;
            _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = SystemParameters.PrimaryScreenHeight * 0.5 }; _scroll.Content = _wrap; Panel.Child = _scroll;
            try { if (!string.IsNullOrEmpty(Dir)) Directory.CreateDirectory(Dir); } catch (Exception ex) { App.Log("创建小文件夹失败: " + ex.Message); }
            // 空白处右键粘贴（项目上的右键会被项目自身处理并标记 Handled）。
            Panel.MouseRightButtonUp += (s, e) => { ShowBlankMenu(); e.Handled = true; };
            // 双击胶囊：在资源管理器中打开该文件夹。
            Pill.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { ShellOpen.OpenPath(Dir); e.Handled = true; } };
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                if (_disposed) return;
                _filesDirty = true;
                if (Open) LoadFiles();
            };
            SetupWatcher(); EnableDragDrop();
        }
        private void SetupWatcher()
        {
            try
            {
                    _watcher = new FileSystemWatcher(Dir) { EnableRaisingEvents = true };
                _watcher.Created += (s, e) => RestartDebounce();
                _watcher.Changed += (s, e) => RestartDebounce();
                _watcher.Deleted += (s, e) => RestartDebounce();
                _watcher.Renamed += (s, e) => RestartDebounce();
                _watcher.Error += (s, e) => RestartDebounce();
            }
            catch (Exception ex) { App.Log("小文件夹监听失败: " + ex.Message); }
        }
        private void RestartDebounce()
        {
            if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed) return;
                    _debounce.Stop();
                    _debounce.Start();
                }));
            }
            catch (InvalidOperationException) { }
        }
        private void ShowBlankMenu()
        {
            // 剪贴板没有文件时“粘贴”置灰不可点；实际路径在点击时捕获，避免菜单打开后剪贴板变化。
            bool has = FileClipboard.HasPasteableFiles && !string.IsNullOrEmpty(Dir);
            TrackFlyout(FlyoutMenu.Show(new List<FlyoutItem>
            {
                new FlyoutItem("粘贴", PasteIntoFolder, enabled: has)
            }));
        }

        private async void PasteIntoFolder()
        {
            string directory = Dir;
            var snapshot = FileClipboard.CapturePasteSnapshot();
            if (snapshot == null || string.IsNullOrEmpty(directory)) return;
            try
            {
                FileClipboard.PasteResult paste = null;
                var result = await OperationProgressDialog.RunDetailedAsync("正在粘贴文件", false, snapshot.Paths.Count,
                    progress => Task.Run(() =>
                    {
                        paste = FileClipboard.PasteSnapshotDetailed(snapshot, directory,
                            (completed, name, placed) => progress.Report(completed, name, placed));
                    }));
                if (!_disposed) LoadFiles();
                if (!result.Started)
                {
                    ConfirmDialog.Notice("操作进行中", "已有文件操作正在进行，请稍后再试。");
                    return;
                }
                if (result.Error != null)
                {
                    ConfirmDialog.Notice("移动未完成",
                        "操作被中断：" + result.Error.Message + "\n部分项目可能已移动，请检查目标文件夹。");
                    return;
                }
                int succeeded = paste?.Succeeded ?? result.Succeeded;
                int total = paste?.Total ?? snapshot.Paths.Count;
                // 剪切语义已完整消费:清掉剪贴板,符合资源管理器惯例,避免"再粘贴"静默无效。
                if (snapshot.Move && paste != null && paste.Total > 0 && paste.Succeeded == paste.Total)
                {
                    try { Clipboard.Clear(); } catch { }
                }
                if (succeeded < total)
                {
                    var failed = paste?.FailedItems ?? Array.Empty<string>();
                    ConfirmDialog.Notice("粘贴未全部完成",
                        "已完成 " + succeeded + "/" + total + " 项。"
                        + (failed.Count == 0 ? "" : "\n\n" + string.Join("\n", failed.Take(5))
                            + (failed.Count > 5 ? "\n……" : "")));
                }
            }
            // async void 的异常不会进调用方,只会落到全局兜底被静默吞掉;粘贴是用户显式动作,
            // 失败必须留下日志和提示,否则表现为"点了没反应"。
            catch (Exception ex)
            {
                App.Log("粘贴文件失败: " + ex.Message);
                ConfirmDialog.Notice("粘贴失败", "操作意外中断: " + ex.Message);
            }
        }

        protected override void OnPanelShown()
        {
            if (_filesDirty) LoadFiles();
        }

        protected override void OnPanelHidden()
        {
            // 面板已收起,悬停打开计时器必须作废,否则文件会在面板消失后"莫名"打开。
            foreach (var timer in _itemOpenTimers) timer.Stop();
            _itemOpenTimers.Clear();
        }

        private async void LoadFiles()
        {
            int version = ++_fileLoadVersion;
            string directory = Dir;
            try
            {
                var items = await Task.Run(() => SnapshotFiles(directory));
                if (_disposed || !Open || version != _fileLoadVersion)
                {
                    _filesDirty = true;
                    return;
                }

                _wrap.Children.Clear();
                int cols = Math.Min(4, Math.Max(1, items.Count));
                // 最多四列；为四列预留完整 viewport，细滚动条不会把第四列挤到下一行。
                double cell = 76 * Ui.Scale;
                _wrap.Width = cols * cell;
                Panel.Width = (cols * 76 + 26) * Ui.Scale;

                const int batchSize = 8;
                for (int index = 0; index < items.Count; index += batchSize)
                {
                    if (_disposed || !Open || version != _fileLoadVersion)
                    {
                        _filesDirty = true;
                        return;
                    }
                    int end = Math.Min(items.Count, index + batchSize);
                    for (int i = index; i < end; i++)
                        _wrap.Children.Add(MakeItem(items[i]));

                    if (end < items.Count)
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
                _filesDirty = false;
            }
            catch (InvalidOperationException) { _filesDirty = true; }
            // InvalidOperationException 是面板竞态的预期路径;其余异常(目录被占用的 IO 等)
            // 也标记脏,下次打开面板会重试,同时留日志供排查。
            catch (Exception ex)
            {
                _filesDirty = true;
                App.Log("加载小文件夹失败: " + ex.Message);
            }
        }

        private static List<FileSystemInfo> SnapshotFiles(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return new List<FileSystemInfo>();
                return new DirectoryInfo(directory)
                    .GetFileSystemInfos()
                    .OrderBy(item => item is DirectoryInfo ? 0 : 1)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<FileSystemInfo>();
            }
        }
        private FrameworkElement MakeItem(FileSystemInfo f)
        {
            bool isDir = f is DirectoryInfo;
            string displayName =
                (string.Equals(f.Extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(f.Extension, ".url", StringComparison.OrdinalIgnoreCase))
                    ? f.Name.Substring(0, f.Name.Length - f.Extension.Length)
                    : f.Name;
            var tile = ItemTile.Create(displayName, isDir, f.Extension, f.FullName, null, () => _disposed);
            var border = tile.Root;
            bool suppressClickAfterDrag = false;
            // 拖出：按住左键移动超过系统阈值后发起 OLE 拖拽（Move 语义），
            // 落点接受 Move 后由这里删除源（若目标是我们自己的其他面板则已被移动，删除自然失败可忽略）。
            bool dragTaken = false;
            border.PreviewMouseMove += (s, e) =>
            {
                if (tile.DragStart == null || dragTaken || e.LeftButton != MouseButtonState.Pressed) return;
                var pos = e.GetPosition(border);
                if (Math.Abs(pos.X - tile.DragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(pos.Y - tile.DragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragTaken = true;
                suppressClickAfterDrag = true;
                try
                {
                    var data = new DataObject(DataFormats.FileDrop, new[] { f.FullName });
                    data.SetData("Preferred DropEffect", FileClipboard.EffectStream(FileClipboard.MoveEffect));
                    var effect = DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
                    if ((effect & DragDropEffects.Move) != 0)
                    {
                        // 仅在源已被目标实际移走后刷新；不按旧路径盲删，避免误删新建同名文件。
                        bool sourceStillExists = isDir ? Directory.Exists(f.FullName) : File.Exists(f.FullName);
                        if (!sourceStillExists) LoadFiles();
                    }
                }
                catch (Exception ex) { App.Log("拖出移动失败: " + ex.Message); }
                finally
                {
                    tile.DragStart = null;
                    dragTaken = false;
                    tile.AnimateBack();
                    border.Background = Brushes.Transparent;
                }
            };
            DispatcherTimer openTimer = null;
            border.MouseLeftButtonUp += (s, e) =>
            {
                tile.AnimateBack();
                border.SetResourceReference(Border.BackgroundProperty, "ThemePanelControl");
                if (suppressClickAfterDrag)
                {
                    suppressClickAfterDrag = false;
                    e.Handled = true;
                    return;
                }
                if (e.ClickCount >= 2)
                {
                    if (openTimer != null) _itemOpenTimers.Remove(openTimer);
                    openTimer?.Stop();
                    openTimer = null;
                    ShellOpen.OpenPath(f.FullName);
                }
                else
                {
                    openTimer?.Stop();
                    openTimer = new DispatcherTimer { Interval = SystemParameters.MouseHoverTime };
                    _itemOpenTimers.Add(openTimer);
                    openTimer.Tick += (ts, te) =>
                    {
                        openTimer.Stop();
                        _itemOpenTimers.Remove(openTimer);
                        openTimer = null;
                        if (!_disposed && Open) ShellOpen.OpenPath(f.FullName);
                    };
                    openTimer.Start();
                }
                e.Handled = true;
            };
            border.MouseRightButtonUp += (s, e) => { ShowItemMenu(f); e.Handled = true; };
            return border;
        }
        private void ShowItemMenu(FileSystemInfo f)
        {
            TrackFlyout(FlyoutMenu.Show(new List<FlyoutItem>
            {
                new FlyoutItem("打开", () => ShellOpen.OpenPath(f.FullName)),
                new FlyoutItem("复制", () => FileClipboard.CopyFiles(new[] { f.FullName })),
                new FlyoutItem("剪切", () => FileClipboard.CutFiles(new[] { f.FullName })),
                new FlyoutItem("重命名", () => RenameItem(f)),
                new FlyoutItem("删除", () => DeleteItem(f), danger: true)
            }));
        }
        private void RenameItem(FileSystemInfo f)
        {
            string name = Prompt.Show("重命名", "新的名称:", f.Name);
            if (string.IsNullOrWhiteSpace(name) || name == f.Name) return;
            string extension = f.Extension;
            if ((string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
                && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                name += extension;
            if (!IsSafeLeafName(name))
            {
                ConfirmDialog.Notice("重命名失败", "名称不能包含路径分隔符或 ..。");
                return;
            }
            try
            {
                string dest = FilePath.Combine(FilePath.GetDirectoryName(f.FullName) ?? "", name);
                if (File.Exists(dest) || Directory.Exists(dest)) { ConfirmDialog.Notice("重命名失败", "同名项目已存在。"); return; }
                if (f is DirectoryInfo dir) dir.MoveTo(dest);
                else ((FileInfo)f).MoveTo(dest);
                LoadFiles();
            }
            catch (Exception ex) { ConfirmDialog.Notice("重命名失败", ex.Message); }
        }
        private async void DeleteItem(FileSystemInfo f)
        {
            if (!ConfirmDialog.ShowDanger("删除", "确定要把“" + f.Name + "”移到回收站吗？", "删除")) return;
            try
            {
                bool success = false;
                var result = await OperationProgressDialog.RunDetailedAsync("正在移入回收站", true, 1,
                    progress => Task.Run(() =>
                    {
                        success = NativeMethods.DeleteToRecycleBin(new[] { f.FullName });
                        progress.Report(1, f.Name, success);
                    }));
                if (_disposed) return;
                if (!result.Started)
                {
                    ConfirmDialog.Notice("操作进行中", "已有文件操作正在进行，请稍后再试。");
                    return;
                }
                if (success) LoadFiles();
                else ConfirmDialog.Notice("删除失败", "无法把“" + f.Name + "”移到回收站。");
            }
            catch (Exception ex)
            {
                App.Log("移入回收站失败: " + ex.Message);
                ConfirmDialog.Notice("删除失败", "操作意外中断: " + ex.Message);
            }
        }
        private void EnableDragDrop()
        {
            Pill.AllowDrop = true;
            PanelWindow.AllowDrop = true;
            foreach (var el in new UIElement[] { Pill, PanelWindow })
            {
                el.DragOver += (s, e) =>
                {
                    // 小文件夹拖入固定为移动语义；来源不允许 Move 时拒绝，不能显示 Copy 却实际移走源文件。
                    e.Effects = !OperationProgressDialog.Busy
                        && e.Data.GetDataPresent(DataFormats.FileDrop)
                        && (e.AllowedEffects & DragDropEffects.Move) != 0
                        ? DragDropEffects.Move
                        : DragDropEffects.None;
                    e.Handled = true;
                };
                el.Drop += (s, e) =>
                {
                    bool canMove = (e.AllowedEffects & DragDropEffects.Move) != 0;
                    if (e.Data.GetDataPresent(DataFormats.FileDrop)
                        && !OperationProgressDialog.Busy && canMove)
                    {
                        bool sourcesPreserved;
                        bool success = MoveInto((string[])e.Data.GetData(DataFormats.FileDrop), out sourcesPreserved);
                        e.Effects = success && !sourcesPreserved
                            ? DragDropEffects.Move
                            : DragDropEffects.None;
                    }
                    else e.Effects = DragDropEffects.None;
                    e.Handled = true;
                };
            }
        }
        // 拖入默认采用移动语义；同名冲突选择“作为副本”时保留源项目。
        private bool MoveInto(string[] files, out bool sourcesPreserved)
        {
            sourcesPreserved = false;
            string destination = Dir;
            if (string.IsNullOrEmpty(destination)) return false;
            try
            {
                Directory.CreateDirectory(destination);
                string destRoot = FilePath.GetFullPath(destination).TrimEnd('\\', '/');
                var requests = new List<MoveRequest>();
                ConflictChoice? choice = null;
                bool applyToAll = false;
                foreach (var f in files ?? Array.Empty<string>())
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    string full = FilePath.GetFullPath(f).TrimEnd('\\', '/');
                    if (!File.Exists(full) && !Directory.Exists(full)) continue;
                    // 跳过：源就是本目录、或把文件夹拖进它自己的子目录。
                    if (string.Equals(full, destRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    if (destRoot.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    string parent = FilePath.GetDirectoryName(full);
                    if (string.Equals(parent?.TrimEnd('\\', '/'), destRoot, StringComparison.OrdinalIgnoreCase)) continue;

                    string dest = FilePath.Combine(destRoot, FilePath.GetFileName(full));
                    string replaceTarget = null;
                    bool copyInstead = false;
                    if (File.Exists(dest) || Directory.Exists(dest))
                    {
                        if (!applyToAll)
                        {
                            choice = ConfirmDialog.ShowConflict("存在同名项目",
                                "“" + FilePath.GetFileName(dest) + "”已在此文件夹中。要替换它，还是把拖入的项保留为副本？",
                                out applyToAll);
                            if (choice == null) break;
                        }
                        if (choice == ConflictChoice.Replace) replaceTarget = dest;
                        else if (choice == ConflictChoice.KeepBoth) copyInstead = true;
                    }
                    requests.Add(new MoveRequest
                    {
                        Source = full,
                        ReplaceTarget = replaceTarget,
                        CopyInstead = copyInstead
                    });
                }

                if (requests.Count == 0) return false;
                sourcesPreserved = requests.Any(request => request.CopyInstead);
                var failures = new List<string>();
                var result = OperationProgressDialog.RunWithDispatcherPumpDetailed("正在移动文件", false, requests.Count,
                    progress =>
                    {
                        for (int i = 0; i < requests.Count; i++)
                        {
                            var request = requests[i];
                            bool placed;
                            string error = null;
                            if (!string.IsNullOrEmpty(request.ReplaceTarget))
                            {
                                placed = FileClipboard.TryReplace(request.Source, request.ReplaceTarget,
                                    () => NativeMethods.DeleteToRecycleBin(new[] { request.ReplaceTarget }),
                                    out error);
                            }
                            else
                            {
                                placed = FileClipboard.TryPlace(request.Source, destination, move: !request.CopyInstead);
                                if (!placed) error = request.CopyInstead ? "复制失败。" : "移动或跨卷复制失败。";
                            }
                            string name = FilePath.GetFileName(request.Source);
                            if (!placed)
                            {
                                failures.Add(name + (string.IsNullOrEmpty(error) ? "" : "：" + error));
                                App.Log("拖入操作失败: " + name + (string.IsNullOrEmpty(error) ? "" : " — " + error));
                            }
                            progress.Report(i + 1, name, placed);
                        }
                        return Task.CompletedTask;
                    });
                if (!_disposed) LoadFiles();
                if (result.SucceededFully) return true;
                if (result.Error != null)
                {
                    ConfirmDialog.Notice("移动被中断",
                        "操作中途发生异常：" + result.Error.Message + "\n部分项目可能已移动，请检查目标文件夹。");
                    return false;
                }
                if (result.Started && failures.Count > 0)
                    ConfirmDialog.Notice("移动未全部完成",
                        "已完成 " + result.Succeeded + "/" + requests.Count + " 项。\n\n"
                        + string.Join("\n", failures.Take(5))
                        + (failures.Count > 5 ? "\n……" : ""));
                return false;
            }
            catch (Exception ex) { App.Log("拖入失败: " + ex.Message); return false; }
        }
        public override void Dispose()
        {
            _disposed = true;
            _fileLoadVersion++;
            _debounce.Stop();
            foreach (var timer in _itemOpenTimers) timer.Stop();
            _itemOpenTimers.Clear();
            try
            {
                if (_watcher != null) _watcher.EnableRaisingEvents = false;
                _watcher?.Dispose();
            }
            catch (Exception ex) { App.Log("关闭小文件夹监听失败: " + ex.Message); }
            base.Dispose();
        }
    }

    /// <summary>回收站模块：点击展开（悬停不展开），单击项目还原到原位置，右键胶囊清空回收站。</summary>
    public class RecycleBinModule : ModuleBase
    {
        private readonly WrapPanel _wrap = new WrapPanel();
        private readonly ScrollViewer _scroll;
        private readonly TextBlock _emptyHint;
        private bool _disposed;
        private int _loadVersion;

        public RecycleBinModule(TabStyle st) : base(st)
        {
            Pill.Width = 38 * Ui.Scale;
            Pill.Padding = new Thickness(0);
            Pill.Child = CenterIcon(MakeTrashGlyph(20.5));
            Pill.MouseLeftButtonUp += (s, e) => ShowPanel();
            Pill.MouseRightButtonUp += (s, e) => { ShowClearMenu(); e.Handled = true; };
            // 双击胶囊:直接打开回收站视图(SHOpenFolderAndSelectItems 会打开"父文件夹"即桌面,
            // 对虚拟根文件夹必须用 shell 命名空间路径导航进去;CLSID 是固定常量)。
            Pill.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount != 2) return;
                e.Handled = true;
                NativeMethods.ShellExecuteOpen("shell:::{645FF040-5081-101B-9F08-00AA002F954E}");
            };

            // 经 MakeText 登记随主题刷新;直接快照 Foreground 会在切主题后保留旧色。
            _emptyHint = MakeText("回收站是空的", -1);
            _emptyHint.FontSize = 12 * Ui.Scale;
            _emptyHint.Opacity = 0.65;
            _emptyHint.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyHint.Margin = new Thickness(12 * Ui.Scale, 16 * Ui.Scale, 12 * Ui.Scale, 10 * Ui.Scale);
            _emptyHint.Visibility = Visibility.Collapsed;
            _wrap.HorizontalAlignment = HorizontalAlignment.Center;
            var host = new StackPanel();
            host.Children.Add(_emptyHint);
            host.Children.Add(_wrap);
            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = SystemParameters.PrimaryScreenHeight * 0.5,
                Content = host
            };
            Panel.Child = _scroll;
            EnableDragDrop();
        }

        protected override bool HoverOpensPanel => false;
        protected override void OnPanelShown() => LoadItems();

        // 拖入回收站 = 把文件移入系统回收站（SHFileOperation 带 undo）。
        private void EnableDragDrop()
        {
            Pill.AllowDrop = true; PanelWindow.AllowDrop = true;
            foreach (var el in new UIElement[] { Pill, PanelWindow })
            {
                el.DragOver += (s, e) =>
                {
                    // 与小文件夹拖入同一约定:来源只允许 Copy 时不能假装 Move(拖入回收站是删除性移动),
                    // 否则 zip/邮件附件等 Copy-only 源会被错误删除。
                    e.Effects = !OperationProgressDialog.Busy
                        && e.Data.GetDataPresent(DataFormats.FileDrop)
                        && (e.AllowedEffects & DragDropEffects.Move) != 0
                        ? DragDropEffects.Move : DragDropEffects.None;
                    e.Handled = true;
                };
                el.Drop += (s, e) =>
                {
                    bool moved = false;
                    if (e.Data.GetDataPresent(DataFormats.FileDrop) && !OperationProgressDialog.Busy
                        && (e.AllowedEffects & DragDropEffects.Move) != 0)
                    {
                        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                        // 忽略本模块拖出又拖回的临时硬链接/副本,避免把回收站内部路径再次回收。
                        files = FilterOwnTempPaths(files);
                        if (files != null && files.Length > 0)
                        {
                            bool success = false;
                            var result = OperationProgressDialog.RunWithDispatcherPumpDetailed("正在移入回收站", true, files.Length,
                                progress =>
                                {
                                    success = NativeMethods.DeleteToRecycleBin(files);
                                    progress.Report(files.Length, success ? "已完成" : "操作失败", success);
                                    if (success) App.Log("已把 " + files.Length + " 项移入回收站");
                                    return Task.CompletedTask;
                                });
                            moved = result.SucceededFully && success;
                            if (result.Started)
                            {
                                LoadItems();
                                if (!moved)
                                    ConfirmDialog.Notice("移入回收站未全部完成", "部分项目未能移入回收站,请重试或逐个检查。");
                            }
                            else
                                ConfirmDialog.Notice("操作进行中", "已有文件操作正在进行，请稍后再试。");
                        }
                    }
                    e.Effects = moved ? DragDropEffects.Move : DragDropEffects.None;
                    e.Handled = true;
                };
            }
        }

        /// <summary>过滤来自本模块拖出的临时路径(回收站 $R 目录内的硬链接、%TEMP% 临时副本)。</summary>
        private static string[] FilterOwnTempPaths(string[] files)
        {
            if (files == null) return files;
            string tempRoot = (FilePath.GetTempPath() ?? "").TrimEnd('\\', '/');
            var kept = files.Where(f =>
            {
                if (string.IsNullOrEmpty(f)) return false;
                string full = f;
                try { full = FilePath.GetFullPath(f); } catch { }
                if (full.IndexOf("\\$Recycle.Bin\\", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (!string.IsNullOrEmpty(tempRoot)
                    && full.StartsWith(tempRoot + "\\", StringComparison.OrdinalIgnoreCase)
                    && full.IndexOf("YukiDeskClipboard", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                return true;
            }).ToArray();
            return kept;
        }

        private async void LoadItems()
        {
            int version = ++_loadVersion;
            try
            {
                var items = await Task.Run(RecycleBin.Enumerate);
                if (_disposed || !Open || version != _loadVersion) return;

                _wrap.Children.Clear();
                bool empty = items.Count == 0;
                _emptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                _wrap.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                int cols = Math.Min(4, Math.Max(1, items.Count));
                double cell = 76 * Ui.Scale;
                _wrap.Width = cols * cell;
                Panel.Width = empty ? 190 * Ui.Scale : (cols * 76 + 26) * Ui.Scale;

                const int batchSize = 8;
                for (int index = 0; index < items.Count; index += batchSize)
                {
                    if (_disposed || !Open || version != _loadVersion) return;
                    int end = Math.Min(items.Count, index + batchSize);
                    for (int i = index; i < end; i++) _wrap.Children.Add(MakeRecycleItem(items[i]));
                    if (end < items.Count)
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
            }
            catch (InvalidOperationException) { }
            // 面板竞态是预期路径;其余异常(磁盘枚举失败等)留日志,面板内容可能不新但不崩。
            catch (Exception ex) { App.Log("加载回收站失败: " + ex.Message); }
        }

        private FrameworkElement MakeRecycleItem(RecycleItem item)
        {
            string ext = FilePath.GetExtension(item.DataPath);
            string tooltip = item.OriginalPath + "\n删除于 " + item.DeletedAt.ToString("yyyy-MM-dd HH:mm")
                + (item.Size >= 0 ? "\n大小 " + FmtRecycleSize(item.Size) : "")
                + "\n双击还原;右键更多操作";
            var tile = ItemTile.Create(item.Name, item.IsDirectory, ext, item.DataPath, tooltip, () => _disposed);
            var border = tile.Root;
            // 拖出 = 把内容移出回收站：落点接受 Move 后删除 $R 内容与 $I 元数据。
            bool dragTaken = false;
            border.PreviewMouseMove += (s, e) =>
            {
                if (tile.DragStart == null || dragTaken || e.LeftButton != MouseButtonState.Pressed) return;
                var pos = e.GetPosition(border);
                if (Math.Abs(pos.X - tile.DragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(pos.Y - tile.DragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragTaken = true;
                // $R 文件名是随机的；文件用硬链接、目录用临时副本，均使用原始名称拖出。
                string dragPath = null;
                string tempLink = null;
                bool prepared = false;
                if (!item.IsDirectory)
                {
                    try
                    {
                        string linkDir = FilePath.GetDirectoryName(item.DataPath) ?? "";
                        tempLink = FileClipboard.UniqueDest(item.Name, linkDir);
                        if (!NativeMethods.CreateHardLink(tempLink, item.DataPath))
                            throw new IOException("无法创建拖拽硬链接");
                        dragPath = tempLink;
                        prepared = true;
                    }
                    catch (Exception ex)
                    {
                        App.Log("准备回收站文件拖出失败: " + ex.Message);
                        if (tempLink != null && File.Exists(tempLink)) { try { File.Delete(tempLink); } catch { } }
                        tempLink = null;
                    }
                }
                else
                {
                    var prepare = OperationProgressDialog.RunWithDispatcherPumpDetailed("正在准备拖出", true, 1,
                        progress => Task.Run(() =>
                        {
                            try
                            {
                                tempLink = FileClipboard.CreateNamedTemporaryCopy(item.DataPath, item.Name);
                                dragPath = tempLink;
                                prepared = true;
                                progress.Report(1, item.Name, true);
                            }
                            catch (Exception ex)
                            {
                                App.Log("准备回收站文件夹拖出失败: " + ex.Message);
                                progress.Report(1, "操作失败", false);
                            }
                        }));
                    if (!prepare.Started) prepared = false;
                }
                if (!prepared)
                {
                    dragTaken = false;
                    ConfirmDialog.Notice("拖出失败", "无法以原始名称准备“" + item.Name + "”。");
                    return;
                }
                var data = new DataObject(DataFormats.FileDrop, new[] { dragPath });
                data.SetData("Preferred DropEffect", FileClipboard.EffectStream(FileClipboard.MoveEffect));
                DragDropEffects effect = DragDropEffects.None;
                try
                {
                    effect = DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
                }
                catch (Exception ex)
                {
                    App.Log("回收站拖出失败: " + ex.Message);
                }
                finally
                {
                    tile.DragStart = null;
                }
                // 关键守卫:目标声称接受 Move 且临时项确实已从原处消失(被目标 rename/移走),
                // 才允许删除 $R/$I。临时项仍在原处说明落点只是"口头接受"没取走数据,
                // 此时删 $R 会造成内容永久丢失(目录分支的临时副本是拷贝,不是硬链接)。
                bool linkStillThere = !string.IsNullOrEmpty(tempLink)
                    && (Directory.Exists(tempLink) || File.Exists(tempLink));
                if (!string.IsNullOrEmpty(tempLink))
                {
                    // 大目录临时副本可能 GB 级,同步删会冻结 UI,放后台清理。
                    string cleanup = tempLink;
                    Task.Run(() =>
                    {
                        try
                        {
                            if (Directory.Exists(cleanup)) Directory.Delete(cleanup, true);
                            else if (File.Exists(cleanup)) File.Delete(cleanup);
                        }
                        catch (Exception ex) { App.Log("清理回收站拖拽临时项目失败: " + ex.Message); }
                    });
                }
                if ((effect & DragDropEffects.Move) != 0 && linkStillThere)
                {
                    // 落点接受了 Drop 却没真正移走内容:保留 $R/$I,项目还在回收站,用户可重试。
                    App.Log("拖出目标未实际取走内容,保留回收站项目: " + item.Name);
                    LoadItems();
                    return;
                }
                if ((effect & DragDropEffects.Move) != 0)
                {
                    // 只有 $R 内容和 $I 元数据都清理成功，才算完成拖出；否则保留元数据以便还原。
                    bool dataDeleted = false;
                    bool metaDeleted = false;
                    var result = OperationProgressDialog.RunWithDispatcherPumpDetailed("正在完成移动", true, 1,
                        progress =>
                        {
                            try
                            {
                                FileClipboard.ForceDelete(item.DataPath);
                                dataDeleted = true;
                            }
                            catch (Exception ex) { App.Log("移出回收站内容失败: " + ex.Message); }
                            if (dataDeleted)
                            {
                                try
                                {
                                    File.Delete(item.MetaFile);
                                    metaDeleted = true;
                                }
                                catch (Exception ex) { App.Log("清理回收站元数据失败: " + ex.Message); }
                            }
                            progress.Report(1, item.Name, dataDeleted && metaDeleted);
                            return Task.CompletedTask;
                        });
                    if (!result.Started)
                        ConfirmDialog.Notice("操作进行中", "已有文件操作正在进行，请稍后再试。");
                    else if (dataDeleted && metaDeleted) LoadItems();
                    else if (dataDeleted)
                    {
                        // 内容已删但元数据没删掉:条目将变成孤儿 $I,提示要如实反映"内容已移出"。
                        ConfirmDialog.Notice("移出完成但元数据清理失败",
                            "“" + item.Name + "”的内容已移到目标位置,但回收站记录清理失败。\n"
                            + "该条目可能继续显示在回收站列表中,可忽略或稍后在资源管理器中清理。");
                        LoadItems();
                    }
                    else
                        ConfirmDialog.Notice("移出回收站失败", "“" + item.Name + "”未能移出，原项目仍保留在回收站中。");
                }
            };
            border.MouseLeftButtonUp += (s, e) =>
            {
                tile.AnimateBack();
                border.SetResourceReference(Border.BackgroundProperty, "ThemePanelControl");
                if (dragTaken) { dragTaken = false; e.Handled = true; return; }
                // 还原改为双击触发:单击只是选中/查看,避免误触 68px 小条目就移动文件。
                if (e.ClickCount >= 2) RestoreItemAsync(item);
                e.Handled = true;
            };
            border.MouseRightButtonUp += (s, e) => { ShowItemMenu(item); e.Handled = true; };
            return border;
        }

        private async void RestoreItemAsync(RecycleItem item)
        {
            try
            {
                bool success = false;
                var result = await OperationProgressDialog.RunDetailedAsync("正在还原文件", true, 1,
                    progress => Task.Run(() =>
                    {
                        success = RecycleBin.Restore(item);
                        progress.Report(1, item.Name, success);
                    }));
                if (_disposed) return;
                if (!result.Started)
                {
                    ConfirmDialog.Notice("操作进行中", "已有文件操作正在进行，请稍后再试。");
                    return;
                }
                if (success) LoadItems();
                else ConfirmDialog.Notice("还原失败", "还原“" + item.Name + "”失败，请检查原始位置是否可用。");
            }
            catch (Exception ex)
            {
                App.Log("还原回收站项目失败: " + ex.Message);
                ConfirmDialog.Notice("还原失败", "操作意外中断: " + ex.Message);
            }
        }

        private async void DeletePermanentlyAsync(RecycleItem item)
        {
            try
            {
                bool dataDeleted = false;
                bool metaDeleted = false;
                var result = await OperationProgressDialog.RunDetailedAsync("正在永久删除", true, 1,
                    progress => Task.Run(() =>
                    {
                        try
                        {
                            if (item.IsDirectory) Directory.Delete(item.DataPath, true);
                            else File.Delete(item.DataPath);
                            dataDeleted = true;
                            try
                            {
                                File.Delete(item.MetaFile);
                                metaDeleted = true;
                            }
                            catch (Exception ex) { App.Log("永久删除元数据失败: " + ex.Message); }
                        }
                        catch (Exception ex) { App.Log("永久删除失败: " + ex.Message); }
                        progress.Report(1, item.Name, dataDeleted && metaDeleted);
                    }));
                if (_disposed) return;
                if (!result.Started)
                {
                    ConfirmDialog.Notice("操作进行中", "已有文件操作正在进行，请稍后再试。");
                    return;
                }
                if (dataDeleted && metaDeleted) LoadItems();
                else if (dataDeleted)
                    ConfirmDialog.Notice("永久删除部分完成", "内容已删除，但回收站元数据清理失败，请检查回收站。");
                else
                    ConfirmDialog.Notice("永久删除失败", "无法删除“" + item.Name + "”。");
                if (!dataDeleted || !metaDeleted) LoadItems();
            }
            catch (Exception ex)
            {
                App.Log("永久删除失败: " + ex.Message);
                ConfirmDialog.Notice("永久删除失败", "操作意外中断: " + ex.Message);
            }
        }

        private async void CopyRecycleItem(RecycleItem item)
        {
            try
            {
                string temporaryPath = null;
                var result = await OperationProgressDialog.RunDetailedAsync("正在准备复制", true, 1,
                    progress => Task.Run(() =>
                    {
                        try
                        {
                            temporaryPath = FileClipboard.CreateNamedTemporaryCopy(item.DataPath, item.Name);
                            progress.Report(1, item.Name, true);
                        }
                        catch (Exception ex)
                        {
                            App.Log("复制回收站项目失败: " + ex.Message);
                            progress.Report(1, "操作失败", false);
                        }
                    }));
                if (!result.Started)
                {
                    ConfirmDialog.Notice("操作进行中", "已有文件操作正在进行，请稍后再试。");
                    return;
                }
                // 剪贴板写入口(OLE)在被他进程短暂占用时会抛 COMException,这里必须兜住。
                bool copied = !string.IsNullOrEmpty(temporaryPath)
                    && FileClipboard.WriteTemporaryPath(temporaryPath);
                if (!copied)
                {
                    FileClipboard.CleanupTemporaryPath(temporaryPath);
                    ConfirmDialog.Notice("复制失败", "无法复制“" + item.Name + "”。");
                }
            }
            catch (Exception ex)
            {
                App.Log("复制回收站项目失败: " + ex.Message);
                ConfirmDialog.Notice("复制失败", "操作意外中断: " + ex.Message);
            }
        }

        private void ShowItemMenu(RecycleItem item)
        {
            TrackFlyout(FlyoutMenu.Show(new List<FlyoutItem>
            {
                new FlyoutItem("还原", () => RestoreItemAsync(item)),
                new FlyoutItem("复制", () => CopyRecycleItem(item)),
                new FlyoutItem("永久删除", () =>
                {
                    if (!ConfirmDialog.ShowDanger("永久删除", "“" + item.Name + "”将被永久删除且无法恢复。\n确定要继续吗？", "永久删除")) return;
                    DeletePermanentlyAsync(item);
                }, danger: true)
            }));
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _loadVersion++;
            base.Dispose();
        }

        private static string FmtRecycleSize(long bytes)
        {
            if (bytes < 0) return "";
            if (bytes >= 1073741824) return (bytes / 1073741824.0).ToString("0.0") + " GB";
            if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return bytes + " B";
        }

        // ---------- 右键菜单：清空回收站 ----------

        private void ShowClearMenu()
        {
            // 菜单打开不为数量同步扫描所有磁盘；展开页会在后台刷新准确内容。
            TrackFlyout(FlyoutMenu.Show(new List<FlyoutItem>
            {
                new FlyoutItem("清空回收站", EmptyBin, danger: true)
            }, "回收站"));
        }

        private async void EmptyBin()
        {
            if (!ConfirmDialog.ShowDanger("清空回收站", "所有项目将被永久删除，此操作不可恢复。\n确定要继续吗？", "清空回收站"))
                return;
            try
            {
                int hr = int.MinValue;
                var result = await OperationProgressDialog.RunDetailedAsync("正在清空回收站", true, 1,
                    progress => Task.Run(() =>
                    {
                        hr = NativeMethods.EmptyRecycleBin();
                        progress.Report(1, hr == 0 ? "已完成" : "操作失败", hr == 0);
                    }));
                if (!result.Started)
                {
                    ConfirmDialog.Notice("操作进行中", "已有文件操作正在进行，请稍后再试。");
                    return;
                }
                App.Log("清空回收站 hr=0x" + hr.ToString("X8"));
                if (!_disposed) LoadItems();
                // 空桶调用 SHEmptyRecycleBin 常返回 E_UNEXPECTED,实际无事发生,不能当失败吓用户。
                if (hr != 0 && hr != NativeMethods.E_UNEXPECTED)
                    ConfirmDialog.Notice("清空回收站失败", "Windows 未能完成清空操作。\n错误代码: 0x" + hr.ToString("X8"));
            }
            catch (Exception ex)
            {
                App.Log("清空回收站失败: " + ex.Message);
                ConfirmDialog.Notice("清空回收站失败", "操作意外中断: " + ex.Message);
            }
        }

    }

    // 小文件夹与回收站条目共用的小瓦片骨架:图标+名称+圆角底+悬停/按压反馈+拖拽起点记录。
    // 此前这段约 70 行的构建与交互代码在 FolderModule.MakeItem 与
    // RecycleBinModule.MakeRecycleItem 里各抄一份,已经出现过只修一边的漂移;
    // 抽到这里后,两边只保留真正的差异(拖出内容、双击行为、右键菜单、提示文字)。
    internal sealed class ItemTile
    {
        public readonly Border Root;
        public readonly ScaleTransform Transform;
        // 按下时记录的拖拽起点,供调用方的 MouseMove 做系统拖拽阈值判定;
        // 拖拽结束后由调用方置回 null,松键未拖动的路径由下一次按下覆盖。
        public Point? DragStart;

        private ItemTile(Border root, ScaleTransform transform)
        {
            Root = root;
            Transform = transform;
        }

        public static ItemTile Create(string displayName, bool isDir, string ext,
            string iconPath, string tooltip, Func<bool> isDisposed)
        {
            double scale = Ui.Scale;
            var img = new Image
            {
                Width = 40 * scale,
                Height = 40 * scale,
                Source = IconCache.GetFallback(isDir, ext),
                HorizontalAlignment = HorizontalAlignment.Center,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            IconCache.LoadAsync(iconPath, isDir, ext, icon =>
            {
                // 缓存命中时会在 img 加入可视化树之前同步回调，此时 IsVisible 必为 false，
                // 不能用它做守卫，否则真实图标会被静默丢弃、条目永远停在占位图标上。
                if (!isDisposed()) img.Source = icon;
            });
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var name = new TextBlock
            {
                Text = displayName,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 11 * scale,
                Opacity = 0.9,
                MaxHeight = 28 * scale,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4 * scale, 0, 0)
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, "ThemePanelText");
            var sp = new StackPanel
            {
                Width = 68 * scale,
                Margin = new Thickness(3 * scale, 8 * scale, 3 * scale, 8 * scale)
            };
            sp.Children.Add(img);
            sp.Children.Add(name);
            var root = new Border
            {
                CornerRadius = new CornerRadius(8 * scale),
                Background = Brushes.Transparent,
                Child = sp,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            var tile = new ItemTile(root, (ScaleTransform)root.RenderTransform);
            root.MouseEnter += (s, e) => root.SetResourceReference(Border.BackgroundProperty, "ThemePanelControl");
            root.MouseLeave += (s, e) => { root.Background = Brushes.Transparent; tile.AnimateBack(); };
            root.PreviewMouseLeftButtonDown += (s, e) =>
            {
                tile.DragStart = e.GetPosition(root);
                tile.Transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                tile.Transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                tile.Transform.ScaleX = 0.93;
                tile.Transform.ScaleY = 0.93;
                root.SetResourceReference(Border.BackgroundProperty, "ThemePanelControlHover");
            };
            return tile;
        }

        public void AnimateBack()
        {
            Transform.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.08 }
                });
            Transform.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.08 }
                });
        }
    }

    public static class IconCache
    {
        private sealed class IconRequest
        {
            public string Path;
            public bool IsDirectory;
            public int PixelSize;
            public string Key;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, BitmapSource> Cache = new Dictionary<string, BitmapSource>();
        private static readonly Dictionary<string, BitmapSource> Fallbacks = new Dictionary<string, BitmapSource>();
        private static readonly ConcurrentDictionary<string, byte> Pending = new ConcurrentDictionary<string, byte>();
        private static readonly Dictionary<string, List<Action<BitmapSource>>> Callbacks = new Dictionary<string, List<Action<BitmapSource>>>();
        private static readonly BlockingCollection<IconRequest> Queue = new BlockingCollection<IconRequest>(new ConcurrentQueue<IconRequest>());
        private static Thread _worker;

        public static BitmapSource GetFallback(bool isDir, string ext)
        {
            int pixelSize = IconPixelSize();
            string key = (isDir ? "dir:" : "file:") + pixelSize;
            lock (Gate)
            {
                if (Fallbacks.TryGetValue(key, out var icon)) return icon;
                // 占位也用 Windows 系统图标（imageres.dll 的文件夹/通用文件），
                // 保证条目从出现到换上真实图标全程都是 Explorer 同款样式。
                icon = NativeMethods.GetSystemTypeIcon(isDir, pixelSize) ?? TypeIcon(isDir, ext);
                Fallbacks[key] = icon;
                return icon;
            }
        }

        private static int IconPixelSize()
            => Math.Max(48, (int)Math.Ceiling(48 * Ui.Scale * 1.5));

        public static void LoadAsync(string fullPath, bool isDir, string ext, Action<BitmapSource> apply)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || apply == null) return;
            int pixelSize = IconPixelSize();
            string key = (isDir ? "dir:" : "file:") + fullPath.ToLowerInvariant() + "|" + pixelSize;
            BitmapSource cached;
            lock (Gate) Cache.TryGetValue(key, out cached);
            if (cached != null)
            {
                apply(cached);
                return;
            }

            lock (Gate)
            {
                if (!Callbacks.TryGetValue(key, out var callbacks))
                    Callbacks[key] = callbacks = new List<Action<BitmapSource>>();
                callbacks.Add(apply);
            }
            if (!Pending.TryAdd(key, 0)) return;
            EnsureWorker();
            Queue.Add(new IconRequest { Path = fullPath, IsDirectory = isDir, PixelSize = pixelSize, Key = key });
        }

        private static void EnsureWorker()
        {
            lock (Gate)
            {
                if (_worker != null) return;
                _worker = new Thread(ProcessQueue)
                {
                    IsBackground = true,
                    Name = "ShellIconLoader"
                };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
        }

        private static void ProcessQueue()
        {
            foreach (var request in Queue.GetConsumingEnumerable())
            {
                BitmapSource icon = null;
                try
                {
                    icon = NativeMethods.GetFileIcon(request.Path, request.IsDirectory, request.PixelSize)
                        ?? GetFallback(request.IsDirectory, FilePath.GetExtension(request.Path));
                    lock (Gate)
                    {
                        // 简单容量上限:长会话浏览海量文件/改缩放会让"路径|尺寸"键只增不减,
                        // 超限整体清空,图标按需重建(重建成本低,远好于无界增长)。
                        if (Cache.Count >= 512) Cache.Clear();
                        Cache[request.Key] = icon;
                    }
                }
                catch (Exception ex)
                {
                    App.Log("提取文件图标失败: " + ex.Message);
                    icon = GetFallback(request.IsDirectory, FilePath.GetExtension(request.Path));
                }
                finally
                {
                    List<Action<BitmapSource>> callbacks = null;
                    lock (Gate)
                    {
                        if (Callbacks.TryGetValue(request.Key, out callbacks)) Callbacks.Remove(request.Key);
                    }
                    Pending.TryRemove(request.Key, out _);
                    if (callbacks != null && icon != null)
                    {
                        try
                        {
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                foreach (var callback in callbacks) callback(icon);
                            }));
                        }
                        catch (InvalidOperationException) { }
                    }
                }
            }
        }

        private static BitmapSource TypeIcon(bool isDir, string ext)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var color = isDir ? Color.FromRgb(245, 190, 64) : ExtensionColor(ext);
                dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(3, 6, 42, 36), 7, 7);
                if (isDir)
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(255, 214, 92)), null, new Rect(6, 3, 21, 10), 4, 4);
                else
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 2);
                    dc.DrawLine(pen, new Point(12, 17), new Point(36, 17));
                    dc.DrawLine(pen, new Point(12, 24), new Point(32, 24));
                    dc.DrawLine(pen, new Point(12, 31), new Point(28, 31));
                }
            }
            var bmp = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        private static Color ExtensionColor(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".doc": case ".docx": case ".rtf": return Color.FromRgb(54, 111, 220);
                case ".xls": case ".xlsx": case ".csv": return Color.FromRgb(48, 164, 104);
                case ".ppt": case ".pptx": return Color.FromRgb(225, 102, 55);
                case ".pdf": return Color.FromRgb(208, 66, 74);
                case ".zip": case ".rar": case ".7z": return Color.FromRgb(163, 104, 212);
                case ".jpg": case ".jpeg": case ".png": case ".gif": return Color.FromRgb(42, 168, 186);
                case ".mp3": case ".wav": case ".flac": return Color.FromRgb(219, 93, 160);
                case ".bat": case ".cmd": case ".ps1": return Color.FromRgb(89, 104, 122);
                case ".txt": case ".md": return Color.FromRgb(92, 137, 196);
                default: return Color.FromRgb(112, 122, 143);
            }
        }
    }

    /// <summary>音乐胶囊左侧的实时频谱:WASAPI 环回采集 + FFT 对数频段柱状显示,无展开页。</summary>
    public class SpectrumModule : ModuleBase
    {
        private readonly SpectrumSurface _surface;
        private readonly float[] _current = new float[SpectrumService.BarCount];
        private readonly double _maxHeight, _minHeight;
        private bool _captureActive;
        private bool _renderSubscribed;
        private bool _disposed;
        private TimeSpan _lastRenderTime = TimeSpan.MinValue;

        public SpectrumModule(TabStyle st) : base(st)
        {
            // 胶囊固定长度(比内容宽,两侧留白),16 根柱子居中分布,不随内容变化。
            double s = Ui.Scale;
            int n = SpectrumService.BarCount;
            double barW = 3.2 * s, gap = 2.8 * s;
            double inner = n * barW + (n - 1) * gap;
            double pillW = 125 * s;
            _maxHeight = 20 * s;
            _minHeight = 1.6 * s;

            _surface = new SpectrumSurface(
                n,
                barW,
                gap,
                _maxHeight,
                _minHeight,
                1.5 * s,
                Ui.Brush(ThemeService.Text, 1));

            Pill.Width = pillW;
            Pill.Padding = new Thickness(0);
            Pill.Child = _surface;

            ThemeService.Changed += OnBarThemeChanged;
            IsVisibleChanged += (s2, e2) => SetSpectrumActive(IsVisible);
            Loaded += (s2, e2) => SetSpectrumActive(IsVisible);
            Unloaded += (s2, e2) => SetSpectrumActive(false);
        }

        protected override bool HasPanel => false;
        protected override bool HoverOpensPanel => false;

        public void SetSpectrumActive(bool active)
        {
            if (_disposed) return;
            active = active && IsVisible && IsLoaded;
            if (active == _captureActive) return;

            _captureActive = active;
            if (active)
            {
                SpectrumService.Instance.AddRef();
                _lastRenderTime = TimeSpan.MinValue;
                if (!_renderSubscribed)
                {
                    CompositionTarget.Rendering += OnRendering;
                    _renderSubscribed = true;
                }
                return;
            }

            if (_renderSubscribed)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderSubscribed = false;
            }
            SpectrumService.Instance.ReleaseRef();
        }

        private const int IdleAfterMs = 20000; // 持续无跳动多久后进入空闲动画
        private bool _idleAnim;
        private double _idleHead;
        private long _idleLastTick;
        private long _lastActiveTick = Environment.TickCount64;

        private void OnBarThemeChanged()
        {
            if (Dispatcher.CheckAccess()) ApplyBarTheme();
            else Dispatcher.BeginInvoke(new Action(ApplyBarTheme));
        }

        private void ApplyBarTheme()
        {
            _surface.SetBrush(Ui.Brush(ThemeService.Text, 1));
        }

        private void OnRendering(object sender, EventArgs args)
        {
            if (!(args is RenderingEventArgs renderingArgs)) return;
            TimeSpan now = renderingArgs.RenderingTime;
            // 空闲波纹是纯装饰的慢波(170ms 行进一根),没必要 60fps 重绘:
            // 分层窗口每被弄脏一次就要整条重呈,实测 60fps 空闲波会在静音桌面上常驻吃掉约 10% 单核。
            // 空闲期降到 20fps 观感几乎无差(每根柱子仍有 8+ 帧插值),开销降到约 1/3。
            double frameMs = _idleAnim ? 50.0 : 16.0;
            if (_lastRenderTime != TimeSpan.MinValue
                && now - _lastRenderTime < TimeSpan.FromMilliseconds(frameMs)) return;

            double elapsedMs = _lastRenderTime == TimeSpan.MinValue
                ? 33
                : Math.Min(150, Math.Max(1, (now - _lastRenderTime).TotalMilliseconds));
            _lastRenderTime = now;
            Tick(elapsedMs);
        }

        private void Tick(double elapsedMs)
        {
            if (_disposed || !_captureActive || Visibility != Visibility.Visible) return;

            var target = SpectrumService.Instance.Bands;
            // 采集线程重连等中断期间让柱子自然落回底端。
            bool stale = Environment.TickCount64 - SpectrumService.Instance.LastDataTick > 600;

            // 任一频段有真实跳动就刷新活跃时间;持续 20 秒无跳动则进入逐根跳起动画。
            bool active = false;
            if (!stale && target != null)
                foreach (var v in target)
                    if (v > 0.12f) { active = true; break; }
            if (active) _lastActiveTick = Environment.TickCount64;
            bool idle = Environment.TickCount64 - _lastActiveTick >= IdleAfterMs;

            if (idle)
            {
                if (!_idleAnim) { _idleAnim = true; _idleHead = -3; _idleLastTick = Environment.TickCount64; }
                long now = Environment.TickCount64;
                // 每 170ms 行进一根;首尾各留 3 根缓冲,扫过一圈后循环。
                _idleHead += (now - _idleLastTick) / 170.0;
                _idleLastTick = now;
                if (_idleHead > _current.Length + 3) _idleHead = -3;
            }
            else _idleAnim = false;

            for (int i = 0; i < _current.Length; i++)
            {
                float t;
                if (idle)
                {
                    // 行进波头:波头到达时该柱跳满,随波头继续前进平滑回落。
                    float d = (float)(_idleHead - i);
                    t = d >= 0 && d <= 2.5f ? 1f - d / 2.5f : 0f;
                }
                else
                {
                    t = !stale && target != null && i < target.Length ? target[i] : 0f;
                }
                float c = _current[i];
                // 上升跟随快、下落衰减慢；按真实帧间隔插值，交互繁忙跳帧后不会积压变慢。
                double baseBlend = t > c ? 0.55 : 0.28;
                double blend = 1 - Math.Pow(1 - baseBlend, elapsedMs / 33.0);
                c += (t - c) * (float)blend;
                _current[i] = Math.Clamp(c, 0f, 1f);
            }

            _surface.SetValues(_current);
        }

        private sealed class SpectrumSurface : FrameworkElement
        {
            private readonly float[] _values;
            private readonly double _barWidth;
            private readonly double _gap;
            private readonly double _maxHeight;
            private readonly double _minHeight;
            private readonly double _radius;
            private Brush _brush;

            public SpectrumSurface(
                int count,
                double barWidth,
                double gap,
                double maxHeight,
                double minHeight,
                double radius,
                Brush brush)
            {
                _values = new float[count];
                _barWidth = barWidth;
                _gap = gap;
                _maxHeight = maxHeight;
                _minHeight = minHeight;
                _radius = radius;
                _brush = brush;
                Width = count * barWidth + (count - 1) * gap;
                Height = maxHeight;
                HorizontalAlignment = HorizontalAlignment.Center;
                VerticalAlignment = VerticalAlignment.Center;
            }

            public void SetValues(float[] values)
            {
                bool changed = false;
                for (int i = 0; i < _values.Length; i++)
                {
                    if (Math.Abs(_values[i] - values[i]) <= 0.002f) continue;
                    _values[i] = values[i];
                    changed = true;
                }
                if (changed) InvalidateVisual();
            }

            public void SetBrush(Brush brush)
            {
                _brush = brush;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                base.OnRender(drawingContext);
                for (int i = 0; i < _values.Length; i++)
                {
                    float value = Math.Clamp(_values[i], 0f, 1f);
                    double height = _minHeight + (_maxHeight - _minHeight) * value;
                    double x = i * (_barWidth + _gap);
                    double y = _maxHeight - height;
                    drawingContext.PushOpacity(0.4 + 0.6 * value);
                    drawingContext.DrawRoundedRectangle(
                        _brush,
                        null,
                        new Rect(x, y, _barWidth, height),
                        _radius,
                        _radius);
                    drawingContext.Pop();
                }
            }
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_renderSubscribed)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderSubscribed = false;
            }
            ThemeService.Changed -= OnBarThemeChanged;
            if (_captureActive)
            {
                _captureActive = false;
                SpectrumService.Instance.ReleaseRef();
            }
            base.Dispose();
        }
    }

    /// <summary>性能监测胶囊指标。</summary>
    public enum PerfMetric { Cpu, Gpu, Memory, Network }

    /// <summary>性能监测胶囊:数值由 PerfService 每秒采样刷新;四个指标各自独立显隐。
    /// 悬停自动展开数据页:CPU/GPU 显示占用、温度、频率,内存显示占用与容量频率,网络显示上下行速率。</summary>
    public class PerfModule : ModuleBase
    {
        private readonly PerfMetric _metric;
        private readonly TextBlock _value;
        private readonly Dictionary<string, TextBlock> _rows = new Dictionary<string, TextBlock>();
        private string _lastPillText;
        private TextBlock _panelTitle;
        private bool _active;
        private bool _disposed;

        public PerfModule(PerfMetric metric, TabStyle st) : base(st)
        {
            _metric = metric;
            double s = Ui.Scale;
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (metric != PerfMetric.Network)
            {
                // 网络胶囊不带图标:上行/下行各自有箭头标识,省出宽度。
                sp.Children.Add(metric switch
                {
                    PerfMetric.Cpu => MakeCpuGlyph(17),
                    PerfMetric.Gpu => MakeGpuGlyph(18),
                    _ => MakeMemoryGlyph(18)
                });
            }
            _value = MakeText("--", -1);
            _value.Margin = new Thickness(metric == PerfMetric.Network ? 0 : 4 * s, 0, 0, 0);
            sp.Children.Add(_value);
            Pill.Child = sp;
            Pill.Padding = new Thickness(0);
            // 固定宽度:数值在 "0%" 与 "100%" 间变化时不让胶囊抖动。
            Pill.Width = (metric == PerfMetric.Network ? 88 : 58) * s;

            BuildHoverPanel();
            IsVisibleChanged += (s2, e2) => SetActive(IsVisible);
            Loaded += (s2, e2) => SetActive(IsVisible);
            Unloaded += (s2, e2) => SetActive(false);
        }

        private void SetPillText(string text)
        {
            // 文字没变就不要再赋值:分层窗口每次被弄脏都要整条重呈,同值赋值在 WPF 里虽多数会被 DP 相等性拦掉,
            // 但 ToolTip 这种会重建控件的属性必须自己挡。
            if (_lastPillText == text) return;
            _lastPillText = text;
            _value.Text = text;
        }

        protected override void OnPanelShown()
        {
            base.OnPanelShown();
            var latest = PerfService.Instance.Latest;
            if (latest != null) Apply(latest);
        }

        private string Caption => _metric switch
        {
            PerfMetric.Cpu => "CPU 占用率",
            PerfMetric.Gpu => "GPU 占用率",
            PerfMetric.Memory => "内存占用",
            _ => "网络上下行"
        };

        protected override bool HasPanel => true;
        protected override bool HoverOpensPanel => true;
        protected override bool CenterPanelOnPill => true;

        // ---------- 悬停展开页 ----------

        private void BuildHoverPanel()
        {
            double s = Ui.Scale;
            double width = _metric switch
            {
                PerfMetric.Gpu => 210,
                PerfMetric.Memory => 170,
                _ => 150
            };
            var body = new StackPanel { Width = width * s };
            string title = _metric == PerfMetric.Memory ? "内存" : _metric == PerfMetric.Network ? "网络" : _metric == PerfMetric.Cpu ? "CPU" : "GPU";
            _panelTitle = MakeText(title);
            _panelTitle.FontSize = 14 * s;
            _panelTitle.FontWeight = FontWeights.SemiBold;
            _panelTitle.Margin = new Thickness(2, 0, 2, 6 * s);
            body.Children.Add(_panelTitle);

            switch (_metric)
            {
                case PerfMetric.Cpu:
                    AddRow(body, "占用");
                    AddRow(body, "温度");
                    AddRow(body, "频率");
                    break;
                case PerfMetric.Gpu:
                    AddRow(body, "型号");
                    AddRow(body, "占用");
                    AddRow(body, "温度");
                    AddRow(body, "频率");
                    break;
                case PerfMetric.Memory:
                    AddRow(body, "占用");
                    AddRow(body, "已用");
                    AddRow(body, "容量");
                    AddRow(body, "频率");
                    break;
                default:
                    AddRow(body, "下行");
                    AddRow(body, "上行");
                    break;
            }
            Panel.Child = body;
        }

        private TextBlock AddRow(StackPanel body, string label)
        {
            double s = Ui.Scale;
            var row = new Grid { Margin = new Thickness(2, 2 * s, 2, 2 * s) };
            var lab = MakeText(label);
            lab.Opacity = 0.72;
            row.Children.Add(lab);
            var val = MakeText("--");
            val.HorizontalAlignment = HorizontalAlignment.Right;
            val.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(val);
            body.Children.Add(row);
            _rows[label] = val;
            return val;
        }

        private void SetActive(bool active)
        {
            if (_disposed) return;
            active = active && IsVisible && IsLoaded;
            if (active == _active) return;
            _active = active;
            var service = PerfService.Instance;
            if (active)
            {
                service.AddRef();
                service.Updated += OnSample;
                var latest = service.Latest;
                if (latest != null) Apply(latest);
                return;
            }
            service.Updated -= OnSample;
            service.ReleaseRef();
        }

        private void OnSample(PerfSample sample)
        {
            if (_disposed) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_disposed) Apply(sample);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void Apply(PerfSample sample)
        {
            switch (_metric)
            {
                case PerfMetric.Cpu:
                    SetPillText(FmtPercent(sample.CpuPercent));
                    SetRow("占用", FmtPercent(sample.CpuPercent));
                    SetRow("温度", FmtTemp(sample.CpuTempC));
                    SetRow("频率", FmtFreq(sample.CpuFreqMhz));
                    break;
                case PerfMetric.Gpu:
                    var gpu = PickGpu(sample.Gpus);
                    SetPillText(gpu == null ? "--" : FmtPercent(gpu.UtilPercent));
                    SetRow("型号", gpu == null ? "--" : gpu.Name);
                    SetRow("占用", gpu == null ? "--" : FmtPercent(gpu.UtilPercent));
                    SetRow("温度", FmtTemp(gpu == null ? -1 : gpu.TempC));
                    SetRow("频率", FmtFreq(gpu == null ? -1 : gpu.ClockMhz));
                    break;
                case PerfMetric.Memory:
                    if (sample.MemTotalBytes <= 0)
                    {
                        SetPillText("--");
                    }
                    else
                    {
                        double percent = 100.0 * sample.MemUsedBytes / sample.MemTotalBytes;
                        SetPillText(FmtPercent(percent));
                    }
                    SetRow("占用", sample.MemTotalBytes <= 0
                        ? "--"
                        : FmtPercent(100.0 * sample.MemUsedBytes / sample.MemTotalBytes));
                    SetRow("已用", sample.MemTotalBytes <= 0 ? "--" : FmtGb(sample.MemUsedBytes) + " GB");
                    SetRow("容量", sample.MemTotalBytes <= 0 ? "--" : FmtGb(sample.MemTotalBytes) + " GB");
                    SetRow("频率", sample.MemSpeedMts > 0 ? Math.Round(sample.MemSpeedMts) + " MT/s" : "--");
                    break;
                default:
                    if (sample.NetUpBps < 0 && sample.NetDownBps < 0)
                    {
                        SetPillText("--");
                    }
                    else
                    {
                        SetPillText("↓" + FmtSpeed(sample.NetDownBps) + " ↑" + FmtSpeed(sample.NetUpBps));
                    }
                    SetRow("下行", sample.NetDownBps < 0 ? "--" : FmtSpeedLong(sample.NetDownBps) + "/s");
                    SetRow("上行", sample.NetUpBps < 0 ? "--" : FmtSpeedLong(sample.NetUpBps) + "/s");
                    break;
            }
        }

        private void SetRow(string label, string text)
        {
            // 面板没打开时那棵树不参与合成,逐秒写它只是白花 DP 与文本排版开销;
            // 打开时 OnPanelShown 会整体刷一遍,不会显示旧值。
            if (Panel == null || !Panel.IsVisible) return;
            if (_rows.TryGetValue(label, out var tb) && tb.Text != text) tb.Text = text;
        }

        private static string FmtTemp(double c)
            => c < 0 ? "--" : Math.Round(c) + "°C";

        private static string FmtFreq(double mhz)
        {
            if (mhz <= 0) return "--";
            return mhz >= 1000 ? (mhz / 1000).ToString("0.00") + " GHz" : Math.Round(mhz) + " MHz";
        }

        private static GpuAdapterInfo PickGpu(List<GpuAdapterInfo> gpus)
        {
            if (gpus == null || gpus.Count == 0) return null;
            string want = App.Settings?.MonitorGpuLuid;
            if (!string.IsNullOrEmpty(want))
                foreach (var g in gpus)
                    if (string.Equals(g.Luid, want, StringComparison.OrdinalIgnoreCase)) return g;
            return gpus[0];
        }

        private static string FmtPercent(double v)
            => v < 0 ? "--" : Math.Round(v).ToString() + "%";

        private static string FmtGb(ulong bytes)
            => (bytes / 1073741824.0).ToString("0.0");

        private static string FmtSpeed(double bps)
        {
            if (bps < 0) return "--";
            if (bps >= 1073741824) return (bps / 1073741824).ToString("0.0") + "G";
            if (bps >= 1048576) return (bps / 1048576).ToString(bps >= 10485760 ? "0" : "0.0") + "M";
            return (bps / 1024).ToString(bps >= 10240 ? "0" : "0.0") + "K";
        }

        private static string FmtSpeedLong(double bps)
        {
            if (bps < 0) return "未知";
            if (bps >= 1073741824) return (bps / 1073741824).ToString("0.0") + " MB";
            if (bps >= 1048576) return (bps / 1048576).ToString("0.0") + " MB";
            if (bps >= 1024) return (bps / 1024).ToString("0.0") + " KB";
            return Math.Round(bps) + " B";
        }

        public override void Dispose()
        {
            if (_disposed) return;
            // 不经由 SetActive(false) 退订:SetActive 的 _disposed 守卫会把它短路,
            // 导致采样线程永不停止、委托持模块。这里直接退订并释放引用。
            var service = PerfService.Instance;
            if (_active)
            {
                _active = false;
                service.Updated -= OnSample;
                service.ReleaseRef();
            }
            _disposed = true;
            base.Dispose();
        }
    }
}
