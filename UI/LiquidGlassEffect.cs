using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using DesktopBar.Interop;

namespace DesktopBar.UI
{
    /// <summary>模糊帧绘制主体，锐帧绘制轮廓折射。胶囊共享一张布局纹理，面板使用圆角距离场。</summary>
    public sealed class LiquidGlassEffect : ShaderEffect
    {
        private readonly PillGlassGeometry _pillGeometry;
        private static PixelShader _pillsShader, _panelShader;
        private static bool _pillsAttempted, _panelAttempted;

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

        private LiquidGlassEffect(PixelShader shader, double radiusPx, double texW, double texH, bool panel)
        {
            PixelShader = shader;
            Radius = radiusPx;
            TexW = texW;
            TexH = texH;
            UpdateShaderValue(RefrProperty);
            UpdateShaderValue(RimProperty);
            UpdateShaderValue(ChromaProperty);
            UpdateShaderValue(SheenProperty);
            UpdateShaderValue(LiftProperty);
            UpdateShaderValue(TintProperty);
            UpdateShaderValue(RadiusProperty);
            UpdateShaderValue(TexWProperty);
            UpdateShaderValue(TexHProperty);
            UpdateShaderValue(SharpUvProperty);
            UpdateShaderValue(EdgeWidthProperty);
            if (!panel) _pillGeometry = new PillGlassGeometry();
        }

        private static readonly DependencyProperty SharpUvProperty = DependencyProperty.Register(
            "SharpUv", typeof(Point4D), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(new Point4D(1, 1, 0, 0), PixelShaderConstantCallback(7)));
        private static readonly DependencyProperty TintProperty = DependencyProperty.Register(
            "Tint", typeof(Point4D), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(new Point4D(), PixelShaderConstantCallback(8)));
        private static readonly DependencyProperty EdgeWidthProperty = DependencyProperty.Register(
            "EdgeWidth", typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(6.0, PixelShaderConstantCallback(9)));

        private static readonly DependencyProperty LiftProperty = DependencyProperty.Register(
            "Lift", typeof(double), typeof(LiquidGlassEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(11)));

        internal double Lift { set { SetValue(LiftProperty, value); } }
        internal Point4D SharpUv { set { SetValue(SharpUvProperty, value); } }
        internal Point4D Tint { set { SetValue(TintProperty, value); } }
        internal double EdgeWidth { set { SetValue(EdgeWidthProperty, value); } }

        private static readonly DependencyProperty PillGeometryProperty = RegisterPixelShaderSamplerProperty(
            "PillGeometry", typeof(LiquidGlassEffect), 3, SamplingMode.Bilinear);

