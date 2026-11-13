using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using HS2CameraSweep.Sweep;
using Studio;
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
        private List<CameraShot> _cameraCache = new List<CameraShot>();
        private float _cameraCacheTime = -10f;
        private Vector2 _cameraScroll;
        private Vector2 _charScroll;
        private Estimate _estimate;
        private float _estimateTime = -10f;

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

            GUILayout.Label("Framerate, format and quality come from VideoExport's own window (Ctrl+E by default).");
        }

        /// <summary>One tick box per camera. Unticked cameras are skipped. Saved with the scene.</summary>
        private void DrawCameraList(Sweep.SweepRunner runner)
        {
            if (Time.realtimeSinceStartup - _cameraCacheTime > 1f)
            {
                _cameraCache = runner.ListCameras();
                _cameraCacheTime = Time.realtimeSinceStartup;
            }

            var on = _cameraCache.Count(SweepSelection.IsOn);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{on} of {_cameraCache.Count} camera(s) on");
            if (on < _cameraCache.Count && GUILayout.Button("All on", GUILayout.Width(60)))
                SweepSelection.AllCamerasOn();
            GUILayout.EndHorizontal();

            if (_cameraCache.Count == 0) return;

            _cameraScroll = GUILayout.BeginScrollView(_cameraScroll, GUILayout.Height(Mathf.Min(24f * _cameraCache.Count + 6f, 150f)));
            foreach (var shot in _cameraCache)
            {
                var was = SweepSelection.IsOn(shot);
                var now = GUILayout.Toggle(was, " " + shot.Label);
                if (now != was) SweepSelection.SetOn(shot, now);
            }
            GUILayout.EndScrollView();
        }

        /// <summary>Who the outfit and face variations change. Women start ticked, men unticked. Saved with the scene.</summary>
        private void DrawCharacterList()
        {
            var chars = SweepSelection.AllCharacters();
            var targets = chars.Where(SweepSelection.IsTarget).ToList();

            GUILayout.Space(4);
            GUILayout.Label(targets.Count == 0
                ? "Characters: none ticked - variations won't change anyone"
                : $"Characters changed: {targets.Count} of {chars.Count}");

            if (chars.Count == 0) return;

            _charScroll = GUILayout.BeginScrollView(_charScroll, GUILayout.Height(Mathf.Min(24f * chars.Count + 6f, 150f)));
            foreach (var c in chars)
            {
                var was = SweepSelection.IsTarget(c);
                var label = $" {Sweep.SweepRunner.CharName(c)}  ({(SweepSelection.IsFemale(c) ? "F" : "M")})";
                var now = GUILayout.Toggle(was, label);
                if (now != was) SweepSelection.SetTarget(c, now);
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Women")) SweepSelection.SetAllTargets(SweepSelection.IsFemale);
            if (GUILayout.Button("All")) SweepSelection.SetAllTargets(_ => true);
            if (GUILayout.Button("None")) SweepSelection.SetAllTargets(_ => false);
            if (GUILayout.Button("Selected"))
            {
                var selected = new HashSet<OCIChar>(KKAPI.Studio.StudioAPI.GetSelectedCharacters() ?? Enumerable.Empty<OCIChar>());
                SweepSelection.SetAllTargets(selected.Contains);
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>How many images/videos the current settings make, and roughly how big and how long.</summary>
        private void DrawEstimate()
        {
            if (Time.realtimeSinceStartup - _estimateTime > 1f || _estimate == null)
            {
                _estimate = Estimate.Compute(_plugin, _cameraCache.Count(SweepSelection.IsOn));
                _estimateTime = Time.realtimeSinceStartup;
            }

            var e = _estimate;
            var what = e.Video ? "video" : "image";
            GUILayout.Label($"<b>{e.Total} {what}{(e.Total == 1 ? "" : "s")}</b>   ({e.Breakdown})", RichLabel);

            if (e.Video)
            {
                GUILayout.Label(e.BytesEach < 0
                    ? "Size and time: shown after your first video sweep."
                    : $"~{Estimate.Size(e.BytesEach)} each, ~{Estimate.Size(e.BytesTotal)} total.  ~{Estimate.Duration(e.SecondsEach)} each, ~{Estimate.Duration(e.SecondsTotal)} total.");
            }
            else
            {
                var up = e.Upsampling > 1 ? $", {e.Upsampling}× upsampling" : "";
                GUILayout.Label($"{e.Width}×{e.Height}{up}:  ~{Estimate.Size(e.BytesEach)} each, ~{Estimate.Size(e.BytesTotal)} total");
                GUILayout.Label($"Time: ~{Estimate.Duration(e.SecondsEach)} each, ~{Estimate.Duration(e.SecondsTotal)} total");
            }

            if (!e.SizeMeasured || !e.TimeMeasured)
                GUILayout.Label("<i>Rough guess - gets accurate after your first sweep.</i>", RichLabel);

            if (e.FreeBytes >= 0 && e.BytesTotal > 0)
            {
                var low = e.BytesTotal > e.FreeBytes * 0.9;
                var text = $"Free space on the output drive: {Estimate.Size(e.FreeBytes)}";
                GUILayout.Label(low ? $"<color=#ff6060><b>{text} - not enough room</b></color>" : text, RichLabel);
            }
        }

        private static GUIStyle _richLabel;
        private static GUIStyle RichLabel => _richLabel ?? (_richLabel = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });

        private bool _showTokens;

        /// <summary>File-name template with a live example of the first file a sweep would write.</summary>
        private void DrawNaming()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("File name", GUILayout.Width(70));
            _plugin.NameTemplateText.Value = GUILayout.TextField(_plugin.NameTemplateText.Value);
            if (GUILayout.Button("Reset", GUILayout.Width(48))) _plugin.NameTemplateText.Value = NameTemplate.Default;
            GUILayout.EndHorizontal();

            var firstCamera = _cameraCache.FirstOrDefault(SweepSelection.IsOn);
            var axisLabels = _plugin.Axes.Where(a => a.Enabled)
                .Select(a => new KeyValuePair<string, string>(a.Name, a.PreviewLabel))
                .Where(kv => !string.IsNullOrEmpty(kv.Value));
            var character = SweepSelection.Targets().FirstOrDefault() ?? SweepSelection.SwapTarget;
            var tokens = NameTemplate.Values(FileNames.Sanitize(_plugin.FilePrefix.Value), firstCamera?.Label ?? "slot01", 1,
                _estimate?.Total ?? 1, DateTime.Now, character == null ? "" : Sweep.SweepRunner.CharName(character), axisLabels);
            var ext = _plugin.Mode.Value == OutputMode.Video ? ".mp4" : ".png";
            var folder = _plugin.FolderPerRun.Value ? "<date-time>\\" : "";
            GUILayout.Label($"<i>e.g. {folder}{NameTemplate.Build(_plugin.NameTemplateText.Value, tokens)}{ext}</i>", RichLabel);

            GUILayout.BeginHorizontal();
            _plugin.FolderPerRun.Value = GUILayout.Toggle(_plugin.FolderPerRun.Value, " Folder per run");
            _showTokens = GUILayout.Toggle(_showTokens, " Show tokens");
            GUILayout.EndHorizontal();

            if (_showTokens)
                GUILayout.Label("{prefix} {scene} {camera} {n} {date} {time} {character} {axes}\n{swap} {outfit} {clothes} {face} {extras}\n" +
                                "Off variations are left out. Use / for subfolders, e.g. {scene}/{character}_{outfit}_{camera}", RichLabel);
        }

        private void DrawContents(int id)
        {
            var runner = _plugin.Runner;
            GUI.enabled = !runner.IsRunning;

            GUILayout.Label("Cameras");
            _plugin.Source.Value = (CameraSource)GUILayout.Toolbar((int)_plugin.Source.Value,
                new[] { "Slots", "Objects", "Both" });
            _plugin.SkipUnsetSlots.Value = GUILayout.Toggle(_plugin.SkipUnsetSlots.Value, " Skip unused / duplicate slots");

            DrawCameraList(runner);

            GUILayout.Space(6);
            GUILayout.Label("Variations");
            foreach (var axis in _plugin.Axes)
            {
                axis.Enabled = GUILayout.Toggle(axis.Enabled, " " + axis.Name);
                if (axis.Enabled) axis.DrawSettings();
            }

            if (_plugin.Axes.Any(a => a.Enabled))
                DrawCharacterList();

            GUILayout.Space(6);
            GUILayout.Label("Output");
            _plugin.Mode.Value = (OutputMode)GUILayout.Toolbar((int)_plugin.Mode.Value, new[] { "Images", "Video" });

            GUILayout.BeginHorizontal();
            GUILayout.Label("File prefix", GUILayout.Width(70));
            _plugin.FilePrefix.Value = GUILayout.TextField(_plugin.FilePrefix.Value);
            GUILayout.EndHorizontal();

            DrawNaming();

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
            DrawEstimate();

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
