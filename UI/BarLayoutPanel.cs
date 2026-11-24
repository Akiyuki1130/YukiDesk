using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DesktopBar.Models;

namespace DesktopBar.UI
{
    internal sealed class BarLayoutPanel : Panel
    {
        internal sealed record Entry(ModuleBase Module, string Id, string Title, Action Launch = null);
        internal sealed record LayoutRule(string Id, string Title, int Zone, bool Keep = false);

        internal static readonly LayoutRule[] Rules =
        {
            new("settings", "设置", 0, true), new("power", "电源", 0), new("thispc", "此电脑", 0),
            new("recycle", "回收站", 0), new("folders", "小文件夹", 0), new("favorites", "常用项", 0),
            new("clock", "时钟", 1, true), new("cpu", "CPU", 2), new("gpu", "GPU", 2),
            new("memory", "内存", 2), new("network", "网络", 2), new("performance", "综合性能", 2), new("spectrum", "频谱", 2),
            new("music", "音乐", 2), new("volume", "音量", 2), new("battery", "电池", 2)
        };

        private readonly List<Entry> _entries = new();
        private readonly List<Entry> _overflow = new();
        private readonly HashSet<ModuleBase> _drawn = new();
        private readonly OverflowModule _more;
        private Dictionary<string, (BarPlacement Rule, int Index)> _rank;
        private Entry _preferred;
        internal bool IsDrawn(ModuleBase module) => _drawn.Contains(module);

        internal static List<BarPlacement> Placements()
        {
            var result = new List<BarPlacement>();
            var ids = Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var row in App.ActiveSettings.BarLayout)
                if (row != null && ids.Remove(row.Id))
                    result.Add(new BarPlacement { Id = row.Id, Zone = Math.Clamp(row.Zone, 0, 2), KeepVisible = row.KeepVisible });
            foreach (var rule in Rules)
                if (ids.Remove(rule.Id))
                    result.Add(new BarPlacement { Id = rule.Id, Zone = rule.Id == "clock" && App.ActiveSettings.TimeOnRight ? 2 : rule.Zone,
                        KeepVisible = rule.Keep });
            return result;
        }

        internal BarLayoutPanel(OverflowModule more)
        {
            _more = more;
            _more.Items = () => _overflow.ToArray();
            _more.Use = OpenOverflowEntry;
            ClipToBounds = true;
            RefreshPlacements();
        }

        internal void RefreshPlacements()
        {
            _rank = Placements().Select((rule, index) => (Rule: rule, Index: index)).ToDictionary(item => item.Rule.Id);
            InvalidateArrange();
        }

        internal void SetEntries(IEnumerable<Entry> entries)
        {
            _preferred = null;
            _entries.Clear();
            _entries.AddRange(entries);
            var expected = _entries.Select(entry => entry.Module).ToHashSet();
            expected.Add(_more);
            for (int index = Children.Count - 1; index >= 0; index--)
                if (Children[index] is ModuleBase module && !expected.Contains(module)) Children.RemoveAt(index);
            foreach (var entry in _entries)
                if (!Children.Contains(entry.Module)) Children.Add(entry.Module);
            if (!Children.Contains(_more)) Children.Add(_more);
            InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size available)
        {
            double height = double.IsInfinity(available.Height) ? 46 * Ui.Scale : available.Height;
            foreach (UIElement child in Children) child.Measure(new Size(double.PositiveInfinity, height));
            return new Size(double.IsInfinity(available.Width) ? _entries.Sum(entry => entry.Module.DesiredSize.Width) : available.Width, height);
        }

