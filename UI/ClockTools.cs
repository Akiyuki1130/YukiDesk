using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopBar.Interop;

namespace DesktopBar.UI
{
    internal sealed class ClockTools : StackPanel
    {
        private const string CopyDateTimeLabel = "复制日期时间";
        private readonly TextBlock _zone;
        private readonly TextBlock _monthTitle;
        private readonly TextBlock _remaining;
        private readonly Button _pauseTimer, _extendTimer, _stopTimer, _copyDate, _calendarToggle;
        private readonly AnimatedPanelSection _calendar;
        private readonly StackPanel _timerActions;
        private readonly Grid _days;
        private readonly TimeZoneInfo _secondZone;
        private DateTime _month;
        private DateTime _renderedDate;
        private bool _copyingDate;
        private static long _timerDeadline;
        private static long _pausedRemaining;
        private static bool _finished;
        private static DispatcherTimer _countdown;

        private static long RemainingMs => _pausedRemaining > 0 ? _pausedRemaining : Math.Max(0, _timerDeadline - Environment.TickCount64);
        private static bool HasCountdown => _timerDeadline != 0 || _pausedRemaining > 0;
        internal static string CapsuleCountdown => HasCountdown ? FormatRemaining(RemainingMs) : _finished ? "计时到点" : null;

        internal ClockTools()
        {
            Width = 218 * Ui.Scale;
            Margin = new Thickness(1, 7, 1, 2);
            _zone = PanelControls.Text("");
            _zone.TextWrapping = TextWrapping.Wrap;
            try
            {
                if (!string.IsNullOrWhiteSpace(App.Settings.SecondTimeZoneId))
                    _secondZone = TimeZoneInfo.FindSystemTimeZoneById(App.Settings.SecondTimeZoneId);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
            _zone.Visibility = _secondZone == null ? Visibility.Collapsed : Visibility.Visible;
            Children.Add(_zone);
            _monthTitle = PanelControls.Text("");
            _monthTitle.HorizontalAlignment = HorizontalAlignment.Center;
            _monthTitle.VerticalAlignment = VerticalAlignment.Center;
            var nav = new DockPanel { Margin = new Thickness(0, 5, 0, 3) };
            var previous = MonthButton("‹", "上个月", -1);
            var next = MonthButton("›", "下个月", 1);
            DockPanel.SetDock(previous, Dock.Left);
            DockPanel.SetDock(next, Dock.Right);
            nav.Children.Add(previous);
            nav.Children.Add(next);
            nav.Children.Add(_monthTitle);
            _days = new Grid();
            for (int column = 0; column < 7; column++) _days.ColumnDefinitions.Add(new ColumnDefinition());
            for (int row = 0; row < 7; row++) _days.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var actions = ActionRow();
            _calendar = new AnimatedPanelSection();
            _calendarToggle = ActionButton("日历", ToggleCalendar);
            _copyDate = ActionButton(CopyDateTimeLabel, () => _ = CopyDateAsync());
            _copyDate.MinWidth = 90 * Ui.Scale;
            actions.Children.Add(_calendarToggle);
            actions.Children.Add(_copyDate);
            actions.Children.Add(ActionButton("倒计时", StartTimer));
            Children.Add(actions);
            _timerActions = ActionRow();
            _pauseTimer = ActionButton("暂停", TogglePause);
            _extendTimer = ActionButton("+1 分钟", ExtendTimer);
            _stopTimer = ActionButton("停止", StopTimer);
            _timerActions.Children.Add(_pauseTimer);
            _timerActions.Children.Add(_extendTimer);
            _timerActions.Children.Add(_stopTimer);
            Children.Add(_timerActions);
            _remaining = PanelControls.Text("");
            _remaining.Margin = new Thickness(6, 2, 6, 3);
            _remaining.HorizontalAlignment = HorizontalAlignment.Center;
            Children.Add(_remaining);
            var calendar = new StackPanel { Margin = new Thickness(3 * Ui.Scale, 6 * Ui.Scale, 3 * Ui.Scale, 0) };
            calendar.Children.Add(nav);
            calendar.Children.Add(_days);
            _calendar.Content = calendar;
            Children.Add(_calendar);
            _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            Refresh(DateTime.Now);
        }

        private static StackPanel ActionRow() => new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 7 * Ui.Scale, 0, 0)
        };

        private static Button ActionButton(string title, Action click)
        {
            var button = PanelControls.GlassButton(title, click, compact: true);
            button.Margin = new Thickness(2 * Ui.Scale, 0, 2 * Ui.Scale, 0);
            button.Padding = new Thickness(7 * Ui.Scale, 2 * Ui.Scale, 7 * Ui.Scale, 2 * Ui.Scale);
            return button;
        }

        private Button MonthButton(string glyph, string name, int direction)
        {
            var button = PanelControls.GlassButton(glyph, () => ChangeMonth(direction), compact: true);
            button.Width = button.Height = 26 * Ui.Scale;
            button.MinWidth = 0;
            button.Padding = button.Margin = new Thickness(0);
            button.FontSize = 17 * Ui.Scale;
            button.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            return button;
        }

        private void ToggleCalendar()
        {
            bool expanded = !_calendar.Expanded;
            if (expanded && _renderedDate != DateTime.Today) RenderCalendar(DateTime.Today);
            _calendar.SetExpanded(expanded);
            _calendarToggle.Content = expanded ? "收起日历" : "日历";
        }

