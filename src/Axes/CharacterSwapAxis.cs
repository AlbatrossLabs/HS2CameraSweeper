using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIChara;
using BepInEx;
using BepInEx.Configuration;
using HS2CameraSweep.Sweep;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.Axes
{
    /// <summary>
    /// Replaces one character in the scene with each character card in a folder, keeping their place,
    /// pose and animation (Studio's own "Change character"). The original character is saved to a
    /// temporary card first and put back afterwards. Cards of the other sex are skipped.
    /// </summary>
    internal sealed class CharacterSwapAxis : IVariationAxis
    {
        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<string> _folder;

        private OCIChar _target;
        private string _backupPath;

        private int _countCache;
        private float _countTime = -10f;

        public CharacterSwapAxis(ConfigFile config)
        {
            _enabled = config.Bind("Axis: Character swap", "Enabled", false,
                "Re-shoot every camera once per character card in the folder below, swapped in for the character picked in the window.");
            _folder = config.Bind("Axis: Character swap", "Card folder", "chara/female/CameraSweep",
                "Folder of character cards (.png). Absolute, or relative to UserData. Cards of the other sex are skipped.");
        }

        public string Name => "swap";

        public bool Enabled
        {
            get => _enabled.Value;
            set => _enabled.Value = value;
        }

        public int Count
        {
            get
            {
                if (Time.realtimeSinceStartup - _countTime < 1f) return _countCache;
                _countTime = Time.realtimeSinceStartup;
                _countCache = Cards().Count;
                return _countCache;
            }
        }

        public string PreviewLabel => Cards().Select(Label).FirstOrDefault();

        // ---------------------------------------------------------------- run

        public IList<AxisValue> Prepare()
        {
            _target = SweepSelection.SwapTarget;
            if (_target?.charInfo == null)
            {
                CameraSweepPlugin.Logger.LogMessage("Swap axis: pick the character to swap in the Camera Sweep window.");
                return new List<AxisValue>();
            }

            var sex = _target.sex;
            var values = new List<AxisValue>();
            foreach (var path in Cards())
            {
                var cardSex = ReadSex(path);
                if (cardSex < 0) { CameraSweepPlugin.Logger.LogWarning($"Swap axis: not a character card: {path}"); continue; }
                if (cardSex != sex) { CameraSweepPlugin.Logger.LogMessage($"Swap axis: skipped {Path.GetFileName(path)} (other sex)."); continue; }
                values.Add(new AxisValue(Label(path), path));
            }

            if (values.Count == 0)
            {
                CameraSweepPlugin.Logger.LogMessage("Swap axis: no usable character cards in " + FileNames.ResolveFromUserData(_folder.Value));
                return values;
            }

            // Keep the original as a card so it can be put back exactly (plugin data included).
            try
            {
                var dir = Path.Combine(Paths.CachePath, "CameraSweep");
                Directory.CreateDirectory(dir);
                _backupPath = Path.Combine(dir, "swap_original_" + DateTime.Now.Ticks + ".png");
                using (var fs = File.Create(_backupPath))
                    _target.charInfo.chaFile.SaveCharaFile(fs, true);
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogError("Swap axis: could not back up the original character, skipping swaps: " + ex.Message);
                _backupPath = null;
                return new List<AxisValue>();
            }

            return values;
        }

        public IEnumerator Apply(AxisValue value)
        {
            if (_target == null) yield break;
            try
            {
                _target.ChangeChara((string)value.Payload);
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning($"Swap axis: could not load {value.Label}: {ex.Message}");
            }

            // A character reload rebuilds meshes, hair bones and physics; give it a few frames.
            for (var i = 0; i < 3; i++) yield return null;
        }

        public void Restore()
        {
            if (_target != null && _backupPath != null && File.Exists(_backupPath))
            {
                try { _target.ChangeChara(_backupPath); }
                catch (Exception ex) { CameraSweepPlugin.Logger.LogWarning("Swap axis: restore failed: " + ex.Message); }
            }

            try { if (_backupPath != null && File.Exists(_backupPath)) File.Delete(_backupPath); }
            catch { /* left in BepInEx\cache\CameraSweep; harmless */ }

            _backupPath = null;
            _target = null;
        }

        // ---------------------------------------------------------------- editor

        public void DrawSettings()
        {
            var chars = SweepSelection.AllCharacters();
            var current = SweepSelection.SwapTarget;
            var idx = current == null ? -1 : chars.IndexOf(current);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Swap", GUILayout.Width(50));
            if (GUILayout.Button("<", GUILayout.Width(24)) && chars.Count > 0)
                SweepSelection.SwapTarget = chars[(idx <= 0 ? chars.Count : idx) - 1];
            GUILayout.Label(current != null ? SweepRunner.CharName(current) : "(pick a character)", GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", GUILayout.Width(24)) && chars.Count > 0)
                SweepSelection.SwapTarget = chars[(idx + 1) % chars.Count];
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Folder", GUILayout.Width(50));
            _folder.Value = GUILayout.TextField(_folder.Value);
            GUILayout.EndHorizontal();

            GUILayout.Label(Count == 0
                ? "No character cards (.png) found in that folder."
                : $"{Count} card(s). Cards of the other sex are skipped when the sweep starts.");
        }

        // ---------------------------------------------------------------- helpers

        private List<string> Cards()
        {
            try
            {
                var folder = FileNames.ResolveFromUserData(_folder.Value);
                return Directory.Exists(folder)
                    ? Directory.GetFiles(folder, "*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                    : new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static string Label(string path) => FileNames.Sanitize(Path.GetFileNameWithoutExtension(path));

        /// <summary>0 male, 1 female, -1 not a character card.</summary>
        private static int ReadSex(string path)
        {
            try
            {
                var file = new ChaFileControl();
                return file.LoadCharaFile(path, byte.MaxValue, true, true) ? file.parameter.sex : -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
