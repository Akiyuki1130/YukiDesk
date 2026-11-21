using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            Pill.Child = CenterIcon(Ui.SnowflakeGlyph(21 * Ui.Scale, ContentInkKey));
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
            _pillText = MakePillText("");
            Pill.Child = _pillText;
            // 下拉内容整体居中:文字居中对齐,并设最小宽度留出左右呼吸感。
            var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 170 * Ui.Scale };
            _pTime = MakeText("", 12); _pWeek = MakeText(""); _pDate = MakeText(""); _pLunar = MakeText(""); _pFest = MakeText("");
            _pTime.FontWeight = FontWeights.Bold;
            foreach (var tb in new[] { _pTime, _pWeek, _pDate, _pLunar, _pFest }) tb.HorizontalAlignment = HorizontalAlignment.Center;
            _pTime.Margin = new Thickness(4, 2, 4, 6); _pWeek.Margin = new Thickness(4, 2, 4, 2); _pDate.Margin = new Thickness(4, 2, 4, 2); _pLunar.Margin = new Thickness(4, 2, 4, 2);
            _pFest.Margin = new Thickness(4, 0, 4, 4);
            _pFest.FontSize = Math.Max(9, 13 * Ui.Scale);
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
        private readonly TextBlock _tArtist, _playGlyph;
        private readonly TrackTransition _pillTransition, _panelTransition;
        private string _trackKey = "", _thumbnailKey = "";
        private long _lastMediaGeneration = -1;
        private long _skipExpiresAt;
        private int _skipDirection;
        private string _lastTitle = "暂无音乐";
        public MusicModule(TabStyle st) : base(st)
        {
            var coverBorder = BuildCover(26, 7, 14, out _smallImg);
            // 歌名超宽时在药丸内滚动；未超宽时药丸收缩到内容宽度。
            _pillMarquee = new MarqueeText(MakePillText("暂无音乐"), 150 * Ui.Scale, 20 * Ui.Scale, hug: true);
            _pillMarquee.Margin = new Thickness(8 * Ui.Scale, 0, 0, 0);
            var pillContent = new StackPanel { Orientation = Orientation.Horizontal };
            pillContent.Children.Add(coverBorder);
            pillContent.Children.Add(_pillMarquee);
            _pillTransition = new TrackTransition(pillContent);
            Pill.Child = _pillTransition;

            var bigCover = BuildCover(96, 14, 30, out _bigImg);
            bigCover.SetResourceReference(Border.BackgroundProperty, "ThemePanelSubtle");
            // 展开页歌名同样滚动；超宽前保持居中。宽度 = 面板 300 - 内边距 24 - 标题左右边距 8。
            _panelMarquee = new MarqueeText(MakeText("暂无音乐", 2), 268 * Ui.Scale, 24 * Ui.Scale, hug: false);
            _panelMarquee.Margin = new Thickness(4 * Ui.Scale, 12 * Ui.Scale, 4 * Ui.Scale, 0);
            _pillMarquee.SetText("暂无音乐");
            _panelMarquee.SetText("暂无音乐");
            _tArtist = MakeText("");
            _tArtist.TextAlignment = TextAlignment.Center;
            _tArtist.Margin = new Thickness(4 * Ui.Scale, 4 * Ui.Scale, 4 * Ui.Scale, 0);
            var panelTrack = new StackPanel();
            panelTrack.Children.Add(bigCover);
            panelTrack.Children.Add(_panelMarquee);
            panelTrack.Children.Add(_tArtist);
            _panelTransition = new TrackTransition(panelTrack);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14 * Ui.Scale, 0, 0)
            };
            buttons.Children.Add(ShellButton(Glyph("⏮", 19), () => Skip(-1), pressFeedback: true));
            _playGlyph = Glyph("▶", 19);
            buttons.Children.Add(ShellButton(_playGlyph, () => NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PLAY_PAUSE)));
            buttons.Children.Add(ShellButton(Glyph("⏭", 19), () => Skip(1), pressFeedback: true));
            var body = new StackPanel { Width = 300 * Ui.Scale };
            body.Children.Add(_panelTransition);
            body.Children.Add(buttons);
            Panel.Child = body;
        }

        private Border BuildCover(double size, double radius, double glyphSize, out Image image)
        {
            double width = size * Ui.Scale, corner = radius * Ui.Scale;
            image = new Image { Stretch = Stretch.UniformToFill };
            var host = new Grid();
            host.Children.Add(Glyph("♫", glyphSize));
            host.Children.Add(image);
            return new Border
            {
                Width = width, Height = width, CornerRadius = new CornerRadius(corner), Child = host,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Clip = new RectangleGeometry(new Rect(0, 0, width, width), corner, corner)
            };
        }

        private void Skip(int direction)
        {
            _skipDirection = direction;
            _skipExpiresAt = Environment.TickCount64 + 3000;
            NativeMethods.SendMediaKey(direction < 0 ? NativeMethods.VK_MEDIA_PREV : NativeMethods.VK_MEDIA_NEXT);
        }

        public void Update(MediaInfo info)
        {
            if (info == null || info.Generation <= _lastMediaGeneration) return;
            _lastMediaGeneration = info.Generation;
            if (info.HasSession && info.TrackKey.Length == 0) return;

            bool trackChanged = !string.Equals(_trackKey, info.TrackKey, StringComparison.Ordinal);
            bool animateTrack = trackChanged && _trackKey.Length != 0 && info.HasSession;
            var outgoingPill = animateTrack ? _pillTransition.Snapshot() : null;
            var outgoingPanel = animateTrack ? _panelTransition.Snapshot() : null;

            string title = string.IsNullOrEmpty(info.Title) ? "暂无音乐" : info.Title;
            if (!string.Equals(title, _lastTitle, StringComparison.Ordinal))
            {
                _lastTitle = title;
                _pillMarquee.SetText(title);
                _panelMarquee.SetText(title);
            }

            if (_tArtist.Text != info.Artist) _tArtist.Text = info.Artist;
            if (trackChanged || _thumbnailKey != info.ThumbnailKey)
            {
                _thumbnailKey = info.ThumbnailKey;
                SetCover(_smallImg, info.Cover, !animateTrack);
                SetCover(_bigImg, info.Cover, !animateTrack);
            }
            string playGlyph = info.Playing ? "⏸" : "▶";
            if (_playGlyph.Text != playGlyph)
            {
                _playGlyph.Text = playGlyph;
                NudgeGlyph(_playGlyph, playGlyph);
            }
            if (trackChanged)
            {
                int direction = Environment.TickCount64 < _skipExpiresAt ? _skipDirection : 0;
                _skipDirection = 0;
                _trackKey = info.TrackKey;
                _pillTransition.Play(outgoingPill, direction, 8 * Ui.Scale);
                _panelTransition.Play(outgoingPanel, direction, 18 * Ui.Scale);
            }
        }

        private static void SetCover(Image image, BitmapSource source, bool fade)
        {
            if (ReferenceEquals(image.Source, source)) return;
            image.BeginAnimation(OpacityProperty, null);
            image.Source = source;
            if (fade && source != null && image.IsVisible && SystemParameters.ClientAreaAnimation)
                image.BeginAnimation(OpacityProperty, TrackAnimation(0, 1, 135));
        }

        protected override void OnPanelHidden()
        {
            _panelTransition.Reset();
            _bigImg.BeginAnimation(OpacityProperty, null);
        }

        public override void Dispose()
        {
            _pillTransition.Reset();
            _panelTransition.Reset();
            _smallImg.BeginAnimation(OpacityProperty, null);
            _bigImg.BeginAnimation(OpacityProperty, null);
            base.Dispose();
        }

        private static DoubleAnimation TrackAnimation(double from, double to, int milliseconds) =>
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };

        private sealed class TrackTransition : Grid
        {
            private readonly FrameworkElement _content;
            private readonly TranslateTransform _motion = new TranslateTransform();
            private readonly Canvas _outgoing = new Canvas { IsHitTestVisible = false };

            internal TrackTransition(FrameworkElement content)
            {
                _content = content;
                _content.RenderTransform = _motion;
                _outgoing.ClipToBounds = true;
                ClipToBounds = true;
                Children.Add(_content);
                Children.Add(_outgoing);
            }

            internal BitmapSource Snapshot()
            {
                if (!SystemParameters.ClientAreaAnimation || !IsVisible || ActualWidth < 1 || ActualHeight < 1)
                    return null;
                var dpi = VisualTreeHelper.GetDpi(this);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                    PixelFormats.Pbgra32);
                bitmap.Render(this);
                bitmap.Freeze();
                return bitmap;
            }

            internal void Play(BitmapSource previous, int direction, double distance)
            {
                Reset();
                if (previous == null) return;
                int duration = Math.Clamp(App.Settings?.AnimationDurationMs ?? 180, 120, 360);
                var past = new Image { Source = previous, Width = previous.Width, Height = previous.Height };
                var travel = new TranslateTransform();
                past.RenderTransform = travel;
                _outgoing.Children.Add(past);
                _content.BeginAnimation(OpacityProperty, TrackAnimation(0.1, 1, duration));
                var fade = TrackAnimation(1, 0, duration);
                fade.Completed += (_, _) => _outgoing.Children.Remove(past);
                past.BeginAnimation(OpacityProperty, fade);
                var axis = direction == 0 ? TranslateTransform.YProperty : TranslateTransform.XProperty;
                double offset = direction == 0 ? distance / 2 : direction * distance;
                _motion.BeginAnimation(axis, TrackAnimation(offset, 0, duration));
                travel.BeginAnimation(axis, TrackAnimation(0, -offset, duration));
            }

            internal void Reset()
            {
                _outgoing.Children.Clear();
                _content.BeginAnimation(OpacityProperty, null);
                _motion.BeginAnimation(TranslateTransform.XProperty, null);
                _motion.BeginAnimation(TranslateTransform.YProperty, null);
            }
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
        private Border ShellButton(FrameworkElement content, Action click, bool pressFeedback = false)
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
            b.MouseLeftButtonUp += (s, e) =>
            {
                if (pressFeedback && SystemParameters.ClientAreaAnimation)
                {
                    var scale = new ScaleTransform();
                    b.RenderTransform = scale;
                    b.RenderTransformOrigin = new Point(0.5, 0.5);
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, TrackAnimation(0.84, 1, 150));
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, TrackAnimation(0.84, 1, 150));
                }
                click();
            };
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
            _slider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = initialVolume,
                Style = (Style)Application.Current.FindResource("CapsuleSlider"),
                LargeChange = 0.02,
                SmallChange = 0.01
            };
            _slider.SetResourceReference(Control.BackgroundProperty, "ThemePanelControl");
            System.Windows.Automation.AutomationProperties.SetName(_slider, "音量");
            _value = MakeText(initialVolumeAvailable ? Math.Round(initialVolume * 100) + "%" : "读取失败");
            _value.FontWeight = FontWeights.Bold;
            body.Children.Add(Ui.SliderWithValue(_slider, _value, 52 * Ui.Scale, panel: true));
            _slider.IsEnabled = initialVolumeAvailable;
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

    public static class IconCache
    {
        private sealed class IconRequest
        {
            public string Path;
            public bool IsDirectory;
            public int PixelSize;
            public string Key;
            public List<Action<BitmapSource>> Subscribers;
        }

        private const int CacheCapacity = 512;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, BitmapSource> Cache = new();
        private static readonly Queue<string> CachedKeys = new();
        private static readonly Dictionary<string, BitmapSource> Fallbacks = new();
        private static readonly Dictionary<string, LinkedListNode<IconRequest>> Pending = new();
        private static readonly LinkedList<IconRequest> PrefetchQueue = new();
        private static readonly LinkedList<IconRequest> VisibleQueue = new();
        private static Thread _worker;

        public static BitmapSource GetFallback(bool isDir, string ext)
        {
            int pixelSize = IconPixelSize();
            string key = (isDir ? "dir:" : "file:") + pixelSize;
            lock (Gate)
                if (Fallbacks.TryGetValue(key, out var cached)) return cached;
            var icon = NativeMethods.GetSystemTypeIcon(isDir, pixelSize) ?? TypeIcon(isDir, ext);
            lock (Gate) Fallbacks[key] = icon;
            return icon;
        }

        private static int IconPixelSize()
            => Math.Max(48, (int)Math.Ceiling(48 * Ui.Scale * 1.5));

        internal static void LoadAsync(FileSystemInfo item, Action<BitmapSource> apply = null)
        {
            bool isDirectory = item is DirectoryInfo;
            int pixels = IconPixelSize();
            string key = (isDirectory ? "dir:" : "file:") + item.FullName + "|" + pixels + "|" + item.LastWriteTimeUtc.Ticks;
            BitmapSource icon;
            lock (Gate)
            {
                if (!Cache.TryGetValue(key, out icon))
                {
                    if (!Pending.TryGetValue(key, out var node))
                    {
                        var target = apply == null ? PrefetchQueue : VisibleQueue;
                        node = target.AddLast(new IconRequest
                        {
                            Path = item.FullName, IsDirectory = isDirectory, PixelSize = pixels, Key = key
                        });
                        Pending.Add(key, node);
                    }
                    if (apply != null)
                    {
                        (node.Value.Subscribers ??= new List<Action<BitmapSource>>()).Add(apply);
                        if (node.List == PrefetchQueue)
                        {
                            // 展开的面板先取图标。启动预取与点击共用同一个请求。
                            PrefetchQueue.Remove(node);
                            VisibleQueue.AddLast(node);
                        }
                    }
                    if (_worker == null)
                    {
                        _worker = new Thread(ProcessQueue) { IsBackground = true, Name = "ShellIconLoader" };
                        _worker.SetApartmentState(ApartmentState.STA);
                        _worker.Start();
                    }
                    Monitor.Pulse(Gate);
                    return;
                }
            }
            apply?.Invoke(icon);
        }

        private static void ProcessQueue()
        {
            while (true)
            {
                IconRequest request;
                lock (Gate)
                {
                    while (VisibleQueue.Count == 0 && PrefetchQueue.Count == 0) Monitor.Wait(Gate);
                    var queue = VisibleQueue.Count > 0 ? VisibleQueue : PrefetchQueue;
                    request = queue.First.Value;
                    queue.RemoveFirst();
                }
                BitmapSource icon = null;
                try
                {
                    icon = NativeMethods.GetFileIcon(request.Path, request.IsDirectory, request.PixelSize)
                        ?? GetFallback(request.IsDirectory, FilePath.GetExtension(request.Path));
                }
                catch (Exception ex) { App.Log("提取文件图标失败: " + ex.Message); }

                List<Action<BitmapSource>> callbacks;
                lock (Gate)
                {
                    Pending.Remove(request.Key);
                    if (icon != null)
                    {
                        if (Cache.Count >= CacheCapacity) Cache.Remove(CachedKeys.Dequeue());
                        Cache.Add(request.Key, icon);
                        CachedKeys.Enqueue(request.Key);
                    }
                    callbacks = request.Subscribers;
                }
                if (callbacks == null || icon == null) continue;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted) continue;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var apply in callbacks) apply(icon);
                }));
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
        private const int IdleAfterMs = 20000;
        private static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(32);
        private static readonly TimeSpan QuietInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(67);
        private readonly DispatcherTimer _refresh = new DispatcherTimer(DispatcherPriority.Background);
        private bool _captureActive;
        private bool _disposed;
        private long _lastTick, _lastAudible;

        public SpectrumModule(TabStyle st) : base(st)
        {
            // 胶囊固定长度(比内容宽,两侧留白),16 根柱子居中分布,不随内容变化。
            double s = Ui.Scale;
            int n = SpectrumService.BarCount;
            double barW = 3.2 * s, gap = 2.8 * s;
            _surface = new SpectrumSurface(n, barW, gap, 20 * s, 1.6 * s, 1.5 * s);
            _surface.SetResourceReference(SpectrumSurface.InkProperty, ContentInkKey);
            _refresh.Tick += OnSpectrumTick;
            Pill.Width = 125 * s;
            Pill.Padding = new Thickness(0);
            Pill.Child = _surface;

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
                _lastTick = _lastAudible = Environment.TickCount64;
                _refresh.Interval = ActiveInterval;
                _refresh.Start();
                return;
            }

            _refresh.Stop();
            SpectrumService.Instance.ReleaseRef();
        }

        private void OnSpectrumTick(object sender, EventArgs e)
        {
            if (_disposed || !_captureActive || !IsVisible) return;
            long now = Environment.TickCount64;
            double elapsedMs = Math.Clamp(now - _lastTick, 1, 150);
            _lastTick = now;

            var target = SpectrumService.Instance.Bands;
            // 采集线程重连等中断期间让柱子自然落回底端。
            bool stale = now - SpectrumService.Instance.LastDataTick > 600;

            bool active = false;
            if (!stale && target != null)
                foreach (var v in target)
                    if (v > 0.12f) { active = true; break; }
            if (active) _lastAudible = now;
            long silentMs = now - _lastAudible;
            bool idle = silentMs >= IdleAfterMs;
            var interval = idle ? IdleInterval : silentMs >= 1500 ? QuietInterval : ActiveInterval;
            if (_refresh.Interval != interval) _refresh.Interval = interval;

            // 20 秒安静后恢复原来的逐柱波纹，按时间推进，避免空闲时每帧刷新整条顶栏。
            double head = ((silentMs - IdleAfterMs) / 170.0) % (_current.Length + 6) - 3;
            float attack = (float)(1 - Math.Pow(0.45, elapsedMs / 33.0));
            float decay = (float)(1 - Math.Pow(0.72, elapsedMs / 33.0));
            for (int i = 0; i < _current.Length; i++)
            {
                float t;
                if (idle)
                {
                    double distance = head - i;
                    t = distance >= 0 && distance <= 2.5 ? (float)(1 - distance / 2.5) : 0f;
                }
                else
                    t = !stale && target != null && i < target.Length ? target[i] : 0f;
                float c = _current[i];
                c += (t - c) * (t > c ? attack : decay);
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
            internal static readonly DependencyProperty InkProperty = DependencyProperty.Register(
                "Ink", typeof(Brush), typeof(SpectrumSurface),
                new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

            public SpectrumSurface(
                int count,
                double barWidth,
                double gap,
                double maxHeight,
                double minHeight,
                double radius)
            {
                _values = new float[count];
                _barWidth = barWidth;
                _gap = gap;
                _maxHeight = maxHeight;
                _minHeight = minHeight;
                _radius = radius;
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

            protected override void OnRender(DrawingContext drawingContext)
            {
                base.OnRender(drawingContext);
                var brush = (Brush)GetValue(InkProperty);
                for (int i = 0; i < _values.Length; i++)
                {
                    float value = Math.Clamp(_values[i], 0f, 1f);
                    double height = _minHeight + (_maxHeight - _minHeight) * value;
                    double x = i * (_barWidth + _gap);
                    double y = _maxHeight - height;
                    drawingContext.PushOpacity(0.4 + 0.6 * value);
                    drawingContext.DrawRoundedRectangle(
                        brush,
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
            _refresh.Stop();
            _refresh.Tick -= OnSpectrumTick;
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
            _value = MakePillText("--", -1);
            _value.Margin = new Thickness(metric == PerfMetric.Network ? 0 : 4 * s, 0, 0, 0);
            sp.Children.Add(_value);
            Pill.Child = sp;
            Pill.Padding = new Thickness(0);
            // 固定宽度:数值在 "0%" 与 "100%" 间变化时不让胶囊抖动。
            Pill.Width = (metric == PerfMetric.Network ? 112 : 58) * s;
            System.Windows.Automation.AutomationProperties.SetName(Pill, Caption);

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
            var titleBlock = MakeText(title, 1.5);
            titleBlock.FontWeight = FontWeights.Bold;
            titleBlock.Margin = new Thickness(2, 0, 2, 6 * s);
            body.Children.Add(titleBlock);

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
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42 * s) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var lab = MakeText(label);
            lab.Opacity = 0.72;
            row.Children.Add(lab);
            var val = MakeText("--");
            val.TextAlignment = TextAlignment.Right;
            val.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(val, 1);
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
                    if (!_disposed && _active) Apply(sample);
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
                    if (!IsValidRate(sample.NetUpBps) && !IsValidRate(sample.NetDownBps))
                    {
                        SetPillText("--");
                    }
                    else
                    {
                        SetPillText("↓" + FormatNetworkRate(sample.NetDownBps, compact: true)
                            + " ↑" + FormatNetworkRate(sample.NetUpBps, compact: true));
                    }
                    SetRow("下行", FormatNetworkRate(sample.NetDownBps, compact: false));
                    SetRow("上行", FormatNetworkRate(sample.NetUpBps, compact: false));
                    break;
            }
        }

        private void SetRow(string label, string text)
        {
            // 面板没打开时那棵树不参与合成,逐秒写它只是白花 DP 与文本排版开销;
            // 打开时 OnPanelShown 会整体刷一遍,不会显示旧值。
            if (Panel == null || !Panel.IsVisible) return;
            if (_rows.TryGetValue(label, out var tb) && tb.Text != text)
            {
                tb.Text = text;
                if (label == "型号") tb.ToolTip = text == "--" ? null : text;
            }
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
            => !double.IsFinite(v) || v < 0 ? "--" : Math.Round(Math.Min(100, v)).ToString() + "%";

        private static string FmtGb(ulong bytes)
            => (bytes / 1073741824.0).ToString("0.0");

        private static readonly string[] RateUnits = { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };
        private static bool IsValidRate(double bytesPerSecond) => double.IsFinite(bytesPerSecond) && bytesPerSecond >= 0;

        internal static string FormatNetworkRate(double bytesPerSecond, bool compact)
        {
            if (!IsValidRate(bytesPerSecond)) return "--";
            double value = bytesPerSecond;
            int unit = 0;
            while (value >= 1024 && unit < RateUnits.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            int decimals = unit == 0 || (compact && value >= 10) ? 0 : 1;
            double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            if (rounded >= 1024 && unit < RateUnits.Length - 1)
            {
                rounded = 1;
                unit++;
            }
            string suffix = compact ? RateUnits[unit].Substring(0, 1) : " " + RateUnits[unit] + "/s";
            string amount = rounded >= 1024 ? (compact ? "999+" : "1023+") : rounded.ToString("0.#");
            return amount + suffix;
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