        internal void CloseCalendar()
        {
            DateTime today = DateTime.Today;
            _month = new DateTime(today.Year, today.Month, 1);
            _renderedDate = default;
            _calendar.SetExpanded(false, animate: false);
            _calendarToggle.Content = "日历";
        }

        internal void Refresh(DateTime now)
        {
            if (_secondZone != null)
            {
                var other = TimeZoneInfo.ConvertTime(now, _secondZone);
                string text = _secondZone.StandardName + "  " + other.ToString("M/d HH:mm", CultureInfo.CurrentCulture);
                if (_zone.Text != text) _zone.Text = text;
            }
            if (_calendar.Expanded && _renderedDate != now.Date) RenderCalendar(now.Date);
            string value = HasCountdown ? (_pausedRemaining > 0 ? "已暂停 " : "剩余 ") + FormatRemaining(RemainingMs)
                : _finished ? "倒计时已结束" : "";
            if (_remaining.Text != value) _remaining.Text = value;
            _remaining.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _pauseTimer.Visibility = _extendTimer.Visibility = HasCountdown ? Visibility.Visible : Visibility.Collapsed;
            _timerActions.Visibility = HasCountdown || _finished ? Visibility.Visible : Visibility.Collapsed;
            _pauseTimer.Content = _pausedRemaining > 0 ? "继续" : "暂停";
            _stopTimer.Visibility = HasCountdown || _finished ? Visibility.Visible : Visibility.Collapsed;
            _stopTimer.Content = _finished ? "清除提醒" : "停止";
        }

        private static string FormatRemaining(long milliseconds)
        {
            var remaining = TimeSpan.FromSeconds(Math.Ceiling(milliseconds / 1000.0));
            return remaining.TotalHours >= 1 ? remaining.ToString(@"hh\:mm\:ss") : remaining.ToString(@"mm\:ss");
        }

        private void ChangeMonth(int direction)
        {
            if (Math.Abs((_month - DateTime.Today).TotalDays) > 3650 && Math.Sign((_month - DateTime.Today).TotalDays) == direction) return;
            _month = _month.AddMonths(direction);
            RenderCalendar(DateTime.Today);
        }

        private void RenderCalendar(DateTime today)
        {
            _renderedDate = today;
            _monthTitle.Text = _month.ToString("yyyy 年 M 月");
            _days.Children.Clear();
            const string week = "一二三四五六日";
            for (int day = 0; day < 7; day++) Add(0, day, week[day].ToString(), false);
            int start = ((int)_month.DayOfWeek + 6) % 7;
            for (int day = 1; day <= DateTime.DaysInMonth(_month.Year, _month.Month); day++)
            {
                int position = start + day - 1;
                Add(position / 7 + 1, position % 7, day.ToString(), today == _month.AddDays(day - 1));
            }

            void Add(int row, int column, string text, bool isToday)
            {
                var label = PanelControls.Text(text);
                label.TextAlignment = TextAlignment.Center;
                label.FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal;
                label.Opacity = row == 0 ? 0.66 : 1;
                var cell = new Border { Height = 22 * Ui.Scale, CornerRadius = new CornerRadius(11 * Ui.Scale), Child = label };
                if (isToday) cell.SetResourceReference(Border.BackgroundProperty, "ThemePanelSelected");
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                _days.Children.Add(cell);
            }
        }

        private async Task CopyDateAsync()
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source == null || _copyingDate) return;
            string date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            _copyingDate = true;
            bool copied = false;
            for (int attempt = 0; attempt < 6 && !source.IsDisposed; attempt++)
            {
                copied = TextClipboard.TrySet(source.Handle, date);
                if (copied) break;
                if (attempt < 5) await Task.Delay(40);
            }
            _copyDate.Content = copied ? "已复制" : "复制失败";
            await Task.Delay(950);
            _copyDate.Content = CopyDateTimeLabel;
            _copyingDate = false;
        }

        private void StartTimer()
        {
            if (Prompt.ShowCountdown() is not TimeSpan duration) return;
            StopTimer();
            _timerDeadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
            _countdown ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
            {
                if (_timerDeadline == 0 || Environment.TickCount64 < _timerDeadline) return;
                StopCountdown();
                _finished = true;
                App.NotifyTimerDone();
            }, Dispatcher);
            _countdown.Start();
            Refresh(DateTime.Now);
        }

        private void StopTimer() { StopCountdown(); Refresh(DateTime.Now); }

        private void TogglePause()
        {
            if (_pausedRemaining > 0)
            {
                _timerDeadline = Environment.TickCount64 + _pausedRemaining;
                _pausedRemaining = 0;
                _countdown?.Start();
            }
            else if (_timerDeadline != 0 && RemainingMs > 0)
            {
                _pausedRemaining = RemainingMs;
                _timerDeadline = 0;
                _countdown?.Stop();
            }
            Refresh(DateTime.Now);
        }

        private void ExtendTimer()
        {
            if (_pausedRemaining > 0) _pausedRemaining = Math.Min(720 * 60000L, _pausedRemaining + 60000);
            else if (_timerDeadline != 0) _timerDeadline = Environment.TickCount64 + Math.Min(720 * 60000L, RemainingMs + 60000);
            Refresh(DateTime.Now);
        }

        private static void StopCountdown()
        {
            _timerDeadline = 0;
            _pausedRemaining = 0;
            _finished = false;
            _countdown?.Stop();
        }
    }
}
