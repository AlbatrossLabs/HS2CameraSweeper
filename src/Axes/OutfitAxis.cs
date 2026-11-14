using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AIChara;
using BepInEx;
using HarmonyLib;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.Axes
{
    /// <summary>
    /// Loads each picked coordinate (outfit) card onto this character, the same way Studio's Costume list
    /// does (OCIChar.LoadClothesFile), so plugin data saved with the outfit (overlays, extra accessories...)
    /// comes along. The outfit the character wore when the sweep began is saved to a temporary coordinate
    /// card first and loaded back afterwards. Optionally the outfit they're wearing now is shot too, first:
    /// that's the scene's outfit, or after a character swap, the swapped-in card's own outfit.
    /// </summary>
    internal sealed class OutfitAxis : IVariationAxis
    {
        public const string OriginalLabel = "original";

        private readonly OCIChar _target;
        private readonly Setting<bool> _enabled;
        private readonly Setting<bool> _includeOriginal;
        private readonly CardList _cards;

        private string _backupPath;
        private byte[] _backupBytes;     // fallback if the coordinate couldn't be saved to a file
        private Version _backupVersion;
        private int _changedAt = -1;     // character reload count when an outfit was last loaded; -1 = none since
        private bool _everChanged;       // an outfit was loaded at some point this run

        public OutfitAxis(OCIChar target, AxisStore store)
        {
            _target = target;
            _enabled = store.Bool("enabled", false);
            _includeOriginal = store.Bool("includeOriginal", false);
            _cards = new CardList(CharacterSwapAxis.CardEntries(store), "outfits", "*.png", PickKind.Outfit, target);
        }

        public string Name => "outfit";
        public string DisplayName => "Outfits";
        public string Description => "Each outfit (coordinate card) you pick";

        public bool Enabled
        {
            get => _enabled.Value;
            set => _enabled.Value = value;
        }

        public int Count
        {
            get
            {
                var n = _cards.Count;
                return n == 0 ? 0 : n + (_includeOriginal.Value ? 1 : 0);
            }
        }

        public string PreviewLabel
        {
            get
            {
                if (_cards.Count == 0) return null;
                return _includeOriginal.Value ? OriginalLabel : CardList.Labels(_cards.Files())[0];
            }
        }

        public IList<AxisValue> Prepare()
        {
            var values = new List<AxisValue>();
            _changedAt = -1;
            _everChanged = false;
            _backupPath = null;
            _backupBytes = null;

            var cha = _target?.charInfo;
            if (cha?.nowCoordinate == null)
            {
                CameraSweepPlugin.Logger.LogMessage("Outfits: character isn't loaded.");
                return values;
            }

            var files = _cards.Files();
            if (files.Count == 0)
            {
                CameraSweepPlugin.Logger.LogMessage("Outfits: no outfits picked.");
                return values;
            }

            // Keep the current outfit so it can be put back (as a card, so plugin data comes back too).
            try
            {
                var dir = Path.Combine(Paths.CachePath, "CameraSweep");
                Directory.CreateDirectory(dir);
                _backupPath = Path.Combine(dir, "outfit_original_" + DateTime.Now.Ticks + ".png");
                CoordinateSaver.Save(cha, _backupPath);
                // Only trust the card if it reads back.
                if (!File.Exists(_backupPath) || !new ChaFileCoordinate().LoadFile(_backupPath)) _backupPath = null;
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogDebug("Outfits: could not save the original outfit as a card: " + ex.Message);
                _backupPath = null;
            }

            if (_backupPath == null)
            {
                _backupBytes = cha.nowCoordinate.SaveBytes();
                _backupVersion = cha.nowCoordinate.loadVersion;
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
                // "Wearing now": nothing to do if no outfit was loaded since the character was (re)loaded -
                // after a swap that's the new card's own outfit, which is what they're wearing.
                if (_changedAt != CharacterReloads.Of(_target)) yield break;
                PutBackOriginal();
                _changedAt = -1;
            }
            else
            {
                try
                {
                    _target.LoadClothesFile(path);
                    _changedAt = CharacterReloads.Of(_target);
                    _everChanged = true;
                }
                catch (Exception ex)
                {
                    CameraSweepPlugin.Logger.LogWarning($"Outfits: could not load {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            // Loading rebuilds clothing meshes; give it a frame before the runner's settle frames.
            yield return null;
        }

        public void Restore()
        {
            if (_everChanged) PutBackOriginal();

            try { if (_backupPath != null && File.Exists(_backupPath)) File.Delete(_backupPath); }
            catch { /* left in BepInEx\cache\CameraSweep; harmless */ }

            _backupPath = null;
            _backupBytes = null;
            _changedAt = -1;
            _everChanged = false;
        }

        private void PutBackOriginal()
        {
            try
            {
                if (_backupPath != null && File.Exists(_backupPath))
                {
                    _target.LoadClothesFile(_backupPath);
                }
                else if (_backupBytes != null)
                {
                    var coord = new ChaFileCoordinate();
                    if (coord.LoadBytes(_backupBytes, _backupVersion))
                        _target.charInfo.ChangeNowCoordinate(coord, true);
                }
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogWarning("Outfits: putting the original outfit back failed: " + ex.Message);
            }
        }

        public void DrawSettings()
        {
            _cards.Draw();
            _includeOriginal.Value = GUILayout.Toggle(_includeOriginal.Value, " Also shoot the outfit they're wearing now");
        }
    }

    /// <summary>
    /// Saves a character's current outfit as a coordinate card with every plugin's data in it.
    /// KKAPI only lets its plugins (overlays, etc.) write their part of a coordinate card inside the
    /// character maker: in Studio it logs an error and leaves their data out, and loading such a card back
    /// would clear those plugins' settings. While we save, we tell KKAPI which character the card is for.
    /// </summary>
    internal static class CoordinateSaver
    {
        private static ChaControl _saving;
        private static bool _kkapiAsked;
        private static System.Reflection.MethodInfo _kkapiSave;

        public static void Install(Harmony harmony)
        {
            // Backup in case the patch below doesn't take (e.g. the method got inlined): call KKAPI's own
            // save handler for our character straight from the save event.
            _kkapiSave = AccessTools.Method(typeof(KKAPI.Chara.CharacterApi), "OnCoordinateBeingSaved",
                new[] { typeof(ChaControl), typeof(ChaFileCoordinate) });
            ExtensibleSaveFormat.ExtendedSave.CoordinateBeingSaved += OnCoordinateBeingSaved;

            var target = AccessTools.Method(typeof(KKAPI.Maker.MakerAPI), nameof(KKAPI.Maker.MakerAPI.GetCharacterControl));
            if (target == null)
            {
                CameraSweepPlugin.Logger.LogWarning("Outfits: KKAPI's MakerAPI.GetCharacterControl not found - plugin data may be missing from the saved original outfit.");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(CoordinateSaver), nameof(GetCharacterControlPrefix)));
        }

        public static void Save(ChaControl cha, string path)
        {
            _saving = cha;
            _kkapiAsked = false;
            try { cha.nowCoordinate.SaveFile(path, 0); }
            finally { _saving = null; }
        }

        private static bool GetCharacterControlPrefix(ref ChaControl __result)
        {
            if (_saving == null) return true;
            _kkapiAsked = true;
            __result = _saving;
            return false;
        }

        private static void OnCoordinateBeingSaved(ChaFileCoordinate file)
        {
            if (_saving == null || _kkapiAsked || _kkapiSave == null || file != _saving.nowCoordinate) return;
            try { _kkapiSave.Invoke(null, new object[] { _saving, file }); }
            catch (Exception ex) { CameraSweepPlugin.Logger.LogDebug("Outfits: KKAPI coordinate save: " + (ex.InnerException ?? ex).Message); }
        }
    }
}
