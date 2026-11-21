using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using DesktopBar.Models;

namespace DesktopBar.Services
{
    public static class SettingsService
    {
        private const int CurrentSchemaVersion = 2;
        /// <summary>本轮启动是否发现过损坏配置(已被隔离);此时不要用默认值自动覆写 settings.json。</summary>
        public static bool CorruptConfigDetected { get; private set; }
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YukiDesk");
        private static readonly string FilePath = Path.Combine(Dir, "settings.json");
        private static readonly string BackupFilePath = Path.Combine(Dir, "settings.backup.json");
        private static readonly string LegacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopBar");
        private static readonly string LegacyFilePath = Path.Combine(LegacyDir, "settings.json");
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static AppSettings Load(out bool firstRun)
        {
            firstRun = false;
            if (TryLoad(FilePath, out var current)) return current;
            if (TryLoad(BackupFilePath, out var backup)) return backup;
            if (TryLoad(LegacyFilePath, out var legacy))
            {
                Save(legacy);
                return legacy;
            }
            firstRun = !File.Exists(FilePath) && !File.Exists(LegacyFilePath);
            return Normalize(new AppSettings());
        }

        private static bool TryLoad(string path, out AppSettings settings)
        {
            settings = null;
            if (!File.Exists(path)) return false;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (settings == null) throw new InvalidDataException("配置内容为空");
                settings = Normalize(settings);
                return true;
            }
            catch (Exception ex)
            {
                App.Log("读取配置失败 " + path + ": " + ex.Message);
                CorruptConfigDetected = true;
                // 保留原始配置以便人工恢复，不把它误判为全新安装再立刻覆盖。
                try
                {
                    string quarantine = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    if (!File.Exists(quarantine)) File.Copy(path, quarantine, false);
                }
                catch { }
                return false;
            }
        }

