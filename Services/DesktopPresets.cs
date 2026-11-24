using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using DesktopBar.Models;
using DesktopBar.UI;

namespace DesktopBar.Services
{
    internal static class DesktopPresets
    {
        private sealed class Archive
        {
            public int Format { get; set; }
            public Dictionary<string, JsonElement> Settings { get; set; }
            public List<DesktopPreset> Presets { get; set; }
            public Dictionary<string, JsonElement> Folders { get; set; }
        }

        internal sealed record ImportData(AppSettings Settings, List<DesktopPreset> Presets, int SettingCount, bool IncludesFolders);

        private static readonly HashSet<string> PresetFields = new(StringComparer.Ordinal)
        {
            "BarLayout", "ShowPower", "ShowThisPc", "ShowRecycleBin", "ShowFolders", "ShowFavorites",
            "ShowMusic", "ShowVolume", "ShowSpectrum", "ShowMonitorCpu", "ShowMonitorGpu", "ShowMonitorMemory",
            "ShowMonitorNetwork", "CombinePerfModules", "PillMaterial", "GlassStyle", "FreezePanelGlass",
            "GlassMaxFps", "ShowInScreenCapture", "WallpaperOnlyCapture", "RevealOnTopEdge", "RevealWithHotkey", "RevealEdgeDwellMs",
            "RevealDurationSeconds", "RevealOnPointerScreen", "RevealDisplayName", "ReserveDesktopSpace", "ShowBattery"
        };

        private static readonly HashSet<string> ArchiveFields = new(PresetFields, StringComparer.Ordinal)
        {
            "BarBgColor", "BarBgOpacity", "ThemeColor", "ThemeOpacity", "ThemeTextColor", "BorderEnabled", "BorderColor",
            "BorderOpacity", "BorderThickness", "Scale", "AutoTheme", "FollowCapsuleBackground", "HoverCloseDelayMs",
            "AnimationDurationMs", "GlassOpacity", "GlassBlurRadius", "GlassTint", "GlassTintOpacity", "GlassGapFill",
            "WallpaperFadeDurationMs", "GlassFrost", "GlassRefraction", "GlassRim", "GlassChroma", "SettingsThemeMode",
            "TimeOnRight", "ShowSeconds", "ShowDate", "ShowWeekday", "SecondTimeZoneId", "DesktopSpaceHeight",
            "RevealHotkeyKey", "RevealHotkeyModifiers",
            "EdgeExcludedApps", "AppScenes"
        };
        private static readonly HashSet<string> AppSceneFields = new(
            PresetFields.Where(field => field != "RevealWithHotkey"), StringComparer.Ordinal);

        private static readonly HashSet<string> FolderFields = new(StringComparer.Ordinal)
        {
            "LinkedFolders", "FolderViews", "InboxFolder", "DesktopLinks"
        };
        private static readonly HashSet<string> FullArchiveFields = new(ArchiveFields.Concat(FolderFields), StringComparer.Ordinal);
        private static readonly PropertyInfo[] Properties = typeof(AppSettings).GetProperties()
            .Where(property => FullArchiveFields.Contains(property.Name)).ToArray();
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 20 };

        private static Dictionary<string, JsonElement> Capture(AppSettings settings, HashSet<string> fields)
            => Properties.Where(property => fields.Contains(property.Name)).ToDictionary(property => property.Name,
                property => JsonSerializer.SerializeToElement(property.GetValue(settings), property.PropertyType, Options));

        internal static DesktopPreset Create(string name)
            => new() { Name = name, Values = Capture(App.Settings, PresetFields) };

        internal static AppSettings ResolveScene(DesktopPreset preset)
        {
            var scene = App.Settings.CloneForScene();
            var values = LoadValues(preset.Values, AppSceneFields);
            foreach (var property in Properties.Where(property => AppSceneFields.Contains(property.Name)))
                property.SetValue(scene, property.GetValue(values));
            return scene;
        }

        internal static DesktopPreset[] Examples()
        {
            var daily = Create("日常");
            var game = Create("游戏");
            game.Values["ShowMusic"] = JsonSerializer.SerializeToElement(false);
            game.Values["ShowSpectrum"] = JsonSerializer.SerializeToElement(false);
            game.Values["RevealOnTopEdge"] = JsonSerializer.SerializeToElement(false);
            game.Values["RevealWithHotkey"] = JsonSerializer.SerializeToElement(true);
            game.Values["FreezePanelGlass"] = JsonSerializer.SerializeToElement(true);
            var recording = Create("录制");
            recording.Values["ShowSpectrum"] = JsonSerializer.SerializeToElement(false);
            recording.Values["ShowInScreenCapture"] = JsonSerializer.SerializeToElement(true);
            recording.Values["FreezePanelGlass"] = JsonSerializer.SerializeToElement(true);
            return new[] { daily, game, recording };
        }

