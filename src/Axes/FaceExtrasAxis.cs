using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HS2CameraSweep.Sweep;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.Axes
{
    /// <summary>
    /// Face extras the expression presets don't cover: blush, tears, where the eyes and head look,
    /// and how open the eyes and mouth are. Same controls as Studio's Anim &gt; Face / Kinematics panels.
    ///
    /// Stored in F1 as: name=blush,tears,eyeLook,neckLook,eyesOpen,mouthOpen; ...   (-1 = leave as is)
    /// Blush, tears and openness are 0-1. Eye/neck look are the index in Studio's list (first = 0).
    /// </summary>
    internal sealed class FaceExtrasAxis : IVariationAxis
    {
        public const string DefaultPresets = "plain=0,0,-1,-1,-1,-1; blush=1,-1,-1,-1,-1,-1; tears=-1,1,-1,-1,-1,-1";

        private static readonly string[] Columns = { "Blush", "Tears", "Eyes", "Neck", "EyeOpen", "Mouth" };
        private static readonly bool[] IsFraction = { true, true, false, false, true, true };
        private const float Step = 0.25f;
        private const int MaxLook = 9;

        private readonly OCIChar _target;
        private readonly Setting<bool> _enabled;
        private readonly Setting<string> _presets;

        private List<Preset> _model = new List<Preset>();
        private string _modelSource;

        private readonly List<Snapshot> _snapshots = new List<Snapshot>();

        public FaceExtrasAxis(OCIChar target, AxisStore store)
        {
            _target = target;
            _enabled = store.Bool("enabled", false);
            _presets = store.Text("presets", () => AxisDefaults.ExtrasPresets.Value);
        }

        public string Name => "extras";
        public string DisplayName => "Face extras";
        public string Description => "Blush, tears, eye and head direction, eye/mouth opening";

        public bool Enabled
        {
            get => _enabled.Value;
            set => _enabled.Value = value;
        }

        public int Count => Model().Count(p => p.Values.Any(v => v >= 0));

        public string PreviewLabel => Model().Where(p => p.Values.Any(v => v >= 0)).Select(p => FileNames.Sanitize(p.Name)).FirstOrDefault();

        // ---------------------------------------------------------------- run

        public IList<AxisValue> Prepare()
        {
            _snapshots.Clear();
            foreach (var oci in new[] { _target })
            {
                if (oci?.charInfo == null) continue;
                try
                {
                    var st = oci.charInfo.fileStatus;
                    _snapshots.Add(new Snapshot
                    {
                        Character = oci,
                        Values = new[] { st.hohoAkaRate, st.tearsRate, st.eyesLookPtn, st.neckLookPtn, st.eyesOpenMax, oci.oiCharInfo.mouthOpen },
                    });
                }
                catch (Exception ex)
                {
                    CameraSweepPlugin.Logger.LogWarning("Face extras: " + ex.Message);
                }
            }

            if (_snapshots.Count == 0)
                CameraSweepPlugin.Logger.LogMessage("Face extras: character isn't loaded.");

            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var values = new List<AxisValue>();
            foreach (var p in Model().Where(p => p.Values.Any(v => v >= 0)))
            {
                var name = FileNames.Sanitize(p.Name);
                if (name.Length == 0) name = "extras";
                if (seen.TryGetValue(name, out var n)) { seen[name] = n + 1; name += n + 1; }
                else seen[name] = 1;
                values.Add(new AxisValue(name, p.Values.ToArray()));
            }
            return values;
        }

        public IEnumerator Apply(AxisValue value)
        {
            var v = (float[])value.Payload;
            foreach (var s in _snapshots)
            {
                // Each preset starts from how the face was when the sweep began.
                Set(s.Character, s.Values);
                Set(s.Character, v);
            }
            yield return null;
        }

        public void Restore()
        {
            foreach (var s in _snapshots) Set(s.Character, s.Values);
            _snapshots.Clear();
        }

        private static void Set(OCIChar oci, float[] v)
        {
            if (oci?.charInfo == null) return;
            try
            {
                if (v[0] >= 0) oci.SetHohoAkaRate(v[0]);
                if (v[1] >= 0) oci.SetTears(v[1]);
                if (v[2] >= 0) oci.ChangeLookEyesPtn((int)v[2]);
                if (v[3] >= 0) oci.ChangeLookNeckPtn((int)v[3]);
                if (v[4] >= 0) oci.ChangeEyesOpen(v[4]);
                if (v[5] >= 0) oci.ChangeMouthOpen(v[5]);
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning("Face extras: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- editor

        public void DrawSettings()
        {
            var model = Model();
            var changed = false;
            // Six columns don't fit across the window, so each preset takes two lines of three.
            const float nameW = 70f, stepW = 20f, valW = 26f, delW = 22f, colW = stepW * 2 + valW + 8;

            for (var line = 0; line < 2; line++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(line == 0 ? "Shot name" : "", GUILayout.Width(nameW));
                for (var k = line * 3; k < line * 3 + 3; k++) GUILayout.Label(Columns[k], GUILayout.Width(colW));
                GUILayout.EndHorizontal();
            }

            for (var i = 0; i < model.Count; i++)
            {
                var p = model[i];
                var delete = false;

                for (var line = 0; line < 2; line++)
                {
                    GUILayout.BeginHorizontal();
                    if (line == 0)
                    {
                        var name = GUILayout.TextField(p.Name, GUILayout.Width(nameW));
                        if (name != p.Name) { p.Name = name; changed = true; }
                    }
                    else
                    {
                        GUILayout.Space(nameW + 4);
                    }

                    for (var k = line * 3; k < line * 3 + 3; k++)
                    {
                        if (GUILayout.Button("<", GUILayout.Width(stepW))) { p.Values[k] = Down(p.Values[k], k); changed = true; }
                        GUILayout.Label(Show(p.Values[k], k), GUILayout.Width(valW));
                        if (GUILayout.Button(">", GUILayout.Width(stepW))) { p.Values[k] = Up(p.Values[k], k); changed = true; }
                        GUILayout.Space(4);
                    }

                    if (line == 1 && GUILayout.Button("x", GUILayout.Width(delW))) delete = true;
                    GUILayout.EndHorizontal();
                }

                GUILayout.Space(3);
                if (!delete) continue;
                model.RemoveAt(i);
                changed = true;
                break;
            }

            GUILayout.Label("Blush, tears, eye/mouth open: 0-1.  Eyes/Neck look: Studio's list order (first = 0).  - = leave as it was.");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+ From current"))
            {
                var oci = _target?.charInfo != null ? _target : null;
                if (oci != null)
                {
                    var st = oci.charInfo.fileStatus;
                    model.Add(new Preset("extras" + (model.Count + 1),
                        new[] { st.hohoAkaRate, st.tearsRate, st.eyesLookPtn, st.neckLookPtn, st.eyesOpenMax, oci.oiCharInfo.mouthOpen }));
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
                model.Add(new Preset("extras" + (model.Count + 1), Enumerable.Repeat(-1f, Columns.Length).ToArray()));
                Store();
                UI.Gui.Relayout();
            }
            if (GUILayout.Button(new GUIContent("Reset", "Load the default presets (F1 > Axis: Face extras)")))
            {
                _model = Parse(AxisDefaults.ExtrasPresets.Value);
                changed = true;
            }
            if (GUILayout.Button(new GUIContent("Save as default", "Characters you add from now on start with these presets")))
                AxisDefaults.ExtrasPresets.Value = _presets.Value;
            GUILayout.EndHorizontal();

            if (changed) Store();
        }

        private static float Up(float v, int k)
        {
            if (v < 0) return 0f;
            return IsFraction[k] ? Mathf.Min(1f, Snap(v + Step)) : Mathf.Min(MaxLook, (int)v + 1);
        }

        private static float Down(float v, int k)
        {
            if (v <= 0) return -1f;
            return IsFraction[k] ? Mathf.Max(0f, Snap(v - Step)) : (int)v - 1;
        }

        private static float Snap(float v) => Mathf.Round(v / Step) * Step;

        private static string Show(float v, int k) =>
            v < 0 ? "-" : IsFraction[k] ? v.ToString("0.##", CultureInfo.InvariantCulture) : ((int)v).ToString();

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
                return (name.Length > 0 ? name : "extras") + "=" +
                       string.Join(",", p.Values.Select(v => v < 0 ? "-1" : v.ToString("0.##", CultureInfo.InvariantCulture)).ToArray());
            }).ToArray());
            _modelSource = text;
            _presets.Value = text;
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
                var values = Enumerable.Repeat(-1f, Columns.Length).ToArray();
                var ok = nums.Length == Columns.Length;
                for (var i = 0; ok && i < Columns.Length; i++)
                    ok = float.TryParse(nums[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);

                if (ok) result.Add(new Preset(parts[0].Trim(), values.Select(v => v < 0 ? -1f : v).ToArray()));
                else CameraSweepPlugin.Logger.LogWarning($"Face extras: could not parse preset '{entry.Trim()}'");
            }

            return result;
        }

        private sealed class Preset
        {
            public Preset(string name, float[] values)
            {
                Name = name;
                Values = values;
            }

            public string Name;
            public readonly float[] Values;
        }

        private sealed class Snapshot
        {
            public OCIChar Character;
            public float[] Values;
        }
    }
}
