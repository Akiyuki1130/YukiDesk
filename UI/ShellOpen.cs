using System;
using System.Diagnostics;
using System.IO;
using DesktopBar.Interop;

namespace DesktopBar.UI
{
    /// <summary>用系统默认程序打开文件/文件夹(无 shell 命令拼接,使用参数化 API)。</summary>
    public static class ShellOpen
    {
        public static void OpenPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (Directory.Exists(path))
                {
                    OpenFolder(path);
                    return;
                }
                if (!File.Exists(path)) return;

                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                };
                using var process = Process.Start(psi);
            }
            catch (Exception ex) { App.Log("打开失败: " + path + " — " + ex.Message); }
        }

        public static void OpenFolder(string path)
        {
            if (!string.IsNullOrWhiteSpace(path)) OpenExplorer(path);
        }

        internal static void OpenRecycleBin() => OpenExplorer(ShellFiles.RecycleBin);

        internal static void RevealItem(string path)
        {
            if (!NativeMethods.SelectItemInExplorer(path))
                OpenFolder(Path.GetDirectoryName(path));
        }

        private static void OpenExplorer(string target)
        {
            try
            {
                var launch = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                {
                    UseShellExecute = false
                };
                launch.ArgumentList.Add(target);
                using var process = Process.Start(launch);
            }
            catch (Exception ex) { App.Log("打开资源管理器失败: " + ex.Message); }
        }
    }
}
