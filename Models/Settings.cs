using System.Collections.Generic;
using System.IO;
using System.Windows.Input;

namespace DesktopBar.Models
{
    /// <summary>旧版配置兼容类型。YukiDesk 运行时统一使用 AppSettings 的主题。</summary>
    public class TabStyle
    {
        public string BgColor { get; set; } = "#14141C";
        public double BgOpacity { get; set; } = 0.55;
        public string FontFamily { get; set; } = "Microsoft YaHei UI";
        public double FontSize { get; set; } = 13;
        public string FontColor { get; set; } = "#FFFFFF";
    }

    public class AppSettings
    {
        internal const int MaxRevealEdgeDwellMs = 1500;
        internal const int MinRevealDurationSeconds = 1;
        internal const int MaxRevealDurationSeconds = 30;

        // 迁移版本用可空 int:旧配置没有该字段时反序列化得到 null(而不是 2),
        // 迁移门 `null 或 < 2` 才真的会对旧配置执行一次;写回时已固化为 2。
        public int? SchemaVersion { get; set; }
        // 顶栏背景仍可独立关闭，以保持桌面点击穿透。
        public string BarBgColor { get; set; } = "#000000";
        public double BarBgOpacity { get; set; } = 0.0;

        // 统一选项卡和展开页主题。
        public string ThemeColor { get; set; } = "#14141C";
        public double ThemeOpacity { get; set; } = 0.55;
        public string ThemeTextColor { get; set; } = "#FFFFFF";
        public bool BorderEnabled { get; set; } = true;
        public string BorderColor { get; set; } = "#FFFFFF";
        public double BorderOpacity { get; set; } = 0.32;
        public double BorderThickness { get; set; } = 1;
        public double Scale { get; set; } = 1.0;
        // AutoTheme 是旧版配置。新选项统一控制纯色和两种玻璃材质。
        public bool AutoTheme { get; set; } = true;
        public bool? FollowCapsuleBackground { get; set; }
        public int HoverCloseDelayMs { get; set; } = 250;
        public int AnimationDurationMs { get; set; } = 180;
        public bool ShowInScreenCapture { get; set; } = true;

        // ---- 胶囊材质(自绘云母 / Mica) ----
        // 云母本质是"壁纸模糊 + 亮度压带 + 主题染色 + 细噪点",只依赖壁纸不依赖背后窗口,
        // 所以可以用已知内容复现:零捕获、零每帧开销、任意胶囊形状(主报告 §9.22.5)。
        // 默认 0 = 纯色,保持现有外观;只有用户显式打开才会烘焙位图。
        /// <summary>胶囊材质:0=纯色(默认) 1=云母(遗留,已下架) 2=亚克力(遗留,已下架) 3=实时背景玻璃 4=液态玻璃。</summary>
        public int PillMaterial { get; set; } = 0;
        /// <summary>云母整体不透明度。</summary>
        public double MicaOpacity { get; set; } = 0.86;
        /// <summary>云母模糊半径(DIP,按显示器 DPI 换算成物理像素)。Mica 的模糊很重。</summary>
        public double MicaBlurRadius { get; set; } = 28;
        /// <summary>云母细节对比保留系数:0=完全压平到目标亮度带,1=保留壁纸原始对比。</summary>
        public double MicaContrast { get; set; } = 0.30;
        /// <summary>云母彩度保留系数。真实 Mica 接近单色,默认只留三成壁纸色相。</summary>
        public double MicaSaturation { get; set; } = 0.35;
        /// <summary>云母主题染色强度(往 ThemeService.Background 混多少)。</summary>
        public double MicaTint { get; set; } = 0.18;
        /// <summary>云母细噪点强度。这是云母与"纯模糊"最大的观感区别之一。</summary>
        public double MicaNoise { get; set; } = 0.035;

        // ---- 胶囊材质(自绘亚克力 / Acrylic) ----
        // 与云母同一条烘焙管线,只是配方不同:亚克力是"磨砂玻璃"——保留更多壁纸
        // 色彩与细节、噪点更显眼、更透明,还有一层随明暗主题的提亮/压暗混合
        // (真亚克里的 luminosity 层);云母则是压平到接近单色的石材质感。
        /// <summary>亚克力不透明度。</summary>
        public double AcrylicOpacity { get; set; } = 0.78;
        /// <summary>亚克力模糊半径(DIP,按显示器 DPI 换算成物理像素)。磨砂感比云母更重。</summary>
        public double AcrylicBlurRadius { get; set; } = 44;
        /// <summary>亚克力细节对比保留系数。</summary>
        public double AcrylicContrast { get; set; } = 0.55;
        /// <summary>亚克力彩度保留系数。亚克力明显比云母更"彩色"。</summary>
        public double AcrylicSaturation { get; set; } = 0.75;
        /// <summary>亚克力主题染色强度。</summary>
        public double AcrylicTint { get; set; } = 0.24;
        /// <summary>亚克力噪点强度。真亚克力的噪点比云母显眼。</summary>
        public double AcrylicNoise { get; set; } = 0.05;