        internal static bool Apply(DesktopPreset preset)
        {
            try { return Apply(LoadValues(preset.Values, PresetFields), PresetFields); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
            { ConfirmDialog.Notice("无法应用预设", ex.Message); return false; }
        }

        internal static Task ExportAsync(string path, bool includeFolders)
        {
            var archive = new Archive
            {
                Format = 1, Settings = Capture(App.Settings, ArchiveFields), Presets = App.Settings.DesktopPresets,
                Folders = includeFolders ? Capture(App.Settings, FolderFields) : null
            };
            var content = JsonSerializer.Serialize(archive, Options);
            return Task.Run(() => SettingsService.WriteAtomicText(path, content));
        }

        internal static async Task<ImportData> ReadImportAsync(string path)
        {
            string content = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                if (stream.Length > 1024 * 1024) throw new InvalidDataException("备份文件超过 1 MB。");
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            });
            var archive = JsonSerializer.Deserialize<Archive>(content, Options);
            if (archive?.Format != 1 || archive.Settings == null || archive.Settings.Keys.All(key => !ArchiveFields.Contains(key)))
                throw new InvalidDataException("这不是支持的 YukiDesk 布局备份。");
            var values = archive.Settings.Where(pair => ArchiveFields.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
            bool includesFolders = archive.Folders?.Keys.Any(FolderFields.Contains) == true;
            if (includesFolders)
                foreach (var pair in archive.Folders.Where(pair => FolderFields.Contains(pair.Key))) values[pair.Key] = pair.Value;
            var settings = LoadValues(values, includesFolders ? FullArchiveFields : ArchiveFields);
            var presets = new List<DesktopPreset>();
            foreach (var preset in archive.Presets ?? new())
            {
                if (preset == null || string.IsNullOrWhiteSpace(preset.Name) || preset.Values == null) continue;
                var parsed = LoadValues(preset.Values, PresetFields);
                presets.Add(new DesktopPreset { Name = preset.Name.Trim(), Values = Capture(parsed, PresetFields) });
            }
            return new ImportData(settings, presets, values.Count, includesFolders);
        }

        internal static bool Apply(ImportData import)
            => Apply(import.Settings, import.IncludesFolders ? FullArchiveFields : ArchiveFields, import.Presets);

        private static AppSettings LoadValues(Dictionary<string, JsonElement> values, HashSet<string> fields)
        {
            var result = new AppSettings { SchemaVersion = App.Settings.SchemaVersion };
            var current = Capture(App.Settings, fields);
            foreach (var property in Properties.Where(property => fields.Contains(property.Name)))
            {
                var content = values.TryGetValue(property.Name, out var replacement) ? replacement : current[property.Name];
                if (content.ValueKind == JsonValueKind.Null && property.PropertyType.IsValueType
                    && Nullable.GetUnderlyingType(property.PropertyType) == null)
                    throw new InvalidDataException("备份字段为空 " + property.Name);
                property.SetValue(result, content.Deserialize(property.PropertyType, Options));
            }
            return SettingsService.Normalize(result);
        }

        private static bool Apply(AppSettings next, HashSet<string> fields, List<DesktopPreset> importedPresets = null)
        {
            var s = App.Settings;
            var previousAppearance = App.ActiveSettings.CloneForScene();
            var previousReveal = BarHotkey.FromSettings(s);
            var newReveal = fields.Contains("RevealHotkeyKey") ? BarHotkey.FromSettings(next) : previousReveal;
            bool revealChange = next.RevealWithHotkey != s.RevealWithHotkey || newReveal != previousReveal;
            var bar = App.Bar;
            if (bar != null && revealChange && !bar.TrySetRevealHotkey(next.RevealWithHotkey, newReveal))
            {
                ConfirmDialog.Notice("快捷键不可用", "预设里的快捷键暂时无法注册。请修改快捷键后重试。");
                return false;
            }
            bar?.SuppressAppSceneForManualPreset();
            foreach (var property in Properties.Where(property => fields.Contains(property.Name)))
                property.SetValue(s, property.GetValue(next));
            if (importedPresets != null)
            {
                foreach (var preset in importedPresets)
                {
                    s.DesktopPresets.RemoveAll(item => string.Equals(item.Name, preset.Name, StringComparison.CurrentCultureIgnoreCase));
                    s.DesktopPresets.Add(preset);
                }
            }
            App.Save();
            if (bar == null) ThemeService.Refresh();
            else bar.ApplyPresetVisuals(previousAppearance, importedAppearance: importedPresets != null);
            if (importedPresets != null) bar?.RefreshAppSceneRules();
            return true;
        }
    }
}
