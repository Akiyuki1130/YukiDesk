using System;
using System.Runtime.InteropServices;

namespace DesktopBar.Interop
{
    internal static class TextClipboard
    {
        private const uint MoveableMemory = 0x0002;
        private const uint UnicodeText = 13;

        internal static bool TrySet(IntPtr owner, string text)
        {
            if (owner == IntPtr.Zero) return false;
            char[] buffer = (text + '\0').ToCharArray();
            IntPtr memory = GlobalAlloc(MoveableMemory, (UIntPtr)(buffer.Length * sizeof(char)));
            if (memory == IntPtr.Zero) return false;
            try
            {
                IntPtr address = GlobalLock(memory);
                if (address == IntPtr.Zero) return false;
                try { Marshal.Copy(buffer, 0, address, buffer.Length); }
                finally { GlobalUnlock(memory); }
                if (!OpenClipboard(owner)) return false;
                try
                {
                    if (!EmptyClipboard() || SetClipboardData(UnicodeText, memory) == IntPtr.Zero) return false;
                    memory = IntPtr.Zero; // Windows owns the buffer after SetClipboardData succeeds.
                    return true;
                }
                finally { CloseClipboard(); }
            }
            finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
        }

        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    }
}
