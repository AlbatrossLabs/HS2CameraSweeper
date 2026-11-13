using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    /// Loads each coordinate (outfit) card from a folder onto the target characters.
    /// Targets = the selected characters in the workspace, or every character if none are selected.
    /// </summary>
    internal sealed class OutfitAxis : IVariationAxis
    {
        private readonly ConfigEntry<bool> _enabled;
        private readonly ConfigEntry<string> _folder;

        private readonly List<Snapshot> _snapshots = new List<Snapshot>();
        private List<OCIChar> _targets = new List<OCIChar>();

        public OutfitAxis(ConfigFile config)
        {
            _enabled = config.Bind("Axis: Outfit", "Enabled", false,
                "Re-shoot every camera once per coordinate card in the folder below.");
            _folder = config.Bind("Axis: Outfit", "Coordinate folder", "coordinate/female/CameraSweep",
                "Folder of coordinate cards (.png). Absolute, or relative to UserData. Use cards made for the target character's sex.");
        }

        public string Name => "outfit";

        public bool Enabled
        {
            get => _enabled.Value;
            set => _enabled.Value = value;
        }

        public IList<AxisValue> Prepare()
        {
            _snapshots.Clear();

            var folder = FileNames.ResolveFromUserData(_folder.Value);
            if (!Directory.Exists(folder))
            {
                CameraSweepPlugin.Logger.LogMessage($"Outfit axis: folder not found: {folder}");
                return new List<AxisValue>();
            }

            _targets = (StudioAPI.GetSelectedCharacters() ?? Enumerable.Empty<OCIChar>()).Where(c => c != null).ToList();
            if (_targets.Count == 0)
                _targets = Studio.Studio.Instance.dicObjectCtrl.Values.OfType<OCIChar>().ToList();

            if (_targets.Count == 0)
            {
                CameraSweepPlugin.Logger.LogMessage("Outfit axis: no characters in the scene.");
                return new List<AxisValue>();
            }

            foreach (var oci in _targets)
            {
                var cha = oci.charInfo;
                if (cha == null || cha.nowCoordinate == null) continue;
                _snapshots.Add(new Snapshot(cha, cha.nowCoordinate.SaveBytes(), cha.nowCoordinate.loadVersion));
            }

            return Directory.GetFiles(folder, "*.png")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => new AxisValue(FileNames.Sanitize(Path.GetFileNameWithoutExtension(p)), p))
                .ToList();
        }

        public IEnumerator Apply(AxisValue value)
        {
            var path = (string)value.Payload;
            var coord = new ChaFileCoordinate();

            bool loaded;
            try
            {
                using (var fs = File.OpenRead(path))
                    loaded = coord.LoadFile(fs, 0);
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning($"Outfit axis: could not read {path}: {ex.Message}");
                loaded = false;
            }

            if (!loaded)
            {
                CameraSweepPlugin.Logger.LogWarning($"Outfit axis: skipped {path} (not a readable coordinate card).");
                yield break;
            }

            foreach (var oci in _targets)
            {
                try { oci.charInfo?.ChangeNowCoordinate(coord, true); }
                catch (Exception ex) { CameraSweepPlugin.Logger.LogWarning("Outfit axis: ChangeNowCoordinate failed: " + ex.Message); }
            }

            // Reload rebuilds clothing meshes; give it a frame before the runner's settle frames.
            yield return null;
        }

        public void Restore()
        {
            foreach (var s in _snapshots)
            {
                try
                {
                    if (s.Character == null) continue;
                    var coord = new ChaFileCoordinate();
                    if (coord.LoadBytes(s.Bytes, s.Version))
                        s.Character.ChangeNowCoordinate(coord, true);
                }
                catch (Exception ex)
                {
                    CameraSweepPlugin.Logger.LogWarning("Outfit axis: restore failed: " + ex.Message);
                }
            }

            _snapshots.Clear();
            _targets.Clear();
        }

        public void DrawSettings()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Folder", GUILayout.Width(50));
            _folder.Value = GUILayout.TextField(_folder.Value);
            GUILayout.EndHorizontal();
            GUILayout.Label("Applies to selected characters (or all if none selected).");
        }

        private sealed class Snapshot
        {
            public Snapshot(ChaControl character, byte[] bytes, Version version)
            {
                Character = character;
                Bytes = bytes;
                Version = version;
            }

            public ChaControl Character { get; }
            public byte[] Bytes { get; }
            public Version Version { get; }
        }
    }
}
