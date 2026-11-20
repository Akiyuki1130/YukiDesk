# AGENTS.md — YukiDesk 项目初始化梗概

> 面向 AI 编码代理与首次接手者的项目速览：先读这里，再按需深入具体文件。
> 用户视角的功能说明看 [`README.md`](README.md)；选型过程看 [`计划书.md`](计划书.md)（**历史文档，部分已过时**）；变更记录看 [`release-notes.md`](release-notes.md)。
> 校准基线：`master` @ `92ba159`，产品版本 `1.0.8`。

## 1. 一句话定位

常驻 **Windows 桌面层**（在所有普通窗口之下、壁纸之上）的 46px 透明"药丸"顶栏，集成设置、电源、此电脑、回收站、小文件夹、时钟/农历、音乐（SMTC）、音频频谱（WASAPI）、音量、性能监测（CPU/GPU/内存/网络）等模块，全部原生 Win32 + 托管代码实现。

## 2. 事实速查

| 项 | 值 |
|---|---|
| 产品名 / 程序集 | YukiDesk / `YukiDesk.exe` |
| **项目文件名** | `DesktopBar.csproj`（历史名，未改名） |
| **根命名空间** | `DesktopBar`（所有 `namespace` 都是 `DesktopBar.*`，新代码必须沿用） |
| 目标框架 | `net8.0-windows10.0.19041.0`，`SupportedOSPlatformVersion 10.0.17763.0` |
| UI 框架 | WPF (`UseWPF`) **且** WinForms (`UseWindowsForms`，仅用于托盘 `NotifyIcon` 与 `Icon`） |
| 语言特性开关 | `Nullable=disable`、`ImplicitUsings=disable`（**不要**假设可空注解可用） |
| 版本号唯一来源 | `DesktopBar.csproj` 的 `<Version>1.0.8</Version>`，安装包名与"应用和功能"都取自它 |
| 架构 | `win-x64;win-arm64` |
| NuGet 依赖 | **零** `PackageReference`，无 `Directory.Build.props`，无 `unsafe`/`AllowUnsafeBlocks` |
| 提权要求 | `asInvoker`（`app.manifest:16`），主进程**从不**提权；唯一提权路径是用户手动装 PawnIO |
| 嵌入资源 | `Resources\PawnIo\AMDFamily17.bin`（AMD Zen SMN 模块，LGPL-2.1） |
| 仓库形态 | 单分支 `master`、无 `.sln`、无 CI、无 LICENSE、20 个提交、标签 `v1.0.0`–`v1.0.8`（跳过 `v1.0.4`） |

## 3. 目录与职责

```
（仓库根目录 == 项目目录，没有 DesktopBar/ 子目录）
App.xaml(.cs)              应用入口：单实例、全局异常、日志、托盘、设置加载、主题启动
MainWindow.cs              顶栏主窗口：桌面层钉底、模块布局组装、显隐控制
SettingsWindow.xaml(.cs)   设置窗口（无 TabControl，左侧 185px 导航 + 右侧 C# 动态构建页面）
DesktopBar.csproj          项目文件（产物名 YukiDesk）
app.manifest               asInvoker + longPathAware；**故意不声明 dpiAware**（WFAC010）
installer.iss              Inno Setup 6 脚本：x64/arm64 × full/slim
packaging.cmd              一键打包（ASCII-only + CRLF，见 .gitattributes）
ChineseSimplified.isl      vendored 的 Inno Setup 简体中文语言包
Models/Settings.cs         AppSettings（唯一可持久化的 POCO）+ 遗留 TabStyle
Services/                  18 个服务类，全部 process-global 静态单例，**无 DI 容器**
Interop/NativeMethods.cs  全部 Win32 P/Invoke（窗口/Z 序/Shell/AppBar/电源/图标/签名/媒体键/GDI 捕获/WDA/D3DCompile/DwmFlush）
Interop/DxgiCapture.cs    GPU 抓屏（DXGI Desktop Duplication，手写 COM 互操作、零 NuGet）：AcquireNextFrame 按合成节拍取帧 → CopySubresourceRegion 只拷顶栏条带 → Map 读回
UI/ModuleBase.cs           模块基类：药丸选项卡 + 独立悬停弹出面板框架
UI/Modules.cs              10 个具体模块 + IconCache + ItemTile + 自绘渲染面（2235 行，见 §5）
UI/LiquidGlassEffect.cs    液态玻璃 ShaderEffect：胶囊从布局纹理采样轮廓，展开页通过寄存器更新圆角几何；d3dcompiler_47 运行时编译 + 按源码 SHA-256 落盘缓存
UI/PillGlassGeometry.cs    布局变化时生成全部可见胶囊的距离和法线纹理，着色器成本不随胶囊数量增长
Services/GlassDiagnostics.cs 玻璃取证/性能自检（--glassshot / --glassperf / --glasssampler），见 §14
UI/*.cs                    ColorEditor / ConfirmDialog / OperationProgressDialog / Prompt / FlyoutMenu / ShellOpen / Ui
docs/                      README 引用的液态玻璃顶栏、展开面板和深浅色设置截图
Assets/YukiDesk.ico        exe 图标 + 安装器 SetupIconFile
```

规模参考：`UI/Modules.cs` 105 KB / 2235 行、`SettingsWindow.xaml.cs` 69 KB / 1365 行、`Services/PerfService.cs` 51 KB / 1065 行、`Interop/NativeMethods.cs` 37 KB、`App.xaml` 35 KB、`UI/ModuleBase.cs` 32 KB、`Services/SpectrumService.cs` 26 KB。**改动前先定位到具体文件，避免整文件重写。**

## 4. 启动链路（`App.xaml.cs:60` → `OnStartup`）

1. 全局异常兜底**最先**注册（`:64`）：`Bar == null` 时重抛并 `Shutdown(1)`，避免"无窗口无托盘却占着单实例互斥体"的僵尸；启动完成后才吞异常。
2. `--tempslave` 分支（`:74`）：无托盘/无主窗/不建互斥体的**提权温度从属进程**，由登录计划任务拉起，写入共享内存 `Global\YukiDeskTctl`，主进程退出后自行结束。
3. 单实例互斥体 `YukiDesk_SingleInstance_Mutex`（`:80`）。
4. `SettingsService.Load` → `AutoStartService.Set` → 清理 `%TEMP%\YukiDeskClipboard` 残留 → 建 `StorageRoot` 与各小文件夹目录 → 无文件夹时seed「常用」。
5. `ThemeService.Start()` → `new MainWindow().Show()` → `BuildTray()` → `UpdateService.StartAutoCheck()`（延迟 25s）→ 后台补齐温度从属进程。
6. `OnExit`（`:153`）是有序拆解，**不要随意改动顺序**：先 `OperationProgressDialog.BeginShutdown()` + `WaitForIdle(5000)` 等在途文件操作收尾，再停服务、还原 AppBar、关托盘与面板。

托盘菜单：打开设置 / 显示·隐藏顶栏 / 打开数据目录 / 退出（`App.xaml.cs:139`）。

## 5. 模块系统

`UI/ModuleBase.cs` 是所有模块的基类（`ModuleBase : UserControl`，`ModuleBase.cs:19`）。每个实例持有：

- `Pill`：38px 高、圆角 19 的胶囊；
- `Panel` + **自己的 `Window PanelWindow`**：`Topmost` + `WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE`，用 `SetWindowRgn` 做圆角。

契约：`HoverOpensPanel`、`HasPanel`、`CenterPanelOnPill`、`OnPanelShown/OnPanelHidden`、`Dispose`；生命周期入口 `ShowPanel` / `HidePanel` / `ClosePanelImmediately`。
**全局只有一个面板能打开**（`static ModuleBase _active`，`:39`）；关闭延迟读 `HoverCloseDelayMs`，动画时长读 `AnimationDurationMs`；`TrackFlyout(Popup)` 用于右键菜单打开期间挂起自动关闭。

`UI/Modules.cs` 中的 10 个模块：

| 类 | 行 | 作用 | 主要依赖 |
|---|---|---|---|
| `SettingsModule` | 26 | 雪花按钮，无面板，点击开设置 | `App.ShowSettings()` |
| `PowerModule` | 39 | 关机/重启/睡眠/锁定/息屏（破坏性操作有确认） | `NativeMethods` 电源 API |
| `ThisPcModule` | 81 | 打开「此电脑」，右键 → `ms-settings:about` | `ShellExecuteOpen`、`FlyoutMenu` |
| `ClockModule` | 106 | 时间/日期/星期/农历/节气，250ms 计时器（隐藏即停） | `Lunar`、`Festivals` |
| `MusicModule` | 145 | 封面 + 跑马灯歌名；控制走**媒体键** | `MediaService` |
| `VolumeModule` | 262 | 点击静音、滚轮 ±4%、滑块；120ms 合并写入 | `VolumeService` |
| `FolderModule` | 442 | 每个 `Settings.Folders` 一项：图标网格、拖入拖出、重命名、粘贴 | `FileSystemWatcher`(300ms 防抖)、`IconCache`、`ItemTile`、`FileClipboard` |
| `RecycleBinModule` | 953 | 回收站枚举、双击还原、永久删除、拖入即删除 | `RecycleBin`、`ItemTile`、`FileClipboard` |
| `SpectrumModule` | 1712 | 胶囊内 16 根频谱柱，`CompositionTarget.Rendering` 自绘 | `SpectrumService` |
| `PerfModule` | 1960 | 一个实例一个指标（`PerfMetric` = Cpu/Gpu/Memory/Network） | `PerfService` |

## 6. 服务层（`Services/`）

| 类 | 职责 | 关键原生依赖 |
|---|---|---|
| `PerfService` | 1s 采样线程；`AddRef`/`ReleaseRef` 引用计数决定是否采样 | `GetSystemTimes`、`GlobalMemoryStatusEx`、PDH、DXGI、WMI、`atiadlxx.dll`、`nvml.dll`、PawnIO |
| `SpectrumService` | WASAPI **环回采集** → 2048 点 FFT（汉宁窗）→ 40Hz–15kHz 对数 16 频段 | `IAudioClient`/`IAudioCaptureClient` |
| `MediaService` | SMTC 全局会话：曲名/歌手/封面/播放状态 | WinRT `GlobalSystemMediaTransportControlsSessionManager` |
| `VolumeService` | 当前默认输出设备的音量/静音 | `IMMDeviceEnumerator`/`IAudioEndpointVolume` |
| `FileClipboard` | Explorer 兼容的 `CF_HDROP` + `Preferred DropEffect`（复制=5/剪切=2） | WPF `Clipboard` |
| `RecycleBin` | 直接解析 `$Recycle.Bin\<SID>\$I` 元数据 | 纯托管 |
| `ThemeService` | 主题色解析、壁纸明暗自动适配、系统偏好监听 | `SystemEvents` |
| `MicaService` | 胶囊自绘材质烘焙（`PillMaterial`：1=云母 2=亚克力，**均已从设置 UI 下架**，仅遗留配置会落到烘焙；采样壁纸→盒式重模糊→压带/保色→染色→噪点→一张冻结位图，后台 STA 烘焙线程） | 注册表壁纸配置 |
| `GlassService` | 实时背景玻璃（`PillMaterial`：3=高斯模糊 4=液态玻璃）。两条抓屏路径：**DXGI Desktop Duplication**（默认，`Interop/DxgiCapture.cs`，0.5ms/帧、按合成节拍取帧）与 GDI BitBlt 兜底（`YUKI_GLASS_GDI=1`，4ms/帧）。捕获线程半分辨率保留锐帧，四分之一分辨率处理主体 + 三趟盒式模糊（O(n)）→ 差分门控（静止 0 写屏）→ 遮挡保护（全屏窗口盖住时 0 采样）→ 发布驱动 UI 泵 WritePixels → 胶囊挂 `LiquidGlassEffect`（模糊帧做主体、未模糊帧做轮廓折射）。悬停面板走**独立实时玻璃管线**（自己的捕获线程 + `Rectangle` 挂同一个 `LiquidGlassEffect`，复用固定捕获区域，动画只按当前可见矩形更新采样坐标）。自家窗口全部 WDA_EXCLUDEFROMCAPTURE 排除自拍 | `IDXGIOutputDuplication`、`ID3D11Device`、`GetDC`/`BitBlt`、`SetWindowDisplayAffinity`、`WindowFromPoint` |
| `SettingsService` | 原子写 + 备份 + 损坏隔离 + 迁移/钳制 | `System.Text.Json` |
| `UpdateService` | GitHub Releases 检查/下载/安装（可选 SHA-256 校验） | `HttpClient` |
| `PawnIoTctl` / `TemperatureSlave` | 读 AMD 原生 Tctl 温度 / 提权从属进程 | `DeviceIoControl`、`MemoryMappedFile` |
| `Festivals` / `Lunar` | 节气节日 / 农历 | `ChineseLunisolarCalendar` |
| `AutoStartService` | 开机自启 | `HKCU\...\Run` |
| `DesktopSpaceService` | Shell AppBar 桌面空间预留 | `SHAppBarMessage` |

## 7. 关键机制（改动前必读）

- **桌面层钉底**：不是 `WorkerW` 挂载（`计划书.md` 里的旧方案已废弃）。实际做法是 `SetWindowPos` 钉在桌面装饰层之上、普通窗口之下，`WS_EX_NOACTIVATE` 保证不抢焦点；**每 3 秒保底重钉**，钉底前会撤掉外部软件误加的置顶标志，遍历时跳过 DWM cloak 的窗口。顶栏被别的窗口盖住是**设计语义**，不是 bug。
- **悬停面板是独立置顶窗口**，不在主窗口内；所以隐藏顶栏前必须 `ClosePanels()`，否则会留下可交互的孤儿窗口。
- **点击穿透**：主窗口 `Background=null` 时鼠标事件穿透到桌面图标；顶栏背景条一旦设置不透明度才会拦截该区域。玻璃档（3/4）下胶囊并集之外 alpha=0（`MainWindow.UpdateGlassClip` 的 GeometryGroup Clip），穿透契约保持；**该函数必须做 Rect 集合比较防抖**——每次给 Clip 赋新 GeometryGroup 都会触发布局失效，不比较会和 LayoutUpdated 形成无限失效循环打满 UI 线程（实测 118%）。
- **实时玻璃（PillMaterial 3/4）**：模糊在捕获线程 CPU 完成（四分之一分辨率盒式三趟），WPF 侧不放 BlurEffect（满宽 BlurEffect 曾被打满一个核，虽有部分原因是上面的失效循环，CPU 模糊仍更可控）；主窗+面板 WDA_EXCLUDEFROMCAPTURE 后**任何外部截图/录屏都看不到顶栏**（取证用 `YUKI_GLASS_DUMP=<目录>` 环境变量，进程内转储发布帧+主窗 RTB）；顶栏被盖住时捕获自动暂停，这是设计不是卡死。
- **GPU 抓屏（DXGI）**：COM 方法按 SDK 定义的槽位和签名调用（例如 `ID3D11Device::CreateTexture2D` 为 slot 5，`IDXGIOutputDuplication::GetDesc` 返回 `void`）。结构体出参用按 `Marshal.SizeOf` 分配的原生缓冲，`DXGI_OUTDUPL_FRAME_INFO` 为 48 字节，`DXGI_OUTDUPL_DESC` 为 36 字节。暂存纹理的读权限为 `0x20000`。更新门控检查脏矩形和移动区域，切换显示设置重建会话。进程退出时由系统回收最后一个 DXGI 会话，避免驱动 Release 阻塞退出。
- **玻璃节奏**：GlassMaxFps 为 0 时使用自动节奏，顶栏和面板上限分别为 48 / 36 fps。正数使用手动上限，静止继续按最近一次发布的时间降频。液态玻璃提供清透、柔雾和自定义外观；前两档会按采样背景选择前景颜色。
- **主题**：`ModuleBase.ApplyTheme` 与 `ThemeService.Refresh` **都会重写** `Application.Current.Resources["ThemePanel*"]`，模块一多就有两个写入方；设置窗口/对话框另用一套冻结的 `Settings*` 键，不跟随运行时主题切换。
- **线程模型**：Shell 图标提取在专用 STA 线程队列（`IconCache`），FDT/解码/文件 IO 全在后台线程，UI 线程只做布局与绘制。退出时必须等在途文件操作收尾，否则会留下半份文件。
- **设置持久化**：`%AppData%\YukiDesk\settings.json`（Roaming），`settings.backup.json` + 随机 `.tmp-<guid>` + `File.Replace` 原子写；旧路径 `%AppData%\DesktopBar\settings.json` 会迁移。`SchemaVersion` 当前为 2。损坏时隔离为 `*.corrupt-<时间戳>` 并**拒绝**用默认值覆写。
- **错误处理风格**：大量 `catch { }` 静默兜底以保证顶栏存活；配置写盘失败是少数会弹窗提示的例外（`App.ShowConfigSaveFailure`）。

## 8. 构建 / 打包 / 发布

```bash
# 开发构建（在仓库根目录，不要 cd 到不存在的子目录）
dotnet build -c Release
bin/Release/net8.0-windows10.0.19041.0/YukiDesk.exe

# 一键出 4 个安装包（需要 .NET 8 SDK + Inno Setup 6）
packaging.cmd
```

- 前置：**.NET 8 SDK**（构建）、**Inno Setup 6**（`winget install JRSoftware.InnoSetup`）、仅运行需 .NET 8 Desktop Runtime。
- 已验证：用 .NET SDK 8.0.426 执行 `dotnet build -c Release` 成功，**0 警告 0 错误**，产物 `bin\Release\net8.0-windows10.0.19041.0\YukiDesk.exe`。仓库无 `.sln`，直接打开 `DesktopBar.csproj`。
- `packaging.cmd` 对 `x64`/`arm64` 分别 publish 两遍：`--self-contained true`（full）与 `false`（slim），输出到 `publish\win-<arch>[-slim]\`，再调 `ISCC /DARCH=<arch> /DFLAVOR=<flavor> installer.iss`，最后写 `installers\SHA256SUMS-<ver>.txt` 并清理旧版本安装包。
- 产物：`installers\YukiDesk-Setup-{x64,arm64}-{full,slim}-<版本>.exe`。
- 安装器是**每用户、`PrivilegesRequired=lowest`、无 `[Registry]` 段**；安装目录 `%LocalAppData%\Programs\YukiDesk`；卸载会清理登录计划任务 `YukiDeskTctlSlave`、`HKCU\...\Run` 值、`%TEMP%` 残留（但**不**卸载 PawnIO 本体）。
- 发布时需把 4 个安装包 + `SHA256SUMS-<ver>.txt` 一起上传到 GitHub Release。
- `packaging.cmd` 必须保持 **ASCII-only + CRLF**（`.gitattributes` 强制），`cmd.exe` 会误解析 LF-only 批处理。

## 9. 运行时数据位置

| 内容 | 路径 |
|---|---|
| 配置 | `%AppData%\YukiDesk\settings.json` |
| 配置备份 / 损坏隔离 | `settings.backup.json` / `settings.json.corrupt-<时间戳>` |
| 日志 | `%AppData%\YukiDesk\log.txt`（2MB 轮转，跨进程用 `Local\YukiDesk_Log_Mutex` 串行化） |
| 小文件夹 | `%LocalAppData%\YukiDesk\MiniFolders\<名称>\`（可用 `StorageRoot` 改） |
| 更新临时目录 | `%TEMP%\YukiDeskUpdate` |
| 安装目录 | `%LocalAppData%\Programs\YukiDesk` |

## 10. 常见改动的完整链路

**新增/修改一个设置项**（漏一处就会出现"界面能改但不生效"）：
1. `Models/Settings.cs` 加字段并给默认值；
2. `Services/SettingsService.Normalize` 加钳制/校验（数值一律 `Clamp`，颜色走 `NormalizeColor`）；
3. `SettingsWindow.xaml.cs` 对应 `Build*Page` 加控件，变更回调里调 `Save()`；
4. `Save()` → `App.ApplyAndSave()` → `SettingsService.Save` + `MainWindow.ApplySettings()`（触发整条顶栏重建）；
5. 若只是显隐，用 `MainWindow.ApplyModuleVisibility()` 路径，**避免**全量重建导致胶囊闪烁。

**新增一个模块**：在 `UI/Modules.cs` 派生 `ModuleBase` → 在 `MainWindow.BuildLayout` 插到左/右 `StackPanel` 并保存字段 → 在 `ApplyModuleVisibility` 接显隐开关 → 在 `DisposeModules` 清引用 → 在 `Settings.cs` 加 `Show*` → 在设置页「模块显示」加开关。

**改主题/外观**：`ThemeService` + `ModuleBase.ApplyTheme` + `App.xaml` 资源键三处保持同步；注意 `Settings*` 与 `Theme*` 是两套键。

## 11. 代码约定

- **命名空间固定 `DesktopBar.*`**，即使产品叫 YukiDesk；`README.md:3` 明确 `DesktopBar` 是弃用旧名，新代码/文案/启动项不要再用它，但项目文件名与命名空间暂未迁移。
- 提交信息用**英文 Conventional Commits**：`feat:` / `fix:` / `chore:` / `docs:` / `ui:`，主题行简短，无正文。
- 注释与文档以**中文**为主，倾向解释"为什么这么写"（历史 bug、平台坑）。
- 不使用可空引用类型语法；不使用 NuGet 包，新依赖需慎重（当前唯一第三方是可选的内核组件 PawnIO）。

## 12. 已知陷阱与风险（代码评审所得，非运行时复现）

1. ~~**更新校验清单文件名不匹配**~~ **【已修复 2026-10-24】**：`packaging.cmd` 现以 3 段 tag 版本命名清单（`SHA256SUMS-1.0.9.txt`，存量更新器只认这个名），`UpdateService.FindChecksumAsset` 同时回退匹配 4 段名（`SHA256SUMS-1.0.8.0.txt`，历史 Release 已带这种名）。注意：修复只对"新版本更新器 + 新发布清单"组合完整生效；存量用户从旧版本更新到下一个版本时校验仍会跳过（旧代码无回退），属预期过渡态。
2. ~~下载的安装包**未做 Authenticode 校验**（只有 PawnIO 安装包做了，`SettingsWindow.xaml.cs:1174`）~~ **【已修复 2026-10-24】**：`NativeMethods.GetAuthenticodeStatus` 返回原始状态串（PawnIO 的 `VerifyAuthenticodeSignature` 改为其上仅认 `Valid` 的薄封装，行为不变）；`UpdateService.VerifySignatureAsync` 在下载后做**分级闸门**——当前发布管线无签名步骤，`NotSigned` 是常态只能记录放行（硬拦会把更新全砖），`HashMismatch`（签名后内容被篡改）硬拦截并删包，其余状态/查询失败（null）只记录。完整性主闸仍是 SHA-256 清单比对。
3. ~~`thisPc` 模块没有 `ShowThisPc` 开关~~ **【已修复 2026-10-24】**：`Settings.ShowThisPc`（默认 true）→ `MainWindow.ApplyModuleVisibility` → 设置页「模块显示·此电脑」开关已接通，README 模块清单也已补上。
4. 无磁盘 IO 监测，尽管叫"性能监测"；四个胶囊只有 CPU/GPU/内存/网络。
5. `ModuleBase._active` 是跨所有模块的静态单面板锁；`MainWindow._modules` 与 `_folderModules`、单独字段需手工同步，容易泄漏。
6. ~~`UI/Modules.cs` 有 8 个 `async void`（异常不可观测，仅靠 `_disposed`/版本号守卫）；`FolderModule.MakeItem` 与 `RecycleBinModule.MakeRecycleItem` 是约 150 行的近似复制~~ **【已修复 2026-10-24】**：8 个 `async void` 全部补了兜底 `catch`——用户主动触发的文件操作（粘贴/删除/还原/永久删除/复制/清空）记日志并弹提示，面板刷新（`LoadFiles`/`LoadItems`）只记日志、`LoadFiles` 同时置脏标记。重复段抽成共享 `ItemTile`（图标+名称+圆角底+悬停/按压反馈+拖拽起点记录，约 70 行构建代码单一来源），两模块只保留真正差异（拖出内容、双击行为、右键菜单、提示文字）；`RecycleBinModule.AnimateItemBack` 已删除，统一走 `ItemTile.AnimateBack`。
7. 顶栏设置项多数走"每次变更即写盘 + 整栏重建"，对单个复选框偏重。
8. ~~`app.manifest` 的 `assemblyIdentity version` 仍是 `1.0.0.0`~~ **【已修复 2026-10-24】**：已同步为 `1.0.8.0`；发版时需随 `<Version>` 手动更新。
9. `installer.iss:32` 的 AppId 最后一段是 **13 位十六进制**（正常 12 位），虽能编译但形态可疑；改这里会导致升级标识变化。
10. ~~`README.md` 的 `cd DesktopBar` / `cd YukiDesk` 不存在~~ **【已修复 2026-10-24】**：两处已删并注明根目录即项目；README 已链接 `release-notes.md`（其无版本/日期标题的格式问题仍在，改动属维护者文案决策）。
11. `计划书.md` 已过时（WorkerW 方案、`%AppData%\DesktopBar`、音乐进度条），文中自称历史文档，**以 README 与源码为准**。
12. **【已排查：非代码缺陷】音量模块在部分机器上"设置成功但实际不变"**：判据是 CoreAudio 写入返回 `S_OK` 且回读一致、而扬声器实际音量不动。根因在渲染链路**下游的 APO/驱动施加了自己的增益**，不在 `VolumeService`。实测案例：一台装有 Equalizer APO（已启用）+ Nahimic（界面显示未启用，但 `NahimicService`/`NahimicSvc64`/`NahimicSvc32`/`NahimicAPO4Volume` 四进程仍在运行，其中 `NahimicAPO4Volume` 是注册在 Realtek 扬声器路径上的音量 APO）的机器上，Realtek 扬声器端点主音量在 100%/70%/40%/20%/5% 五档下输出峰值恒为 0.5714（比值 1.00x），而耳机端点正常。**处理方向是让用户清理 APO 链（卸 Nahimic、停 Equalizer APO、关 Realtek 音频增强），不要改 `VolumeService`。** 复现判定时用静音开关做对照即可，**切勿播放测试音**——会直接吵到用户。

## 13. 当前缺口

- **无 CI**：构建、打包、校验和生成、上传全手动，唯一防陈旧保护是 `packaging.cmd:68`。
- **无全局热键**：全库无 `RegisterHotKey` / `WM_HOTKEY`。顶栏按设计位于所有普通窗口之下，一旦被全屏或置顶应用（典型如全屏游戏）盖住，**所有模块连同音量一起不可达**，且没有任何替代操作手段——这是「顶栏在某台机器上完全点不到」类问题的第一嫌疑，先查谁盖住了屏幕，不要先改代码。
- **无 LICENSE**：`AMDFamily17.bin`（LGPL-2.1）与 PawnIO（GPL-2.0）已随分发，仓库却无许可证文件。
- **无 `.sln`**，IDE 需直接打开 `DesktopBar.csproj`。
- 版本已到 `1.0.8`，但 `v1.0.8` 之后还有 3 个未打标签的提交（`master` 领先于发布版本）。

## 14. 调试技巧

- 频谱自检：环境变量 `YUKI_SPECTRUM_SELFTEST=1`（并可用 `YUKI_SPECTRUM_DEBUG=1`）输出采集诊断。
- 提权温度从属进程可手动验证：`YukiDesk.exe --tempslave`（正常情况由计划任务 `YukiDeskTctlSlave` 以最高权限在登录时拉起）。
- 出问题先看 `%AppData%\YukiDesk\log.txt`；启动阶段异常会记"未处理异常"或"后台线程致命异常"。
- **排查"点了没反应"**：不要先改代码。用 `WindowFromPoint(x,y)` 问 Windows 该坐标的点击会落到哪个窗口——顶栏按设计在所有普通窗口之下，任何全屏/置顶应用都会让它整条不可达。另外做这类探针时**必须先把探针进程设为 PerMonitorV2 DPI 感知**（`SetProcessDpiAwarenessContext(-4)`），否则拿到的屏幕尺寸和窗口 rect 都是虚拟化后的值（例如 1707 而非 2560），坐标会整体错位，得出完全错误的结论。

### 玻璃自检模式（命令行，全部在 `Services/GlassDiagnostics.cs`）

```bash
# 取证：装一张测试壁纸（顶部强对比条纹 + 棋盘 + 大字）→ 抓顶栏 + 逐个模块的面板 → 还原壁纸
YukiDesk.exe --glassshot "<输出目录>" [--nopanels] [--nobg] [--only=<模块名片段>]

# 性能：正常跑顶栏，量 12 秒的单核占用 / 捕获帧 / 发布 / 各线程 / 分阶段 / 延迟，写 log 后退出
YukiDesk.exe --glassperf 12

# 着色器自检（无界面）：用近似胶囊几何反复编译 HLSL，把成败与耗时写日志
YukiDesk.exe --glasstest
```

- **这三个开关都排在单实例互斥体之后**——已经有 YukiDesk 在跑时会**静默退出、无产物**。先 `taskkill /F /IM YukiDesk.exe`。
- **连续跑自检要间隔 ≥6 秒**，否则上一个实例还占着 exe/互斥体，后一次会以 `exit 127/139` 静默失败（127 是 bash 无法 exec，不是应用崩溃；事件日志里也查不到 Application Error）。
- `--glassshot` 的产物：`shot-bar-original`/`shot-bar-test`（全屏截图）、`glass-input-test.png`（发布帧，四分之一分辨率）、`rtb-*`（窗口内 RenderTargetBitmap，不含 DWM）、`shot-panel-<模块>`（每块面板）、`wallpaper-test.png`。
- **顶栏在外部截图里看不见是正常的**：主窗与面板都设了 `WDA_EXCLUDEFROMCAPTURE`，凡是从进程外抓屏都拍不到顶栏自己。要拍顶栏必须走进程内（`ShootAsync` 里的 `SaveRtb` 或 `YUKI_GLASS_DUMP` 转储）。

### 玻璃与面板相关的环境变量（都是诊断/兜底，正常运行时不需要）

| 变量 | 作用 |
|---|---|
| `YUKI_GLASS_GDI=1` | 强制 GDI BitBlt 抓屏，跳过 DXGI（A/B 对照、GPU 路径出问题时的兜底） |
| `YUKI_GLASS_ALWAYS_PUB=1` | 绕过差分门控，每帧都发布——用来复现「顶栏背后一直在动」的最坏 CPU |
| `YUKI_GLASS_TESTSRC=1` | 不抓屏，往位图里填合成图案（白/红/绿/蓝分块），用来判断「采样通道坏了」还是「背景本来就糊」 |
| `YUKI_GLASS_FILL=<0..1>` | 覆写「间隙玻璃」比例（0=只有胶囊是玻璃、胶囊间可点击穿透） |
| `YUKI_GLASS_DUMP=<目录>` | 进程内转储发布帧（`glass-input.png`）与主窗 RTB（`glass-window.png`） |
| `YUKI_GLASS_SHADER_DEBUG=<1..10>` | 液态玻璃着色器调试档：1=直通、2=edge 灰度、3=uv、6=alpha 灰度、9=常量红、10=常量红半透明…… |
| `YUKI_GLASS_SHADER_PS=ps_2_0` | 换着色器目标版本（默认 `ps_3_0`；换版本会自动失效字节码缓存） |
| `YUKI_LIQUID_SHEEN=<0..2>` | 斜面明暗强度（默认 1.0） |
| `YUKI_NO_REPIN=1` | 不启动每 3 秒的钉底保底（量化它到底花多少 CPU） |
| `YUKI_PANEL_SHOWLOG=1` | 面板显形瞬间记一行「不透明度/变换/玻璃层状态」，查黑闪类问题 |
| `YUKI_SHOT_PANEL_DELAY=<ms>` / `YUKI_SHOT_GRAB_DELAY=<ms>` | 覆盖取证时「等面板动画结束」/「等 DWM 合成」的固定延时 |
| `YUKI_PERF_PANEL=<模块片段>` | `--glassperf` 期间保持一个面板打开，量面板玻璃的代价 |
| `YUKI_PERF_HIDE=1` / `YUKI_PERF_EMPTY=1` / `YUKI_PERF_STRIP=<a,b>` | 性能归因：隐藏窗口 / 清空 `Bar.Content` / 逐部件剥离（`glass`、`bg`、`pills`、`onlysettings`、`solo:<片段>`） |

### 几件容易被误判的事

- **「液态玻璃没效果」先看日志有没有 `液态玻璃:胶囊着色器已启用`**。HLSL 编不过会**静默降级成普通模糊**，C# 侧一切正常、只是没折射。死分支也会参与类型检查，改 HLSL 时任何一个分支写错整份着色器都编不过。
- **胶囊/面板的折射靠「未模糊帧」做轮廓**：主体用模糊帧（保证"模糊其下一切"），只有轮廓那一圈从 `Sharp` 采样源取未模糊像素。所以"胶囊里看到的背景比面板更清楚"不是 bug。
- **面板的不透明度必须接近 1**：`Panel` 的玻璃是 `ImageBrush(捕获糊图)` 叠在**真实桌面**上，`GlassOpacity` 调低会让背后清晰画面透上来，观感就是"面板什么也没有"。默认已改成 1.0。
- **`--glassperf` 的延迟统计**：顶栏与面板分别记录从捕获开始到位图写入后的首次 WPF 渲染回调的 P50/P95，附样本数。面板区间另统计采样、模糊、位图上传和缓存显形次数。
- **单次 `--glassperf 12` 的进程 CPU 抖动可达 2 倍**（同一配置两次能差一倍）。小于 1% 的差异不可信，只能靠「同一轮里逐部件剥离对比」或 30~40 秒长窗口下结论。
