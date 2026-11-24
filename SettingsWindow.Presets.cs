using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DesktopBar.Models;
using DesktopBar.Services;
using DesktopBar.UI;
using Microsoft.Win32;

namespace DesktopBar
{
    public partial class SettingsWindow
    {
        private FrameworkElement BuildPresetsCard()
        {
            var card = Card("顶栏预设", "从托盘切换。预设包含模块、布局、唤出方式和玻璃取景。", out var body);
            var list = new ListBox { Height = 103, DisplayMemberPath = nameof(DesktopPreset.Name) };
            list.SetResourceReference(StyleProperty, "DarkList");
            body.Children.Add(list);
            var actions = new WrapPanel();
            body.Children.Add(actions);
            void Refresh(DesktopPreset selected = null)
            {
                list.ItemsSource = S.DesktopPresets.ToArray();
                list.SelectedItem = selected;
            }
            bool TryName(string title, string initial, out string name)
            {
                name = Prompt.Show(title, "预设名称", initial)?.Trim();
                if (string.IsNullOrWhiteSpace(name)) return false;
                string candidate = name;
                if (!S.DesktopPresets.Any(preset => string.Equals(preset.Name, candidate, StringComparison.CurrentCultureIgnoreCase))) return true;
                ConfirmDialog.Notice("名称已存在", "请给预设换一个名字。");
                return false;
            }
            actions.Children.Add(PanelControls.Button("保存当前…", () =>
            {
                if (!TryName("保存当前布局", "", out var name)) return;
                var preset = DesktopPresets.Create(name);
                S.DesktopPresets.Add(preset);
                App.Save();
                Refresh(preset);
            }));
            actions.Children.Add(PanelControls.Button("添加示例", () =>
            {
                foreach (var preset in DesktopPresets.Examples())
                    if (!S.DesktopPresets.Any(item => item.Name == preset.Name)) S.DesktopPresets.Add(preset);
                App.Save();
                Refresh();
            }));
            actions.Children.Add(PanelControls.Button("应用", () =>
            {
                if (list.SelectedItem is DesktopPreset preset && DesktopPresets.Apply(preset)) ReopenPage("Modules");
            }));
            actions.Children.Add(PanelControls.Button("复制…", () =>
            {
                if (list.SelectedItem is not DesktopPreset selected || !TryName("复制预设", selected.Name + "副本", out var name)) return;
                var copy = new DesktopPreset { Name = name, Values = new(selected.Values) };
                S.DesktopPresets.Add(copy);
                App.Save();
                Refresh(copy);
            }));
            actions.Children.Add(PanelControls.Button("改名…", () =>
            {
                if (list.SelectedItem is not DesktopPreset selected || !TryName("预设改名", selected.Name, out var name)) return;
                string previous = selected.Name;
                selected.Name = name;
                foreach (var rule in S.AppScenes.Where(item => item.Preset == previous)) rule.Preset = name;
                App.Save();
                App.Bar?.RefreshAppSceneRules();
                Refresh(selected);
            }));
            actions.Children.Add(PanelControls.Button("删除预设", () =>
            {
                if (list.SelectedItem is not DesktopPreset selected) return;
                S.DesktopPresets.Remove(selected);
                S.AppScenes.RemoveAll(item => item.Preset == selected.Name);
                App.Save();
                App.Bar?.RefreshAppSceneRules();
                Refresh();
            }));
            Refresh();
            return card;
        }

        private FrameworkElement BuildAppSceneCard()
        {
            var card = Card("按应用使用预设", "应用在前台停留一段时间后临时切换，离开后恢复。", out var body);
            var presetPicker = new ComboBox { Width = 200, Style = (Style)FindResource("DarkCombo") };
            void LoadPresets()
            {
                string previous = presetPicker.SelectedItem as string;
                presetPicker.ItemsSource = S.DesktopPresets.Select(item => item.Name).ToArray();
                presetPicker.SelectedItem = previous;
                if (presetPicker.SelectedIndex < 0 && presetPicker.Items.Count > 0) presetPicker.SelectedIndex = 0;
            }
            presetPicker.DropDownOpened += (_, _) => LoadPresets();
            LoadPresets();
            body.Children.Add(Row("关联预设", presetPicker));
            var list = new ListBox { Height = 92, DisplayMemberPath = nameof(AppSceneRow.Label) };
            list.SetResourceReference(StyleProperty, "DarkList");
            body.Children.Add(list);
            void Refresh() => list.ItemsSource = S.AppScenes.Select(item => new AppSceneRow(item)).ToArray();
            var actions = new WrapPanel();
            actions.Children.Add(PanelControls.Button("关联应用…", () =>
            {
                if (presetPicker.SelectedItem is not string preset)
                {
                    ConfirmDialog.Notice("先保存预设", "保存或添加一个预设后即可关联应用。");
                    return;
                }
                var dialog = new OpenFileDialog { Filter = "Windows 应用|*.exe", Title = "选择要关联的应用" };
                if (dialog.ShowDialog(this) != true) return;
                string executable = Path.GetFileName(dialog.FileName);
                S.AppScenes.RemoveAll(item => string.Equals(item.Program, executable, StringComparison.OrdinalIgnoreCase));
                S.AppScenes.Add(new AppSceneRule { Program = executable, Preset = preset });
                Commit();
            }));
            actions.Children.Add(PanelControls.Button("移除关联", () =>
            {
                if (list.SelectedItem is not AppSceneRow row) return;
                S.AppScenes.Remove(row.Rule);
                Commit();
            }));
            body.Children.Add(actions);
            Refresh();
            return card;

            void Commit()
            {
                App.Save();
                App.Bar?.RefreshAppSceneRules();
                Refresh();
            }
        }

