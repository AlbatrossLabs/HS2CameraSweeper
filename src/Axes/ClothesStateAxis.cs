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
    /// Cycles clothing states (on / half / off) per part on the characters ticked under Characters.
    /// Each preset starts from the character's state when the sweep began, so parts a preset leaves
    /// at "-" stay as they were.
    ///
    /// Edited as a grid of buttons in the window; stored as text in F1 so it also works there:
    ///   name=part:state,part:state; name2=...
    ///
    /// HS2 part numbers: 0 top, 1 bottom, 2 bra, 3 panties, 4 gloves, 5 pantyhose, 6 socks, 7 shoes.
    /// States: 0 on, 1 half (shifted), 2 off.
    /// </summary>
    internal sealed class ClothesStateAxis : IVariationAxis
    {
        private const sbyte Unchanged = -1;
        private const string DefaultPresets =
            "halfTop=top:half; halfBottom=bottom:half; topOff=top:off; bottomOff=bottom:off; topBottomOff=top:off,bottom:off";

        // Index = HS2 clothes kind.
        private static readonly string[] PartNames = { "top", "bottom", "bra", "panties", "gloves", "pantyhose", "socks", "shoes" };
        private static readonly string[] PartShort = { "Top", "Bottom", "Bra", "Panties", "Gloves", "Hose", "Socks", "Shoes" };
        private const int MainParts = 4; // shown by default; "All parts" shows the rest

        private static readonly Dictionary<string, int[]> PartAliases = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "innertop", new[] { 2 } },
            { "innerbottom", new[] { 3 } },
            { "all", new[] { 0, 1, 2, 3, 4, 5, 6, 7 } },
        };

        private static readonly Dictionary<string, sbyte> States = new Dictionary<string, sbyte>(StringComparer.OrdinalIgnoreCase)
        {
            { "on", 0 }, { "0", 0 },
            { "half", 1 }, { "1", 1 },
            { "off", 2 }, { "2", 2 },
        };

        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<string> _presets;

        private List<Preset> _model = new List<Preset>();
        private string _modelSource;
        private bool _showAllParts;

        private readonly List<KeyValuePair<ChaControl, byte[]>> _snapshots = new List<KeyValuePair<ChaControl, byte[]>>();
        private readonly HashSet<string> _warned = new HashSet<string>();

        public ClothesStateAxis(ConfigFile config)
        {
            _enabled = config.Bind("Axis: Clothes state", "Enabled", false,
                "Re-shoot every camera once per clothing-state preset below.");
            _presets = config.Bind("Axis: Clothes state", "Presets", DefaultPresets,
                "Easiest to edit as buttons in the Camera Sweep window. As text: semicolon-separated presets, name=part:state,part:state. " +
                "Parts: top, bottom, bra, panties, gloves, pantyhose, socks, shoes, all. States: on, half, off. " +
                "Parts a preset doesn't name stay as they were. Example: topless=top:off,bra:off");
        }

        public string Name => "clothes";

        public bool Enabled
        {
            get => _enabled.Value;
            set => _enabled.Value = value;
        }

        public int Count => Model().Count(p => p.States.Any(s => s != Unchanged));

        public string PreviewLabel => UniqueNames(Model().Where(p => p.States.Any(s => s != Unchanged))).Select(kv => kv.Key).FirstOrDefault();

        // ---------------------------------------------------------------- run

        public IList<AxisValue> Prepare()
        {
            _snapshots.Clear();
            _warned.Clear();

            foreach (var oci in SweepSelection.Targets())
            {
                var cha = oci.charInfo;
                var state = cha?.fileStatus?.clothesState;
                if (state == null) continue;
                _snapshots.Add(new KeyValuePair<ChaControl, byte[]>(cha, (byte[])state.Clone()));
            }

            if (_snapshots.Count == 0)
                CameraSweepPlugin.Logger.LogMessage("Clothes axis: no characters ticked under Characters.");

            var values = UniqueNames(Model().Where(p => p.States.Any(s => s != Unchanged)))
                .Select(p => new AxisValue(p.Key, p.Value.States.ToArray()))
                .ToList();
            if (values.Count == 0)
                CameraSweepPlugin.Logger.LogMessage("Clothes axis: no presets set - every part is '-'.");
            return values;
        }

        public IEnumerator Apply(AxisValue value)
        {
            var states = (sbyte[])value.Payload;

            foreach (var snap in _snapshots)
            {
                var cha = snap.Key;
                if (cha == null) continue;
                try
                {
                    // Start every preset from the state the sweep began with.
                    Array.Copy(snap.Value, cha.fileStatus.clothesState, snap.Value.Length);

                    for (var kind = 0; kind < states.Length; kind++)
                    {
                        if (states[kind] == Unchanged) continue;
                        var state = (byte)states[kind];

                        // No clothing (or no state support) in that slot.
                        if (!cha.IsClothesStateKind(kind)) continue;

                        // Not every garment has a half state. Leave it as it was instead of letting
                        // the game skip ahead to the next state (half would become off).
                        if (!cha.IsClothesStateType(kind, state))
                        {
                            Warn(cha, kind, state, value.Label);
                            continue;
                        }

                        // The game links one-piece outfits: taking the top off also takes the bottom off.
                        cha.SetClothesState(kind, state, true);
                    }
                }
                catch (Exception ex)
                {
                    CameraSweepPlugin.Logger.LogWarning("Clothes axis: " + ex.Message);
                }
            }

            yield return null;
        }

        public void Restore()
        {
            foreach (var snap in _snapshots)
            {
                try
                {
                    if (snap.Key == null) continue;
                    Array.Copy(snap.Value, snap.Key.fileStatus.clothesState, snap.Value.Length);
                    snap.Key.UpdateClothesStateAll();
                }
                catch (Exception ex)
                {
                    CameraSweepPlugin.Logger.LogWarning("Clothes axis: restore failed: " + ex.Message);
                }
            }

            _snapshots.Clear();
        }

        // ---------------------------------------------------------------- editor

        public void DrawSettings()
        {
            var model = Model();
            var parts = _showAllParts ? PartNames.Length : MainParts;
            var changed = false;
            const float nameW = 80f, cellW = 46f, delW = 22f;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Shot name", GUILayout.Width(nameW));
            for (var k = 0; k < parts; k++) GUILayout.Label(PartShort[k], GUILayout.Width(cellW));
            GUILayout.EndHorizontal();

            for (var i = 0; i < model.Count; i++)
            {
                var p = model[i];
                GUILayout.BeginHorizontal();

                var name = GUILayout.TextField(p.Name, GUILayout.Width(nameW));
                if (name != p.Name) { p.Name = name; changed = true; }

                for (var k = 0; k < parts; k++)
                {
                    if (!GUILayout.Button(StateText(p.States[k]), GUILayout.Width(cellW))) continue;
                    p.States[k] = Next(p.States[k]);
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

            GUILayout.Label("Click a cell to cycle  -  / On / Half / Off.   - = leave as it was.");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add shot"))
            {
                model.Add(new Preset("shot" + (model.Count + 1), NewStates()));
                changed = true;
            }
            if (GUILayout.Button("Defaults"))
            {
                _model = Parse(DefaultPresets);
                changed = true;
            }
            _showAllParts = GUILayout.Toggle(_showAllParts, " All parts");
            GUILayout.EndHorizontal();

            if (changed) Store();
        }

        private static string StateText(sbyte s) => s == 0 ? "On" : s == 1 ? "Half" : s == 2 ? "Off" : "-";

        private static sbyte Next(sbyte s) => s == Unchanged ? (sbyte)0 : s == 2 ? Unchanged : (sbyte)(s + 1);

        private static sbyte[] NewStates() => Enumerable.Repeat(Unchanged, PartNames.Length).ToArray();

        /// <summary>The editable list, re-read whenever the text in F1 changes.</summary>
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
            var text = string.Join("; ", _model.Select(Serialize).ToArray());
            _modelSource = text;
            _presets.Value = text;
        }

        private static string Serialize(Preset p)
        {
            var parts = new List<string>();
            for (var k = 0; k < p.States.Length; k++)
                if (p.States[k] != Unchanged)
                    parts.Add(PartNames[k] + ":" + StateText(p.States[k]).ToLowerInvariant());

            var name = FileNames.Sanitize(p.Name);
            return (name.Length > 0 ? name : "shot") + "=" + string.Join(",", parts.ToArray());
        }

        /// <summary>Filenames carry the preset name, so duplicates get a number.</summary>
        private static IEnumerable<KeyValuePair<string, Preset>> UniqueNames(IEnumerable<Preset> presets)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in presets)
            {
                var name = FileNames.Sanitize(p.Name);
                if (name.Length == 0) name = "shot";
                if (seen.TryGetValue(name, out var n)) { seen[name] = n + 1; name = name + (n + 1); }
                else seen[name] = 1;
                yield return new KeyValuePair<string, Preset>(name, p);
            }
        }

        private void Warn(ChaControl cha, int kind, byte state, string preset)
        {
            var name = cha.fileParam?.fullname ?? "character";
            if (!_warned.Add(name + "|" + kind + "|" + state)) return;
            CameraSweepPlugin.Logger.LogMessage(
                $"Clothes axis: {name}'s {PartNames[kind]} has no '{StateText((sbyte)state).ToLowerInvariant()}' state - left as is in {preset}.");
        }

        private static List<Preset> Parse(string raw)
        {
            var result = new List<Preset>();
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (var entry in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = entry.Split('=');
                if (eq.Length != 2)
                {
                    CameraSweepPlugin.Logger.LogWarning($"Clothes axis: could not parse preset '{entry.Trim()}'");
                    continue;
                }

                var states = NewStates();
                foreach (var pair in eq[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var ps = pair.Split(':');
                    if (ps.Length != 2 || !States.TryGetValue(ps[1].Trim(), out var state)) continue;

                    var part = ps[0].Trim();
                    var idx = Array.FindIndex(PartNames, n => string.Equals(n, part, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) states[idx] = state;
                    else if (PartAliases.TryGetValue(part, out var kinds)) foreach (var k in kinds) states[k] = state;
                }

                result.Add(new Preset(eq[0].Trim(), states));
            }

            return result;
        }

        private sealed class Preset
        {
            public Preset(string name, sbyte[] states)
            {
                Name = name;
                States = states;
            }

            public string Name;
            public readonly sbyte[] States;
        }
    }
}
