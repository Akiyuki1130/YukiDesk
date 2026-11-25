using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using DesktopBar.Interop;
using DesktopBar.Models;
using DesktopBar.Services;

namespace DesktopBar
{
    public partial class MainWindow
    {
        private DispatcherTimer _appSceneTimer;
        private string _sceneCandidate, _sceneCurrentProgram, _sceneSuppressedProgram;
        private long _sceneCandidateAt;
        internal string ActiveSceneName { get; private set; }

        internal void RefreshAppSceneRules()
        {
            _appSceneTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(480) };
            _appSceneTimer.Tick -= AdvanceAppScene;
            _appSceneTimer.Tick += AdvanceAppScene;
            _sceneCandidate = null;
            _sceneCandidateAt = 0;
            bool hasRules = S.AppScenes.Count > 0;
            _appSceneTimer.IsEnabled = hasRules && !PresentationSuspended;
            if (!hasRules && ActiveSceneName != null) ApplyAppScene(null);
        }

        internal void EndAppSceneForSettings()
        {
            if (ActiveSceneName != null) ApplyAppScene(null);
            _sceneCandidateAt = 0;
        }

        internal void SuppressAppSceneForManualPreset()
        {
            _sceneSuppressedProgram = _sceneCurrentProgram;
            App.SceneSettings = null;
            ActiveSceneName = null;
        }

        private void AdvanceAppScene(object sender, EventArgs args)
        {
            if (_closing || PresentationSuspended || App.IsSettingsOpen || HasRevealInteraction) return;
            string program = NativeMethods.ForegroundProgramName();
            if (string.IsNullOrWhiteSpace(program) || program.Equals("YukiDesk", StringComparison.OrdinalIgnoreCase)) return;
            long tick = Environment.TickCount64;
            if (!string.Equals(program, _sceneCandidate, StringComparison.OrdinalIgnoreCase) || _sceneCandidateAt == 0)
            {
                _sceneCandidate = program;
                _sceneCandidateAt = tick;
                return;
            }
            if (tick - _sceneCandidateAt < 780) return;
            _sceneCurrentProgram = program;
            if (string.Equals(_sceneSuppressedProgram, program, StringComparison.OrdinalIgnoreCase)) return;
            _sceneSuppressedProgram = null;
            string desired = S.AppScenes.FirstOrDefault(rule => string.Equals(Path.GetFileNameWithoutExtension(rule.Program),
                program, StringComparison.OrdinalIgnoreCase))?.Preset;
            if (desired == ActiveSceneName) return;
            ApplyAppScene(desired);
        }

        private void ApplyAppScene(string name)
        {
            var preset = S.DesktopPresets.FirstOrDefault(item => item.Name == name);
            var before = App.ActiveSettings;
            try
            {
                App.SceneSettings = preset == null ? null : DesktopPresets.ResolveScene(preset);
                ActiveSceneName = preset?.Name;
                ApplyPresetVisuals(before);
            }
            catch (Exception ex)
            {
                App.Log("应用临时预设失败: " + ex.Message);
                var failed = App.ActiveSettings;
                App.SceneSettings = null;
                ActiveSceneName = null;
                _sceneSuppressedProgram = _sceneCurrentProgram;
                try { ApplyPresetVisuals(failed); }
                catch (Exception restoreError) { App.Log("还原日常布局失败: " + restoreError.Message); }
            }
        }

        internal void ApplyPresetVisuals(AppSettings previous, bool importedAppearance = false)
        {
            var current = App.ActiveSettings;
            SetEdgeReveal(current.RevealOnTopEdge);
            ThemeService.Refresh();
            bool captureChanged = previous.ShowInScreenCapture != current.ShowInScreenCapture
                || previous.WallpaperOnlyCapture != current.WallpaperOnlyCapture;
            if (captureChanged || importedAppearance) GlassService.ApplyCaptureVisibility();
            if (importedAppearance || _barLayout == null)
            {
                ApplySettings();
                return;
            }

            if (previous.PillMaterial != current.PillMaterial) RefreshBarMaterial();
            else if (previous.GlassStyle != current.GlassStyle || previous.GlassMaxFps != current.GlassMaxFps
                || previous.FreezePanelGlass != current.FreezePanelGlass) GlassService.ApplyLive();
            if (!ConfiguredPlacements(previous).SequenceEqual(ConfiguredPlacements(current))) RefreshBarLayout();
            if (previous.ShowFavorites != current.ShowFavorites) RefreshFavorites();
            ApplyModuleVisibility();
            if (previous.ReserveDesktopSpace != current.ReserveDesktopSpace && (!IsOnSecondaryScreen || !current.ReserveDesktopSpace))
                DesktopSpaceService.Apply(current);

            static IEnumerable<(string, int, bool)> ConfiguredPlacements(AppSettings settings) =>
                settings.BarLayout.Where(entry => entry != null).Select(entry => (entry.Id, entry.Zone, entry.KeepVisible));
        }
    }
}
