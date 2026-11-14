using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.Axes
{
    /// <summary>
    /// Loads each picked Studio pose file (UserData\Studio\pose\*.dat) onto this character, like Studio's
    /// Pose list does (PauseCtrl.Load). The pose they had when the sweep began - animation, FK/IK and all -
    /// is captured first (PauseCtrl.FileInfo) and put back afterwards. Optionally that pose is shot too, first.
    /// </summary>
    internal sealed class PoseAxis : IVariationAxis
    {
        public const string OriginalLabel = "original";

        private readonly OCIChar _target;
        private readonly Setting<bool> _enabled;
        private readonly Setting<bool> _includeOriginal;
        private readonly CardList _poses;

        private PauseCtrl.FileInfo _original;
        private bool _changed;

        public PoseAxis(OCIChar target, AxisStore store)
        {
            _target = target;
            _enabled = store.Bool("enabled", false);
            _includeOriginal = store.Bool("includeOriginal", false);
            _poses = new CardList(store.Text("cards", () => ""), "poses", "*.dat", PickKind.Pose, target);
        }

        public string Name => "pose";
        public string DisplayName => "Poses";
        public string Description => "Each Studio pose you pick";

        public bool Enabled
        {
            get => _enabled.Value;
            set => _enabled.Value = value;
        }

        public int Count
        {
            get
            {
                var n = _poses.Count;
                return n == 0 ? 0 : n + (_includeOriginal.Value ? 1 : 0);
            }
        }

        public string PreviewLabel
        {
            get
            {
                if (_poses.Count == 0) return null;
                return _includeOriginal.Value ? OriginalLabel : CardList.Labels(_poses.Files())[0];
            }
        }

        public IList<AxisValue> Prepare()
        {
            var values = new List<AxisValue>();
            _changed = false;
            _original = null;

            if (_target?.charInfo == null)
            {
                CameraSweepPlugin.Logger.LogMessage("Poses: character isn't loaded.");
                return values;
            }

            var files = _poses.Files();
            if (files.Count == 0)
            {
                CameraSweepPlugin.Logger.LogMessage("Poses: no poses picked.");
                return values;
            }

            try
            {
                _original = new PauseCtrl.FileInfo(_target);
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogError("Poses: could not remember the current pose, skipping poses: " + ex.Message);
                return values;
            }

            if (_includeOriginal.Value) values.Add(new AxisValue(OriginalLabel, null));
            var labels = CardList.Labels(files);
            for (var i = 0; i < files.Count; i++) values.Add(new AxisValue(labels[i], files[i]));
            return values;
        }

        public IEnumerator Apply(AxisValue value)
        {
            var path = (string)value.Payload;
            if (path == null)
            {
                if (!_changed) yield break;
                PutBackOriginal();
                _changed = false;
            }
            else
            {
                try
                {
                    if (PauseCtrl.Load(_target, path)) _changed = true;
                    else CameraSweepPlugin.Logger.LogWarning($"Poses: {Path.GetFileName(path)} isn't a Studio pose file.");
                }
                catch (Exception ex)
                {
                    CameraSweepPlugin.Logger.LogWarning($"Poses: could not load {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            // Let the animator and IK settle into the new pose.
            for (var i = 0; i < 2; i++) yield return null;
        }

        public void Restore()
        {
            if (_changed) PutBackOriginal();
            _original = null;
            _changed = false;
        }

        private void PutBackOriginal()
        {
            try { _original?.Apply(_target); }
            catch (Exception ex) { CameraSweepPlugin.Logger.LogWarning("Poses: putting the original pose back failed: " + ex.Message); }
        }

        public void DrawSettings()
        {
            _poses.Draw();
            _includeOriginal.Value = GUILayout.Toggle(_includeOriginal.Value, " Also shoot the pose they're in now");
        }
    }
}
