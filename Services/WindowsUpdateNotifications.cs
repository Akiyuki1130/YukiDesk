using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DesktopBar.Services
{
    internal static class WindowsUpdateNotifications
    {
        // 与安装器写入开始菜单快捷方式的 AppUserModelID 一致。
        internal const string AppId = "Akiyuki1130.YukiDesk";
        internal const string Group = "updates";

        internal static void Available(string version) => Show("available", "发现 YukiDesk 新版本",
            "v" + version + " 已发布，可在「设置 → 关于」中更新。");

        internal static void Downloaded(string version) => Show("downloaded", "YukiDesk 更新下载完成",
            "v" + version + " 的安装包已下载完成。");

        private static void Show(string tag, string title, string message)
        {
            try
            {
                var notifier = ToastNotificationManager.CreateToastNotifier(AppId);

                var xml = new XmlDocument();
                xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text/><text/></binding></visual></toast>");
                var text = xml.GetElementsByTagName("text");
                text[0].AppendChild(xml.CreateTextNode(title));
                text[1].AppendChild(xml.CreateTextNode(message));
                var toast = new ToastNotification(xml) { Group = Group, Tag = tag };
                // 首条通知前 Windows 可能还查不到 Setting，交给系统按通知偏好展示。
                notifier.Show(toast);
            }
            catch (Exception ex)
            {
                // 直接运行未安装的构建时，可能没有为 Windows 通知登记快捷方式。
                App.Log("更新: Windows 通知发送失败 " + ex.Message);
                App.ShowTrayBalloon(title, message);
            }
        }
    }
}
