using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ExtensibleSaveFormat;
using KKAPI.Studio.SaveLoad;
using KKAPI.Utilities;
using Studio;

namespace HS2CameraSweep.Sweep
{
    /// <summary>
    /// Which cameras are switched on for the sweep, and which characters the variations change.
    /// Both are per scene: saved inside the scene file and restored when it loads.
    ///
    /// Defaults: every camera on. Women are changed by outfit/face variations, men are not,
    /// until you tick or untick someone.
    /// </summary>
    internal static class SweepSelection
    {
        private const int FemaleSex = 1; // HS2: 0 = male, 1 = female

        private static readonly HashSet<int> SlotsOff = new HashSet<int>();
        private static readonly HashSet<ObjectCtrlInfo> CamerasOff = new HashSet<ObjectCtrlInfo>();
        private static readonly Dictionary<ObjectCtrlInfo, bool> CharacterChoice = new Dictionary<ObjectCtrlInfo, bool>();
        private static ObjectCtrlInfo _swapTarget;

        /// <summary>The character the swap variation replaces. Defaults to the first woman in the scene.</summary>
        public static OCIChar SwapTarget
        {
            get
            {
                var all = AllCharacters();
                if (_swapTarget is OCIChar c && all.Contains(c)) return c;
                return all.FirstOrDefault(IsFemale) ?? all.FirstOrDefault();
            }
            set => _swapTarget = value;
        }

        // ---------------------------------------------------------------- cameras

        public static bool IsOn(CameraShot shot)
        {
            switch (shot)
            {
                case SavedSlotShot slot: return !SlotsOff.Contains(slot.Index);
                case CameraObjectShot cam: return !CamerasOff.Contains(cam.Camera);
                default: return true;
            }
        }

        public static void SetOn(CameraShot shot, bool on)
        {
            switch (shot)
            {
                case SavedSlotShot slot:
                    if (on) SlotsOff.Remove(slot.Index); else SlotsOff.Add(slot.Index);
                    break;
                case CameraObjectShot cam:
                    if (on) CamerasOff.Remove(cam.Camera); else CamerasOff.Add(cam.Camera);
                    break;
            }
        }

        public static void AllCamerasOn()
        {
            SlotsOff.Clear();
            CamerasOff.Clear();
        }

        // ---------------------------------------------------------------- characters

        public static List<OCIChar> AllCharacters()
        {
            var studio = Studio.Studio.Instance;
            if (studio == null) return new List<OCIChar>();
            return studio.dicObjectCtrl.OrderBy(kv => kv.Key).Select(kv => kv.Value).OfType<OCIChar>().ToList();
        }

        public static bool IsFemale(OCIChar c)
        {
            try { return c.sex == FemaleSex; }
            catch { return true; }
        }

        public static bool IsTarget(OCIChar c) =>
            CharacterChoice.TryGetValue(c, out var on) ? on : IsFemale(c);

        public static void SetTarget(OCIChar c, bool on) => CharacterChoice[c] = on;

        /// <summary>Characters that outfit and face variations will change.</summary>
        public static List<OCIChar> Targets() => AllCharacters().Where(IsTarget).ToList();

        public static void SetAllTargets(Func<OCIChar, bool> rule)
        {
            foreach (var c in AllCharacters()) CharacterChoice[c] = rule(c);
        }

        // ---------------------------------------------------------------- scene save / load

        public static void Reset()
        {
            SlotsOff.Clear();
            CamerasOff.Clear();
            CharacterChoice.Clear();
            _swapTarget = null;
        }

        public static PluginData Save()
        {
            var alive = new HashSet<ObjectCtrlInfo>(Studio.Studio.Instance.dicObjectCtrl.Values);
            CamerasOff.RemoveWhere(o => !alive.Contains(o));
            foreach (var dead in CharacterChoice.Keys.Where(o => !alive.Contains(o)).ToList())
                CharacterChoice.Remove(dead);

            if (_swapTarget != null && !alive.Contains(_swapTarget)) _swapTarget = null;

            if (SlotsOff.Count == 0 && CamerasOff.Count == 0 && CharacterChoice.Count == 0 && _swapTarget == null)
                return null;

            var data = new PluginData { version = 1 };
            data.data["slotsOff"] = SlotsOff.ToArray();
            data.data["camsOff"] = CamerasOff.Select(Key).ToArray();
            data.data["charsOn"] = CharacterChoice.Where(kv => kv.Value).Select(kv => Key(kv.Key)).ToArray();
            data.data["charsOff"] = CharacterChoice.Where(kv => !kv.Value).Select(kv => Key(kv.Key)).ToArray();
            if (_swapTarget != null) data.data["swapTarget"] = new[] { Key(_swapTarget) };
            return data;
        }

        public static void Load(PluginData data, SceneOperationKind operation, ReadOnlyDictionary<int, ObjectCtrlInfo> loaded)
        {
            if (data?.data == null) return;

            // Camera slots belong to the scene being loaded; an import keeps the current scene's slots.
            if (operation == SceneOperationKind.Load)
                foreach (var i in Ints(data.data, "slotsOff")) SlotsOff.Add(i);

            // Objects are saved by their id at save time; loadedItems maps those ids to the new objects.
            foreach (var key in Ints(data.data, "camsOff"))
                if (loaded.TryGetValue(key, out var o) && o != null) CamerasOff.Add(o);
            foreach (var key in Ints(data.data, "charsOn"))
                if (loaded.TryGetValue(key, out var o) && o != null) CharacterChoice[o] = true;
            foreach (var key in Ints(data.data, "charsOff"))
                if (loaded.TryGetValue(key, out var o) && o != null) CharacterChoice[o] = false;
            if (operation == SceneOperationKind.Load)
                foreach (var key in Ints(data.data, "swapTarget"))
                    if (loaded.TryGetValue(key, out var o) && o != null) _swapTarget = o;
        }

        private static int Key(ObjectCtrlInfo o) => o.objectInfo.dicKey;

        /// <summary>Saved int arrays can come back as int[], object[] or a list, depending on the serializer.</summary>
        private static IEnumerable<int> Ints(Dictionary<string, object> d, string name)
        {
            if (!d.TryGetValue(name, out var value) || value == null) yield break;
            if (value is IEnumerable list)
                foreach (var item in list)
                {
                    int n;
                    try { n = Convert.ToInt32(item); }
                    catch { continue; }
                    yield return n;
                }
        }
    }

    /// <summary>Hooks Camera Sweep's per-scene choices into Studio's scene save/load (via KKAPI).</summary>
    internal sealed class SweepSceneController : SceneCustomFunctionController
    {
        protected override void OnSceneSave()
        {
            try { SetExtendedData(SweepSelection.Save()); }
            catch (Exception ex) { CameraSweepPlugin.Logger.LogWarning("Could not save Camera Sweep choices: " + ex.Message); }
        }

        protected override void OnSceneLoad(SceneOperationKind operation, ReadOnlyDictionary<int, ObjectCtrlInfo> loadedItems)
        {
            if (operation != SceneOperationKind.Import) SweepSelection.Reset();
            if (operation == SceneOperationKind.Clear)
            {
                SceneName.Reset();
                return;
            }

            try { SweepSelection.Load(GetExtendedData(), operation, loadedItems); }
            catch (Exception ex) { CameraSweepPlugin.Logger.LogWarning("Could not read Camera Sweep choices: " + ex.Message); }
        }
    }
}
