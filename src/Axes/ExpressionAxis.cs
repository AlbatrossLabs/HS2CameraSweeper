using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AIChara;
using BepInEx.Configuration;
using HS2CameraSweep.Sweep;
using UnityEngine;

namespace HS2CameraSweep.Axes
{
    /// <summary>
    /// Cycles named expression presets (eyebrow / eyes / mouth pattern numbers) on the target characters.
    /// Pattern numbers are the same indexes as Studio's Anim &gt; Face dropdowns (first entry = 0).
    ///
    /// Edited as rows with steppers in the window ("From current face" copies what a ticked character
    /// is showing right now); stored as text in F1: name=eyebrow,eyes,mouth; ...  (-1 = leave as is)
    /// </summary>
    internal sealed class ExpressionAxis : IVariationAxis
    {
        private const string DefaultPresets = "neutral=0,0,0; smile=1,1,1";
        private static readonly string[] PartShort = { "Brow", "Eyes", "Mouth" };

        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<string> _presets;

        private List<Preset> _model = new List<Preset>();
        private string _modelSource;

        private readonly List<Snapshot> _snapshots = new List<Snapshot>();
        private List<ChaControl> _targets = new List<ChaControl>();

        public ExpressionAxis(ConfigFile config)
        {
            _enabled = config.Bind("Axis: Expression", "Enabled", false,
                "Re-shoot every camera once per expression preset below.");
            _presets = config.Bind("Axis: Expression", "Presets", DefaultPresets,
                "Easiest to edit in the Camera Sweep window. As text: semicolon-separated presets, name=eyebrow,eyes,mouth. " +
                "Numbers match the Face dropdowns in Studio (first entry = 0). Use -1 to leave that part unchanged.");
        }

        public string Name => "face";

        public bool Enabled
        {
            get => _enabled.Value;
            set => _enabled.Value = value;
        }

        public int Count => Model().Count;

        public string PreviewLabel => Model().Select(p => FileNames.Sanitize(p.Name)).FirstOrDefault();

        // ---------------------------------------------------------------- run

        public IList<AxisValue> Prepare()
        {
            _snapshots.Clear();

            _targets = SweepSelection.Targets().Select(o => o.charInfo).Where(c => c != null).ToList();
            foreach (var cha in _targets)
                _snapshots.Add(new Snapshot(cha, cha.GetEyebrowPtn(), cha.GetEyesPtn(), cha.GetMouthPtn()));

            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var values = new List<AxisValue>();
            foreach (var p in Model())
            {
                var name = FileNames.Sanitize(p.Name);
                if (name.Length == 0) name = "face";
                if (seen.TryGetValue(name, out var n)) { seen[name] = n + 1; name += n + 1; }
                else seen[name] = 1;
                values.Add(new AxisValue(name, p.Values.ToArray()));
            }

            if (values.Count == 0)
                CameraSweepPlugin.Logger.LogMessage("Expression axis: no presets configured.");
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

        // ---------------------------------------------------------------- editor

        public void DrawSettings()
        {
            var model = Model();
            var changed = false;
            const float nameW = 80f, stepW = 20f, valW = 26f, delW = 22f;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Shot name", GUILayout.Width(nameW));
            foreach (var h in PartShort) GUILayout.Label(h, GUILayout.Width(stepW * 2 + valW + 4));
            GUILayout.EndHorizontal();

            for (var i = 0; i < model.Count; i++)
            {
                var p = model[i];
                GUILayout.BeginHorizontal();

                var name = GUILayout.TextField(p.Name, GUILayout.Width(nameW));
                if (name != p.Name) { p.Name = name; changed = true; }

                for (var k = 0; k < 3; k++)
                {
                    if (GUILayout.Button("<", GUILayout.Width(stepW))) { p.Values[k] = Math.Max(-1, p.Values[k] - 1); changed = true; }
                    GUILayout.Label(p.Values[k] < 0 ? "-" : p.Values[k].ToString(), GUILayout.Width(valW));
                    if (GUILayout.Button(">", GUILayout.Width(stepW))) { p.Values[k] = Math.Min(99, p.Values[k] + 1); changed = true; }
                }

                if (GUILayout.Button("x", GUILayout.Width(delW)))
                {
                    model.RemoveAt(i);
                    changed = true;
                    GUILayout.EndHorizontal();
                    break;
                }

                GUILayout.EndHorizontal();
            }

            GUILayout.Label("Numbers match Studio's Face dropdowns (first entry = 0).  - = leave as it was.");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+ From current face"))
            {
                var cha = SweepSelection.Targets().Select(o => o.charInfo).FirstOrDefault(c => c != null);
                if (cha != null)
                {
                    model.Add(new Preset("face" + (model.Count + 1), new[] { cha.GetEyebrowPtn(), cha.GetEyesPtn(), cha.GetMouthPtn() }));
                    changed = true;
                }
                else
                {
                    CameraSweepPlugin.Logger.LogMessage("Camera Sweep: tick a character under Characters first.");
                }
            }
            if (GUILayout.Button("+ Add"))
            {
                model.Add(new Preset("face" + (model.Count + 1), new[] { -1, -1, -1 }));
                changed = true;
            }
            if (GUILayout.Button("Defaults"))
            {
                _model = Parse(DefaultPresets);
                changed = true;
            }
            GUILayout.EndHorizontal();

            if (changed) Store();
        }

        private List<Preset> Model()
        {
            if (_modelSource != _presets.Value)
            {
                _model = Parse(_presets.Value);
                _modelSource = _presets.Value;
            }
            return _model;
        }

        private void Store()
        {
            var text = string.Join("; ", _model.Select(p =>
            {
                var name = FileNames.Sanitize(p.Name);
                return (name.Length > 0 ? name : "face") + "=" + string.Join(",", p.Values.Select(v => v.ToString()).ToArray());
            }).ToArray());
            _modelSource = text;
            _presets.Value = text;
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

        private static List<Preset> Parse(string raw)
        {
            var result = new List<Preset>();
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

                if (ok) result.Add(new Preset(parts[0].Trim(), values));
                else CameraSweepPlugin.Logger.LogWarning($"Expression axis: could not parse preset '{entry.Trim()}'");
            }

            return result;
        }

        private sealed class Preset
        {
            public Preset(string name, int[] values)
            {
                Name = name;
                Values = values;
            }

            public string Name;
            public readonly int[] Values;
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
