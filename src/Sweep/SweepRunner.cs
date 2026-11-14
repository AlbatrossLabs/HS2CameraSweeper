using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HS2CameraSweep.Axes;
using HS2CameraSweep.Capture;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.Sweep
{
    /// <summary>
    /// Runs one sweep: for every combination of every added character's variations, shoot every camera.
    /// Loop order (outer to inner): variation kind (swap, outfit, clothes, face, extras), then character,
    /// then cameras. So slow changes like outfits load as few times as possible.
    /// The scene's camera, outfits and expressions are restored afterwards, including on cancel.
    /// </summary>
    internal sealed class SweepRunner
    {
        private readonly CameraSweepPlugin _plugin;
        private readonly ShotWriter _writer;
        private readonly MotionPause _pause = new MotionPause();
        private bool _cancelRequested;

        public SweepRunner(CameraSweepPlugin plugin)
        {
            _plugin = plugin;
            _writer = new ShotWriter(plugin);
        }

        public bool IsRunning { get; private set; }

        /// <summary>A full sweep of still images is running (the screen can be covered).</summary>
        public bool IsSweepingImages { get; private set; }
        public int Done { get; private set; }
        public int Total { get; private set; }
        public string Current { get; private set; } = "";
        public string LastFolder { get; private set; } = "";
        public bool UsingScreencap => _writer.UsingScreencap;
        public string CaptureDescription => _writer.Description;
        public ImageFormat Format => _writer.Format;
        public string Extension => _writer.Extension;

        public void Cancel()
        {
            if (IsRunning) _cancelRequested = true;
        }

        /// <summary>Preview of how many images a run would produce with the current settings.</summary>
        /// <summary>Every camera the current source settings find, on or off (for the window's toggle list).</summary>
        public List<CameraShot> ListCameras()
        {
            var studio = Studio.Studio.Instance;
            if (studio == null) return new List<CameraShot>();
            return CameraShotCollector.Collect(studio, _plugin.Source.Value, _plugin.SkipUnsetSlots.Value);
        }

        public IEnumerator Run()
        {
            var studio = Studio.Studio.Instance;
            if (studio == null || IsRunning) yield break;

            var shots = CameraShotCollector.Collect(studio, _plugin.Source.Value, _plugin.SkipUnsetSlots.Value)
                .Where(SweepSelection.IsOn).ToList();
            if (shots.Count == 0)
            {
                CameraSweepPlugin.Logger.LogMessage(
                    "Camera Sweep: no cameras to shoot. Save a camera slot or add a camera object, and make sure at least one is ticked.");
                yield break;
            }

            var video = _plugin.Mode.Value == OutputMode.Video;
            OCIChar videoTarget = null;
            if (video)
            {
                if (!VideoExportBridge.Available)
                {
                    CameraSweepPlugin.Logger.LogMessage("Camera Sweep: video mode needs the VideoExport plugin.");
                    yield break;
                }

                videoTarget = ResolveVideoTarget(studio);
                if (videoTarget == null)
                {
                    CameraSweepPlugin.Logger.LogMessage("Camera Sweep: pick a character for video mode (select one in the workspace or in the Camera Sweep window).");
                    yield break;
                }
            }

            IsRunning = true;
            IsSweepingImages = !video;
            _cancelRequested = false;
            var startTime = Time.realtimeSinceStartup;
            Estimate.RenderSize(_plugin, out var renderW, out var renderH, out var renderUp);
            var format = _writer.Format;
            var transparentRun = _plugin.Transparent.Value && format != ImageFormat.Jpg;
            Done = 0;
            var originalSelection = studio.treeNodeCtrl.selectNode;

            // Remember the view so we can put it back.
            var originalView = studio.cameraCtrl.Export();
            var originalCamera = studio.ociCamera;

            // Prepare every enabled variation of every added character (each snapshots what it will change).
            var plans = SweepSelection.Plans();
            var prepared = new List<IVariationAxis>();
            var axes = new List<Dimension>();
            foreach (var kind in CharacterPlan.Kinds)
            foreach (var plan in plans)
            {
                var axis = plan.Axis(kind);
                if (!axis.Enabled) continue;

                prepared.Add(axis);
                IList<AxisValue> values;
                try { values = axis.Prepare(); }
                catch (Exception ex)
                {
                    CameraSweepPlugin.Logger.LogError($"{plan.Name}: {axis.DisplayName} failed to prepare: {ex}");
                    values = null;
                }

                if (values != null && values.Count > 0)
                    axes.Add(new Dimension(plan, axis, values));
            }

            var combos = axes.Aggregate(1, (n, a) => n * a.Values.Count);
            Total = combos * shots.Count;

            var started = DateTime.Now;
            var outputRoot = FileNames.ResolveFromGameRoot(_plugin.OutputFolder.Value);
            var folderPerRun = _plugin.FolderPerRun.Value;
            var runFolder = folderPerRun ? Path.Combine(outputRoot, started.ToString("yyyyMMdd-HHmmss")) : outputRoot;
            LastFolder = runFolder;
            var prefix = FileNames.Sanitize(_plugin.FilePrefix.Value);
            var template = _plugin.NameTemplateText.Value;
            var unique = new NameTemplate.Uniquifier();
            var shotNumber = 0;

            CameraSweepPlugin.Logger.LogMessage(video
                ? $"Camera Sweep: {Total} video(s) of {CharName(videoTarget)}, {_plugin.VideoCycles.Value:0.##} cycle(s) each - {shots.Count} camera(s) x {combos} variation(s)."
                : $"Camera Sweep: {Total} shot(s) - {shots.Count} camera(s) x {combos} variation(s).");

            if (!video) _writer.BeginBatch();
            try
            {
                // Odometer over axis value indexes. Only axes whose index changed get re-applied,
                // so an outfit (slow: rebuilds meshes) is loaded once per outfit, not once per expression.
                var index = new int[axes.Count];
                var applied = Enumerable.Repeat(-1, axes.Count).ToArray();

                for (var combo = 0; combo < combos && !_cancelRequested; combo++)
                {
                    for (var a = 0; a < axes.Count; a++)
                    {
                        if (applied[a] == index[a]) continue;
                        yield return axes[a].Axis.Apply(axes[a].Values[index[a]]);
                        applied[a] = index[a];

                        // A new outer value (e.g. a new outfit) can reset what inner variations set, so
                        // re-apply the inner ones of the same character.
                        for (var b = a + 1; b < axes.Count; b++)
                            if (axes[b].Plan == axes[a].Plan) applied[b] = -1;
                    }

                    var several = axes.Select(d => d.Plan).Distinct().Count() > 1;
                    var comboLabel = string.Join("  ",
                        axes.Select((d, a) => (several ? d.Plan.Name + " " : "") + d.Axis.Name + ":" + d.Values[index[a]].Label).ToArray());

                    foreach (var shot in shots)
                    {
                        if (_cancelRequested) break;

                        Current = string.IsNullOrEmpty(comboLabel) ? shot.Label : comboLabel + " / " + shot.Label;
                        shot.Apply(studio);
                        // No blinking or breathing in stills (re-checked each shot: a swap loads new settings).
                        if (!video && _plugin.PauseMotion.Value) _pause.Apply();

                        for (var f = 0; f < _plugin.SettleFrames.Value; f++)
                            yield return null;

                        // Point the camera again right before the shot: a character or outfit that was still
                        // loading during the settle frames can nudge the view (one shot came out zoomed out).
                        if (!video) shot.Apply(studio);

                        shotNumber++;
                        // Names are read now, so a swapped-in character's own name is used.
                        var tokens = NameTemplate.Values(prefix, shot.Label, shotNumber, Total, started,
                            plans.Count > 0 ? plans[0].Name : "",
                            plans.Select(p => p.Name),
                            axes.Select((d, a) => new VariationLabel(d.Plan.Name, d.Axis.Name, d.Values[index[a]].Label)));
                        var name = unique.Make(NameTemplate.Build(template, tokens));
                        // Writing straight into the output folder: never overwrite an earlier run's file.
                        if (!folderPerRun)
                            while (File.Exists(Path.Combine(runFolder, name + (video ? ".mp4" : _writer.Extension))))
                                name = unique.Make(name);
                        if (video)
                            yield return VideoExportBridge.Record(videoTarget, _plugin.VideoCycles.Value, _plugin.VideoPrewarm.Value,
                                Path.Combine(runFolder, name), () => _cancelRequested);
                        else
                            yield return _writer.Capture(Path.Combine(runFolder, name + _writer.Extension));
                        Done++;
                    }

                    // Advance the odometer (last axis spins fastest).
                    for (var a = axes.Count - 1; a >= 0; a--)
                    {
                        if (++index[a] < axes[a].Values.Count) break;
                        index[a] = 0;
                    }
                }

                // Images are compressed in the background; wait for the last ones before reporting.
                if (!video)
                {
                    Current = "saving...";
                    yield return _writer.Flush();
                }
            }
            finally
            {
                if (!video) _writer.EndBatch();

                if (video)
                {
                    try
                    {
                        if (originalSelection != null) SweepSelection.SelectOnly(originalSelection);
                    }
                    catch (Exception ex) { CameraSweepPlugin.Logger.LogDebug("Could not restore selection: " + ex.Message); }
                }

                // Outer axes first: putting the original outfit back can reset clothing state, so the clothes
                // axis restores after it.
                for (var i = 0; i < prepared.Count; i++)
                {
                    try { prepared[i].Restore(); }
                    catch (Exception ex) { CameraSweepPlugin.Logger.LogWarning($"{prepared[i].DisplayName} restore failed: {ex.Message}"); }
                }

                // After the variations: a swap restore reloads the original card, then this sets
                // blinking / breathing back to what they were.
                _pause.Restore();

                RestoreView(studio, originalView, originalCamera);

                CameraSweepPlugin.Logger.LogMessage(_cancelRequested
                    ? $"Camera Sweep cancelled after {Done} of {Total} shots. Saved to {runFolder}"
                    : $"Camera Sweep done: {Done} shot(s) saved to {runFolder}");

                Estimate.Record(_plugin, runFolder, started, Done, Time.realtimeSinceStartup - startTime, video, format, transparentRun,
                    renderW, renderH, renderUp, _plugin.VideoCycles.Value, _plugin.VideoPrewarm.Value);

                Current = "";
                IsRunning = false;
                IsSweepingImages = false;
                _cancelRequested = false;
            }
        }

        /// <summary>
        /// Capture the current view once with the current image settings, into "test" in the output folder.
        /// compareEngines: one shot with Screencap and one with Camera Sweep's renderer, side by side.
        /// </summary>
        public IEnumerator TestShot(bool compareEngines = false)
        {
            if (IsRunning) yield break;
            IsRunning = true;
            Done = 0;
            Current = "test shot";
            var folder = Path.Combine(FileNames.ResolveFromGameRoot(_plugin.OutputFolder.Value), "test");
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            var shots = new List<KeyValuePair<CaptureEngine?, string>>();
            if (compareEngines && ScreencapBridge.Available)
            {
                shots.Add(new KeyValuePair<CaptureEngine?, string>(CaptureEngine.Screencap, $"test_{stamp}_screencap"));
                shots.Add(new KeyValuePair<CaptureEngine?, string>(CaptureEngine.BuiltIn, $"test_{stamp}_camerasweep"));
            }
            else
            {
                shots.Add(new KeyValuePair<CaptureEngine?, string>(null, "test_" + stamp));
            }
            Total = shots.Count;

            _writer.BeginBatch();
            try
            {
                if (_plugin.PauseMotion.Value)
                {
                    _pause.Apply();
                    for (var f = 0; f < Mathf.Max(2, _plugin.SettleFrames.Value); f++) yield return null;
                }

                foreach (var shot in shots)
                {
                    _writer.EngineOverride = shot.Key;
                    var path = Path.Combine(folder, shot.Value + _writer.Extension);
                    yield return _writer.Capture(path);
                    LastTestShot = path;
                    Done++;
                }
                _writer.EngineOverride = null;
                yield return _writer.Flush();
            }
            finally
            {
                _writer.EngineOverride = null;
                _writer.EndBatch();
                _pause.Restore();
                LastFolder = folder;
                Current = "";
                IsRunning = false;
                CameraSweepPlugin.Logger.LogMessage(File.Exists(LastTestShot)
                    ? (shots.Count > 1 ? $"Camera Sweep: compare shots saved in {folder}" : $"Camera Sweep test shot: {LastTestShot}")
                    : "Camera Sweep test shot failed - see the log.");
            }
        }

        public string LastTestShot { get; private set; } = "";

        /// <summary>One character's variation with the values this run cycles through.</summary>
        private sealed class Dimension
        {
            public Dimension(CharacterPlan plan, IVariationAxis axis, IList<AxisValue> values)
            {
                Plan = plan;
                Axis = axis;
                Values = values;
            }

            public readonly CharacterPlan Plan;
            public readonly IVariationAxis Axis;
            public readonly IList<AxisValue> Values;
        }

        /// <summary>Window choice if it's still in the scene, else the first selected character, else the only/first character.</summary>
        internal OCIChar ResolveVideoTarget(Studio.Studio studio)
        {
            var chars = studio.dicObjectCtrl.Values.OfType<OCIChar>().ToList();
            if (_plugin.VideoTarget != null && chars.Contains(_plugin.VideoTarget)) return _plugin.VideoTarget;

            var selected = KKAPI.Studio.StudioAPI.GetSelectedCharacters()?.FirstOrDefault(c => c != null);
            if (selected != null) return selected;

            return chars.FirstOrDefault();
        }

        internal static string CharName(OCIChar c)
        {
            var name = c?.treeNodeObject?.textName;
            return string.IsNullOrEmpty(name) ? "character" : name;
        }

        private static void RestoreView(Studio.Studio studio, Studio.CameraControl.CameraData view, OCICamera camera)
        {
            try
            {
                studio.ChangeCamera(null, false, true);
                studio.cameraCtrl.Import(view);
                if (camera != null) studio.ChangeCamera(camera, true);
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning("Could not restore the original camera: " + ex.Message);
            }
        }
    }
}
