using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DesktopBar.UI;

namespace DesktopBar.Services
{
    /// <summary>一条可用更新:版本号、发布说明和匹配当前架构/运行时类型的安装包。</summary>
    public sealed class UpdateInfo
    {
        public string Version { get; init; }
        public string Notes { get; init; }
        public string DownloadUrl { get; init; }
        public string FileName { get; init; }
        /// <summary>Release 内 SHA256SUMS 文件的下载地址;缺失时跳过完整性校验(向后兼容旧 Release)。</summary>
        public string ChecksumUrl { get; init; }
    }

    /// <summary>
    /// 通过 GitHub Releases 实现的更新检查与下载。
    /// 检查走 api.github.com,下载走 release 资源;HttpClient 默认读取系统代理
    /// (WinINET/WinHTTP,含 PAC 自动发现),用户开了系统代理就会自动走代理,
    /// 这里只为需要认证的系统代理补上当前用户凭据。
    /// </summary>
    public static class UpdateService
    {
        private const string Repo = "Akiyuki1130/YukiDesk";
        private const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";

        /// <summary>当前程序版本。必须先于 Http 初始化。</summary>
        public static Version CurrentVersion { get; } = Normalize(
            typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0));

        /// <summary>是否为自带 .NET 运行时的发布(full);优先读取打包标记，兼容旧包再检查 hostfxr.dll。</summary>
        public static bool IsSelfContained => DetectSelfContained();
        private const string RuntimeMarker = "yukidesk.runtime";