        internal void SetPills(IReadOnlyList<Rect> layout, double scaleX, double scaleY, double edgeWidth)
        {
            EdgeWidth = edgeWidth;
            var brush = _pillGeometry.Update(layout, Math.Max(1, (int)TexW), Math.Max(1, (int)TexH),
                scaleX, scaleY, Radius, edgeWidth);
            if (!ReferenceEquals(GetValue(PillGeometryProperty), brush)) SetValue(PillGeometryProperty, brush);
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

        // 无界面自检传入的胶囊是纹理像素坐标 (cx, cy, hx, hy)。
        public static LiquidGlassEffect Create(float[][] rects, int count, double radiusPx, double texW, double texH)
        {
            var effect = CreatePills(radiusPx, texW, texH);
            if (effect == null) return null;
            var layout = new List<Rect>();
            int length = Math.Min(count, rects?.Length ?? 0);
            for (int i = 0; i < length; i++)
            {
                var r = rects[i];
                layout.Add(new Rect(r[0] - r[2], r[1] - r[3], r[2] * 2, r[3] * 2));
            }
            effect.SetPills(layout, 1, 1, Math.Max(1, radiusPx * 0.55));
            return effect;
        }

        internal static LiquidGlassEffect CreatePills(double radiusPx, double texW, double texH)
            => CreateCore(false, radiusPx, texW, texH);

        public static LiquidGlassEffect CreatePanel(double radiusPx, double texW, double texH)
            => CreateCore(true, radiusPx, texW, texH);

        private static LiquidGlassEffect CreateCore(bool panel, double radiusPx, double texW, double texH)
        {
            ref PixelShader shader = ref (panel ? ref _panelShader : ref _pillsShader);
            ref bool attempted = ref (panel ? ref _panelAttempted : ref _pillsAttempted);
            try
            {
                if (!attempted)
                {
                    attempted = true;
                    string hlsl = BuildHlsl(panel);
                    string target = Environment.GetEnvironmentVariable("YUKI_GLASS_SHADER_PS");
                    if (string.IsNullOrWhiteSpace(target)) target = "ps_3_0";
                    if (target == "ps_3_0" && (!RenderCapability.IsPixelShaderVersionSupported(3, 0)
                        || RenderOptions.ProcessRenderMode == System.Windows.Interop.RenderMode.SoftwareOnly))
                        throw new NotSupportedException("当前 WPF 渲染设备不支持 Pixel Shader 3.0");
                    string path = CachePath(hlsl + "\n// target=" + target);
                    byte[] bytecode = LoadCached(path, target);
                    bool loadedFromCache = bytecode != null;
                    bytecode ??= NativeMethods.CompilePs(hlsl, target);
                    shader = CreateShader(bytecode);
                    if (!loadedFromCache) SaveCached(path, bytecode);
                }
                return shader == null ? null : new LiquidGlassEffect(shader, radiusPx, texW, texH, panel);
            }
            catch (Exception ex)
            {
                shader = null;
                App.Log("液态玻璃着色器不可用,降级为普通玻璃: " + ex);
                return null;
            }
        }

        private static PixelShader CreateShader(byte[] bytecode)
        {
            var shader = new PixelShader();
            using var stream = new MemoryStream(bytecode);
            shader.SetStreamSource(stream);
            shader.Freeze();
            return shader;
        }

        private static string BuildHlsl(bool panel)
        {
            var sb = new StringBuilder(@"
sampler2D Input : register(s1);
sampler2D Sharp : register(s2);
float pRefr : register(c0);
float pRim : register(c1);
float pChroma : register(c2);
float pRadius : register(c3);
float pTexW : register(c4);
float pTexH : register(c5);
float pSheen : register(c6);
float4 pSharpUv : register(c7);
float4 pTint : register(c8);
float pEdgeWidth : register(c9);
");
            sb.AppendLine("float pLift : register(c11);");
            if (panel)
            {
                sb.AppendLine(@"
float field(float2 p)
{
    float2 halfSize = float2(pTexW, pTexH) * 0.5;
    float radius = min(pRadius, min(halfSize.x, halfSize.y));
    float2 q = abs(p - halfSize) - halfSize + radius;
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - radius;
}");
            }
            else
            {
                sb.AppendLine("sampler2D PillGeometry : register(s3);");
            }
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
");
            if (panel)
            {
                sb.AppendLine(@"    float2 px = uv * tex;
    float d = field(px);
    // 按纹理像素校正屏幕导数，面板非等比缩放时法线仍在采样坐标系。
    float2 stepPx = max(abs(float2(ddx(px.x), ddy(px.y))), 1e-4);
    float2 g = float2(ddx(d), ddy(d)) / stepPx;
    float2 n = g / (length(g) + 1e-4);");
            }
            else
            {
                sb.AppendLine(@"    float3 geometry = tex2D(PillGeometry, uv).rgb * 2.0 - 1.0;
    float d = geometry.b * pEdgeWidth;
    float2 n = geometry.rg / (length(geometry.rg) + 1e-4);");
            }
            sb.AppendLine(@"
    float edge = 1.0 - saturate(abs(d) / pEdgeWidth);
    float bevel = edge * edge;
    float2 disp = n * pRefr * bevel * pEdgeWidth * 2.45;
    float ca = pChroma * bevel * 0.055;
    float3 body = tex2D(Input, uv).rgb;
    float2 sharpUv = uv * pSharpUv.xy + pSharpUv.zw;
    float2 delta = disp / tex * pSharpUv.xy;
    float3 col = body;

    // 光学作用集中在边缘，居中的文字和控件由柔和的底图衬托。
    [branch] if (bevel * pRefr > 0.005)
    {
        float3 lens = tex2Dlod(Sharp, float4(saturate(sharpUv + delta), 0.0, 0.0)).rgb;
        [branch] if (pChroma > 0.001)
        {
            lens.r = tex2Dlod(Sharp, float4(saturate(sharpUv + delta * (1.0 + ca)), 0.0, 0.0)).r;
            lens.b = tex2Dlod(Sharp, float4(saturate(sharpUv + delta * (1.0 - ca)), 0.0, 0.0)).b;
        }
        col = lerp(col, lens, bevel * saturate(pRefr * 1.55));
    }
    float dotL = -(n.x * 0.55 + n.y * 0.83);
    float lit = max(dotL, 0.0);
    float back = max(-dotL, 0.0);
    float rim = pRim * edge * bevel * saturate(-d / pEdgeWidth + 0.5);
    float dark = 1.0 - dot(col, float3(0.2126, 0.7152, 0.0722));
    col = lerp(col, 1.0, pLift * dark * dark);
    col *= 1.0 - pSheen * bevel * back * 0.035;
    col = lerp(col, 1.0, saturate(rim * (0.24 + 0.30 * lit) + pSheen * bevel * lit * 0.18));
    col = lerp(col, pTint.rgb, pTint.a);
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
                case "2": case "3": case "4": case "5": case "6": case "7": case "8": break;
                default: mode = "0"; break;
            }
            return sb.ToString().Replace("DEBUG_MODE", mode);
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

        private static byte[] LoadCached(string path, string target)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bytes = File.ReadAllBytes(path);
                uint version = target == "ps_3_0" ? 0xFFFF0300u : 0xFFFF0200u;
                return bytes.Length >= 8 && bytes.Length % 4 == 0 && BitConverter.ToUInt32(bytes, 0) == version
                    && BitConverter.ToUInt32(bytes, bytes.Length - 4) == 0x0000FFFF ? bytes : null;
            }
            catch { return null; }
        }

        private static void SaveCached(string path, byte[] bytecode)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(temporary, bytecode);
                File.Move(temporary, path, overwrite: true);
            }
            catch { }
            finally
            {
                try { File.Delete(temporary); } catch { }
            }
        }
    }
}
