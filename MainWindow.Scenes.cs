using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using DesktopBar.Interop;
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
            _appSceneTimer.IsEnabled = S.AppScenes.Count > 0;
            if (!_appSceneTimer.IsEnabled && ActiveSceneName != null) ApplyAppScene(null);
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
            if (_closing || App.IsSettingsOpen || HasRevealInteraction) return;
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
            try
            {
                App.SceneSettings = preset == null ? null : DesktopPresets.ResolveScene(preset);
                ActiveSceneName = preset?.Name;
                SetEdgeReveal(App.ActiveSettings.RevealOnTopEdge);
                ThemeService.Refresh();
                GlassService.ApplyCaptureVisibility();
                ApplySettings();
            }
            catch (Exception ex)
            {
                App.Log("应用临时预设失败: " + ex.Message);
                App.SceneSettings = null;
                ActiveSceneName = null;
                _sceneSuppressedProgram = _sceneCurrentProgram;
            }
        }
    }
}