        public static bool IsValidFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim()) return false;
            if (name.EndsWith(".", StringComparison.Ordinal)) return false;
            if (Path.IsPathRooted(name) || name == "." || name == "..") return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)) return false;
            string stem = Path.GetFileNameWithoutExtension(name);
            string upper = stem.ToUpperInvariant();
            if (upper is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$") return false;
            if ((upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && upper.Length == 4 && upper[3] >= '1' && upper[3] <= '9') return false;
            return true;
        }

        private static AppSettings Normalize(AppSettings s)
        {
            if (s == null) s = new AppSettings();
            s.Folders ??= new List<string>();
            s.Folders = s.Folders.Where(IsValidFolderName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();

            if ((s.SchemaVersion ?? 0) < CurrentSchemaVersion)
            {
                // 旧版只有每模块色彩,迁移一次。注意不能以"ThemeColor 为空"为条件:
                // 旧 JSON 缺 themeColor 键时,反序列化得到的是属性初始化默认值("#14141C"),
                // 不是 null——迁移必须无条件用每模块配色覆盖,再固化版本号。
                if (s.Folder != null)
                {
                    s.ThemeColor = NormalizeColor(s.Folder.BgColor, null) ?? s.ThemeColor;
                    s.ThemeTextColor = NormalizeColor(s.Folder.FontColor, null) ?? s.ThemeTextColor;
                    s.ThemeOpacity = Clamp(s.Folder.BgOpacity, 0.05, 1, s.ThemeOpacity);
                }
                s.SchemaVersion = CurrentSchemaVersion;
            }

            s.StorageRoot = NormalizeRoot(s.StorageRoot);
            s.BarBgColor = NormalizeColor(s.BarBgColor, "#000000");
            s.ThemeColor = NormalizeColor(s.ThemeColor, "#14141C");
            s.ThemeTextColor = NormalizeColor(s.ThemeTextColor, "#FFFFFF");
            s.BorderColor = NormalizeColor(s.BorderColor, "#FFFFFF");
            s.BarBgOpacity = Clamp(s.BarBgOpacity, 0, 1, 0);
            s.ThemeOpacity = Clamp(s.ThemeOpacity, 0.05, 1, 0.55);
            s.BorderOpacity = Clamp(s.BorderOpacity, 0, 1, 0.32);
            s.BorderThickness = Clamp(s.BorderThickness, 0, 6, 1);
            s.Scale = Clamp(s.Scale, 0.75, 1.5, 1);
            s.DesktopSpaceHeight = Math.Clamp(s.DesktopSpaceHeight, 38, 160);
            s.HoverCloseDelayMs = Math.Clamp(s.HoverCloseDelayMs, 80, 2000);
            s.AnimationDurationMs = Math.Clamp(s.AnimationDurationMs, 60, 1200);
            s.RevealEdgeDwellMs = Math.Clamp(s.RevealEdgeDwellMs, 0, AppSettings.MaxRevealEdgeDwellMs);
            s.RevealDurationSeconds = Math.Clamp(s.RevealDurationSeconds,
                AppSettings.MinRevealDurationSeconds, AppSettings.MaxRevealDurationSeconds);
            if (!UI.BarHotkey.FromSettings(s).IsValid)
            {
                s.RevealHotkeyKey = UI.BarHotkey.Default.Key;
                s.RevealHotkeyModifiers = UI.BarHotkey.Default.Modifiers;
            }
            if (s.SettingsThemeMode < 0 || s.SettingsThemeMode > 2) s.SettingsThemeMode = 0;
            // 胶囊材质档位:0=纯色 3=实时玻璃 4=液态玻璃。云母(1)/亚克力(2)已从设置 UI 下架,
            // 存量配置迁移到玻璃;后端烘焙代码保留,老版本回滚后读 3/4 会因 >2 钳制回落纯色(优雅降级)。
            if (s.PillMaterial == 1 || s.PillMaterial == 2) s.PillMaterial = 3;
            if (s.PillMaterial < 0 || s.PillMaterial > 4) s.PillMaterial = 0;
            s.MicaOpacity = Clamp(s.MicaOpacity, 0.05, 1, 0.86);
            s.MicaBlurRadius = Clamp(s.MicaBlurRadius, 4, 80, 28);
            s.MicaContrast = Clamp(s.MicaContrast, 0, 1, 0.30);
            s.MicaSaturation = Clamp(s.MicaSaturation, 0, 1, 0.35);
            s.MicaTint = Clamp(s.MicaTint, 0, 1, 0.18);
            s.MicaNoise = Clamp(s.MicaNoise, 0, 0.2, 0.035);
            s.AcrylicOpacity = Clamp(s.AcrylicOpacity, 0.05, 1, 0.78);
            s.AcrylicBlurRadius = Clamp(s.AcrylicBlurRadius, 4, 80, 44);
            s.AcrylicContrast = Clamp(s.AcrylicContrast, 0, 1, 0.55);
            s.AcrylicSaturation = Clamp(s.AcrylicSaturation, 0, 1, 0.75);
            s.AcrylicTint = Clamp(s.AcrylicTint, 0, 1, 0.24);
            s.AcrylicNoise = Clamp(s.AcrylicNoise, 0, 0.2, 0.05);
            s.GlassOpacity = Clamp(s.GlassOpacity, 0.05, 1, 1.00);
            s.GlassBlurRadius = Clamp(s.GlassBlurRadius, 4, 60, 24);
            s.GlassTint = NormalizeColor(s.GlassTint, "#14141C");
            s.GlassTintOpacity = Clamp(s.GlassTintOpacity, 0, 1, 0.18);
            s.GlassMaxFps = Math.Clamp(s.GlassMaxFps, 0, 300);
            s.WallpaperFadeDurationMs = Math.Clamp(s.WallpaperFadeDurationMs, 0, 1200);
            s.GlassGapFill = Clamp(s.GlassGapFill, 0, 1, 0);
            s.GlassRefraction = Clamp(s.GlassRefraction, 0, 1, 0.45);
            s.GlassRim = Clamp(s.GlassRim, 0, 1, 0.5);
            s.GlassChroma = Clamp(s.GlassChroma, 0, 1, 0.35);
            if (s.GlassStyle < 0 || s.GlassStyle > 2)
            {
                // 沿用修改过的材质参数；使用旧默认配方的配置可直接体验清透外观。
                bool custom = s.GlassBlurRadius != 24 || s.GlassTintOpacity != 0.18
                    || !string.Equals(s.GlassTint, "#14141C", StringComparison.OrdinalIgnoreCase)
                    || s.GlassRefraction != 0.45 || s.GlassRim != 0.50 || s.GlassChroma != 0.35;
                s.GlassStyle = custom ? 2 : 0;
            }
            s.FollowCapsuleBackground ??= ThemeService.FollowsBackground(s);
            if (s.GlassFrost is double frost)
                s.GlassFrost = double.IsFinite(frost) ? Math.Clamp(frost, 0, 1) : null;
            return s;
        }

        private static string NormalizeRoot(string path)
        {
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YukiDesk", "MiniFolders");
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return fallback;
                return Path.GetFullPath(path);
            }
            catch { return fallback; }
        }

        private static string NormalizeColor(string value, string fallback)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(value);
                return color.A == byte.MaxValue
                    ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                    : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            }
            catch { return fallback; }
        }

        private static double Clamp(double value, double minimum, double maximum, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        public static void Save(AppSettings s)
        {
            if (s == null) return;
            try
            {
                s = Normalize(s);
                Directory.CreateDirectory(Dir);
                // 随机 tmp 名:并发写或杀软短暂锁定 .tmp 时不会互相踩文件。
                string temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, JsonSerializer.Serialize(s, JsonOptions));
                if (File.Exists(FilePath))
                    File.Replace(temp, FilePath, BackupFilePath, true);
                else
                    File.Move(temp, FilePath);
            }
            catch (Exception ex)
            {
                App.Log("保存配置失败: " + ex.Message);
                // 磁盘满/ACL 拒绝/杀软锁定时用户以为已保存,重启后回滚——必须可见。
                try
                {
                    DesktopBar.App.ShowConfigSaveFailure(ex);
                }
                catch { }
            }
        }
    }
}
