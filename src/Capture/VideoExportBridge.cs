using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HS2CameraSweep.Sweep;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.Capture
{
    /// <summary>
    /// Drives Joan6694's VideoExport plugin by reflection to record a video per camera.
    ///
    /// VideoExport already does the hard parts: locks the game clock to the video framerate
    /// (Time.captureFramerate), waits for the animation to reach a loop boundary ("prewarm"),
    /// records an exact number of animation cycles of the selected object's Animator, and encodes with ffmpeg.
    /// This bridge just:
    ///   1. selects the target character in the workspace (VideoExport reads the animator from the selection),
    ///   2. temporarily switches VideoExport to "Limit by: Animation" with our cycle counts,
    ///   3. calls RecordVideo() and waits for it to finish (including encoding),
    ///   4. restores VideoExport's settings and moves the new video into our run folder with our filename.
    ///
    /// Fields used (present in VideoExport 1.3.0 and current): _limitDuration, _selectedLimitDuration,
    /// _limitDurationNumber, _prewarmLoopCount, _currentAnimator, _outputFolder, _autoGenerateVideo,
    /// isRecording, RecordVideo(), StopRecording().
    /// </summary>
    internal static class VideoExportBridge
    {
        public const string VideoExportGuid = "com.joan6694.illusionplugins.videoexport";
        private const int LimitByAnimation = 2; // LimitDurationType: Frames, Seconds, Animation, Timeline

        private static readonly string[] VideoExtensions = { ".mp4", ".webm", ".avi", ".gif", ".mkv" };

        private static bool _resolved;
        private static object _instance;
        private static Type _type;
        private static MethodInfo _record;
        private static MethodInfo _stop;
        private static PropertyInfo _isRecording;

        public static bool Available
        {
            get
            {
                Resolve();
                return _instance != null && _record != null && _isRecording != null;
            }
        }

        public static bool IsRecording
        {
            get
            {
                if (!Available) return false;
                try { return (bool)_isRecording.GetValue(_instance, null); }
                catch { return false; }
            }
        }

        /// <summary>VideoExport's window (framerate, format, quality, and the progress / ETA while recording).</summary>
        public static bool WindowOpen
        {
            get
            {
                if (!Available) return false;
                try { return _type.GetProperty("ShowUI", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null) is bool b && b; }
                catch { return false; }
            }
            set
            {
                if (!Available) return;
                try { _type.GetProperty("ShowUI", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, value, null); }
                catch (Exception ex) { CameraSweepPlugin.Logger.LogDebug("Could not toggle VideoExport's window: " + ex.Message); }
            }
        }

        public static void Stop()
        {
            if (!Available || _stop == null) return;
            try { _stop.Invoke(_instance, null); }
            catch (Exception ex) { CameraSweepPlugin.Logger.LogWarning("VideoExport StopRecording failed: " + ex.Message); }
        }

        /// <summary>
        /// Records one video of <paramref name="target"/>'s animation from the current camera.
        /// Must run as a coroutine. <paramref name="fullPathNoExt"/> is where the finished video is moved
        /// (extension added from whatever VideoExport produced).
        /// </summary>
        public static IEnumerator Record(OCIChar target, float cycles, int prewarm, string fullPathNoExt, Func<bool> cancelRequested)
        {
            if (!Available)
            {
                CameraSweepPlugin.Logger.LogMessage("Camera Sweep: VideoExport not found - can't record video.");
                yield break;
            }

            if (target?.treeNodeObject == null)
            {
                CameraSweepPlugin.Logger.LogMessage("Camera Sweep: no character to record.");
                yield break;
            }

            // VideoExport picks its animator from the workspace selection in its own Update().
            // (SelectOnly: Studio's own select toggles, which deselected every other video's character.)
            SweepSelection.SelectOnly(target.treeNodeObject);
            yield return null;
            yield return null;

            var animator = GetField(_instance, "_currentAnimator") as Animator;
            if (animator == null)
            {
                CameraSweepPlugin.Logger.LogMessage($"Camera Sweep: {SweepRunner.CharName(target)} has no animator - skipped.");
                yield break;
            }

            if (animator.speed <= 0.001f || animator.GetCurrentAnimatorStateInfo(0).length <= 0f)
            {
                // VideoExport would wait forever for a loop boundary on a paused animation.
                CameraSweepPlugin.Logger.LogMessage("Camera Sweep: the character's animation is paused or empty - start it playing first.");
                yield break;
            }

            // Snapshot VideoExport's duration settings, then apply ours.
            var saved = new Dictionary<string, object>
            {
                { "_limitDuration", GetField(_instance, "_limitDuration") },
                { "_selectedLimitDuration", GetField(_instance, "_selectedLimitDuration") },
                { "_limitDurationNumber", GetField(_instance, "_limitDurationNumber") },
                { "_prewarmLoopCount", GetField(_instance, "_prewarmLoopCount") },
            };

            var outputFolder = ReadOutputFolder();
            var before = ListVideos(outputFolder);
            var startedAt = DateTime.Now.AddSeconds(-1);

            // VideoExport takes transparency from Screencap's own setting, and an .mp4 can't hold it; keep videos opaque.
            var savedAlpha = ScreencapBridge.ForceOpaque();
            // Show VideoExport's window: it has the frame count and ETA for this video.
            WindowOpen = true;

            try
            {
                SetField(_instance, "_limitDuration", true);
                SetField(_instance, "_selectedLimitDuration", LimitByAnimation);
                SetField(_instance, "_limitDurationNumber", Mathf.Max(0.001f, cycles));
                SetField(_instance, "_prewarmLoopCount", Mathf.Max(0, prewarm));
                _record.Invoke(_instance, null);
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogError("VideoExport RecordVideo failed: " + (ex.InnerException ?? ex));
                Restore(saved);
                ScreencapBridge.RestoreAlpha(savedAlpha);
                yield break;
            }

            // Wait for it to start, then to finish (finish includes ffmpeg encoding).
            var startWait = 0;
            while (!IsRecording && startWait++ < 30) yield return null;

            var stopSent = false;
            while (IsRecording)
            {
                if (!stopSent && cancelRequested())
                {
                    Stop();
                    stopSent = true;
                }
                yield return null;
            }

            Restore(saved);
            ScreencapBridge.RestoreAlpha(savedAlpha);

            if (!(GetField(_instance, "_autoGenerateVideo") is bool generate) || !generate)
            {
                CameraSweepPlugin.Logger.LogMessage("Camera Sweep: VideoExport's 'Create video' is off - frames were saved, no video to move.");
                yield break;
            }

            MoveNewVideo(outputFolder, before, startedAt, fullPathNoExt);
        }

        private static void Restore(Dictionary<string, object> saved)
        {
            foreach (var kv in saved)
                if (kv.Value != null)
                    SetField(_instance, kv.Key, kv.Value);
        }

        private static void MoveNewVideo(string outputFolder, HashSet<string> before, DateTime startedAt, string fullPathNoExt)
        {
            try
            {
                var fresh = ListVideos(outputFolder)
                    .Where(p => !before.Contains(p))
                    .Select(p => new FileInfo(p))
                    .Where(f => f.LastWriteTime >= startedAt)
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();

                if (fresh == null)
                {
                    CameraSweepPlugin.Logger.LogWarning($"Camera Sweep: couldn't find the new video in {outputFolder}.");
                    return;
                }

                var dest = fullPathNoExt + fresh.Extension;
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(fresh.FullName, dest);
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning("Camera Sweep: could not move the video: " + ex.Message);
            }
        }

        private static HashSet<string> ListVideos(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return new HashSet<string>();
                return new HashSet<string>(Directory.GetFiles(folder)
                    .Where(p => VideoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new HashSet<string>();
            }
        }

        private static string ReadOutputFolder()
        {
            var value = GetField(_instance, "_outputFolder");
            if (value is ConfigEntryBase entry) value = entry.BoxedValue;
            var path = value as string;
            if (string.IsNullOrEmpty(path)) return null;
            return FileNames.ResolveFromGameRoot(path);
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            if (Chainloader.PluginInfos.TryGetValue(VideoExportGuid, out var info) && info.Instance != null)
                _instance = info.Instance;
            if (_instance == null) return;

            _type = _instance.GetType();
            _record = _type.GetMethod("RecordVideo", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            _stop = _type.GetMethod("StopRecording", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            _isRecording = _type.GetProperty("isRecording", BindingFlags.Public | BindingFlags.Instance);

            CameraSweepPlugin.Logger.LogInfo($"VideoExport found (record={_record != null}, stop={_stop != null}).");
        }

        private static object GetField(object o, string name)
        {
            try
            {
                return o?.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);
            }
            catch
            {
                return null;
            }
        }

        private static void SetField(object o, string name, object value)
        {
            var f = o?.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (f == null)
            {
                CameraSweepPlugin.Logger.LogWarning($"VideoExport field {name} not found.");
                return;
            }

            var t = f.FieldType;
            object converted = t.IsInstanceOfType(value) ? value
                : t.IsEnum ? Enum.ToObject(t, value)
                : Convert.ChangeType(value, t);
            f.SetValue(o, converted);
        }
    }
}
