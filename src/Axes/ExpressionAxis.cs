using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AIChara;
using BepInEx.Configuration;
using HS2CameraSweep.Sweep;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.Axes
{
    /// <summary>
    /// Cycles named expression presets (eyebrow / eyes / mouth pattern IDs) on this character.
    /// The steppers walk the game's own pattern lists for the character's sex (eyebrow and eye IDs have
    /// gaps, e.g. 0,1,2,3,5,6,10...), so every value is one Studio can show. "Set" copies whatever face
    /// the character has right now into a row - the easy way to capture faces like tongue-out or ahegao.
    /// The mouth goes through Studio's own setter, which also switches the face's mouth-shape sliders
    /// off for the wide-open / tongue patterns, as Studio's Face panel does.
    /// Rows left all "-" (placeholders) aren't shot. Stored as text: name=eyebrow,eyes,mouth; ...
    /// </summary>
    internal sealed class ExpressionAxis : IVariationAxis
    {
        public const string DefaultPresets = "neutral=0,0,0; smile=1,1,1; ahegao=11,11,17";

        /// <summary>Earlier defaults: replaced by the current ones wherever nobody edited them.</summary>
        public static readonly string[] OldDefaults =
        {
            "neutral=0,0,0; smile=1,1,1",
            "neutral=0,0,0; smile=1,1,1; tongueOut=-1,-1,-1; ahegao=-1,-1,-1",
        };
        private static readonly string[] PartShort = { "Brow", "Eyes", "Mouth" };

        private readonly OCIChar _target;
        private readonly Setting<bool> _enabled;
        private readonly Setting<string> _presets;

        private List<Preset> _model = new List<Preset>();
        private string _modelSource;

        private int[] _original; // eyebrow, eyes, mouth when the sweep began

        public ExpressionAxis(OCIChar target, AxisStore store)
        {
            _target = target;
            _enabled = store.Bool("enabled", false);
            _presets = store.Text("presets", () => AxisDefaults.ExpressionPresets.Value);
            if (Array.IndexOf(OldDefaults, _presets.Value) >= 0) _presets.Value = DefaultPresets;
        }

        public string Name => "face";
        public string DisplayName => "Expressions";
        public string Description => "Eyebrow, eye and mouth presets";

        public bool Enabled
        {
            get => _enabled.Value;
            set => _enabled.Value = value;
        }

        public int Count => Model().Count(IsSet);

        public string PreviewLabel => Model().Where(IsSet).Select(p => FileNames.Sanitize(p.Name)).FirstOrDefault();

        private static bool IsSet(Preset p) => p.Values.Any(v => v >= 0);

        // ---------------------------------------------------------------- run

        public IList<AxisValue> Prepare()
        {
            var cha = _target?.charInfo;
            _original = cha == null ? null : Current(cha);

            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var values = new List<AxisValue>();
            if (_original == null)
            {
                CameraSweepPlugin.Logger.LogMessage("Expressions: character isn't loaded.");
                return values;
            }

            foreach (var p in Model().Where(IsSet))
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
            SetFace(_target, p[0], p[1], p[2]);
            yield return null;
        }

        public void Restore()
        {
            if (_original != null) SetFace(_target, _original[0], _original[1], _original[2]);
            _original = null;
        }

        // ---------------------------------------------------------------- editor

        public void DrawSettings()
        {
            var model = Model();
            var changed = false;
            const float nameW = 70f, stepW = 18f, valW = 22f, delW = 22f, setW = 34f;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Shot name", GUILayout.Width(nameW));
            foreach (var h in PartShort) GUILayout.Label(h, GUILayout.Width(stepW * 2 + valW + 4));
            GUILayout.EndHorizontal();

            var ids = new[] { PatternIds(_target, 0), PatternIds(_target, 1), PatternIds(_target, 2) };

            for (var i = 0; i < model.Count; i++)
            {
                var p = model[i];
                GUILayout.BeginHorizontal();

                var name = GUILayout.TextField(p.Name, GUILayout.Width(nameW));
                if (name != p.Name) { p.Name = name; changed = true; }

                for (var k = 0; k < 3; k++)
                {
                    if (GUILayout.Button("<", GUILayout.Width(stepW))) { p.Values[k] = Step(ids[k], p.Values[k], -1); changed = true; }
                    GUILayout.Label(p.Values[k] < 0 ? "-" : p.Values[k].ToString(), GUILayout.Width(valW));
                    if (GUILayout.Button(">", GUILayout.Width(stepW))) { p.Values[k] = Step(ids[k], p.Values[k], +1); changed = true; }
                }

                if (GUILayout.Button(new GUIContent("Set", "Copy the face this character has right now into this row"), GUILayout.Width(setW))
                    && _target?.charInfo != null)
                {
                    var now = Current(_target.charInfo);
                    for (var k = 0; k < 3; k++) p.Values[k] = now[k];
                    changed = true;
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

            if (model.Any(m => !IsSet(m)))
                GUILayout.Label(UI.Theme.Hint("Rows with all \"-\" aren't shot yet: set the face in Studio (Anim > Face), then press Set on the row."), UI.Theme.Rich);
            GUILayout.Label(UI.Theme.Hint("< > step through the game's own face patterns and wrap around.  - = leave as it was."), UI.Theme.Rich);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+ From current face"))
            {
                var cha = _target?.charInfo;
                if (cha != null)
                {
                    model.Add(new Preset("face" + (model.Count + 1), Current(cha)));
                    Store();
                    UI.Gui.Relayout();
                }
                else
                {
                    CameraSweepPlugin.Logger.LogMessage("Camera Sweep: this character isn't loaded.");
                }
            }
            if (GUILayout.Button("+ Add"))
            {
                model.Add(new Preset("face" + (model.Count + 1), new[] { -1, -1, -1 }));
                Store();
                UI.Gui.Relayout();
            }
            if (GUILayout.Button(new GUIContent("Reset", "Load the default presets (F1 > Axis: Expression)")))
            {
                _model = Parse(AxisDefaults.ExpressionPresets.Value);
                changed = true;
            }
            if (GUILayout.Button(new GUIContent("Save as default", "Characters you add from now on start with these presets")))
                AxisDefaults.ExpressionPresets.Value = _presets.Value;
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

        private static int[] Current(ChaControl cha) => new[] { cha.GetEyebrowPtn(), cha.GetEyesPtn(), cha.GetMouthPtn() };

        private static void SetFace(OCIChar oci, int eyebrow, int eyes, int mouth)
        {
            var cha = oci?.charInfo;
            if (cha == null) return;
            try
            {
                // blend = false so the change is instant (no fade between captures).
                if (eyebrow >= 0) cha.ChangeEyebrowPtn(eyebrow, false);
                if (eyes >= 0) cha.ChangeEyesPtn(eyes, false);
                // Studio's own setter: also turns the mouth-shape sliders off for the open/tongue patterns.
                if (mouth >= 0) oci.ChangeMouthPtn(mouth);
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

        // ---------------------------------------------------------------- the game's pattern lists

        private static readonly Dictionary<string, int[]> IdCache = new Dictionary<string, int[]>();

        /// <summary>
        /// Valid pattern IDs for a part (0 eyebrow, 1 eyes, 2 mouth) for this character's sex, in the order
        /// Studio's Face panel steps through them. Eyebrows and eyes come from the character list
        /// (categories 502/503 and 504/505, as Studio uses); the mouth is 0 .. its pattern count.
        /// </summary>
        private static int[] PatternIds(OCIChar oci, int part)
        {
            var female = SweepSelection.IsFemale(oci);
            var key = part + (female ? "f" : "m");
            if (part < 2 && IdCache.TryGetValue(key, out var cached)) return cached;

            int[] ids = null;
            try
            {
                if (part < 2)
                {
                    var category = part == 0 ? (female ? 503 : 502) : (female ? 505 : 504);
                    var info = Manager.Character.Instance?.chaListCtrl?.GetCategoryInfo((ChaListDefine.CategoryNo)category);
                    if (info != null) ids = info.Keys.OrderBy(k => k).ToArray();
                }
                else
                {
                    var max = oci?.charInfo?.mouthCtrl?.GetMaxPtn() ?? 0;
                    if (max > 0) ids = Enumerable.Range(0, max).ToArray();
                }
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogDebug("Expressions: couldn't read the pattern list: " + ex.Message);
            }

            if (ids == null || ids.Length == 0) ids = Enumerable.Range(0, 40).ToArray();
            if (part < 2) IdCache[key] = ids;
            return ids;
        }

        /// <summary>
        /// Next / previous valid ID, wrapping around: > on the last goes back to the first. Stepping down
        /// from the first gives "-" (leave as is), and down from "-" goes to the last.
        /// </summary>
        private static int Step(int[] ids, int current, int dir)
        {
            if (dir > 0)
            {
                if (current < 0) return ids[0];
                foreach (var i in ids) if (i > current) return i;
                return ids[0];
            }

            if (current < 0) return ids[ids.Length - 1];
            if (current <= ids[0]) return -1;
            return ids.Where(i => i < current).Max();
        }
    }
}