        protected override Size ArrangeOverride(Size size)
        {
            double margin = 10 * Ui.Scale;
            var visible = _entries.Where(entry => entry.Module.Visibility == Visibility.Visible)
                .OrderBy(entry => _rank[entry.Id].Index).ToList();
            _overflow.Clear();
            _drawn.Clear();
            double available = Math.Max(0, size.Width - 2 * margin);
            double total = visible.Sum(DesiredWidth);
            double moreWidth = _more.DesiredSize.Width;

            if (total > available)
            {
                foreach (var entry in visible.AsEnumerable().Reverse()
                    .OrderBy(entry => ReferenceEquals(entry, _preferred) ? 2 : _rank[entry.Id].Rule.KeepVisible ? 1 : 0).ToArray())
                {
                    if (total + moreWidth <= available) break;
                    visible.Remove(entry);
                    _overflow.Insert(0, entry);
                    total -= DesiredWidth(entry);
                }
            }

            var zones = Enumerable.Range(0, 3).Select(zone => visible.Where(entry => _rank[entry.Id].Rule.Zone == zone).ToArray()).ToArray();
            double left = margin;
            double right = Math.Max(left, size.Width - margin - zones[2].Sum(DesiredWidth) - (_overflow.Count == 0 ? 0 : moreWidth));
            double middleWidth = zones[1].Sum(DesiredWidth);
            double leftEnd = left + zones[0].Sum(DesiredWidth);
            double center = Math.Clamp((size.Width - middleWidth) / 2, leftEnd, Math.Max(leftEnd, right - middleWidth));
            Place(zones[0], left);
            Place(zones[1], center);
            double end = Place(zones[2], right);
            foreach (var entry in _entries.Where(entry => !_drawn.Contains(entry.Module)))
                entry.Module.Arrange(new Rect(-10000, 0, DesiredWidth(entry), size.Height));
            _more.Arrange(new Rect(_overflow.Count > 0 ? end : -10000, 0, moreWidth, size.Height));
            if (_overflow.Count > 0) _drawn.Add(_more);
            return size;

            double Place(Entry[] entries, double position)
            {
                foreach (var entry in entries)
                {
                    var width = DesiredWidth(entry);
                    entry.Module.Arrange(new Rect(position, 0, width, size.Height));
                    _drawn.Add(entry.Module);
                    position += width;
                }
                return position;
            }
        }

        private static double DesiredWidth(Entry entry) => entry.Module.DesiredSize.Width;

        internal void OpenOverflowEntry(Entry entry)
        {
            if (entry.Launch != null) { entry.Launch(); return; }
            _preferred = entry;
            InvalidateArrange();
            UpdateLayout();
            if (IsDrawn(entry.Module)) entry.Module.ShowPanel();
        }
    }

    internal sealed class OverflowModule : ModuleBase
    {
        internal Func<BarLayoutPanel.Entry[]> Items { get; set; }
        internal Action<BarLayoutPanel.Entry> Use { get; set; }

        internal OverflowModule(TabStyle style) : base(style)
        {
            Pill.Child = MakePillText("⋯", 5);
            System.Windows.Automation.AutomationProperties.SetName(Pill, "更多模块");
            Pill.MouseLeftButtonUp += (_, _) =>
            {
                var items = Items?.Invoke();
                if (items?.Length > 0)
                    TrackFlyout(FlyoutMenu.Show(items.Select(item => new FlyoutItem(item.Title, () => Use(item))).ToArray(), "更多模块"));
            };
        }

        protected override bool HasPanel => false;
    }

    internal sealed class FavoriteModule : ModuleBase
    {
        private readonly Action _run;

        internal FavoriteModule(TabStyle style, string name, Action run = null) : base(style)
        {
            _run = run;
            var text = MakePillText(name);
            text.MaxWidth = 122 * Ui.Scale;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            Pill.Child = text;
            Pill.ToolTip = name;
            Pill.MouseLeftButtonUp += (_, _) => Invoke();
        }

        internal void Invoke()
        {
            if (_run != null) _run();
            else TrackFlyout(FlyoutMenu.Show(DesktopActions.FavoriteItems(), "常用项"));
        }

        protected override bool HasPanel => false;
    }
}
