using System.Collections.Generic;
using System.Windows.Input;
using DesktopBar.Models;

namespace DesktopBar.UI
{
    internal readonly record struct BarHotkey(Key Key, ModifierKeys Modifiers)
    {
        private const ModifierKeys AllModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;

        internal static BarHotkey Default => new(Key.Y, ModifierKeys.Control | ModifierKeys.Alt);
        internal static BarHotkey FromSettings(AppSettings settings) => new(settings.RevealHotkeyKey, settings.RevealHotkeyModifiers);

        internal bool IsValid => (Modifiers & ~AllModifiers) == 0 && Key != Key.F12
            && !IsModifier(Key) && KeyInterop.VirtualKeyFromKey(Key) != 0
            && (Modifiers != ModifierKeys.None || Key is >= Key.F1 and <= Key.F24);

        internal static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

        internal string Display
        {
            get
            {
                var parts = new List<string>(5);
                if ((Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
                if ((Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
                if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
                if ((Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
                parts.Add(new KeyConverter().ConvertToInvariantString(Key));
                return string.Join(" + ", parts);
            }
        }
    }
}
