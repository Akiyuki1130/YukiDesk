using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DesktopBar.Interop;

namespace DesktopBar.UI
{
    /// <summary>
    /// 液态玻璃着色器:在已高斯模糊的捕获图上叠加"真实折射感"——
    /// 沿胶囊圆角矩形的距离场(SDF)数值梯度做边缘位移采样(凸透镜收缩)、
    /// RGB 三通道错位(色散)、轮廓内侧一圈亮边(rim light)。
    ///
    /// 兼容性取舍:WPF ShaderEffect 的常量寄存器只对标量(double→float1)有完备封送,
    /// 数组映射各家实现不一;胶囊矩形干脆在生成 HLSL 时烘焙成字面量,
    /// 几何变化(模块显隐/缩放/分辨率)才重编译一次(d3dcompiler_47 运行时编译,
    /// 字节码按源码 SHA-256 落盘缓存——改几何/改源码都自动失效,lgsrc 库的教训)。
    /// 可调参数(折射/边缘光/色散)走 6 个标量寄存器,滑杆实时改不触发重编译。
    /// 编译失败返回 null,调用方降级为普通玻璃,顶栏不因此不可用。
    /// </summary>
    public sealed class LiquidGlassEffect : ShaderEffect
    {
        // ---- 标量参数寄存器(滑杆可实时调) ----
        public static readonly DependencyProperty RefrProperty = DependencyProperty.Register(
            "Refr", typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));
        public static readonly DependencyProperty RimProperty = DependencyProperty.Register(
            "Rim", typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));
        public static readonly DependencyProperty ChromaProperty = DependencyProperty.Register(
            "Chroma", typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(2)));

        /// <summary>折射强度(0..1,轮廓边缘位移采样)。</summary>
        public double Refr { get { return (double)GetValue(RefrProperty); } set { SetValue(RefrProperty, value); } }
        /// <summary>边缘光强度(0..1,轮廓内侧亮边)。</summary>
        public double Rim { get { return (double)GetValue(RimProperty); } set { SetValue(RimProperty, value); } }
        /// <summary>色散强度(0..1,边缘 RGB 错位)。</summary>
        public double Chroma { get { return (double)GetValue(ChromaProperty); } set { SetValue(ChromaProperty, value); } }
        /// <summary>斜面明暗(0..1):方向光在轮廓斜面上打高光、背光侧压暗,给玻璃"厚度"。</summary>
        public static readonly DependencyProperty SheenProperty = DependencyProperty.Register(
            "Sheen", typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(6)));
        public double Sheen { get { return (double)GetValue(SheenProperty); } set { SetValue(SheenProperty, value); } }

        private LiquidGlassEffect(byte[] bytecode, double radiusPx, double texW, double texH)
        {
            var ps = new PixelShader();
            using (var ms = new MemoryStream(bytecode))
            {
                ps.SetStreamSource(ms);
            }
            PixelShader = ps;
            // 几何寄存器随构造固定(几何变了会整个重建效果对象)
            Radius = radiusPx; TexW = texW; TexH = texH;
        }

        public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
            "Radius", typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(3)));
        public static readonly DependencyProperty TexWProperty = DependencyProperty.Register(
            "TexW", typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(4)));
        public static readonly DependencyProperty TexHProperty = DependencyProperty.Register(
            "TexH", typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(5)));
        // 采样源:显式用 Brush 绑定到 **sampler 索引 1(register(s1))**。
        // 关键坑:索引 0(register(s0)) 被 WPF 留给了隐式输入,注册在 0 上的 Brush 会被忽略,
        // 采样到的是"元素自己被渲染的内容"——在本项目里就是顶栏窗口自身的合成结果:
        // rgb 全 0(预乘后透明)、alpha 0.1~0.4 有结构,于是玻璃被压成一块死黑+白边。
        // 实测证据:把索引改成 1 立刻拿到真正的捕获帧。
        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
            "Input", typeof(LiquidGlassEffect), 1);
        public Brush Input { get { return (Brush)GetValue(InputProperty); } set { SetValue(InputProperty, value); } }

        /// <summary>
        /// 第二采样源:**未模糊**的降采样帧(register s2)。
        /// 为什么需要它:折射位移作用在已模糊的图上等于没有——模糊图没有高频细节可弯曲,
        /// 位移只会得到"还是那块糊",观感像磨砂塑料而不是液态玻璃。
        /// 所以主体仍用模糊帧(保证"模糊其下一切"的契约),只有轮廓那一圈用未模糊帧折射,
        /// 于是边缘能看到真实背景被透镜压缩/弯折,中间仍是糊的——这才是液态玻璃的观感。
        /// </summary>
        public static readonly DependencyProperty SharpProperty = RegisterPixelShaderSamplerProperty(
            "Sharp", typeof(LiquidGlassEffect), 2);
        public Brush Sharp { get { return (Brush)GetValue(SharpProperty); } set { SetValue(SharpProperty, value); } }

        public double Radius { get { return (double)GetValue(RadiusProperty); } set { SetValue(RadiusProperty, value); } }
        public double TexW { get { return (double)GetValue(TexWProperty); } set { SetValue(TexWProperty, value); } }
        public double TexH { get { return (double)GetValue(TexHProperty); } set { SetValue(TexHProperty, value); } }

        /// <summary>
        /// 生成/编译着色器。rects 是胶囊在半分辨率捕获图像素坐标下的 (cx,cy,hx,hy)。
        /// 几何不同生成的源码不同,缓存自动区分。失败返回 null。
        /// </summary>
        public static LiquidGlassEffect Create(float[][] rects, int count,
            double radiusPx, double texW, double texH)
        {
            return CreateCore(BuildHlsl(rects, Math.Max(0, count), false), radiusPx, texW, texH);
        }

        /// <summary>
        /// 展开页(整块圆角矩形)版本:与胶囊版共用同一份 main() 主体,只有距离场不同——
        /// 展开页的距离场完全来自 pTexW/pTexH/pRadius 三个寄存器,不烘焙任何几何字面量,
        /// 因此**任意尺寸、任意圆角的展开页共用同一份字节码**。
        /// 这是"面板实时玻璃"的前提:动画期间面板每帧都在变尺寸,重编译着色器是不可接受的,
        /// 而这里每帧只改寄存器(参数),编译一次就够。
        /// 另外展开页窗口没有 GeometryGroup 裁剪,圆角/边缘柔化由着色器按距离场输出 alpha。
        /// </summary>
        public static LiquidGlassEffect CreatePanel(double radiusPx, double texW, double texH)
        {
            return CreateCore(BuildHlsl(null, 0, true), radiusPx, texW, texH);
        }

        private static LiquidGlassEffect CreateCore(string hlsl, double radiusPx, double texW, double texH)
        {
            try
            {
                try
                {
                    var dbgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YukiDesk");
                    Directory.CreateDirectory(dbgDir);
                    File.WriteAllText(Path.Combine(dbgDir, "liquid-debug.hlsl"), hlsl);
                }
                catch { }
                // 目标着色器版本可覆盖(实验用:ps_2_0 与 ps_3_0 在 WPF 里的支持面不一样)。
                // 版本进缓存键,否则换版本会命中旧字节码。
                string target = Environment.GetEnvironmentVariable("YUKI_GLASS_SHADER_PS");
                if (string.IsNullOrWhiteSpace(target)) target = "ps_3_0";
                byte[] bytecode = LoadCached(hlsl + "\n// target=" + target);
                if (bytecode == null)
                {
                    bytecode = NativeMethods.CompilePs(hlsl, target);
                    SaveCached(hlsl + "\n// target=" + target, bytecode);
                }
                return new LiquidGlassEffect(bytecode, radiusPx, texW, texH);
            }
            catch (Exception ex)
            {
                App.Log("液态玻璃着色器不可用,降级为普通玻璃: " + ex);
                return null;
            }
        }

        // ---------- HLSL 生成与字节码缓存 ----------

        private static string BuildHlsl(float[][] rects, int count, bool panel)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("sampler2D Input : register(s1);");
            sb.AppendLine("sampler2D Sharp : register(s2);");
            sb.AppendLine("float pRefr : register(c0);");
            sb.AppendLine("float pRim : register(c1);");
            sb.AppendLine("float pChroma : register(c2);");
            sb.AppendLine("float pRadius : register(c3);");
            sb.AppendLine("float pTexW : register(c4);");
            sb.AppendLine("float pTexH : register(c5);");
            sb.AppendLine("float pSheen : register(c6);");
            sb.AppendLine(@"
float sd(float cx, float cy, float hx, float hy, float2 p)
{
    float2 q = abs(p - float2(cx, cy)) - float2(hx, hy) + pRadius;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - pRadius;
}
float field(float2 p)
{
    float b = 1e9;");
            if (panel)
            {
                // 展开页:整块圆角矩形,几何全部来自寄存器(texW/texH/radius),
                // 所以同一份字节码能服务任意尺寸——实时面板每帧尺寸都在变,不能重编译。
                sb.AppendLine("    b = min(b, sd(pTexW * 0.5, pTexH * 0.5, pTexW * 0.5, pTexH * 0.5, p));");
            }
            else
            {
                for (int i = 0; i < count && i < rects.Length; i++)
                {
                    // 胶囊几何烘焙成字面量:寄存器数组封送在各 WPF 版本上不可靠,字面量零风险
                    sb.AppendLine(string.Format(inv, "    b = min(b, sd({0}, {1}, {2}, {3}, p));",
                        Fmt(rects[i][0]), Fmt(rects[i][1]), Fmt(rects[i][2]), Fmt(rects[i][3])));
                }
            }
            sb.AppendLine(@"    return b;
}");
            // 调试档(YUKI_GLASS_SHADER_DEBUG=1/2/3):把某一步单独输出成画面,便于截图定位问题
            string dbg = Environment.GetEnvironmentVariable("YUKI_GLASS_SHADER_DEBUG");
            if (dbg == "1")
            {
                sb.AppendLine(@"float4 main(float2 uv : TEXCOORD) : COLOR
{
    return float4(tex2D(Input, uv).rgb, 1);   // 直通:验证采样通道本身
}");
                return sb.ToString();
            }
            if (dbg == "9")
            {
                // 常量档:完全不采样、不读寄存器。判定"效果到底有没有被画出来"的对照组。
                sb.AppendLine(@"float4 main(float2 uv : TEXCOORD) : COLOR
{
    return float4(1.0, 0.0, 0.0, 1.0);
}");
                return sb.ToString();
            }
            if (dbg == "10")
            {
                // 常量档 + 半透明:对照组,验证 alpha 合成路径。
                sb.AppendLine(@"float4 main(float2 uv : TEXCOORD) : COLOR
{
    return float4(1.0, 0.0, 0.0, 0.5);
}");
                return sb.ToString();
            }
            sb.AppendLine(@"float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 tex = float2(pTexW, pTexH);
    float2 px = uv * tex;
    float d = field(px);
    // 数值梯度得到场的法线,只在轮廓附近有意义
    float e = 2.0;
    float2 g = float2(
        field(px + float2(e, 0)) - field(px - float2(e, 0)),
        field(px + float2(0, e)) - field(px - float2(0, e)));
    float2 n = g / (length(g) + 1e-4);
    // 折射带宽度(半分辨率像素)。带太宽会把整颗胶囊都变成未模糊+彩虹,带太窄看不见透镜感;
    // 半径 12.8px 的胶囊上 6px 正好是一圈边,主体仍保持模糊。
    float edge = 1.0 - saturate(abs(d) / 6.0);
    float2 disp = n * pRefr * edge * edge * 14.0;
    float ca = pChroma * edge * edge * 0.08;
    // 主体:模糊帧(契约:顶栏之下的一切都被模糊)
    float3 body = tex2D(Input, uv).rgb;
    // 轮廓一圈:从未模糊帧折射采样,才有「透镜把背景压弯」的真实感(带 RGB 色散)
    float3 lens;
    lens.r = tex2D(Sharp, (px + disp * (1.0 + ca)) / tex).r;
    lens.g = tex2D(Sharp, (px + disp) / tex).g;
    lens.b = tex2D(Sharp, (px + disp * (1.0 - ca)) / tex).b;
    float3 col = lerp(body, lens, saturate(edge * edge * 1.1) * 0.85);
    // 轮廓内侧一圈亮边(压薄一点,否则远景看就是白描边)
    float rim = pRim * edge * edge * saturate(-d / 6.0 + 0.5);
    col += rim * 0.5;
    // 方向光(左上)在斜面上打高光、右下侧压暗:玻璃厚板的体积感靠这一对比,
    // 否则胶囊内部只是均匀半透明色,大块面板尤其像塑料片(与 CPU 烘焙面板同一口径)。
    float dotL = -(n.x * 0.55 + n.y * 0.83);
    float lit = max(dotL, 0.0);
    float shade = max(-dotL, 0.0);
    float sheen = pSheen * edge * edge * (lit - shade * 0.55);
    col += sheen * 0.35;
    col = saturate(col);
    // ---- 调试档(SM3 下构造函数/隐式转换很挑,统一用分量赋值,别用 float4(x,y,z,w) 混类型) ----
    if (DEBUG_MODE == 2) { float4 c0 = tex2D(Input, uv); c0.rgb = edge; c0.a = 1.0; return c0; }
    if (DEBUG_MODE == 3) { float4 c0 = tex2D(Input, uv); c0.r = uv.x; c0.g = uv.y; c0.b = 0.0; c0.a = 1.0; return c0; }
    if (DEBUG_MODE == 4) { float4 c0 = tex2D(Input, uv); c0.r = abs(disp.x) / 30.0; c0.g = abs(disp.y) / 30.0; c0.b = 0.0; c0.a = 1.0; return c0; }
    if (DEBUG_MODE == 5) { float4 c0 = tex2D(Input, uv); float a0 = max(c0.a, 0.0009765625); c0.rgb = c0.rgb / a0; c0.a = 1.0; return c0; }
    if (DEBUG_MODE == 6) { float4 c0 = tex2D(Input, uv); c0.rgb = c0.a; c0.a = 1.0; return c0; }
    if (DEBUG_MODE == 7) { float4 c0 = tex2D(Input, uv); c0.rgb = pow(saturate(c0.rgb), 0.45454544); c0.a = 1.0; return c0; }
    if (DEBUG_MODE == 8) { float4 c0 = tex2D(Input, uv); return c0; }
");
            if (panel)
            {
                // 展开页:窗口没有几何裁剪,圆角与边缘柔化直接由距离场给 alpha。
                // (WPF 对带 Effect 的元素按整体包围盒跑像素着色器,形状必须自己画,否则四角是方的)
                sb.AppendLine(@"    float pa = saturate(0.5 - d);
    return float4(col * pa, pa);
}");
            }
            else
            {
                sb.AppendLine(@"    return float4(col, 1);
}");
            }
            // 调试档号填进模板里的 DEBUG_MODE 占位符;不认识的号码一律按 0(正常路径)
            string mode = dbg == null ? "0" : dbg;
            switch (mode)
            {
                case "2": case "3": case "4": case "5": case "6": case "7": break;
                default: mode = "0"; break;
            }
            return sb.ToString().Replace("DEBUG_MODE", mode);
        }

        /// <summary>
        /// 把浮点数写成合法的 HLSL 字面量。Historic bug:直接用 "R" 格式会产出 "35f"/"1E-05f",
        /// HLSL 要求小数点(35f 是语法错),所以固定 "0.0###" 且补足小数位。
        /// </summary>
        private static string Fmt(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
            var s = v.ToString("0.0###", CultureInfo.InvariantCulture);
            if (s == "-0.0") s = "0.0";
            return s;
        }

        private static string CachePath(string hlsl)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(hlsl));
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "YukiDesk", "shadercache");
                var name = new StringBuilder();
                foreach (var b in hash) name.Append(b.ToString("x2"));
                return Path.Combine(dir, name.ToString() + ".ps");
            }
        }

        private static byte[] LoadCached(string hlsl)
        {
            try
            {
                var path = CachePath(hlsl);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch { return null; }
        }

        private static void SaveCached(string hlsl, byte[] bytecode)
        {
            try
            {
                var path = CachePath(hlsl);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytecode);
            }
            catch { }
        }
    }
}
