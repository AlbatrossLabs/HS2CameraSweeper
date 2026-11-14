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
        // Underwear only shows with the clothes over it off, so presets that change the bra / panties take the
        // top / bottom off too (and Apply does that for any preset that leaves the top / bottom at "-").
        public const string DefaultPresets =
            "dressed=top:on,bra:on,bottom:on,panties:on; half=top:off,bra:half,bottom:off,panties:half; " +
            "topless=top:off,bra:off; bottomless=bottom:off,panties:off; nude=all:off";

        /// <summary>Earlier defaults: replaced by the current ones wherever nobody edited them.</summary>
        public static readonly string[] OldDefaults =
        {
            "halfTop=top:half; halfBottom=bottom:half; topOff=top:off; bottomOff=bottom:off; topBottomOff=top:off,bottom:off",
            "dressed=top:on,bottom:on,bra:on,panties:on; half=top:half,bottom:half; topless=top:off,bra:off; bottomless=bottom:off,panties:off; nude=all:off",
            "dressed=top:on,bottom:on,bra:on,panties:on; half=top:half,bottom:half; topless=top:off,bra:off; bottomless=bottom:off,panties:off; nude=top:off,bottom:off,bra:off,panties:off,gloves:off,pantyhose:off,socks:off,shoes:off",
        };

        // Index = HS2 clothes kind.
        private static readonly string[] PartNames = { "top", "bottom", "bra", "panties", "gloves", "pantyhose", "socks", "shoes" };
        private static readonly string[] PartShort = { "Top", "Bottom", "Bra", "Panties", "Gloves", "Hose", "Socks", "Shoes" };
        private static readonly int[] DisplayOrder = { 0, 2, 1, 3, 4, 5, 6, 7 }; // Top | Bra | Bottom | Panties, then the rest
        private const int MainParts = 4; // shown by default; "All parts" shows the rest
        private const int Top = 0, Bottom = 1, Bra = 2, Panties = 3;

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

        private readonly OCIChar _target;
        private readonly Setting<bool> _enabled;
        private readonly Setting<string> _presets;
        private readonly Setting<bool> _includeOriginal;
        public const string OriginalLabel = "original";

        private List<Preset> _model = new List<Preset>();
        private string _modelSource;
        private bool _showAllParts;

        private readonly List<KeyValuePair<ChaControl, byte[]>> _snapshots = new List<KeyValuePair<ChaControl, byte[]>>();
        private readonly HashSet<string> _warned = new HashSet<string>();

        public ClothesStateAxis(OCIChar target, AxisStore store)
        {
            _target = target;
            _enabled = store.Bool("enabled", false);
            _presets = store.Text("presets", () => AxisDefaults.ClothesPresets.Value);
            _includeOriginal = store.Bool("includeOriginal", true);
            if (Array.IndexOf(OldDefaults, _presets.Value) >= 0) _presets.Value = DefaultPresets;
        }

        public string Name => "clothes";
        public string DisplayName => "Clothing states";
        public string Description => "On / half / off per clothing part";

        public bool Enabled
        {
            get => _enabled.Value;
            set => _enabled.Value = value;
        }

        public int Count
        {
            get
            {
                var n = Model().Count(p => p.States.Any(s => s != Unchanged));
                return n == 0 ? 0 : n + (_includeOriginal.Value ? 1 : 0);
            }
        }

        public string PreviewLabel => _includeOriginal.Value && Count > 0 ? OriginalLabel : UniqueNames(Model().Where(p => p.States.Any(s => s != Unchanged))).Select(kv => kv.Key).FirstOrDefault();

        // ---------------------------------------------------------------- run

        public IList<AxisValue> Prepare()
        {
            _snapshots.Clear();
            _warned.Clear();

            foreach (var oci in new[] { _target })
            {
                var cha = oci?.charInfo;
                var state = cha?.fileStatus?.clothesState;
                if (state == null) continue;
                _snapshots.Add(new KeyValuePair<ChaControl, byte[]>(cha, (byte[])state.Clone()));
            }

            if (_snapshots.Count == 0)
                CameraSweepPlugin.Logger.LogMessage("Clothing states: character has no clothing data.");

            var values = UniqueNames(Model().Where(p => p.States.Any(s => s != Unchanged)))
                .Select(p => new AxisValue(p.Key, WithOuterOff(p.Value.States)))
                .ToList();
            if (values.Count == 0)
                CameraSweepPlugin.Logger.LogMessage("Clothing states: no presets set - every part is '-'.");
            else if (_includeOriginal.Value)
                values.Insert(0, new AxisValue(OriginalLabel, NewStates())); // all '-' = as dressed when the sweep began
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
                    // Start every preset from the state the sweep began with - and redraw it, or parts an
                    // earlier preset took off would stay off.
                    Array.Copy(snap.Value, cha.fileStatus.clothesState, snap.Value.Length);
                    cha.UpdateClothesStateAll();

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
            // Four parts per line; "All parts" adds a second line with the other four.
            var lines = _showAllParts ? 2 : 1;
            var changed = false;
            const float nameW = 80f, cellW = 50f, delW = 22f;

            for (var line = 0; line < lines; line++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(line == 0 ? "Shot name" : "", GUILayout.Width(nameW));
                for (var c = line * MainParts; c < line * MainParts + MainParts; c++) GUILayout.Label(PartShort[DisplayOrder[c]], GUILayout.Width(cellW));
                GUILayout.EndHorizontal();
            }

            for (var i = 0; i < model.Count; i++)
            {
                var p = model[i];
                var delete = false;

                for (var line = 0; line < lines; line++)
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

                    for (var c = line * MainParts; c < line * MainParts + MainParts; c++)
                    {
                        var k = DisplayOrder[c];
                        if (!GUILayout.Button(StateText(p.States[k]), GUILayout.Width(cellW))) continue;
                        p.States[k] = Next(p.States[k]);
                        changed = true;
                    }

                    if (line == lines - 1 && GUILayout.Button("x", GUILayout.Width(delW))) delete = true;
                    GUILayout.EndHorizontal();
                }

                if (lines > 1) GUILayout.Space(3);
                if (!delete) continue;
                model.RemoveAt(i);
                changed = true;
                break;
            }

            GUILayout.Label(UI.Theme.Hint("Click a cell to cycle  -  / On / Half / Off.   - = leave as it was. Rows with every part at - aren't shot. " +
                                          "Bra or panties at Half / Off take a top or bottom left at - off too, so they show."), UI.Theme.Rich);
            _includeOriginal.Value = GUILayout.Toggle(_includeOriginal.Value, " Also shoot them as they're dressed now (first, as \"original\")");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add shot"))
            {
                model.Add(new Preset("shot" + (model.Count + 1), NewStates()));
                Store();
                UI.Gui.Relayout();
            }
            if (GUILayout.Button(new GUIContent("Reset", "Load the default presets (F1 > Axis: Clothes state)")))
            {
                _model = Parse(AxisDefaults.ClothesPresets.Value);
                changed = true;
            }
            if (GUILayout.Button(new GUIContent("Save as default", "Characters you add from now on start with these presets")))
                AxisDefaults.ClothesPresets.Value = _presets.Value;
            var all = GUILayout.Toggle(_showAllParts, " All parts");
            if (all != _showAllParts) { _showAllParts = all; UI.Gui.Relayout(); }
            GUILayout.EndHorizontal();

            if (changed) Store();
        }

        /// <summary>
        /// A bra or panties set to half or off can't be seen under the top / bottom, so a preset that changes
        /// them and leaves the top / bottom at "-" takes the top / bottom off as well.
        /// </summary>
        private static sbyte[] WithOuterOff(sbyte[] states)
        {
            var s = states.ToArray();
            if ((s[Bra] == 1 || s[Bra] == 2) && s[Top] == Unchanged) s[Top] = 2;
            if ((s[Panties] == 1 || s[Panties] == 2) && s[Bottom] == Unchanged) s[Bottom] = 2;
            return s;
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
            foreach (var k in DisplayOrder)
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
