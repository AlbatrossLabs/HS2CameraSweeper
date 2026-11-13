using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AIChara;
using BepInEx.Configuration;
using HS2CameraSweep.Sweep;
using KKAPI.Studio;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.Axes
{
    /// <summary>
    /// Cycles named expression presets (eyebrow / eyes / mouth pattern numbers) on the target characters.
    /// Pattern numbers are the same indexes as Studio's Anim &gt; Face dropdowns (first entry = 0).
    /// </summary>
    internal sealed class ExpressionAxis : IVariationAxis
    {
        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<string> _presets;

        private readonly List<Snapshot> _snapshots = new List<Snapshot>();
        private List<ChaControl> _targets = new List<ChaControl>();

        public ExpressionAxis(ConfigFile config)
        {
            _enabled = config.Bind("Axis: Expression", "Enabled", false,
                "Re-shoot every camera once per expression preset below.");
            _presets = config.Bind("Axis: Expression", "Presets", "neutral=0,0,0; smile=1,1,1",
                "Semicolon-separated presets: name=eyebrow,eyes,mouth. Numbers match the Face dropdowns in Studio (first entry = 0). " +
                "Use -1 to leave that part unchanged.");
        }

        public string Name => "face";

        public bool Enabled
        {
            get => _enabled.Value;
            set => _enabled.Value = value;
        }

        public IList<AxisValue> Prepare()
        {
            _snapshots.Clear();

            var selected = (StudioAPI.GetSelectedCharacters() ?? Enumerable.Empty<OCIChar>()).Where(c => c != null).ToList();
            if (selected.Count == 0)
                selected = Studio.Studio.Instance.dicObjectCtrl.Values.OfType<OCIChar>().ToList();

            _targets = selected.Select(o => o.charInfo).Where(c => c != null).ToList();
            foreach (var cha in _targets)
                _snapshots.Add(new Snapshot(cha, cha.GetEyebrowPtn(), cha.GetEyesPtn(), cha.GetMouthPtn()));

            var values = Parse(_presets.Value);
            if (values.Count == 0)
                CameraSweepPlugin.Logger.LogMessage("Expression axis: no valid presets configured.");
            return values;
        }

        public IEnumerator Apply(AxisValue value)
        {
            var p = (int[])value.Payload;
            foreach (var cha in _targets)
                SetFace(cha, p[0], p[1], p[2]);
            yield return null;
        }

        public void Restore()
        {
            foreach (var s in _snapshots)
                SetFace(s.Character, s.Eyebrow, s.Eyes, s.Mouth);
            _snapshots.Clear();
            _targets.Clear();
        }

        public void DrawSettings()
        {
            GUILayout.Label("Presets (name=eyebrow,eyes,mouth; ...)");
            _presets.Value = GUILayout.TextField(_presets.Value);
        }

        private static void SetFace(ChaControl cha, int eyebrow, int eyes, int mouth)
        {
            if (cha == null) return;
            try
            {
                // blend = false so the change is instant (no fade between captures).
                if (eyebrow >= 0) cha.ChangeEyebrowPtn(eyebrow, false);
                if (eyes >= 0) cha.ChangeEyesPtn(eyes, false);
                if (mouth >= 0) cha.ChangeMouthPtn(mouth, false);
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning("Expression axis: " + ex.Message);
            }
        }

        private static List<AxisValue> Parse(string raw)
        {
            var result = new List<AxisValue>();
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (var entry in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('=');
                if (parts.Length != 2) continue;

                var nums = parts[1].Split(',');
                if (nums.Length != 3) continue;

                var values = new int[3];
                var ok = true;
                for (var i = 0; i < 3; i++)
                    ok &= int.TryParse(nums[i].Trim(), out values[i]);

                var name = FileNames.Sanitize(parts[0]);
                if (ok && name.Length > 0)
                    result.Add(new AxisValue(name, values));
                else
                    CameraSweepPlugin.Logger.LogWarning($"Expression axis: could not parse preset '{entry.Trim()}'");
            }

            return result;
        }

        private sealed class Snapshot
        {
            public Snapshot(ChaControl character, int eyebrow, int eyes, int mouth)
            {
                Character = character;
                Eyebrow = eyebrow;
                Eyes = eyes;
                Mouth = mouth;
            }

            public ChaControl Character { get; }
            public int Eyebrow { get; }
            public int Eyes { get; }
            public int Mouth { get; }
        }
    }
}