        // ---- 胶囊材质(实时背景玻璃 / Glass, PillMaterial=3) ----
        // 截图兼容模式合成背后的可采集窗口和壁纸。整屏模式排除自家窗口，避免反馈。
        /// <summary>玻璃整体不透明度(玻璃层 Image 的 Opacity)。</summary>
        public double GlassOpacity { get; set; } = 1.00;
        /// <summary>玻璃高斯模糊半径(DIP,BlurEffect.Radius 按显示器 DPI 换算)。越大越糊、逐帧成本越高。</summary>
        public double GlassBlurRadius { get; set; } = 24;
        /// <summary>玻璃染色颜色(叠在模糊层之上的边框层)。</summary>
        public string GlassTint { get; set; } = "#14141C";
        /// <summary>玻璃染色不透明度。</summary>
        public double GlassTintOpacity { get; set; } = 0.18;
        /// <summary>
        /// 胶囊间隙的玻璃强度:0=间隙完全透明(保留"点空白处穿透到桌面"的契约);
        /// 大于 0 时整条顶栏都铺玻璃,间隙按此强度淡化。注意:铺了间隙后顶栏整条不再点击穿透
        /// (WPF 分层窗口的穿透判据是像素 alpha,不是 IsHitTestVisible,实测探针验证)。
        /// </summary>
        public double GlassGapFill { get; set; } = 0;
        /// <summary>0=自动节奏；正数为手动更新上限。</summary>
        public int GlassMaxFps { get; set; }
        public int WallpaperFadeDurationMs { get; set; } = 360;

        // ---- 液态玻璃(PillMaterial=4)专属:着色器参数 ----
        /// <summary>-1=旧配置；0=清透，1=柔雾，2=自定义。</summary>
        public int GlassStyle { get; set; } = -1;
        /// <summary>液态玻璃雾面强度(0..1)；null 使用当前外观的默认值。</summary>
        public double? GlassFrost { get; set; }
        /// <summary>折射强度(0..1,胶囊轮廓边缘的位移采样,凸透镜收缩感)。</summary>
        public double GlassRefraction { get; set; } = 0.45;
        /// <summary>边缘光强度(0..1,轮廓内侧一圈亮边)。</summary>
        public double GlassRim { get; set; } = 0.5;
        /// <summary>色散强度(0..1,边缘 RGB 错位,玻璃棱镜感)。</summary>
        public double GlassChroma { get; set; } = 0.35;

        // 旧字段保留用于读取旧配置并迁移，不再单独应用。
        public TabStyle Folder { get; set; } = new TabStyle();
        public TabStyle Music { get; set; } = new TabStyle();
        public TabStyle Clock { get; set; } = new TabStyle();
        public TabStyle SettingsBtn { get; set; } = new TabStyle { BgOpacity = 0.25 };

        // 时间
        public bool TimeOnRight { get; set; } = false;
        public bool ShowSeconds { get; set; } = true;
        public bool ShowDate { get; set; } = false;
        public bool ShowWeekday { get; set; } = false;

        // 小文件夹
        public string StorageRoot { get; set; } = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "YukiDesk", "MiniFolders");
        public List<string> Folders { get; set; } = new List<string>();

        // 桌面与通用
        public bool AutoStart { get; set; } = false;
        public bool RevealOnTopEdge { get; set; }
        public int RevealEdgeDwellMs { get; set; } = 110;
        public bool RevealWithHotkey { get; set; }
        public Key RevealHotkeyKey { get; set; } = Key.Y;
        public ModifierKeys RevealHotkeyModifiers { get; set; } = ModifierKeys.Control | ModifierKeys.Alt;
        public int RevealDurationSeconds { get; set; } = 5;
        public bool ReserveDesktopSpace { get; set; } = false;
        public int DesktopSpaceHeight { get; set; } = 46;
        public bool ShowPower { get; set; } = true;
        public bool ShowThisPc { get; set; } = true;
        public bool ShowFolders { get; set; } = true;
        public bool ShowMusic { get; set; } = true;
        public bool ShowVolume { get; set; } = true;
        public bool ShowRecycleBin { get; set; } = true;
        public bool ShowSpectrum { get; set; } = true;

        // 更新(经 GitHub Releases 检测与下载)
        public bool AutoCheckUpdate { get; set; } = true;
        public bool AutoUpdate { get; set; } = false;

        // 性能监测(频谱左侧的四个胶囊;采样线程只在任一胶囊可见时运行)
        public bool ShowMonitorCpu { get; set; } = true;
        public bool ShowMonitorGpu { get; set; } = true;
        public bool ShowMonitorMemory { get; set; } = true;
        public bool ShowMonitorNetwork { get; set; } = true;
        /// <summary>要监测的 GPU(LUID "高32位:低32位");空 = 第一块显卡。</summary>
        public string MonitorGpuLuid { get; set; } = "";

        // 设置窗口外观
        public bool SettingsFollowSystemTheme
        {
            get => SettingsThemeMode == 0;
            set => SettingsThemeMode = value ? 0 : 2;
        }
        /// <summary>设置窗口主题模式:0=跟随系统(默认) 1=浅色 2=深色。</summary>
        public int SettingsThemeMode { get; set; } = 0;
    }
}
