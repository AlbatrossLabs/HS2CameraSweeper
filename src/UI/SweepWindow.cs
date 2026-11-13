using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HS2CameraSweep.UI
{
    /// <summary>
    /// Small IMGUI window: camera source, axes, Run/Cancel, progress.
    /// Every setting here is also in the F1 Configuration Manager.
    /// </summary>
    internal sealed class SweepWindow
    {
        private const int WindowId = 0x43535750; // "CSWP"
        private readonly CameraSweepPlugin _plugin;
        private Rect _rect = new Rect(260, 120, 340, 10);
        private int _cameraCountCache = -1;
        private float _cameraCountTime;

        public SweepWindow(CameraSweepPlugin plugin)
        {
            _plugin = plugin;
        }

        public bool Visible { get; set; }

        public void Draw()
        {
            var runner = _plugin.Runner;

            // Always show a slim progress bar while running, even if the window is closed.
            if (runner.IsRunning && !Visible)
            {
                var r = new Rect(Screen.width / 2f - 170, 12, 340, 26);
                GUI.Box(r, $"Camera Sweep {runner.Done}/{runner.Total}  {runner.Current}   (Esc to cancel)");
                return;
            }

            if (!Visible) return;

            _rect = GUILayout.Window(WindowId, _rect, DrawContents, "Camera Sweep " + CameraSweepPlugin.Version);

            // Stop clicks and scroll on the window from reaching Studio's camera.
            if (_rect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y)))
                Input.ResetInputAxes();
        }

        private void DrawVideoSettings()
        {
            if (!Capture.VideoExportBridge.Available)
            {
                GUILayout.Label("VideoExport plugin not found - video mode is unavailable.");
                return;
            }

            var studio = Studio.Studio.Instance;
            var chars = studio == null
                ? new System.Collections.Generic.List<Studio.OCIChar>()
                : studio.dicObjectCtrl.OrderBy(kv => kv.Key).Select(kv => kv.Value).OfType<Studio.OCIChar>().ToList();

            // Character picker: < name >   (defaults to the workspace selection at run time)
            var current = _plugin.VideoTarget != null && chars.Contains(_plugin.VideoTarget) ? _plugin.VideoTarget : null;
            var idx = current == null ? -1 : chars.IndexOf(current);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Character", GUILayout.Width(70));
            if (GUILayout.Button("<", GUILayout.Width(24)) && chars.Count > 0)
                _plugin.VideoTarget = chars[(idx - 1 + chars.Count + (idx < 0 ? 1 : 0)) % chars.Count];
            GUILayout.Label(current != null ? Sweep.SweepRunner.CharName(current) : "(selected in workspace)", GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", GUILayout.Width(24)) && chars.Count > 0)
                _plugin.VideoTarget = chars[(idx + 1) % chars.Count];
            GUILayout.EndHorizontal();

            if (current != null && GUILayout.Button("Use workspace selection instead"))
                _plugin.VideoTarget = null;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Cycles", GUILayout.Width(70));
            if (float.TryParse(GUILayout.TextField(_plugin.VideoCycles.Value.ToString("0.##"), GUILayout.Width(50)), out var cycles) && cycles > 0)
                _plugin.VideoCycles.Value = cycles;
            GUILayout.Label("Prewarm", GUILayout.Width(55));
            if (int.TryParse(GUILayout.TextField(_plugin.VideoPrewarm.Value.ToString(), GUILayout.Width(30)), out var prewarm) && prewarm >= 0)
                _plugin.VideoPrewarm.Value = prewarm;
            GUILayout.EndHorizontal();

            GUILayout.Label("Framerate, format and quality come from VideoExport's own window (Shift+V+E).");
        }

        private void DrawContents(int id)
        {
            var runner = _plugin.Runner;
            GUI.enabled = !runner.IsRunning;

            GUILayout.Label("Cameras");
            _plugin.Source.Value = (CameraSource)GUILayout.Toolbar((int)_plugin.Source.Value,
                new[] { "Slots", "Objects", "Both" });
            _plugin.SkipUnsetSlots.Value = GUILayout.Toggle(_plugin.SkipUnsetSlots.Value, " Skip unused / duplicate slots");

            if (Time.realtimeSinceStartup - _cameraCountTime > 1f)
            {
                _cameraCountCache = runner.CountCameras();
                _cameraCountTime = Time.realtimeSinceStartup;
            }
            GUILayout.Label($"Found {_cameraCountCache} camera(s).");

            GUILayout.Space(6);
            GUILayout.Label("Variations");
            foreach (var axis in _plugin.Axes)
            {
                axis.Enabled = GUILayout.Toggle(axis.Enabled, " " + axis.Name);
                if (axis.Enabled) axis.DrawSettings();
            }

            GUILayout.Space(6);
            GUILayout.Label("Output");
            _plugin.Mode.Value = (OutputMode)GUILayout.Toolbar((int)_plugin.Mode.Value, new[] { "Images", "Video" });

            GUILayout.BeginHorizontal();
            GUILayout.Label("File prefix", GUILayout.Width(70));
            _plugin.FilePrefix.Value = GUILayout.TextField(_plugin.FilePrefix.Value);
            GUILayout.EndHorizontal();

            if (_plugin.Mode.Value == OutputMode.Images)
            {
                _plugin.Transparent.Value = GUILayout.Toggle(_plugin.Transparent.Value, " Transparent background");
                GUILayout.Label("Capture: " + runner.CaptureDescription +
                                (runner.UsingScreencap ? " - size/upsampling from its settings unless set in F1." : " - Screencap not found."));
            }
            else
            {
                DrawVideoSettings();
            }

            GUI.enabled = true;
            GUILayout.Space(6);

            if (!runner.IsRunning)
            {
                var label = _plugin.Mode.Value == OutputMode.Video ? "Record all cameras" : "Capture all cameras";
                if (GUILayout.Button(label, GUILayout.Height(30)))
                    _plugin.StartSweep();
            }
            else
            {
                GUILayout.Label($"{runner.Done} / {runner.Total}   {runner.Current}");
                if (GUILayout.Button("Cancel", GUILayout.Height(30)))
                    runner.Cancel();
            }

            if (!string.IsNullOrEmpty(runner.LastFolder) && !runner.IsRunning && Directory.Exists(runner.LastFolder))
            {
                if (GUILayout.Button("Open last output folder"))
                {
                    try { Process.Start(runner.LastFolder); }
                    catch (Exception ex) { CameraSweepPlugin.Logger.LogWarning(ex.Message); }
                }
            }

            if (GUILayout.Button("Close")) Visible = false;

            GUI.DragWindow();
        }
    }
}
