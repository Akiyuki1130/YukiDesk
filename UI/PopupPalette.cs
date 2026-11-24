using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    internal static class PopupPalette
    {
        internal static void Apply(Border card, bool dialog = false)
        {
            var settings = App.ActiveSettings;
            bool glass = settings?.PillMaterial is 3 or 4;
            bool automatic = ThemeService.FollowsBackground(settings);
            string background = automatic
                ? ThemeService.Background : settings?.ThemeColor ?? ThemeService.Background;
            string text = automatic
                ? ThemeService.Text : settings?.ThemeTextColor ?? ThemeService.Text;
            if (glass)
            {
                background = "#FFFFFF";
                text = "#202020";
            }
            else
            {
                double surfaceLuminance = Luminance(Ui.ParseColor(background));
                double inkLuminance = Luminance(Ui.ParseColor(text));
                double contrast = (Math.Max(inkLuminance, surfaceLuminance) + .05)
                    / (Math.Min(inkLuminance, surfaceLuminance) + .05);
                if (contrast < 4.5)
                    text = surfaceLuminance > .179 ? "#000000" : "#FFFFFF";
            }

            var resources = card.Resources;
            resources["ThemePanelText"] = Ui.Brush(text);
            resources["ThemePanelBackground"] = Ui.Brush(background, glass ? (dialog ? .88 : .84) : 1);
            resources["ThemePanelBorder"] = glass ? Ui.Brush("#FFFFFF", .76) : Ui.Brush(text, .20);
            resources["ThemePanelSubtle"] = Ui.Brush(text, .06);
            resources["ThemePanelControl"] = Ui.Brush(text, .10);
            resources["ThemePanelHover"] = Ui.Brush(text, .12);
            resources["ThemePanelControlHover"] = Ui.Brush(text, .16);
            resources["ThemePanelSelected"] = Ui.Brush(text, .20);
            card.SetResourceReference(Border.BackgroundProperty, "ThemePanelBackground");
            card.SetResourceReference(Border.BorderBrushProperty, "ThemePanelBorder");
        }

        private static double Luminance(Color color) => .2126 * color.ScR + .7152 * color.ScG + .0722 * color.ScB;
    }
}
