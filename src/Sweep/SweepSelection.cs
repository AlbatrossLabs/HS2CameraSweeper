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
    /// Per-scene choices: which cameras are switched on, and which characters are in the sweep with
    /// which variations (CharacterPlan). Saved inside the scene file and restored when it loads.
    /// Defaults: every camera on, no characters added.
    /// </summary>
    internal static class SweepSelection
    {
        private const int FemaleSex = 1; // HS2: 0 = male, 1 = female

        private static readonly HashSet<int> SlotsOff = new HashSet<int>();
        private static readonly HashSet<ObjectCtrlInfo> CamerasOff = new HashSet<ObjectCtrlInfo>();
        private static readonly List<CharacterPlan> PlanList = new List<CharacterPlan>();

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
            try { return c == null || c.sex == FemaleSex; }
            catch { return true; }
        }

        /// <summary>Characters added to the sweep that are still in the scene, in the order added.</summary>
        public static List<CharacterPlan> Plans()
        {
            var alive = new HashSet<OCIChar>(AllCharacters());
            PlanList.RemoveAll(p => p.Character == null || !alive.Contains(p.Character));
            return PlanList.ToList();
        }

        public static bool IsAdded(OCIChar c) => PlanList.Any(p => p.Character == c);

        /// <summary>
        /// Make this the only selected item in the workspace. Studio's SelectSingle toggles: called on an
        /// item that is already selected it deselects it, so only call it when needed, and without toggling.
        /// </summary>
        public static void SelectOnly(TreeNodeObject node)
        {
            var ctrl = Studio.Studio.Instance?.treeNodeCtrl;
            if (ctrl == null || node == null) return;
            if (ctrl.selectNode == node && ctrl.CheckSelect(node)) return;
            ctrl.SelectSingle(node, false);
        }

        public static CharacterPlan Add(OCIChar c)
        {
            var existing = PlanList.FirstOrDefault(p => p.Character == c);
            if (existing != null) return existing;
            var plan = new CharacterPlan(c);
            PlanList.Add(plan);
            return plan;
        }

        public static void Remove(CharacterPlan plan) => PlanList.Remove(plan);

        // ---------------------------------------------------------------- scene save / load

        public static void Reset()
        {
            SlotsOff.Clear();
            CamerasOff.Clear();
            PlanList.Clear();
        }

        public static PluginData Save()
        {
            var alive = new HashSet<ObjectCtrlInfo>(Studio.Studio.Instance.dicObjectCtrl.Values);
            CamerasOff.RemoveWhere(o => !alive.Contains(o));
            var plans = Plans();

            if (SlotsOff.Count == 0 && CamerasOff.Count == 0 && plans.Count == 0)
                return null;

            CameraSweepPlugin.Logger.LogInfo($"Scene choices saved: {SlotsOff.Count} saved view(s) off, {CamerasOff.Count} camera object(s) off, {plans.Count} character(s).");
            var data = new PluginData { version = 2 };
            data.data["slotsOff"] = SlotsOff.ToArray();
            data.data["camsOff"] = CamerasOff.Select(Key).ToArray();
            data.data["plans"] = plans.Select(SerializePlan).ToArray();
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

            foreach (var text in Strings(data.data, "plans"))
            {
                if (!DeserializePlan(text, out var key, out var stores)) continue;
                if (!loaded.TryGetValue(key, out var o) || !(o is OCIChar c) || IsAdded(c)) continue;
                PlanList.Add(new CharacterPlan(c, stores) { Expanded = false });
            }

            CameraSweepPlugin.Logger.LogInfo($"Scene choices loaded ({operation}): {SlotsOff.Count} saved view(s) off, " +
                                             $"{CamerasOff.Count} camera object(s) off, {PlanList.Count} character(s) added.");
        }

        private static int Key(ObjectCtrlInfo o) => o.objectInfo.dicKey;

        // A plan is saved as  "dicKey|kind.field=value&kind.field=value"  with values URL-escaped.
        private static string SerializePlan(CharacterPlan p)
        {
            var fields = p.Stores.SelectMany(kv => kv.Value.Values.Select(f =>
                Uri.EscapeDataString(kv.Key + "." + f.Key) + "=" + Uri.EscapeDataString(f.Value ?? "")));
            return Key(p.Character) + "|" + string.Join("&", fields.ToArray());
        }

        private static bool DeserializePlan(string text, out int key, out Dictionary<string, Dictionary<string, string>> stores)
        {
            stores = new Dictionary<string, Dictionary<string, string>>();
            key = -1;
            if (string.IsNullOrEmpty(text)) return false;

            var bar = text.IndexOf('|');
            if (bar <= 0 || !int.TryParse(text.Substring(0, bar), out key)) return false;

            foreach (var pair in text.Substring(bar + 1).Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var name = Uri.UnescapeDataString(pair.Substring(0, eq));
                var value = Uri.UnescapeDataString(pair.Substring(eq + 1));
                var dot = name.IndexOf('.');
                if (dot <= 0) continue;
                var kind = name.Substring(0, dot);
                if (!stores.TryGetValue(kind, out var d)) stores[kind] = d = new Dictionary<string, string>();
                d[name.Substring(dot + 1)] = value;
            }
            return true;
        }

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

        private static IEnumerable<string> Strings(Dictionary<string, object> d, string name)
        {
            if (!d.TryGetValue(name, out var value) || value == null) yield break;
            if (value is string one) { yield return one; yield break; }
            if (value is IEnumerable list)
                foreach (var item in list)
                    if (item is string s) yield return s;
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
