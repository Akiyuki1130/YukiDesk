using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using DesktopBar.Interop;

namespace DesktopBar.UI
{
    internal static class ShellFileActions
    {
        internal static void Run(Action command)
        {
            try { command(); }
            catch (Exception ex)
            {
                const int Cancelled = unchecked((int)0x800704c7);
                if (ex is OperationCanceledException || ex.HResult == Cancelled) return;
                App.Log("Windows 文件操作失败: " + ex.Message);
                ConfirmDialog.Notice("Windows 文件操作未完成", ex.Message);
            }
        }

        internal static void Rename(IntPtr owner, string path)
        {
            string name = Path.GetFileName(path);
            string newName = Prompt.Show("重命名", "输入新名称", name);
            if (string.IsNullOrEmpty(newName) || newName == name) return;
            if (newName is "." or ".." || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                ConfirmDialog.Notice("名称无效", "请输入有效的 Windows 文件名。");
                return;
            }
            ShellFiles.Rename(owner, path, newName);
        }

        internal static void WireDrag(FrameworkElement element, Action drag, Action<int> click)
        {
            Point? start = null;
            int clickCount = 0;
            element.PreviewMouseLeftButtonDown += (_, e) =>
            {
                start = e.GetPosition(element);
                clickCount = e.ClickCount;
                element.CaptureMouse();
            };
            element.LostMouseCapture += (_, _) => start = null;
            element.PreviewMouseMove += (_, e) =>
            {
                if (!start.HasValue || e.LeftButton != MouseButtonState.Pressed) return;
                var offset = e.GetPosition(element) - start.Value;
                if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                start = null;
                element.ReleaseMouseCapture();
                e.Handled = true;
                drag();
            };
            element.MouseLeftButtonUp += (_, e) =>
            {
                if (!start.HasValue) return;
                start = null;
                element.ReleaseMouseCapture();
                e.Handled = true;
                if (new Rect(element.RenderSize).Contains(e.GetPosition(element))) click(clickCount);
            };
        }
    }
}