        private sealed record AppSceneRow(AppSceneRule Rule)
        {
            public string Label => Rule.Program + "  ·  " + Rule.Preset;
        }

        private FrameworkElement BuildBackupCard()
        {
            var card = Card("布局和外观备份", "备份包含布局、预设、外观和快捷键。保存在你选择的位置。", out var body);
            bool includeFolders = false;
            body.Children.Add(ToggleRow("包含小文件夹偏好和常用项", false, enabled => includeFolders = enabled));
            var actions = new WrapPanel();
            body.Children.Add(actions);
            actions.Children.Add(PanelControls.Button("导出备份…", () =>
            {
                var picker = new SaveFileDialog { FileName = "YukiDesk-layout.json", Filter = "YukiDesk 布局备份|*.json" };
                if (picker.ShowDialog(this) != true) return;
                try { DesktopPresets.Export(picker.FileName, includeFolders); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { ConfirmDialog.Notice("导出失败", ex.Message); }
            }));
            actions.Children.Add(PanelControls.Button("导入备份…", () =>
            {
                var picker = new OpenFileDialog { Filter = "YukiDesk 布局备份|*.json" };
                if (picker.ShowDialog(this) != true) return;
                try
                {
                    var import = DesktopPresets.ReadImport(picker.FileName);
                    var options = import.Settings;
                    string material = options.PillMaterial switch { 4 => "液态玻璃", 3 => "高斯模糊", _ => "纯色" };
                    string shortcuts = "顶栏快捷键 " + BarHotkey.FromSettings(options).Display;
                    string folderDetail = import.IncludesFolders ? "\n包含直连目录、显示偏好和常用项。" : "";
                    if (ConfirmDialog.Show("预览导入", $"{import.SettingCount} 项设置 · {import.Presets.Count} 个预设\n"
                        + $"{material} · {options.Scale:P0}\n{shortcuts}{folderDetail}", "应用备份", okIsDefault: false)
                        && DesktopPresets.Apply(import)) ReopenPage("General");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
                { ConfirmDialog.Notice("导入失败", ex.Message); }
            }));
            return card;
        }

        private FrameworkElement BuildDiagnosticsCard()
        {
            var card = Card("排障信息", "查看当前玻璃采样和快捷键状态，复制后可用于反馈。", out var body);
            var status = MakeHint("");
            status.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(status);
            string Read()
            {
                var version = typeof(App).Assembly.GetName().Version;
                string capture = S.PillMaterial is 3 or 4
                    ? (S.WallpaperOnlyCapture ? "轻量录制 · 壁纸" : S.ShowInScreenCapture ? "逐窗口采样" : "整屏采样")
                        + (S.FreezePanelGlass ? " · 展开页定格" : " · 展开页实时")
                    : "纯色或壁纸材质";
                return $"YukiDesk {version}\n玻璃取景 {capture}\n"
                    + $"顶栏快捷键 {HotkeyStatus(S.RevealWithHotkey, App.Bar?.RevealHotkeyRegistered)}\n"
                    + GlassService.DiagnosticSnapshot();
            }
            var actions = new WrapPanel();
            actions.Children.Add(PanelControls.Button("刷新状态", () => status.Text = Read()));
            var release = MakeHint("");
            var releaseButton = new Button { Content = "查看发行版信息", Style = (Style)FindResource("Btn") };
            releaseButton.Click += async (_, _) =>
            {
                releaseButton.IsEnabled = false;
                release.Text = "正在读取发行版…";
                try { release.Text = await UpdateService.ReadReleaseSummaryAsync(System.Threading.CancellationToken.None); }
                catch (Exception ex) { release.Text = "暂时无法读取发行版。"; App.Log("读取发行版摘要失败: " + ex.Message); }
                finally { releaseButton.IsEnabled = true; }
            };
            actions.Children.Add(releaseButton);
            actions.Children.Add(PanelControls.Button("复制排障信息", () =>
            {
                status.Text = Read();
                try { Clipboard.SetText(status.Text); }
                catch (System.Runtime.InteropServices.ExternalException) { ConfirmDialog.Notice("暂时无法复制", "请稍后再试。"); }
            }));
            body.Children.Add(actions);
            body.Children.Add(release);
            status.Text = Read();
            return card;
        }

        private static string HotkeyStatus(bool enabled, bool? registered)
            => !enabled ? "关闭" : registered == true ? "已注册" : "未注册";

        private void ReopenPage(string page)
        {
            Close();
            Dispatcher.BeginInvoke(new Action(() => App.ShowSettingsPage(page)));
        }
    }
}
