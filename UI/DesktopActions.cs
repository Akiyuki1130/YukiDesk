using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DesktopBar.Models;

namespace DesktopBar.UI
{
    internal static class DesktopActions
    {
        internal static event Action Changed;

        internal static bool IsWeb(string target) => Uri.TryCreate(target, UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);

        internal static bool IsSupportedTarget(string target) => !string.IsNullOrWhiteSpace(target)
            && (IsWeb(target) || Path.IsPathFullyQualified(target));

        internal static string Title(string target)
        {
            if (IsWeb(target)) return new Uri(target).Host;
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(target));
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileNameWithoutExtension(name);
            return name.Length == 0 ? target : name;
        }

        internal static bool Add(string target, string title = null)
        {
            if (!AddLink(target, title)) return false;
            SaveLinks(refreshBar: false);
            return true;
        }

        internal static void AddFiles(IEnumerable<string> paths)
        {
            bool changed = false;
            foreach (var path in paths) changed |= AddLink(path, null);
            if (changed) SaveLinks(refreshBar: false);
        }

        private static bool AddLink(string target, string title)
        {
            var links = App.Settings.DesktopLinks;
            if (!IsSupportedTarget(target) || links.Any(link => SameTarget(link.Target, target))) return false;
            links.Add(new DesktopLink { Target = target, Title = string.IsNullOrWhiteSpace(title) ? Title(target) : title.Trim() });
            return true;
        }

        internal static bool LinkFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (App.Settings.LinkedFolders.Any(item => SameTarget(item, path))) return false;
            App.Settings.LinkedFolders.Add(path);
            App.Save();
            App.Bar?.ApplyFolderOrder();
            Changed?.Invoke();
            return true;
        }

        internal static void UnlinkFolder(string path)
        {
            App.Settings.LinkedFolders.RemoveAll(item => SameTarget(item, path));
            for (int slot = 0; slot < App.Settings.FolderShortcuts.Length; slot++)
            {
                if (!SameTarget(App.Settings.FolderShortcuts[slot], path)) continue;
                App.Settings.FolderShortcuts[slot] = null;
                App.Bar?.SetFolderShortcut(slot, false);
            }
            if (SameTarget(App.Settings.InboxFolder, path)) App.Settings.InboxFolder = "";
            App.Save();
            App.Bar?.ApplyFolderOrder();
            Changed?.Invoke();
        }

        internal static bool RelinkFolder(string oldPath, string newPath)
        {
            var settings = App.Settings;
            int index = settings.LinkedFolders.FindIndex(path => SameTarget(path, oldPath));
            newPath = Path.TrimEndingDirectorySeparator(newPath);
            if (index < 0 || settings.LinkedFolders.Any(path => SameTarget(path, newPath))) return false;
            settings.LinkedFolders[index] = newPath;
            foreach (var pair in settings.FolderViews.ToArray())
            {
                string replacement = Remap(pair.Key);
                if (replacement == pair.Key) continue;
                settings.FolderViews.Remove(pair.Key);
                settings.FolderViews.TryAdd(replacement, pair.Value);
            }
            foreach (var link in settings.DesktopLinks)
                if (!IsWeb(link.Target)) link.Target = Remap(link.Target);
            settings.InboxFolder = Remap(settings.InboxFolder);
            for (int slot = 0; slot < settings.FolderShortcuts.Length; slot++)
                settings.FolderShortcuts[slot] = Remap(settings.FolderShortcuts[slot]);
            App.Bar?.ApplyFolderOrder();
            SaveLinks();
            return true;

            string Remap(string path)
            {
                if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return path;
                if (SameTarget(path, oldPath)) return newPath;
                string prefix = Path.EndsInDirectorySeparator(oldPath) ? oldPath : oldPath + Path.DirectorySeparatorChar;
                return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(newPath, path[prefix.Length..]) : path;
            }
        }

        internal static void SaveLinks(bool refreshBar = true)
        {
            App.Save();
            if (refreshBar) App.Bar?.RefreshFavorites();
            Changed?.Invoke();
        }

        internal static bool SameTarget(string first, string second)
            => string.Equals(first, second, IsWeb(first) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        internal static void AddInboxToSendTo()
        {
            string target = App.Settings.InboxFolder;
            if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
            {
                ConfirmDialog.Notice("收件箱不可用", "请先选择一个可用的收件箱文件夹。");
                return;
            }
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            if (string.IsNullOrEmpty(folder)) return;
            string shortcut = Path.Combine(folder, "YukiDesk 收件箱.lnk");
            bool existed = File.Exists(shortcut);
            object shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true));
                link = ((dynamic)shell).CreateShortcut(shortcut);
                const string marker = "YukiDesk · SendTo inbox";
                if (existed && !string.Equals((string)((dynamic)link).Description, marker, StringComparison.Ordinal))
                {
                    ConfirmDialog.Notice("已有同名快捷方式", "请在 Windows「发送到」目录中移走同名快捷方式后再试。");
                    return;
                }
                ((dynamic)link).TargetPath = target;
                ((dynamic)link).Description = marker;
                ((dynamic)link).Save();
                ConfirmDialog.Notice("已加入发送到", "可在资源管理器右键菜单的「发送到」中使用。");
            }
            catch (Exception ex)
            {
                App.Log("更新 Windows 发送到入口失败: " + ex.Message);
                ConfirmDialog.Notice("无法加入发送到", ex.Message);
            }
            finally
            {
                if (link != null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }

        internal static IReadOnlyList<FlyoutItem> FavoriteItems()
        {
            var settings = App.Settings;
            var items = settings.DesktopLinks
                .Select(link => new FlyoutItem(link.Title ?? Title(link.Target), () => Open(link.Target))).ToList();
            if (settings.TrackRecentDesktopTargets)
                foreach (string path in settings.RecentDesktopTargets)
                    if (!settings.DesktopLinks.Any(link => SameTarget(link.Target, path)))
                        items.Add(new FlyoutItem("最近 · " + Title(path), () => Open(path)));
            items.Add(new FlyoutItem("管理常用项…", () => App.ShowSettingsPage("Modules")));
            return items;
        }

        internal static async void Open(string target)
        {
            if (!IsSupportedTarget(target)) return;
            try
            {
                if (IsWeb(target))
                {
                    using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    Remember(target);
                }
                else if (await Task.Run(() => File.Exists(target) || Directory.Exists(target)))
                {
                    ShellOpen.OpenPath(target);
                    Remember(target);
                }
                else ConfirmDialog.Notice("无法打开", "该位置不存在或暂不可用。");
            }
            catch (Exception ex) { App.Log("打开常用项失败: " + ex.Message); }
        }

        private static void Remember(string target)
        {
            if (!App.Settings.TrackRecentDesktopTargets) return;
            var recent = App.Settings.RecentDesktopTargets;
            recent.RemoveAll(item => SameTarget(item, target));
            recent.Insert(0, target);
            if (recent.Count > 20) recent.RemoveRange(20, recent.Count - 20);
            App.Save();
        }
    }
}
