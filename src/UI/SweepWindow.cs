using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HS2CameraSweep.Axes;
using HS2CameraSweep.Capture;
using HS2CameraSweep.Sweep;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.UI
{
    /// <summary>
    /// The Camera Sweep window, laid out in the order you set up a shoot:
    ///   Still images / Video
    ///   1 Cameras     which cameras to shoot (saved with the scene)
    ///   2 Characters  who changes, and how: each added character has their own variations
    ///   3 Image/Video format, size, quality (or video length)
    ///   4 Files       where files go and what they're called
    /// The sections scroll and fold up to a one-line summary; the footer (warnings, estimate,
    /// Test shot, Capture) always stays on screen. Every setting is also in F1.
    /// </summary>
    internal sealed class SweepWindow
    {
        private const int WindowId = 0x43535750; // "CSWP"
        private const float Width = 440f;
        private const float Inner = Width - 44f; // content width inside the scroll area (room for the scrollbar)

        private readonly CameraSweepPlugin _plugin;
        private Rect _rect = new Rect(260, 80, Width, 10);
        private Vector2 _scroll;
        private float _contentHeight; // measured each repaint; the scroll area is sized to it
        private bool _showAllCameras;
        private const int CameraColumns = 4;
        private const int CameraRowsFolded = 3;

        private bool _showCameras = true;
        private bool _showCharacters = true;
        private bool _showImage = true;
        private bool _showFiles;
        private bool _showAdvanced;
        private bool _showTokens;
        private bool _adding;

        private List<CameraShot> _cameraCache = new List<CameraShot>();
        private float _cameraCacheTime = -10f;
        private Estimate _estimate;
        private float _estimateTime = -10f;

        public SweepWindow(CameraSweepPlugin plugin)
        {
            _plugin = plugin;
        }

        public bool Visible { get; set; }

        public void Draw()
        {
            // Everything Camera Sweep draws uses its own skin (Theme), whatever skin is active.
            var oldSkin = GUI.skin;
            GUI.skin = Theme.Skin;
            try { DrawAll(); }
            finally { GUI.skin = oldSkin; }
        }

        private void DrawAll()
        {
            var runner = _plugin.Runner;

            // Picking files in Studio's lists: a banner with Done, shown even if the window is closed.
            PickMode.DrawBanner();

            // Studio switches to each camera to shoot it. Optionally cover that with a progress screen
            // (F1 > Cover the screen while capturing); neither the cover nor the bar is in the images.
            if (runner.IsSweepingImages && _plugin.CoverScreen.Value)
            {
                DrawCover(runner);
                return;
            }

            // A progress bar at the bottom of the screen while running, window open or not.
            if (runner.IsRunning) DrawProgressBar(runner);

            if (!Visible) return;

            _rect.width = Width;
            // GUILayout.Window only ever grows. Start it small in the layout pass (the only pass that
            // re-measures), so it shrinks back to fit; in the other passes (paint, mouse, drag) keep the
            // measured size, or the window would be drawn tiny for a moment and flicker or vanish.
            if (Event.current.type == EventType.Layout) _rect.height = 10f;
            _rect = GUILayout.Window(WindowId, _rect, DrawContents, "Camera Sweep " + CameraSweepPlugin.Version);

            // Stop clicks and scroll on the window from reaching Studio's camera.
            if (_rect.Contains(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y)))
                Input.ResetInputAxes();
        }

        /// <summary>Progress bar centred at the bottom of the screen, clear of Studio's menus. Esc cancels.</summary>
        private static void DrawProgressBar(SweepRunner runner)
        {
            var w = Mathf.Min(520f, Screen.width - 40f);
            var r = new Rect((Screen.width - w) / 2f, Screen.height - 40f, w, 28f);
            GUI.Box(r, "");
            var frac = runner.Total > 0 ? Mathf.Clamp01(runner.Done / (float)runner.Total) : 0f;
            var bar = new Rect(r.x + 3, r.y + 3, r.width - 76, r.height - 6);
            GUI.Box(new Rect(bar.x, bar.y, bar.width * frac, bar.height), "", Theme.Progress);
            GUI.Label(bar, $"  <b>Camera Sweep</b>  {runner.Done} / {runner.Total}   {runner.Current}", Theme.RichSingle);
            if (GUI.Button(new Rect(r.xMax - 70, r.y + 3, 67, r.height - 6), new GUIContent("Cancel", "Esc also cancels"))) runner.Cancel();
        }

        /// <summary>Full-screen progress screen during a sweep. Esc cancels; Cancel button too.</summary>
        private void DrawCover(SweepRunner runner)
        {
            var screen = new Rect(0, 0, Screen.width, Screen.height);
            var old = GUI.color;
            GUI.color = new Color(0.06f, 0.067f, 0.082f, 0.97f); // VN Deck background
            GUI.DrawTexture(screen, Texture2D.whiteTexture);
            GUI.color = old;

            var w = Mathf.Min(520f, Screen.width - 40f);
            var area = new Rect((Screen.width - w) / 2f, Screen.height / 2f - 70f, w, 150f);
            GUILayout.BeginArea(area);
            GUILayout.Label($"<size=20><b>Camera Sweep</b></size>   <color={Theme.HintHex}>capturing</color>", Theme.Rich);
            var frac = runner.Total > 0 ? Mathf.Clamp01(runner.Done / (float)runner.Total) : 0f;
            var bar = GUILayoutUtility.GetRect(w, 22);
            GUI.Box(bar, "");
            GUI.Box(new Rect(bar.x, bar.y, bar.width * frac, bar.height), "", Theme.Progress);
            GUI.Label(bar, $"  {runner.Done} / {runner.Total}", Theme.Rich);
            GUILayout.Label(Hint(runner.Current), Theme.Rich);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Hint("The view is hidden while Studio switches cameras. Esc cancels."), Theme.Rich);
            if (GUILayout.Button("Cancel", GUILayout.Width(80))) runner.Cancel();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // ================================================================ layout

        private void DrawContents(int id)
        {
            var runner = _plugin.Runner;
            Refresh(runner);

            GUI.enabled = !runner.IsRunning;
            var mode = (OutputMode)GUILayout.Toolbar((int)_plugin.Mode.Value, new[] { "Still images", "Video" }, Theme.Choice,
                GUILayout.Width(Width - 20), GUILayout.Height(24));
            if (mode != _plugin.Mode.Value) { _plugin.Mode.Value = mode; Gui.Relayout(); }
            var video = _plugin.Mode.Value == OutputMode.Video;

            // A GUILayout scroll view doesn't size itself to its content, so measure the content on each
            // repaint and use that height (up to most of the screen; past that it scrolls).
            var maxHeight = Mathf.Max(260f, Screen.height * 0.62f);
            var height = _contentHeight > 0f ? Mathf.Min(_contentHeight + 6f, maxHeight) : maxHeight;
            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(height));
            GUILayout.BeginVertical(GUILayout.Width(Inner));

            if (Section("1  Cameras", CamerasSummary(), ref _showCameras)) DrawCameras(runner);
            if (Section("2  Characters", CharactersSummary(), ref _showCharacters)) DrawCharacters();
            if (video)
            {
                if (Section("3  Video", VideoSummary(), ref _showImage)) DrawVideoSettings();
            }
            else if (Section("3  Image", ImageSummary(), ref _showImage)) DrawImageSettings(runner);
            if (Section("4  Files", FilesSummary(), ref _showFiles)) DrawFiles();

            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint) _contentHeight = GUILayoutUtility.GetLastRect().height;
            GUILayout.EndScrollView();

            GUI.enabled = true;
            DrawFooter(runner, video);

            GUI.DragWindow();
        }

        private void Refresh(SweepRunner runner)
        {
            if (Time.realtimeSinceStartup - _cameraCacheTime > 1f)
            {
                _cameraCache = runner.ListCameras();
                _cameraCacheTime = Time.realtimeSinceStartup;
            }

            if (Time.realtimeSinceStartup - _estimateTime > 1f || _estimate == null)
            {
                _estimate = Estimate.Compute(_plugin, _cameraCache.Count(SweepSelection.IsOn));
                _estimateTime = Time.realtimeSinceStartup;
            }
        }

        /// <summary>Foldable section header with a one-line summary. Returns true when open.</summary>
        private static bool Section(string title, string summary, ref bool open)
        {
            GUILayout.Space(4);
            var enabled = GUI.enabled;
            GUI.enabled = true; // folding works while a sweep runs
            if (GUILayout.Button($"{(open ? "▾" : "▸")} <b>{title}</b>   <color=#8B91A1>{summary}</color>", SectionStyle, GUILayout.Width(Inner)))
            {
                open = !open;
                Gui.Relayout();
            }
            GUI.enabled = enabled;
            return open;
        }

        // ================================================================ 1 cameras

        private string CamerasSummary()
        {
            var on = _cameraCache.Count(SweepSelection.IsOn);
            return _cameraCache.Count == 0 ? "none found" : $"{on} of {_cameraCache.Count} on";
        }

        private static readonly string[] SourceNames = { "Saved views", "Camera objects", "Both" };

        /// <summary>Cameras as a grid of toggle buttons, 4 across: click = on/off, right-click = look through it.</summary>
        private void DrawCameras(SweepRunner runner)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Use", "Studio's numbered camera buttons (plus MoarCamz slots), cameras added with Add > Camera, or both"), GUILayout.Width(LabelW));
            _plugin.Source.Value = (CameraSource)Choice((int)_plugin.Source.Value, SourceNames);
            GUILayout.EndHorizontal();
            _plugin.SkipUnsetSlots.Value = GUILayout.Toggle(_plugin.SkipUnsetSlots.Value, " Skip unused and duplicate saved views");

            if (_cameraCache.Count == 0)
            {
                GUILayout.Label(Hint("No cameras found. Save a view to one of Studio's camera buttons, or add one with Add > Camera."), Rich);
                return;
            }

            var shown = _showAllCameras ? _cameraCache.Count : Math.Min(_cameraCache.Count, CameraColumns * CameraRowsFolded);
            var cellW = (Inner - (CameraColumns - 1) * 4f) / CameraColumns;
            var evt = Event.current;

            for (var i = 0; i < shown; i += CameraColumns)
            {
                GUILayout.BeginHorizontal();
                for (var j = i; j < Math.Min(i + CameraColumns, shown); j++)
                {
                    var shot = _cameraCache[j];
                    var content = new GUIContent(CameraLabel(shot), shot.Label + "\nClick: on/off   Right-click: look through it");
                    var r = GUILayoutUtility.GetRect(content, Theme.Choice, GUILayout.Width(cellW), GUILayout.Height(24));

                    // Right-click looks through the camera without changing on/off.
                    if (evt.type == EventType.MouseDown && evt.button == 1 && r.Contains(evt.mousePosition))
                    {
                        try { shot.Apply(Studio.Studio.Instance); }
                        catch (Exception ex) { CameraSweepPlugin.Logger.LogWarning("Could not switch to " + shot.Label + ": " + ex.Message); }
                        evt.Use();
                        continue;
                    }

                    var was = SweepSelection.IsOn(shot);
                    var now = GUI.Toggle(r, was, content, Theme.Choice);
                    if (now != was) SweepSelection.SetOn(shot, now);
                }
                GUILayout.EndHorizontal();
            }

            var on = _cameraCache.Count(SweepSelection.IsOn);
            GUILayout.BeginHorizontal();
            if (_cameraCache.Count > CameraColumns * CameraRowsFolded &&
                GUILayout.Button(_showAllCameras ? "Show fewer" : $"Show all {_cameraCache.Count}", GUILayout.Width(110)))
            {
                _showAllCameras = !_showAllCameras;
                Gui.Relayout();
            }
            GUILayout.Label(Hint("Click to turn on/off, right-click to look through it. Saved with the scene."), Rich);
            if (on < _cameraCache.Count && GUILayout.Button("All on", GUILayout.Width(56))) SweepSelection.AllCamerasOn();
            GUILayout.EndHorizontal();
        }

        /// <summary>"cam03" -> "Cam 3", "cam_Front" -> "Front".</summary>
        private static string CameraLabel(CameraShot shot)
        {
            var label = shot.Label ?? "";
            if (shot is SavedSlotShot slot) return "Cam " + (slot.Index + 1);
            if (label.StartsWith("cam_", StringComparison.OrdinalIgnoreCase)) return label.Substring(4);
            return label;
        }

        // ================================================================ 2 characters

        private string CharactersSummary()
        {
            var plans = SweepSelection.Plans();
            if (plans.Count == 0) return "none - cameras only";
            if (plans.Count == 1) return $"{plans[0].Name}: {plans[0].Summary}";
            var used = plans.Where(p => p.Enabled.Any(a => a.Count > 0)).ToList();
            var combos = used.Aggregate(1, (n, p) => n * p.Combinations);
            return $"{plans.Count} characters, {combos} combination{(combos == 1 ? "" : "s")}";
        }

        private void DrawCharacters()
        {
            var plans = SweepSelection.Plans();

            if (plans.Count == 0)
                GUILayout.Label(Hint("Add a character to change their outfit, face or clothes between shots. " +
                                     "Without one, each camera is shot once with the scene as it is."), Rich);

            foreach (var plan in plans)
            {
                GUILayout.BeginVertical(Theme.Card(plan.Expanded));

                GUILayout.BeginHorizontal();
                var sex = SweepSelection.IsFemale(plan.Character) ? "F" : "M";
                if (GUILayout.Button($"{(plan.Expanded ? "▾" : "▸")} <b>{plan.Name}</b> ({sex})   <color=#8B91A1>{plan.Summary}</color>", SectionStyle, GUILayout.Width(Inner - 80)))
                {
                    plan.Expanded = !plan.Expanded;
                    Gui.Relayout();
                }
                if (GUILayout.Button(new GUIContent("Remove", "Take this character out of the sweep (their settings are forgotten)"), GUILayout.Width(62)))
                {
                    SweepSelection.Remove(plan);
                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                    break;
                }
                GUILayout.EndHorizontal();

                if (plan.Expanded) DrawVariations(plan);

                GUILayout.EndVertical();
            }

            DrawAddCharacter(plans);

            var used = plans.Where(p => p.Enabled.Any(a => a.Count > 0)).ToList();
            if (used.Count > 1)
            {
                var combos = used.Aggregate(1, (n, p) => n * p.Combinations);
                GUILayout.Label(Hint("Every combination: " + string.Join(" × ", used.Select(p => $"{p.Name} {p.Combinations}").ToArray()) +
                                     $" = {combos} shots per camera"), Rich);
            }
        }

        private void DrawVariations(CharacterPlan plan)
        {
            foreach (var axis in plan.Axes)
            {
                GUILayout.BeginHorizontal();
                var count = axis.Enabled ? (axis.Count > 0 ? $"  <color=#8B91A1>×{axis.Count}</color>" : "  <color=#FBBF24>nothing to cycle</color>") : "";
                var name = axis.Enabled ? Theme.Title(axis.DisplayName) : $"<b>{axis.DisplayName}</b>";
                var on = GUILayout.Toggle(axis.Enabled, $" {name}{count}", RichToggle, GUILayout.Width(190));
                if (on != axis.Enabled) { axis.Enabled = on; Gui.Relayout(); }
                if (!axis.Enabled) GUILayout.Label(Hint(axis.Description), Rich);
                GUILayout.EndHorizontal();

                if (!axis.Enabled) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Space(18);
                GUILayout.BeginVertical();
                axis.DrawSettings();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
            }
        }

        private void DrawAddCharacter(List<CharacterPlan> plans)
        {
            var available = SweepSelection.AllCharacters().Where(c => !SweepSelection.IsAdded(c)).ToList();

            GUILayout.BeginHorizontal();
            if (available.Count > 0 && GUILayout.Button(_adding ? "Cancel" : "+ Add character"))
            {
                _adding = !_adding;
                Gui.Relayout();
            }
            var selected = (KKAPI.Studio.StudioAPI.GetSelectedCharacters() ?? Enumerable.Empty<OCIChar>())
                .Where(c => c != null && !SweepSelection.IsAdded(c)).ToList();
            if (selected.Count > 0 && GUILayout.Button(new GUIContent("+ Add selected", "Add the characters selected in Studio's workspace")))
            {
                foreach (var c in selected) SweepSelection.Add(c);
                _adding = false;
                Gui.Relayout();
            }
            GUILayout.EndHorizontal();

            if (available.Count == 0 && plans.Count == 0)
                GUILayout.Label(Hint("No characters in the scene."), Rich);

            if (!_adding) return;
            foreach (var c in available)
            {
                if (!GUILayout.Button($"{SweepRunner.CharName(c)}  ({(SweepSelection.IsFemale(c) ? "F" : "M")})")) continue;
                SweepSelection.Add(c);
                _adding = false;
                Gui.Relayout();
                break;
            }
        }

        // ================================================================ 3 image

        private static readonly string[] FormatNames = { "PNG", "WebP", "WebP lossy", "JPG" };

        private string ImageSummary()
        {
            var e = _estimate;
            var parts = new List<string> { FormatNames[(int)_plugin.Format.Value], $"{e.Width}×{e.Height}" };
            if (e.Upsampling > 1) parts.Add($"{e.Upsampling}×");
            if (_plugin.Transparent.Value && _plugin.Format.Value != ImageFormat.Jpg) parts.Add("transparent");
            return string.Join(" · ", parts.ToArray());
        }

        private void DrawImageSettings(SweepRunner runner)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Format", GUILayout.Width(LabelW));
            var fmt = (ImageFormat)Choice((int)_plugin.Format.Value, FormatNames);
            if (fmt != _plugin.Format.Value) { _plugin.Format.Value = fmt; Gui.Relayout(); }
            GUILayout.EndHorizontal();

            var format = _plugin.Format.Value;
            GUILayout.Label(Hint(FormatHint(format)), Rich);

            if (format == ImageFormat.WebPLossy || format == ImageFormat.Jpg)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Quality", GUILayout.Width(LabelW));
                _plugin.Quality.Value = Mathf.RoundToInt(GUILayout.HorizontalSlider(_plugin.Quality.Value, 1, 100));
                GUILayout.Label(_plugin.Quality.Value.ToString(), GUILayout.Width(28));
                GUILayout.EndHorizontal();
            }

            DrawRenderSize();

            GUI.enabled = !runner.IsRunning && format != ImageFormat.Jpg;
            var transparent = GUILayout.Toggle(_plugin.Transparent.Value && format != ImageFormat.Jpg,
                new GUIContent(" Transparent background", "Only where nothing 3D (a map, an object) is behind the characters"), RichToggle);
            if (format != ImageFormat.Jpg) _plugin.Transparent.Value = transparent;
            GUI.enabled = !runner.IsRunning;

            GUILayout.Space(2);
            var enabled = GUI.enabled;
            GUI.enabled = true;
            if (GUILayout.Button($"{(_showAdvanced ? "▾" : "▸")} Advanced   <color=#8B91A1>{AdvancedSummary()}</color>", SectionStyle, GUILayout.Width(Inner)))
            {
                _showAdvanced = !_showAdvanced;
                Gui.Relayout();
            }
            GUI.enabled = enabled;
            if (_showAdvanced) DrawAdvanced(format);
        }

        private static string FormatHint(ImageFormat f)
        {
            switch (f)
            {
                case ImageFormat.WebP:
                    return WebpEncoder.Available
                        ? "Lossless, usually 25-40% smaller than PNG, keeps transparency."
                        : "<color=#F87171>WebP encoder (libwebp_cs.dll) not found next to the plugin - PNG will be saved.</color>";
                case ImageFormat.WebPLossy:
                    return WebpEncoder.Available
                        ? "Much smaller files, keeps transparency. Quality sets how much detail is kept."
                        : "<color=#F87171>WebP encoder (libwebp_cs.dll) not found next to the plugin - PNG will be saved.</color>";
                case ImageFormat.Jpg: return "Small files, no transparency.";
                default: return "Lossless, opens anywhere. Largest files.";
            }
        }

        // Common output sizes. ↔ flips any of them to portrait (e.g. 1080×1920 for VN sprites).
        private static readonly string[] SizeNames = { "Screen", "1080p", "1440p", "4K" };
        private static readonly int[,] SizeValues = { { -1, -1 }, { 1920, 1080 }, { 2560, 1440 }, { 3840, 2160 } };
        private static readonly string[] UpsampleNames = { "1×", "2×", "3×", "4×" };

        /// <summary>Output size, upsampling, presets, and what that means in pixels.</summary>
        private void DrawRenderSize()
        {
            // Older settings used 0 for "Screencap's / the screen's"; turn that into real numbers once.
            if (_plugin.Width.Value <= 0 || _plugin.Height.Value <= 0 || _plugin.Upsampling.Value <= 0)
            {
                Estimate.RenderSize(_plugin, out var w0, out var h0, out var u0);
                _plugin.Width.Value = Math.Max(16, w0);
                _plugin.Height.Value = Math.Max(16, h0);
                _plugin.Upsampling.Value = Mathf.Clamp(u0, 1, 4);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Size", GUILayout.Width(LabelW));
            _plugin.Width.Value = Math.Max(16, IntField(_plugin.Width.Value, 54));
            GUILayout.Label("×", GUILayout.Width(12));
            _plugin.Height.Value = Math.Max(16, IntField(_plugin.Height.Value, 54));
            if (GUILayout.Button(new GUIContent("↔", "Swap width and height (landscape / portrait)"), GUILayout.Width(28)))
            {
                var t = _plugin.Width.Value;
                _plugin.Width.Value = _plugin.Height.Value;
                _plugin.Height.Value = t;
            }
            GUILayout.EndHorizontal();

            // Presets keep the current orientation; the one matching the current size is highlighted.
            var portrait = _plugin.Height.Value > _plugin.Width.Value;
            var current = -1;
            for (var i = 0; i < SizeNames.Length; i++)
            {
                PresetSize(i, portrait, out var pw, out var ph);
                if (pw == _plugin.Width.Value && ph == _plugin.Height.Value) current = i;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Space(LabelW + 4);
            var picked = Choice(current, SizeNames, Inner - LabelW - 4);
            if (picked != current && picked >= 0)
            {
                PresetSize(picked, portrait, out var pw, out var ph);
                _plugin.Width.Value = pw;
                _plugin.Height.Value = ph;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Upsample", "Render this many times larger and shrink it down: smoother edges and fine detail, slower, more memory. 2× is the sweet spot."), GUILayout.Width(LabelW));
            _plugin.Upsampling.Value = Choice(Mathf.Clamp(_plugin.Upsampling.Value, 1, 4) - 1, UpsampleNames) + 1;
            GUILayout.EndHorizontal();

            // What the file will be and how big the render behind it is.
            Estimate.RenderSize(_plugin, out var outW, out var outH, out var up);
            var renderW = outW * up;
            var renderH = outH * up;
            const string auto = "";
            var line = up > 1
                ? $"Saves {outW}×{outH}, rendered at {renderW}×{renderH} ({up}×){auto}"
                : $"Saves and renders {outW}×{outH}{auto}";

            var max = SystemInfo.maxTextureSize;
            if (renderW > max || renderH > max)
                line = $"<color=#F87171>{line} - over this GPU's {max}px limit; upsampling will be lowered.</color>";
            else if ((long)renderW * renderH > 8192L * 8192L)
                line = $"<color=#FBBF24>{line} - very large render, needs a lot of video memory.</color>";
            else
                line = Hint(line);

            GUILayout.Label(line, Rich);
        }

        private static void PresetSize(int i, bool portrait, out int w, out int h)
        {
            w = SizeValues[i, 0];
            h = SizeValues[i, 1];
            if (w < 0) { w = Screen.width; h = Screen.height; }
            if (portrait != (h > w)) { var t = w; w = h; h = t; }
        }

        private string AdvancedSummary()
        {
            var engine = _plugin.Engine.Value == CaptureEngine.Screencap && ScreencapBridge.Available ? "Screencap" : "Camera Sweep renderer";
            var shadows = _plugin.ShadowResolution.Value > 0 ? $", shadows {_plugin.ShadowResolution.Value}" : "";
            return engine + shadows;
        }

        private static readonly int[] ShadowSizes = { 0, 2048, 4096, 8192, 16384 };

        private void DrawAdvanced(ImageFormat format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Engine", GUILayout.Width(LabelW));
            _plugin.Engine.Value = (CaptureEngine)Choice((int)_plugin.Engine.Value, new[] { "Screencap", "Camera Sweep" });
            GUILayout.EndHorizontal();
            if (ScreencapBridge.Available)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(LabelW + 4);
                if (GUILayout.Button(new GUIContent("Compare engines", "Shoot the current view once with each engine into the 'test' folder, to compare them side by side"),
                        GUILayout.Width(140)))
                    _plugin.StartTestShot(compareEngines: true);
                GUILayout.EndHorizontal();
            }
            GUILayout.Label(Hint(_plugin.Engine.Value == CaptureEngine.Screencap
                ? (ScreencapBridge.Available ? "Screencap's render shot." : "Screencap isn't installed - Camera Sweep's renderer is used.")
                : "Camera Sweep's own renderer (no Screencap needed)."), Rich);

            if (format == ImageFormat.WebP || format == ImageFormat.WebPLossy)
            {
                var maxEffort = format == ImageFormat.WebP ? 9 : 6;
                GUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("WebP effort", "How hard WebP works to shrink files. Past about 4 the gain is a few % for a lot more time."), GUILayout.Width(LabelW));
                var effort = Mathf.Min(_plugin.WebpEffort.Value, maxEffort);
                var next = Mathf.RoundToInt(GUILayout.HorizontalSlider(effort, 0, maxEffort));
                if (next != effort) _plugin.WebpEffort.Value = next;
                GUILayout.Label(next + (next <= 2 ? " fast" : next >= maxEffort - 1 ? " small" : ""), GUILayout.Width(56));
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Shadows", "Shadow map resolution during captures (both engines). Off = the game's setting."), GUILayout.Width(LabelW));
            var shadowIndex = Array.IndexOf(ShadowSizes, _plugin.ShadowResolution.Value);
            var shadowPick = Choice(shadowIndex, ShadowSizes.Select(z => z == 0 ? "Game" : (z / 1024) + "K").ToArray());
            if (shadowPick != shadowIndex && shadowPick >= 0) _plugin.ShadowResolution.Value = ShadowSizes[shadowPick];
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Cascades", "Shadow cascades during captures. More = sharper shadows near the camera."), GUILayout.Width(LabelW));
            _plugin.ShadowCascades.Value = (ShadowCascadeSetting)Choice((int)_plugin.ShadowCascades.Value, new[] { "Game", "1", "2", "4" });
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Amb. occl.", "Ambient occlusion bands when upsampled (Camera Sweep renderer only)."), GUILayout.Width(LabelW));
            _plugin.AmbientOcclusion.Value = (AmbientOcclusionSetting)Choice((int)_plugin.AmbientOcclusion.Value, new[] { "Keep", "Off if upsampled", "Off" });
            GUILayout.EndHorizontal();

            _plugin.PauseMotion.Value = GUILayout.Toggle(_plugin.PauseMotion.Value,
                new GUIContent(" Pause blinking and breathing while shooting", "Every character, put back when the sweep ends. Breathing needs AdvIK."));

            _plugin.CameraPresets.Value = GUILayout.Toggle(_plugin.CameraPresets.Value,
                new GUIContent(" Use each camera's Graphics preset", "Set per camera in Studio Theme's camera menu (needs Studio Theme and Graphics). The look from before comes back after the sweep."));

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Settle", "Frames to wait after switching camera before capturing. Raise if shots catch the camera mid-move."), GUILayout.Width(LabelW));
            _plugin.SettleFrames.Value = Mathf.Clamp(IntField(_plugin.SettleFrames.Value, 36), 0, 120);
            GUILayout.Label(Hint("frames after each camera switch"), Rich);
            GUILayout.EndHorizontal();
        }

        // ================================================================ 3 video

        private string VideoSummary()
        {
            var who = _plugin.VideoTarget != null ? SweepRunner.CharName(_plugin.VideoTarget) : "selected character";
            return $"{who}, {_plugin.VideoCycles.Value:0.##} loop{(_plugin.VideoCycles.Value == 1f ? "" : "s")}";
        }

        private void DrawVideoSettings()
        {
            if (!VideoExportBridge.Available)
            {
                GUILayout.Label("<color=#F87171>The VideoExport plugin isn't installed - video mode is unavailable.</color>", Rich);
                return;
            }

            var chars = SweepSelection.AllCharacters();

            // Character picker: < name >   (defaults to the workspace selection at run time)
            var current = _plugin.VideoTarget != null && chars.Contains(_plugin.VideoTarget) ? _plugin.VideoTarget : null;
            var idx = current == null ? -1 : chars.IndexOf(current);

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Timed by", "Whose animation sets the video length"), GUILayout.Width(LabelW));
            if (GUILayout.Button("<", GUILayout.Width(24)) && chars.Count > 0)
                _plugin.VideoTarget = chars[(idx - 1 + chars.Count + (idx < 0 ? 1 : 0)) % chars.Count];
            GUILayout.Label(current != null ? SweepRunner.CharName(current) : "(selected in workspace)", GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", GUILayout.Width(24)) && chars.Count > 0)
                _plugin.VideoTarget = chars[(idx + 1) % chars.Count];
            GUILayout.EndHorizontal();

            if (current != null && GUILayout.Button("Use workspace selection instead"))
                _plugin.VideoTarget = null;

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Loops", "How many loops of the animation each video lasts (fractions allowed)"), GUILayout.Width(LabelW));
            if (float.TryParse(GUILayout.TextField(_plugin.VideoCycles.Value.ToString("0.##"), GUILayout.Width(50)), out var cycles) && cycles > 0)
                _plugin.VideoCycles.Value = cycles;
            GUILayout.Label(new GUIContent("Warm-up", "Loops played before recording so physics settles and every video starts on a loop boundary"), GUILayout.Width(60));
            if (int.TryParse(GUILayout.TextField(_plugin.VideoPrewarm.Value.ToString(), GUILayout.Width(30)), out var prewarm) && prewarm >= 0)
                _plugin.VideoPrewarm.Value = prewarm;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(VideoExportBridge.WindowOpen ? "Hide VideoExport settings" : "VideoExport settings",
                    "Framerate, format and quality live in VideoExport's own window. It also opens while recording to show the ETA."),
                    GUILayout.Width(170)))
                VideoExportBridge.WindowOpen = !VideoExportBridge.WindowOpen;
            GUILayout.Label(Hint("The animation must be playing."), Rich);
            GUILayout.EndHorizontal();
        }

        // ================================================================ 4 files

        private string FilesSummary() => FirstFileName();

        private void DrawFiles()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Folder", GUILayout.Width(LabelW));
            _plugin.OutputFolder.Value = GUILayout.TextField(_plugin.OutputFolder.Value, GUILayout.Width(Inner - 130));
            if (GUILayout.Button(new GUIContent("Open", "Open the output folder"), GUILayout.Width(46)))
                OpenFolder(FileNames.ResolveFromGameRoot(_plugin.OutputFolder.Value));
            GUILayout.EndHorizontal();
            _plugin.FolderPerRun.Value = GUILayout.Toggle(_plugin.FolderPerRun.Value, " New date-time folder for each run");

            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent("Prefix", "Fills {prefix} in the file name"), GUILayout.Width(LabelW));
            _plugin.FilePrefix.Value = GUILayout.TextField(_plugin.FilePrefix.Value, GUILayout.Width(Inner - 80));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("File name", GUILayout.Width(LabelW));
            _plugin.NameTemplateText.Value = GUILayout.TextField(_plugin.NameTemplateText.Value, GUILayout.Width(Inner - 132));
            if (GUILayout.Button("Reset", GUILayout.Width(48))) _plugin.NameTemplateText.Value = NameTemplate.Default;
            GUILayout.EndHorizontal();

            GUILayout.Label($"<i>e.g. {FirstFileName()}</i>", Rich);

            var tokens = GUILayout.Toggle(_showTokens, " Show what I can put in the name");
            if (tokens != _showTokens) { _showTokens = tokens; Gui.Relayout(); }
            if (_showTokens)
            {
                string T(string t) => $"<color={Theme.TokenHex}><b>{t}</b></color>";
                string X(string t) => $"<color={Theme.ExampleHex}>{t}</color>";
                GUILayout.Label(
                    $"{T("{prefix}")} {T("{scene}")} {T("{camera}")} {T("{n}")} {T("{date}")} {T("{time}")}\n" +
                    $"{T("{character}")} {T("{characters}")} {T("{axes}")}\n" +
                    $"{T("{swap}")} {T("{outfit}")} {T("{pose}")} {T("{clothes}")} {T("{face}")} {T("{extras}")}\n\n" +
                    "Variations that are off drop out of the name.\n" +
                    $"/ makes subfolders, e.g. {X("{character}/{character}_{outfit}_{face}")}\n" +
                    $"With several characters {T("{face}")} reads {X("Aiko-smile_Kenji-grin")}.", Theme.Panel);
            }
        }

        /// <summary>What the first file of the next run would be called.</summary>
        private string FirstFileName()
        {
            var plans = SweepSelection.Plans();
            var firstCamera = _cameraCache.FirstOrDefault(SweepSelection.IsOn);
            var labels = plans.SelectMany(p => p.Enabled
                .Select(a => new VariationLabel(p.Name, a.Name, a.PreviewLabel))
                .Where(l => !string.IsNullOrEmpty(l.Label)));
            var tokens = NameTemplate.Values(FileNames.Sanitize(_plugin.FilePrefix.Value), firstCamera?.Label ?? "cam01", 1,
                _estimate?.Total ?? 1, DateTime.Now, plans.Count > 0 ? plans[0].Name : "", plans.Select(p => p.Name), labels);
            var ext = _plugin.Mode.Value == OutputMode.Video ? ".mp4" : _plugin.Runner.Extension;
            var folder = _plugin.FolderPerRun.Value ? "<date-time>\\" : "";
            return folder + NameTemplate.Build(_plugin.NameTemplateText.Value, tokens) + ext;
        }

        // ================================================================ footer

        private void DrawFooter(SweepRunner runner, bool video)
        {
            GUILayout.Space(6);
            var e = _estimate;

            foreach (var w in Warnings(video))
                GUILayout.Label(w, Rich);

            var what = video ? "video" : "image";
            GUILayout.Label($"<b>{e.Total} {what}{(e.Total == 1 ? "" : "s")}</b>   <color=#8B91A1>{e.Breakdown}</color>", Rich);

            if (video)
            {
                GUILayout.Label(Hint(e.BytesEach < 0
                    ? "Size and time: shown after your first video sweep."
                    : $"~{Estimate.Size(e.BytesEach)} each, ~{Estimate.Size(e.BytesTotal)} total · ~{Estimate.Duration(e.SecondsTotal)}"), Rich);
            }
            else
            {
                var free = e.FreeBytes >= 0 ? $" · {Estimate.Size(e.FreeBytes)} free" : "";
                var guess = !e.SizeMeasured || !e.TimeMeasured ? "  (rough guess until your first sweep)" : "";
                GUILayout.Label(Hint($"~{Estimate.Size(e.BytesTotal)} · ~{Estimate.Duration(e.SecondsTotal)}{free}{guess}"), Rich);
            }

            if (runner.IsRunning)
            {
                var frac = runner.Total > 0 ? Mathf.Clamp01(runner.Done / (float)runner.Total) : 0f;
                var bar = GUILayoutUtility.GetRect(Width - 20, 18);
                GUI.Box(bar, "");
                GUI.Box(new Rect(bar.x, bar.y, bar.width * frac, bar.height), "", Theme.Progress);
                GUI.Label(bar, $"  {runner.Done} / {runner.Total}   {runner.Current}");
                if (GUILayout.Button("Cancel  (Esc)", GUILayout.Height(30))) { runner.Cancel(); Gui.Relayout(); }
            }
            else
            {
                GUILayout.BeginHorizontal();
                if (!video && GUILayout.Button(new GUIContent("Test shot", "Capture the current view once with these image settings, into the 'test' folder"),
                        GUILayout.Width(90), GUILayout.Height(32)))
                {
                    _plugin.StartTestShot();
                    Gui.Relayout(); // the footer changes to the progress bar; finish this pass with the new layout
                }

                var label = video
                    ? $"Record {e.Total} video{(e.Total == 1 ? "" : "s")}"
                    : $"Capture {e.Total} image{(e.Total == 1 ? "" : "s")}";
                if (e.SecondsTotal > 0) label += $"  ·  ~{Estimate.Duration(e.SecondsTotal)}";
                GUI.enabled = e.Total > 0;
                if (GUILayout.Button(label, Theme.Primary, GUILayout.Height(32)))
                {
                    _plugin.StartSweep();
                    Gui.Relayout();
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            if (!runner.IsRunning && !string.IsNullOrEmpty(runner.LastFolder) && Directory.Exists(runner.LastFolder)
                && GUILayout.Button("Open last output folder"))
                OpenFolder(runner.LastFolder);
            if (GUILayout.Button("Close", GUILayout.Width(60))) { Visible = false; Gui.Relayout(); }
            GUILayout.EndHorizontal();
        }

        /// <summary>Things that would make the run do less than you expect, shown above the Capture button.</summary>
        private IEnumerable<string> Warnings(bool video)
        {
            const string red = "<color=#F87171>", orange = "<color=#FBBF24>", end = "</color>";

            if (_cameraCache.Count(SweepSelection.IsOn) == 0)
                yield return red + "No cameras are on." + end;

            foreach (var plan in SweepSelection.Plans())
            {
                if (!plan.Enabled.Any())
                    yield return orange + $"{plan.Name} has no variations ticked - they won't change." + end;
                foreach (var axis in plan.Enabled.Where(a => a.Count == 0))
                    yield return orange + $"{plan.Name}: {axis.DisplayName} has nothing to cycle (empty folder or no presets)." + end;
            }

            if (video && !VideoExportBridge.Available)
                yield return red + "Video needs the VideoExport plugin." + end;

            if (!video && (_plugin.Format.Value == ImageFormat.WebP || _plugin.Format.Value == ImageFormat.WebPLossy) && !WebpEncoder.Available)
                yield return orange + "WebP encoder missing - PNG will be saved." + end;

            var e = _estimate;
            if (e.FreeBytes >= 0 && e.BytesTotal > e.FreeBytes * 0.9)
                yield return red + $"Not enough free space: needs ~{Estimate.Size(e.BytesTotal)}, {Estimate.Size(e.FreeBytes)} free." + end;
            else if (e.Total >= 500)
                yield return orange + $"Large run: {e.Total} {(video ? "videos" : "images")}." + end;
        }

        // ================================================================ helpers

        private static void OpenFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(folder);
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning(ex.Message);
            }
        }

        private static int IntField(int value, float width)
        {
            var text = GUILayout.TextField(value.ToString(), GUILayout.Width(width));
            if (text.Length == 0) return 0;
            return int.TryParse(text, out var v) && v >= 0 ? v : value;
        }

        private const float LabelW = 70f;

        /// <summary>A row of choices filling the given width (default: the rest of the row); the selected one in the accent colour.</summary>
        private static int Choice(int selected, string[] labels, float width = Inner - LabelW - 4)
        {
            return GUILayout.Toolbar(selected, labels, Theme.Choice, GUILayout.Width(width));
        }

        private static string Hint(string text) => Theme.Hint(text);
        private static GUIStyle Rich => Theme.Rich;
        private static GUIStyle RichToggle => Theme.RichToggle;
        private static GUIStyle SectionStyle => Theme.Section;
    }
}
