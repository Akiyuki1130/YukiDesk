using System.Collections.Generic;

namespace DesktopBar.Models
{
    public class DesktopPreset
    {
        public string Name { get; set; }
        public Dictionary<string, System.Text.Json.JsonElement> Values { get; set; } = new();
    }

    public class AppSceneRule
    {
        public string Program { get; set; }
        public string Preset { get; set; }
    }

    public class FolderPreferences
    {
        public string Sort { get; set; } = "name";
        public bool ListView { get; set; }
        public List<string> Pinned { get; set; } = new();
        public Dictionary<string, System.DateTime> Arrivals { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
    }

    public class DesktopLink
    {
        public string Title { get; set; }
        public string Target { get; set; }
        public bool OnBar { get; set; }
    }

    public class BarPlacement
    {
        public string Id { get; set; }
        public int Zone { get; set; }
        public bool KeepVisible { get; set; }
    }

    public partial class AppSettings
    {
        public List<string> LinkedFolders { get; set; } = new();
        public Dictionary<string, FolderPreferences> FolderViews { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
        public string InboxFolder { get; set; } = "";
        public bool FreezePanelGlass { get; set; }
        public bool WallpaperOnlyCapture { get; set; }
        public List<DesktopLink> DesktopLinks { get; set; } = new();
        public List<string> RecentDesktopTargets { get; set; } = new();
        public bool TrackRecentDesktopTargets { get; set; }
        public bool RecycleNewestFirst { get; set; } = true;
        public bool ShowFavorites { get; set; }
        public List<BarPlacement> BarLayout { get; set; } = new();
        public List<string> EdgeExcludedApps { get; set; } = new();
        public List<DesktopPreset> DesktopPresets { get; set; } = new();
        public List<AppSceneRule> AppScenes { get; set; } = new();
        public bool RevealOnPointerScreen { get; set; }
        public string RevealDisplayName { get; set; } = "";
        public string[] FolderShortcuts { get; set; } = new string[3];
        public bool ShowBattery { get; set; } = true;
        public bool CombinePerfModules { get; set; }
        public string SecondTimeZoneId { get; set; } = "";
        public string PreferredMediaSource { get; set; } = "";

        internal AppSettings CloneForScene() => (AppSettings)MemberwiseClone();
    }
}