        private static bool DetectSelfContained()
        {
            try
            {
                string marker = Path.Combine(AppContext.BaseDirectory, RuntimeMarker);
                if (File.Exists(marker))
                {
                    string value = File.ReadAllText(marker).Trim();
                    if (string.Equals(value, "slim", StringComparison.OrdinalIgnoreCase)) return false;
                    if (string.Equals(value, "full", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "hostfxr.dll"));
        }

        /// <summary>当前设备架构标签(更新资源按架构命名)。</summary>
        public static string ArchTag =>
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        private static readonly HttpClient Http = CreateHttp();
        private static DispatcherTimer _autoTimer;
        private static int _busy;
        private static int _autoCheckRunning;

        /// <summary>是否正在进行检查/下载。</summary>
        public static bool Busy => Volatile.Read(ref _busy) != 0;

        private static Version Normalize(Version v)
            => new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        private static string CurrentVersionLabel => CurrentVersion.ToString(CurrentVersion.Revision == 0 ? 3 : 4);

        private static HttpClient CreateHttp()
        {
            var handler = new HttpClientHandler();
            try
            {
                // 默认 UseProxy=true 且 Proxy=系统代理;这里只补凭据,不改变代理来源。
                if (handler.Proxy != null) handler.Proxy.Credentials = System.Net.CredentialCache.DefaultCredentials;
            }
            catch { }
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("YukiDesk/" + CurrentVersionLabel);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        // ---------- 检查 ----------

        /// <summary>查询最新 Release;无更新返回 null,网络/解析失败抛异常。</summary>
        public static async Task<UpdateInfo> CheckLatestAsync(CancellationToken ct)
        {
            using var doc = await ReadReleaseAsync(ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.GetProperty("prerelease").GetBoolean() || root.GetProperty("draft").GetBoolean())
                return null;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (Normalize(latest) <= CurrentVersion) return null;

            // 直接用 tag 文本匹配资产名:ToString(3) 会把 4 段版本 v1.0.8.1 截成 1.0.8 而失配。
            string version = tag.TrimStart('v', 'V');
            string flavor = IsSelfContained ? "full" : "slim";
            var asset = FindAsset(root, ArchTag, flavor, version);
            if (asset == null && !IsSelfContained)
            {
                // slim 版没有专用安装包时退回同版本 full 版（自带运行时）。
                asset = FindAsset(root, ArchTag, "full", version);
            }
            if (asset == null)
                throw new InvalidOperationException("发行版中没有适用于 " + ArchTag + " / " + flavor + " / v" + version + " 的安装包");

            return new UpdateInfo
            {
                Version = version,
                Notes = root.TryGetProperty("body", out var notes) ? notes.GetString() ?? "" : "",
                DownloadUrl = asset.Value.Url,
                FileName = asset.Value.Name,
                ChecksumUrl = FindChecksumAsset(root, version, ArchTag)
            };
        }

        internal static async Task<string> ReadReleaseSummaryAsync(CancellationToken cancellationToken)
        {
            using var doc = await ReadReleaseAsync(cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "未知";
            string version = tag.TrimStart('v', 'V');
            string manifest = FindChecksumAsset(root, version, ArchTag) != null ? "发行版附带 SHA-256 清单" : "发行版未附带 SHA-256 清单";
            return $"本机  v{CurrentVersionLabel} · {ArchTag} · {(IsSelfContained ? "完整包" : "精简包")}\n"
                + $"GitHub 正式版  {tag}\n{manifest}";
        }

        private static async Task<JsonDocument> ReadReleaseAsync(CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await Http.GetAsync(ApiUrl, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return JsonDocument.Parse(json);
        }

        private static string FindChecksumAsset(JsonElement root, string version, string arch)
        {
            if (!Version.TryParse(version, out var parsed)) return null;
            var normalized = Normalize(parsed);
            string assembly = normalized.ToString(4);
            string tag = normalized.ToString(3);
            // Older packages use the assembly version; single-architecture packs add the architecture.
            string[] names =
            {
                $"SHA256SUMS-{assembly}-{arch}.txt", $"SHA256SUMS-{tag}-{arch}.txt",
                $"SHA256SUMS-{assembly}.txt", $"SHA256SUMS-{tag}.txt"
            };
            string bestUrl = null;
            int bestMatch = names.Length;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                int match = Array.FindIndex(names, expected => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase));
                if (match < 0 || match >= bestMatch) continue;
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (IsReleaseAssetUrl(url, name))
                {
                    bestMatch = match;
                    bestUrl = url;
                }
            }
            return bestUrl;
        }

        private static (string Name, string Url)? FindAsset(JsonElement root,
            string arch, string flavor, string version)
        {
            string prefix = "YukiDesk-Setup-" + arch + "-" + flavor + "-";
            string expectedShort = prefix + version + ".exe";
            string expectedAssembly = prefix + version + ".0.exe";
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                bool versionMatch = string.Equals(name, expectedShort, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, expectedAssembly, StringComparison.OrdinalIgnoreCase);
                if (versionMatch && IsReleaseAssetUrl(url, name))
                    return (name, url);
            }
            return null;
        }

        private static bool IsReleaseAssetUrl(string url, string name)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                return false;
            string path = uri.AbsolutePath;
            return path.StartsWith("/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/" + Uri.EscapeDataString(name), StringComparison.OrdinalIgnoreCase);
        }

        // ---------- 下载 ----------

        /// <summary>下载更新安装包到临时目录,返回文件完整路径。</summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
        {
            string dir = Path.Combine(Path.GetTempPath(), "YukiDeskUpdate");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "YukiDesk-Update-" + Guid.NewGuid().ToString("N") + ".exe");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(15));
            using var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            try
            {
                await using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, useAsync: true);
                var buffer = new byte[64 * 1024];
                long readTotal = 0;
                int lastPercent = -1;
                int read;
                while ((read = await src.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
                    readTotal += read;
                    if (progress == null || total is not > 0) continue;
                    int percent = (int)(readTotal * 100.0 / total.Value);
                    if (percent == lastPercent) continue;
                    lastPercent = percent;
                    progress.Report(percent);
                }
                return target;
            }
            catch
            {
                DeleteDownloadedInstaller(target);
                throw;
            }
        }

        private static void DeleteDownloadedInstaller(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) { App.Log("更新: 清理临时安装包失败 " + ex.Message); }
        }

        // ---------- 安装 ----------

        /// <summary>启动已下载的安装程序并退出 YukiDesk(每用户安装,无需管理员)。</summary>
        public static async Task<bool> InstallAndRestartAsync(string setupPath)
        {
            if (!await Task.Run(() => TryStartInstaller(setupPath))) return false;
            var app = Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted) return true;
            await app.Dispatcher.InvokeAsync(() =>
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    app.Shutdown();
                };
                timer.Start();
            });
            return true;
        }

        private static bool TryStartInstaller(string setupPath)
        {
            if (string.IsNullOrEmpty(setupPath) || !File.Exists(setupPath)) return false;
            App.Log("更新: 启动安装程序 " + setupPath);
            try
            {
                using var proc = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(setupPath) { UseShellExecute = true });
                if (proc == null) return false;
                try { proc.WaitForInputIdle(4000); } catch { }
                if (!proc.WaitForExit(150)) return true;
                App.Log("更新: 安装程序启动后立即退出 code=" + proc.ExitCode + ",YukiDesk 继续运行");
            }
            catch (Exception ex)
            {
                App.Log("更新: 安装程序启动失败: " + ex.Message);
            }
            return false;
        }

        /// <summary>Release 提供 SHA256SUMS-<版本>.txt 时比对下载包哈希;缺失时仅记录并放行(旧 Release 兼容)。</summary>
        private static async Task VerifyChecksumAsync(string installerPath, UpdateInfo info)
        {
            if (string.IsNullOrEmpty(info.ChecksumUrl))
            {
                App.Log("更新: Release 未提供 SHA256SUMS,跳过完整性校验");
                return;
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string text = await Http.GetStringAsync(info.ChecksumUrl, cts.Token).ConfigureAwait(false);
            string expected = ParseChecksum(text, info.FileName);
            if (expected == null)
                throw new InvalidOperationException("校验清单缺少安装包的有效 SHA-256 条目: " + info.FileName);
            await using var fs = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = await System.Security.Cryptography.SHA256.HashDataAsync(fs, cts.Token).ConfigureAwait(false);
            string actual = Convert.ToHexString(digest).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SHA-256 不匹配(期望 " + expected + ",实际 " + actual + ")");
            App.Log("更新: 安装包 SHA-256 校验通过");
        }

        /// <summary>解析 sha256sum 格式("<哈希>  <文件名>")的清单,返回目标文件的哈希。</summary>
        private static string ParseChecksum(string text, string fileName)
        {
            string found = null;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim().TrimStart('\uFEFF');
                int sep = line.IndexOfAny(new[] { ' ', '\t' });
                if (sep <= 0) continue;
                string hash = line.Substring(0, sep).Trim();
                string name = line.Substring(sep + 1).Trim().TrimStart('*');
                if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)
                    || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) continue;
                hash = hash.ToLowerInvariant();
                if (found != null && found != hash) return null;
                found = hash;
            }
            return found;
        }

        /// <summary>
        /// 安装包 Authenticode 分级闸门:当前发布管线没有代码签名步骤,NotSigned 是常态,
        /// 硬拦会把所有更新变砖,只能记录放行;一旦未来某个包带上了签名,HashMismatch
        /// (签名后内容被篡改/损坏)就是明确的攻击/损坏信号,必须拦截。
        /// 其余状态(NotTrusted/UnknownError/查询失败返回 null)在无签名发布体系下无法
        /// 区分环境问题与攻击,同样只记录;完整性主闸是上面的 SHA-256 清单比对。
        /// 查询要起一个 PowerShell 进程,放后台线程避免冻结 UI。
        /// </summary>
        private static async Task VerifySignatureAsync(string installerPath)
        {
            string sig = await Task.Run(() => Interop.NativeMethods.GetAuthenticodeStatus(installerPath));
            if (sig == null)
            {
                App.Log("更新: 无法判定安装包签名状态(记录放行)");
                return;
            }
            App.Log("更新: 安装包签名状态 " + sig);
            if (string.Equals(sig, "HashMismatch", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装包带有签名但内容与签名不符,可能已被篡改");
        }

        // ---------- 高层入口 ----------

        /// <summary>
        /// 一次完整的"检查 → 询问 → 下载 → 询问安装"流程,状态文本通过 status 回调上屏。
        /// autoDownload=true 时跳过第一步询问(用于"自动下载更新"开关)。
        /// </summary>
        public static async Task RunCheckAsync(Action<string> status, bool autoDownload, UpdateInfo preChecked = null)
        {
            if (Interlocked.Exchange(ref _busy, 1) != 0) return;
            try
            {
                UpdateInfo info = preChecked;
                if (info == null)
                {
                    status?.Invoke("正在检查更新…");
                    try { info = await CheckLatestAsync(CancellationToken.None); }
                    catch (Exception ex) { status?.Invoke("检查更新失败: " + ex.Message); return; }
                }

                if (info == null) { status?.Invoke("当前已是最新版本 (v" + CurrentVersionLabel + ")"); return; }
                status?.Invoke("发现新版本 v" + info.Version);

                if (!autoDownload)
                {
                    if (!ConfirmDialog.Show("YukiDesk 更新",
                        "发现新版本 v" + info.Version + "(当前 v" + CurrentVersionLabel + ")。\n\n是否立即下载并安装?", "下载并安装"))
                    {
                        status?.Invoke("已取消。");
                        return;
                    }
                }

                string path;
                int downloading = 1;
                try
                {
                    status?.Invoke("正在下载 v" + info.Version + "…");
                    var progress = status == null ? null : new Progress<double>(p =>
                    {
                        if (Volatile.Read(ref downloading) != 0) status("正在下载 v" + info.Version + "… " + Math.Round(p) + "%");
                    });
                    path = await DownloadAsync(info, progress, CancellationToken.None);
                }
                catch (Exception ex) { status?.Invoke("下载失败: " + ex.Message); return; }
                finally { Volatile.Write(ref downloading, 0); }

                // 完整性校验:Release 附带 SHA256SUMS 时比对安装包哈希,不符即删。
                try
                {
                    status?.Invoke("正在校验安装包…");
                    await VerifyChecksumAsync(path, info);
                    await VerifySignatureAsync(path);
                }
                catch (Exception ex)
                {
                    DeleteDownloadedInstaller(path);
                    status?.Invoke("安装包校验失败: " + ex.Message);
                    ConfirmDialog.Notice("更新已取消", "下载的安装包未通过完整性校验,已删除。\n" + ex.Message);
                    return;
                }

                status?.Invoke("下载完成,可以安装。");
                // 安装会退出应用,默认键/焦点必须在"取消"侧,回车不能直接触发安装。
                if (!ConfirmDialog.Show("YukiDesk 更新",
                    "新版本 v" + info.Version + " 已下载完成。\n\n立即安装吗?安装时会先退出 YukiDesk。", "立即安装",
                    okIsDefault: false))
                {
                    DeleteDownloadedInstaller(path);
                    status?.Invoke("已取消安装。");
                    return;
                }
                status?.Invoke("正在启动安装程序…");
                if (!await InstallAndRestartAsync(path))
                {
                    DeleteDownloadedInstaller(path);
                    status?.Invoke("安装未启动。");
                    ConfirmDialog.Notice("更新未完成", "安装程序未能启动或已取消，YukiDesk 将继续运行。");
                }
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        /// <summary>启动自动检查:开机 25 秒后首次检查,之后每 24 小时一次。</summary>
        public static void StartAutoCheck()
        {
            if (_autoTimer != null) return;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            _autoTimer = timer;
            timer.Tick += async (s, e) =>
            {
                timer.Stop();
                try
                {
                    if (App.Settings.AutoCheckUpdate
                        && Interlocked.Exchange(ref _autoCheckRunning, 1) == 0)
                    {
                        try
                        {
                            var info = await CheckLatestAsync(CancellationToken.None);
                            if (info != null)
                            {
                                App.Log("更新: 发现新版本 v" + info.Version);
                                if (App.Settings.AutoUpdate)
                                    await RunCheckAsync(null, autoDownload: true, info);
                                else
                                    App.ShowTrayBalloon("YukiDesk 有新版本",
                                        "v" + info.Version + " 已发布,可在 设置 → 关于 中手动更新。");
                            }
                        }
                        finally { Volatile.Write(ref _autoCheckRunning, 0); }
                    }
                }
                catch (Exception ex) { App.Log("更新: 自动检查失败 " + ex.Message); }
                finally
                {
                    if (ReferenceEquals(_autoTimer, timer)
                        && Application.Current != null
                        && !Application.Current.Dispatcher.HasShutdownStarted)
                    {
                        timer.Interval = TimeSpan.FromHours(24);
                        timer.Start();
                    }
                }
            };
            timer.Start();
        }

        public static void StopAutoCheck()
        {
            var timer = _autoTimer;
            _autoTimer = null;
            timer?.Stop();
        }
    }
}
