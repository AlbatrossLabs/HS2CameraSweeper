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
    /// Replaces one character in the scene with each picked character card, keeping their place,
    /// pose and animation (Studio's own "Change character"). The original character is saved to a
    /// temporary card first and put back afterwards. Cards of the other sex are skipped.
    /// Optionally the original character is shot too, first, as the "original" value.
    /// </summary>
    internal sealed class CharacterSwapAxis : IVariationAxis
    {
        private readonly OCIChar _character;
        private readonly Setting<bool> _enabled;
        private readonly CardList _cards;
        private readonly Setting<bool> _includeOriginal;

        public const string OriginalLabel = "original";

        private OCIChar _target;
        private string _backupPath;
        private bool _swapped; // a card is loaded right now (not the original)

        private int _countCache;
        private float _countTime = -10f;

        public CharacterSwapAxis(OCIChar character, AxisStore store)
        {
            _character = character;
            _enabled = store.Bool("enabled", false);
            _cards = new CardList(CardEntries(store), "character cards", "*.png", PickKind.Character, character);
            _includeOriginal = store.Bool("includeOriginal", AxisDefaults.SwapIncludeOriginal.Value);
        }

        public string Name => "swap";
        public string DisplayName => "Character swap";
        public string Description => "Swap in other character cards, same place and pose";

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
                var cards = Cards().Count;
                _countCache = cards == 0 ? 0 : cards + (_includeOriginal.Value ? 1 : 0);
                return _countCache;
            }
        }

        public string PreviewLabel => _includeOriginal.Value && Cards().Count > 0 ? OriginalLabel : Cards().Select(Label).FirstOrDefault();

        // ---------------------------------------------------------------- run

        public IList<AxisValue> Prepare()
        {
            _target = _character;
            if (_target?.charInfo == null)
            {
                CameraSweepPlugin.Logger.LogMessage("Character swap: character isn't loaded.");
                return new List<AxisValue>();
            }

            var sex = _target.sex;
            _swapped = false;
            var values = new List<AxisValue>();
            var usable = new List<string>();
            foreach (var path in Cards())
            {
                var cardSex = ReadSex(path);
                if (cardSex < 0) { CameraSweepPlugin.Logger.LogWarning($"Character swap: not a character card: {path}"); continue; }
                if (cardSex != sex) { CameraSweepPlugin.Logger.LogMessage($"Character swap: skipped {Path.GetFileName(path)} (other sex)."); continue; }
                usable.Add(path);
            }
            var labels = CardList.Labels(usable);
            for (var i = 0; i < usable.Count; i++) values.Add(new AxisValue(labels[i], usable[i]));

            if (values.Count == 0)
            {
                CameraSweepPlugin.Logger.LogMessage("Character swap: no usable character cards picked.");
                return values;
            }

            // The original first: it's already loaded, so it costs no reload.
            if (_includeOriginal.Value) values.Insert(0, new AxisValue(OriginalLabel, null));

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

            var path = (string)value.Payload ?? (_swapped ? _backupPath : null);
            if (path == null) yield break; // original requested and already loaded

            try
            {
                _target.ChangeChara(path);
                CharacterReloads.Bump(_target);
                _swapped = value.Payload != null;
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
            if (_swapped && _target != null && _backupPath != null && File.Exists(_backupPath))
            {
                try { _target.ChangeChara(_backupPath); CharacterReloads.Bump(_target); }
                catch (Exception ex) { CameraSweepPlugin.Logger.LogWarning("Swap axis: restore failed: " + ex.Message); }
            }

            try { if (_backupPath != null && File.Exists(_backupPath)) File.Delete(_backupPath); }
            catch { /* left in BepInEx\cache\CameraSweep; harmless */ }

            _backupPath = null;
            _target = null;
            _swapped = false;
        }

        // ---------------------------------------------------------------- editor

        public void DrawSettings()
        {
            _cards.Draw();
            _includeOriginal.Value = GUILayout.Toggle(_includeOriginal.Value, " Also shoot the original (no swap)");
        }

        // ---------------------------------------------------------------- helpers

        private List<string> Cards() => _cards.Files();

        /// <summary>The picked cards. Scenes from 0.8 stored a folder instead; it becomes the first entry.</summary>
        internal static Setting<string> CardEntries(AxisStore store) =>
            store.Text("cards", () => store.Values.TryGetValue("folder", out var folder) ? folder : "");

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
