using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
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
            Button awake = null;
            awake = PanelControls.GlassButton(AwakeSession.Label, ShowAwakeChoices);
            body.Children.Add(awake);
            Panel.Child = body;

            void ShowAwakeChoices()
            {
                var actions = new List<FlyoutItem>
                {
                    new("30 分钟", () => SetAwake(TimeSpan.FromMinutes(30))),
                    new("直到取消", () => SetAwake(null))
                };
                if (AwakeSession.Enabled) actions.Add(new("取消保持唤醒", () =>
                {
                    AwakeSession.Stop();
                    awake.Content = AwakeSession.Label;
                }));
                TrackFlyout(FlyoutMenu.Show(actions, "保持唤醒", awake));
            }

            void SetAwake(TimeSpan? duration)
            {
                if (AwakeSession.Start(duration)) awake.Content = AwakeSession.Label;
                else ConfirmDialog.Notice("保持唤醒未生效", "Windows 未接受此次设置。");
            }
        }

        private void AddPowerButton(StackPanel body, string title, string sub, Func<bool> action,
            bool confirm, bool danger)
        {
            var b = PanelControls.GlassButton(title, () =>
            {
                bool allowed = !confirm || (danger
                    ? ConfirmDialog.ShowDanger(title, "确定要" + title + "吗？", title)
                    : ConfirmDialog.Show(title, "确定要" + title + "吗？", title));
                if (!allowed) return;
                if (!action()) ConfirmDialog.Notice(title + "失败", title + "操作未能完成。");
            });
            b.ToolTip = sub;
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
        private readonly ClockTools _clockTools;
        private DateTime _detailsDate;
        private bool _disposed;
        private bool _presentationDemand = true;
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
            body.Children.Add(_pTime); body.Children.Add(_pWeek); body.Children.Add(_pDate); body.Children.Add(_pLunar); body.Children.Add(_pFest);
            _clockTools = new ClockTools();
            body.Children.Add(_clockTools);
            Panel.Child = body;
            _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _timer.Tick += (_, _) => Refresh();
            ClockTools.CountdownChanged += Refresh;
            Microsoft.Win32.SystemEvents.TimeChanged += OnSystemTimeChanged;
            Loaded += (_, _) => SyncClockTimer();
            IsVisibleChanged += (_, _) => SyncClockTimer();
            Refresh();
        }
        protected override bool CenterPanelOnPill => true;
        protected override Size GetPanelEnvelope(Size content) => new(content.Width, content.Height + 252 * Ui.Scale);
        private void SyncClockTimer()
        {
            if (_disposed || !_presentationDemand || !IsVisible || !IsLoaded) _timer.Stop();
            else { Refresh(); _timer.Start(); }
        }

        internal void SetPresentationDemand(bool demand)
        {
            _presentationDemand = demand;
            SyncClockTimer();
        }

        private void OnSystemTimeChanged(object sender, EventArgs args)
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(Refresh));
        }

        internal void RefreshPreferences()
        {
            _clockTools.UpdateTimeZone();
            Refresh();
        }

        private void Refresh()
        {
            if (_disposed) return;
            var now = DateTime.Now;
            bool showSeconds = App.Settings.ShowSeconds;
            bool showDetails = Open || HasVisiblePanel;
            int delay = ClockTools.CountdownRunning ? 250 : showSeconds || showDetails ? 1000 - now.Millisecond
                : 60_000 - (now.Second * 1000 + now.Millisecond);
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, delay));
            string pill = now.ToString(showSeconds ? "HH:mm:ss" : "HH:mm");
            if (App.Settings.ShowDate) pill += "  " + now.ToString("M月d日");
            if (App.Settings.ShowWeekday) pill += "  周" + "日一二三四五六"[(int)now.DayOfWeek];
            if (ClockTools.CapsuleCountdown is string timer) pill += "  " + timer;
            // 只在文字真的变了才赋值:分层窗口每次被弄脏都会整条重呈,同值赋值是纯浪费。
            if (_pillText.Text != pill) _pillText.Text = pill;
            // 面板没打开时不刷面板那 5 个 TextBlock(打开时 OnPanelShown 会补一次)。
            if (!showDetails) return;
            _clockTools.Refresh(now);
            string time = now.ToString("HH:mm:ss");
            if (_pTime.Text != time) _pTime.Text = time;
            if (_detailsDate == now.Date) return;
            _detailsDate = now.Date;
            _pWeek.Text = WeekNames[(int)now.DayOfWeek];
            _pDate.Text = now.ToString("yyyy年M月d日");
            _pLunar.Text = Lunar.ToLunarString(now);
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
        protected override void OnPanelHidden()
        {
            _clockTools.CloseCalendar();
            Refresh();
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClockTools.CountdownChanged -= Refresh;
            Microsoft.Win32.SystemEvents.TimeChanged -= OnSystemTimeChanged;
            _timer.Stop();
            base.Dispose();
        }
    }

    public partial class MusicModule : ModuleBase
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
        private readonly Button _sourceButton;
        private MediaService _controller;
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
            buttons.Children.Add(ShellButton(_playGlyph, () => _ = SendMediaCommandAsync(0)));
            buttons.Children.Add(ShellButton(Glyph("⏭", 19), () => Skip(1), pressFeedback: true));
            var body = new StackPanel { Width = 300 * Ui.Scale };
            body.Children.Add(_panelTransition);
            body.Children.Add(BuildTimeline());
            body.Children.Add(buttons);
            _sourceButton = PanelControls.GlassButton("播放来源", ShowSources);
            _sourceButton.HorizontalAlignment = HorizontalAlignment.Center;
            _sourceButton.Margin = new Thickness(0, 9 * Ui.Scale, 0, 0);
            body.Children.Add(_sourceButton);
            Panel.Child = body;
        }

        internal void UseMediaService(MediaService service) => _controller = service;

        private void ShowSources()
        {
            if (_controller == null) return;
            var preferred = App.Settings.PreferredMediaSource;
            var actions = new List<FlyoutItem> { new FlyoutItem(
                (string.IsNullOrEmpty(preferred) ? "✓  " : "") + "跟随系统", () => _controller.ChooseSource("")) };
            foreach (var source in _controller.Sources())
                actions.Add(new FlyoutItem((string.Equals(source.Id, preferred, StringComparison.OrdinalIgnoreCase) ? "✓  " : "")
                    + source.Title, () => _controller.ChooseSource(source.Id)));
            TrackFlyout(FlyoutMenu.Show(actions.ToArray(), "播放来源", _sourceButton));
        }

        private async System.Threading.Tasks.Task SendMediaCommandAsync(int direction)
        {
            if (_controller != null && await _controller.ControlAsync(direction)) return;
            if (App.Settings.PreferredMediaSource.Length == 0)
                NativeMethods.SendMediaKey(direction < 0 ? NativeMethods.VK_MEDIA_PREV
                    : direction > 0 ? NativeMethods.VK_MEDIA_NEXT : NativeMethods.VK_MEDIA_PLAY_PAUSE);
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
            _ = SendMediaCommandAsync(direction);
        }

        public void Update(MediaInfo info)
        {
            if (info == null || info.Generation <= _lastMediaGeneration) return;
            string sourceLabel = "来源 · " + MediaService.SourceTitle(info.SessionKey);
            if (!Equals(_sourceButton.Content, sourceLabel)) _sourceButton.Content = sourceLabel;
            _lastMediaGeneration = info.Generation;
            UpdateTimeline(info);
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
            StopTimeline();
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
        private readonly System.Windows.Shapes.Path _speakerWave1, _speakerWave2, _speakerMute;
        private readonly TextBlock _value;
        private readonly Slider _slider;
        private readonly TextBlock _output;
        private readonly Button _muteButton;
        private readonly AnimatedPanelSection _mixer;
        private AudioOutput _outputWorker;
        private string _outputId;
        private long _requestedVolume, _requestedMute;
        private int _outputUiQueued;
        private AudioMixer _mixerWorker;
        private int _mixerRequest;
        private bool _syncing;
        private bool _muted;
        private bool _volumeWritesEnabled;
        private volatile bool _disposed;
        public VolumeModule(TabStyle st) : base(st)
        {
            Pill.Width = 38 * Ui.Scale;
            Pill.MinWidth = Pill.MaxWidth = 38 * Ui.Scale;
            Pill.Padding = new Thickness(0);
            Pill.HorizontalAlignment = HorizontalAlignment.Left;
            Pill.Child = CenterIcon(MakeSpeakerGlyph(out _speakerWave1, out _speakerWave2, out _speakerMute, 23));
            var body = new StackPanel { Width = 230 * Ui.Scale };
            body.Children.Add(MakePanelTitle("音量"));
            var outputRow = new DockPanel { Margin = new Thickness(0, 3 * Ui.Scale, 0, 5 * Ui.Scale) };
            _muteButton = PanelControls.GlassButton("静音", ToggleMute, compact: true);
            _muteButton.MinWidth = 74 * Ui.Scale;
            _muteButton.IsEnabled = false;
            DockPanel.SetDock(_muteButton, Dock.Right);
            outputRow.Children.Add(_muteButton);
            _output = MakeText("正在读取输出设备…", -1);
            _output.TextTrimming = TextTrimming.CharacterEllipsis;
            _output.VerticalAlignment = VerticalAlignment.Center;
            outputRow.Children.Add(_output);
            body.Children.Add(outputRow);
            _slider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Style = (Style)Application.Current.FindResource("CapsuleSlider"),
                IsEnabled = false,
                LargeChange = 0.02,
                SmallChange = 0.01
            };
            _slider.SetResourceReference(Control.BackgroundProperty, "ThemePanelControl");
            PanelControls.ReleasePointerFocus(_slider);
            System.Windows.Automation.AutomationProperties.SetName(_slider, "音量");
            _value = MakeText("—");
            _value.FontWeight = FontWeights.Bold;
            body.Children.Add(Ui.SliderWithValue(_slider, _value, 52 * Ui.Scale, panel: true));
            body.Children.Add(PanelControls.GlassButton("Windows 声音设置", () => NativeMethods.ShellExecuteOpen("ms-settings:sound")));
            _mixer = new AnimatedPanelSection();
            var applicationMixer = PanelControls.GlassButton("应用混音", () =>
            {
                if (_mixer.Expanded) CloseMixer(animate: true);
                else
                {
                    SetMixerContent(null, "正在读取应用音量…");
                    _mixer.SetExpanded(true);
                    UpdateMixer();
                }
            });
            applicationMixer.Margin = new Thickness(0, 10 * Ui.Scale, 0, 0);
            body.Children.Add(applicationMixer);
            body.Children.Add(_mixer);
            Panel.Child = body;
            _slider.ValueChanged += (s, e) =>
            {
                _value.Text = Math.Round(e.NewValue * 100) + "%";
                UpdateIcon(e.NewValue);
                if (!_syncing && _volumeWritesEnabled && IsLoaded)
                    _requestedVolume = _outputWorker?.SetVolume(_outputId, (float)e.NewValue) ?? 0;
            };
            Pill.MouseLeftButtonUp += (_, _) => ToggleMute();
            UpdateIcon(0);
            Loaded += (_, _) => StartOutput();
            IsVisibleChanged += (_, _) => StartOutput();
            Unloaded += (s, e) => _volumeWritesEnabled = false;
            _slider.PreviewMouseLeftButtonDown += (s, e) => _volumeWritesEnabled = true;
            _slider.PreviewKeyDown += (s, e) => _volumeWritesEnabled = true;
            Pill.MouseWheel += (s, e) => { _volumeWritesEnabled = true; Adjust(e.Delta); };
            Panel.MouseWheel += (s, e) =>
            {
                if (_mixer.IsMouseOver) return;
                _volumeWritesEnabled = true;
                Adjust(e.Delta);
            };
        }

        protected override void OnPanelShown()
        {
            StartOutput();
            _outputWorker?.RequestRefresh();
        }

        private void StartOutput()
        {
            if (!_disposed && IsLoaded && IsVisible && _outputWorker == null)
                _outputWorker = new AudioOutput(QueueOutputUpdate);
        }

        private void QueueOutputUpdate()
        {
            if (_disposed || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _outputUiQueued, 1) != 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref _outputUiQueued, 0);
                if (!_disposed && _outputWorker?.Current is { } snapshot) ApplyOutput(snapshot);
            }));
        }

        private void ApplyOutput(AudioOutput.Snapshot snapshot)
        {
            bool changedDevice = !string.Equals(snapshot.Id, _outputId, StringComparison.OrdinalIgnoreCase);
            _outputId = snapshot.Id;
            if (changedDevice) _requestedVolume = _requestedMute = 0;
            if (snapshot.MuteRevision >= _requestedMute) _muted = snapshot.Muted ?? false;
            _output.Text = snapshot.Name ?? (snapshot.Id != null ? "默认输出" : "暂无默认播放设备");
            _output.ToolTip = _output.Text;
            _slider.IsEnabled = snapshot.Volume.HasValue;
            _muteButton.IsEnabled = snapshot.Muted.HasValue;
            if (snapshot.Volume is float value)
            {
                if (changedDevice || !_slider.IsMouseCaptureWithin && snapshot.VolumeRevision >= _requestedVolume)
                    SyncControls(value);
            }
            else _value.Text = "—";
            UpdateIcon(snapshot.Volume == null ? 0 : _slider.Value);
            if (changedDevice && _mixer.Expanded) UpdateMixer();
        }

        protected override Size GetPanelEnvelope(Size content) => new(content.Width, content.Height + 265 * Ui.Scale);

        protected override void OnPanelHidden() => CloseMixer();

        private void CloseMixer(bool animate = false)
        {
            _mixerRequest++;
            _mixerWorker?.Clear();
            _mixer.SetExpanded(false, animate, () =>
            {
                _mixer.Content = null;
            });
        }

        private async void UpdateMixer()
        {
            int request = ++_mixerRequest;
            _mixerWorker ??= new AudioMixer();
            if (_mixer.Content is StackPanel content && content.Children.OfType<ScrollViewer>().FirstOrDefault() is { } entries)
                entries.IsEnabled = false;
            var snapshot = await _mixerWorker.ReadAsync();
            if (_disposed || !_mixer.Expanded || request != _mixerRequest) return;
            SetMixerContent(snapshot, snapshot == null ? "当前设备无法读取应用音量" : "暂无音频应用");
        }

        private void SetMixerContent(AudioMixer.Snapshot snapshot, string emptyText)
        {
            var content = new StackPanel { Margin = new Thickness(0, 8 * Ui.Scale, 0, 0) };
            var refresh = PanelControls.GlassButton("刷新应用", UpdateMixer, compact: true);
            refresh.HorizontalAlignment = HorizontalAlignment.Right;
            refresh.Margin = new Thickness(0, 0, 0, 6 * Ui.Scale);
            content.Children.Add(refresh);
            if (snapshot?.Entries.Length > 0)
                content.Children.Add(BuildMixerEntries(snapshot));
            else
                content.Children.Add(PanelControls.Text(emptyText));
            _mixer.Content = content;
        }

        private ScrollViewer BuildMixerEntries(AudioMixer.Snapshot snapshot)
        {
            var entries = new StackPanel();
            foreach (var item in snapshot.Entries)
            {
                var title = new DockPanel { Margin = new Thickness(0, 0, 0, 2 * Ui.Scale) };
                bool muted = item.Muted;
                Button mute = null;
                mute = PanelControls.GlassButton("", () =>
                {
                    muted = !muted;
                    _mixerWorker?.SetMute(snapshot.Revision, item.Index, muted);
                    ShowMuteState();
                }, compact: true);
                mute.MinWidth = 74 * Ui.Scale;
                void ShowMuteState()
                {
                    string action = muted ? "取消静音" : "静音";
                    mute.Content = action;
                    System.Windows.Automation.AutomationProperties.SetName(mute, item.Name + " " + action);
                }
                ShowMuteState();
                DockPanel.SetDock(mute, Dock.Right);
                title.Children.Add(mute);
                var name = PanelControls.Text(item.Name);
                name.VerticalAlignment = VerticalAlignment.Center;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.ToolTip = item.Name;
                title.Children.Add(name);
                entries.Children.Add(title);
                var slider = new Slider { Minimum = 0, Maximum = 1, Value = item.Volume, SmallChange = .02,
                    Style = (Style)Application.Current.FindResource("CapsuleSlider") };
                slider.SetResourceReference(Control.BackgroundProperty, "ThemePanelControl");
                PanelControls.ReleasePointerFocus(slider);
                System.Windows.Automation.AutomationProperties.SetName(slider, item.Name + " 音量");
                var value = PanelControls.Text(item.Volume.ToString("P0"));
                slider.ValueChanged += (_, args) =>
                {
                    _mixerWorker?.SetVolume(snapshot.Revision, item.Index, (float)args.NewValue);
                    value.Text = args.NewValue.ToString("P0");
                };
                entries.Children.Add(Ui.SliderWithValue(slider, value, 50 * Ui.Scale, panel: true));
            }
            return new ScrollViewer { Content = entries, MaxHeight = 215 * Ui.Scale,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private void SyncControls(float value)
        {
            _syncing = true;
            _slider.Value = value;
            _value.Text = Math.Round(value * 100) + "%";
            _syncing = false;
        }

        private void ToggleMute()
        {
            if (_outputId == null || !_muteButton.IsEnabled) return;
            _muted = !_muted;
            UpdateIcon(_slider.Value);
            _requestedMute = _outputWorker?.SetMute(_outputId, _muted) ?? 0;
        }

        private void UpdateIcon(double volume)
        {
            _speakerWave1.Opacity = _muted || volume < 0.01 ? 0 : 1;
            _speakerWave2.Opacity = _muted || volume < 0.34 ? 0 : 1;
            _speakerMute.Opacity = _muted ? 1 : 0;
            _muteButton.Content = _muted ? "取消静音" : "静音";
            Pill.ToolTip = _outputId == null ? _output.Text
                : $"{_output.Text} · {volume:P0}" + (_muted ? " · 已静音" : "");
        }
        private void Adjust(int delta)
        {
            if (!_slider.IsEnabled || delta == 0) return;
            _slider.Value = Math.Max(0, Math.Min(1, _slider.Value + (delta > 0 ? 0.04 : -0.04)));
        }
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CloseMixer();
            _mixerWorker?.Dispose();
            _outputWorker?.Dispose();
            base.Dispose();
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
            active = active && IsVisible && IsLoaded && _presentationDemand;
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

        private bool _presentationDemand = true;

        internal void SetPresentationDemand(bool active)
        {
            if (_presentationDemand == active) return;
            _presentationDemand = active;
            SetSpectrumActive(active);
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
    public enum PerfMetric { Cpu, Gpu, Memory, Network, Summary }

    /// <summary>性能监测胶囊:数值由 PerfService 每秒采样刷新;四个指标各自独立显隐。
    /// 悬停自动展开数据页:CPU/GPU 显示占用、温度、频率,内存显示占用与容量频率,网络显示上下行速率。</summary>
    public class PerfModule : ModuleBase
    {
        private readonly PerfMetric _metric;
        private readonly TextBlock _value;
        private readonly Dictionary<string, TextBlock> _rows = new Dictionary<string, TextBlock>();
        private PerfTrend _trend;
        private Button _trendChoice;
        private TextBlock _trendPeak;
        private PerfMetric _trendMetric;
        private PerfSample _panelSample;
        private DispatcherOperation _panelPreload;
        private bool _panelLayoutPrepared;
        private string _networkAdapter;
        private bool _watchingNetwork;
        private bool _readingNetwork;
        private bool _networkReadPending;
        private string _lastPillText;
        private bool _active;
        private bool _sampleSubscribed;
        private bool _disposed;

        public PerfModule(PerfMetric metric, TabStyle st) : base(st)
        {
            _metric = metric;
            _trendMetric = metric == PerfMetric.Summary ? PerfMetric.Cpu : metric;
            double s = Ui.Scale;
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (metric != PerfMetric.Network && metric != PerfMetric.Summary)
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
            Pill.Width = (metric == PerfMetric.Network ? 112 : metric == PerfMetric.Summary ? 83 : 58) * s;
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
            _panelLayoutPrepared = true;
            UpdateNetworkWatch();
            if (_networkAdapter != null) SetRow("连接", _networkAdapter);
            var latest = PerfService.Instance.LatestForConfiguration;
            if (latest != null) Apply(latest);
            UpdateSampleSubscription();
        }

        protected override void OnPanelHidden()
        {
            UpdateNetworkWatch();
            QueuePanelPreload();
        }

        private void QueuePanelPreload()
        {
            if (!CanPreload || _panelPreload != null || (_panelLayoutPrepared && _panelSample != null)) return;
            _panelPreload = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                _panelPreload = null;
                if (!CanPreload || Open || HasVisiblePanel) return;
                var sample = PerfService.Instance.LatestForConfiguration;
                if (sample != null) Apply(sample, preload: true);
                PreparePanelLayout();
                _panelLayoutPrepared = true;
                UpdateSampleSubscription();
            }));
        }

        private bool CanPreload => !_disposed && IsLoaded && IsVisible;

        private void UpdateSampleSubscription()
        {
            var service = PerfService.Instance;
            bool waitingForFirstSample = CanPreload && _panelSample == null;
            bool subscribe = !_disposed && (_active || waitingForFirstSample);
            if (subscribe != _sampleSubscribed)
            {
                _sampleSubscribed = subscribe;
                if (subscribe) service.Updated += OnSample;
                else service.Updated -= OnSample;
            }
            if (waitingForFirstSample) service.RequestInitialSample();
        }

        private void UpdateNetworkWatch()
        {
            bool watch = !_disposed && _metric == PerfMetric.Network
                && (_active || Open || (CanPreload && _networkAdapter == null));
            if (_watchingNetwork == watch) return;
            _watchingNetwork = watch;
            if (watch)
            {
                NetworkChange.NetworkAddressChanged += UpdateNetworkConnection;
                RefreshNetworkConnection();
            }
            else
                NetworkChange.NetworkAddressChanged -= UpdateNetworkConnection;
        }

        private void UpdateNetworkConnection(object sender, EventArgs args)
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (_watchingNetwork) RefreshNetworkConnection();
                }));
        }

        private async void RefreshNetworkConnection()
        {
            if (_readingNetwork)
            {
                _networkReadPending = true;
                return;
            }
            _readingNetwork = true;
            try
            {
                do
                {
                    _networkReadPending = false;
                    string label = await Task.Run(ReadNetworkConnection);
                    if (!_watchingNetwork) return;
                    if (_networkReadPending) continue;
                    bool changed = Open && _networkAdapter != null && _networkAdapter != label;
                    _networkAdapter = label;
                    SetRow("连接", changed ? "已切换 · " + label : label);
                    UpdateNetworkWatch();
                } while (_networkReadPending);
            }
            finally { _readingNetwork = false; }
        }

        private static string ReadNetworkConnection()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                var active = interfaces.FirstOrDefault(item =>
                    item.OperationalStatus == OperationalStatus.Up
                    && item.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && item.GetIPProperties().GatewayAddresses.Count > 0);
                return active?.Name ?? (NetworkInterface.GetIsNetworkAvailable() ? "适配器已连接" : "无网络连接");
            }
            catch (NetworkInformationException)
            {
                return "连接状态暂不可用";
            }
        }

        private string Caption => _metric switch
        {
            PerfMetric.Cpu => "CPU 占用率",
            PerfMetric.Gpu => "GPU 占用率",
            PerfMetric.Memory => "内存占用",
            PerfMetric.Summary => "综合性能",
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
                PerfMetric.Summary => 205,
                _ => 150
            };
            var body = new StackPanel { Width = width * s };
            string title = MetricName(_metric);
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
                case PerfMetric.Summary:
                    foreach (var metric in MonitorMetrics) AddRow(body, MetricName(metric));
                    _trendMetric = EnabledMetrics().FirstOrDefault();
                    _trendChoice = PanelControls.Button(MetricName(_trendMetric) + " · 最近 60 秒", ChooseTrend);
                    body.Children.Add(_trendChoice);
                    break;
                default:
                    AddRow(body, "下行");
                    AddRow(body, "上行");
                    AddRow(body, "连接");
                    break;
            }
            if (_metric != PerfMetric.Summary)
            {
                var text = MakeText("最近 60 秒", -1);
                text.Opacity = 0.72;
                text.Margin = new Thickness(2, 6, 0, 0);
                body.Children.Add(text);
            }
            _trend = new PerfTrend();
            body.Children.Add(_trend);
            _trendPeak = MakeText("峰值 · --", -1);
            _trendPeak.Margin = new Thickness(2, 4, 0, 1);
            body.Children.Add(_trendPeak);
            body.Children.Add(PanelControls.GlassButton(_metric == PerfMetric.Network ? "Windows 网络设置" : "任务管理器",
                () => NativeMethods.ShellExecuteOpen(_metric == PerfMetric.Network ? "ms-settings:network-status" : "taskmgr.exe"), compact: true));
            Panel.Child = body;
        }

        private static readonly PerfMetric[] MonitorMetrics = { PerfMetric.Cpu, PerfMetric.Gpu, PerfMetric.Memory, PerfMetric.Network };

        internal void RefreshSummaryMetrics()
        {
            if (_metric != PerfMetric.Summary) return;
            var enabled = EnabledMetrics().ToHashSet();
            foreach (var metric in MonitorMetrics)
                if (_rows[MetricName(metric)].Parent is Grid row)
                    row.Visibility = enabled.Contains(metric) ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled.Contains(_trendMetric))
            {
                _trendMetric = MonitorMetrics.FirstOrDefault(enabled.Contains);
                _trendChoice.Content = MetricName(_trendMetric) + " · 最近 60 秒";
                _panelSample = null;
            }
            if (PerfService.Instance.LatestForConfiguration is PerfSample sample) Apply(sample, preload: true);
        }

        internal static IEnumerable<PerfMetric> EnabledMetrics()
        {
            if (App.ActiveSettings.ShowMonitorCpu) yield return PerfMetric.Cpu;
            if (App.ActiveSettings.ShowMonitorGpu) yield return PerfMetric.Gpu;
            if (App.ActiveSettings.ShowMonitorMemory) yield return PerfMetric.Memory;
            if (App.ActiveSettings.ShowMonitorNetwork) yield return PerfMetric.Network;
        }

        private static string MetricName(PerfMetric metric) => metric switch
        {
            PerfMetric.Cpu => "CPU", PerfMetric.Gpu => "GPU", PerfMetric.Memory => "内存",
            PerfMetric.Network => "网络", _ => "性能"
        };

        private void ChooseTrend()
        {
            TrackFlyout(FlyoutMenu.Show(EnabledMetrics().Select(metric => new FlyoutItem(MetricName(metric), () =>
            {
                _trendMetric = metric;
                _trendChoice.Content = MetricName(metric) + " · 最近 60 秒";
                RefreshTrend();
            })).ToArray(), "显示趋势", _trendChoice));
        }

        private static double MetricValue(PerfSample sample, PerfMetric metric) => metric switch
        {
            PerfMetric.Cpu => sample.CpuPercent,
            PerfMetric.Gpu => PickGpu(sample.Gpus)?.UtilPercent ?? -1,
            PerfMetric.Memory => sample.MemTotalBytes == 0 ? -1 : 100.0 * sample.MemUsedBytes / sample.MemTotalBytes,
            PerfMetric.Network => sample.NetDownBps >= 0 ? sample.NetDownBps : -1,
            _ => -1
        };

        private void RefreshTrend()
        {
            double peak = _trend.SetSamples(sample => MetricValue(sample, _trendMetric), _trendMetric != PerfMetric.Network);
            string text = "峰值 · " + (_trendMetric == PerfMetric.Network ? FormatNetworkRate(peak, false) : FmtPercent(peak));
            if (_trendPeak.Text != text) _trendPeak.Text = text;
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
            active = active && IsVisible && IsLoaded && _presentationDemand;
            if (active != _active)
            {
                _active = active;
                UpdateSampleSubscription();
                var service = PerfService.Instance;
                if (active)
                {
                    service.AddRef();
                    var latest = service.LatestForConfiguration;
                    if (latest != null) Apply(latest);
                }
                else service.ReleaseRef();
            }
            else UpdateSampleSubscription();
            UpdateNetworkWatch();
            QueuePanelPreload();
        }

        private bool _presentationDemand = true;

        internal void SetPresentationDemand(bool active)
        {
            if (_presentationDemand == active) return;
            _presentationDemand = active;
            SetActive(active);
        }

        private void OnSample(PerfSample _)
        {
            if (_disposed) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed) return;
                    if (_active && PerfService.Instance.LatestForConfiguration is { } current) Apply(current);
                    QueuePanelPreload();
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void Apply(PerfSample sample, bool preload = false)
        {
            bool refreshPanel = Open || preload;
            switch (_metric)
            {
                case PerfMetric.Summary:
                    var first = EnabledMetrics().FirstOrDefault();
                    SetPillText(MetricName(first) + " " + FormatMetric(first));
                    foreach (var metric in EnabledMetrics()) UpdateRow(MetricName(metric), FormatMetric(metric));
                    break;
                case PerfMetric.Cpu:
                    SetPillText(FmtPercent(sample.CpuPercent));
                    UpdateRow("占用", FmtPercent(sample.CpuPercent));
                    UpdateRow("温度", FmtTemp(sample.CpuTempC));
                    UpdateRow("频率", FmtFreq(sample.CpuFreqMhz));
                    break;
                case PerfMetric.Gpu:
                    var gpu = PickGpu(sample.Gpus);
                    SetPillText(gpu == null ? "--" : FmtPercent(gpu.UtilPercent));
                    UpdateRow("型号", gpu == null ? "--" : gpu.Name);
                    UpdateRow("占用", gpu == null ? "--" : FmtPercent(gpu.UtilPercent));
                    UpdateRow("温度", FmtTemp(gpu == null ? -1 : gpu.TempC));
                    UpdateRow("频率", FmtFreq(gpu == null ? -1 : gpu.ClockMhz));
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
                    UpdateRow("占用", sample.MemTotalBytes <= 0
                        ? "--"
                        : FmtPercent(100.0 * sample.MemUsedBytes / sample.MemTotalBytes));
                    UpdateRow("已用", sample.MemTotalBytes <= 0 ? "--" : FmtGb(sample.MemUsedBytes) + " GB");
                    UpdateRow("容量", sample.MemTotalBytes <= 0 ? "--" : FmtGb(sample.MemTotalBytes) + " GB");
                    UpdateRow("频率", sample.MemSpeedMts > 0 ? Math.Round(sample.MemSpeedMts) + " MT/s" : "--");
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
                    UpdateRow("下行", FormatNetworkRate(sample.NetDownBps, compact: false));
                    UpdateRow("上行", FormatNetworkRate(sample.NetUpBps, compact: false));
                    break;
            }
            if (refreshPanel && !ReferenceEquals(_panelSample, sample))
            {
                _panelSample = sample;
                RefreshTrend();
            }
            void UpdateRow(string label, string text) { if (refreshPanel) SetRow(label, text); }
            string FormatMetric(PerfMetric metric) => metric == PerfMetric.Network
                ? FormatNetworkRate(sample.NetDownBps, compact: true) : FmtPercent(MetricValue(sample, metric));
        }

        private void SetRow(string label, string text)
        {
            if (_rows.TryGetValue(label, out var tb) && tb.Text != text)
            {
                tb.Text = text;
                if (label is "型号" or "连接") tb.ToolTip = text == "--" ? null : text;
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
            _disposed = true;
            if (_active)
            {
                _active = false;
                PerfService.Instance.ReleaseRef();
            }
            UpdateSampleSubscription();
            UpdateNetworkWatch();
            _panelPreload?.Abort();
            base.Dispose();
        }
    }
}
