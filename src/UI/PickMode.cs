using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using HS2CameraSweep.Axes;
using HS2CameraSweep.Sweep;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.UI
{
    /// <summary>
    /// Picking files with Studio's own lists. While pick mode is on, the Load / Change button of Studio's
    /// character list, a character's Costume list and the Pose list add the selected file to the Camera
    /// Sweep list being filled instead of loading it. Whatever improves those lists (Browser Folders,
    /// Studio Character Sort, search plugins) works here too, since the lists themselves are untouched.
    /// "Add all in this list" takes everything the last-used list is showing (e.g. the current folder).
    ///
    /// Other plugins can pick for Camera Sweep too: the public members below are a small API, read by
    /// reflection (Studio Browser's Character, Outfit and Pose browsers use it, so a double-click there adds
    /// the card instead of loading it). Kind matches Studio Browser's browser ids.
    /// </summary>
    public static class PickMode
    {
        /// <summary>Bumped when the members below change in a way callers must know about.</summary>
        public const int ApiVersion = 1;
        private static CardList _list;
        private static int _added;
        private static Func<IEnumerable<string>> _lastListFiles; // files shown by the list last clicked
        private static string _lastListName = "";

        public static bool Active => _list != null;

        /// <summary>What's being picked: "characters", "outfits" or "poses"; null when not picking.</summary>
        public static string Kind
        {
            get
            {
                if (_list == null) return null;
                switch (_list.Kind)
                {
                    case PickKind.Character: return "characters";
                    case PickKind.Outfit: return "outfits";
                    default: return "poses";
                }
            }
        }

        /// <summary>Sex the picked cards must be for (0 male, 1 female); -1 when it doesn't matter (poses).</summary>
        public static int Sex => _list == null || _list.Kind == PickKind.Pose ? -1 : SweepSelection.IsFemale(_list.Character) ? 1 : 0;

        /// <summary>Name of the character the list belongs to.</summary>
        public static string Target => _list?.Character != null ? SweepRunner.CharName(_list.Character) : "";

        /// <summary>How many files the list holds now.</summary>
        public static int Count => _list?.Count ?? 0;

        /// <summary>True if the file is in the list already.</summary>
        public static bool Contains(string path) =>
            _list != null && !string.IsNullOrEmpty(path) && _list.Files().Any(f => SamePath(f, path));

        /// <summary>Adds files to the list being picked for; returns how many were new. Logs a message like Studio's lists do.</summary>
        public static int Add(string[] paths)
        {
            if (_list == null || paths == null) return 0;
            var before = _added;
            Accept(paths, paths.Length > 1);
            return _added - before;
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        internal static bool IsPickingFor(CardList list) => _list == list;

        internal static void Start(CardList list)
        {
            _list = list;
            _added = 0;
            _lastListFiles = null;

            var studio = Studio.Studio.Instance;
            var oci = list.Character;
            try
            {
                // The Costume and Pose lists work on the selected character, so select ours.
                if (list.Kind != PickKind.Character && oci?.treeNodeObject != null && studio != null)
                    SweepSelection.SelectOnly(oci.treeNodeObject);

                // Open the right Studio list straight away (the selection above needs a frame to land).
                CameraSweepPlugin.Instance.StartCoroutine(OpenList(list.Kind, oci));
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogDebug("Pick mode: " + ex.Message);
            }
        }

        public static void Stop()
        {
            if (_list != null && _added > 0)
                CameraSweepPlugin.Logger.LogMessage($"Camera Sweep: added {_added} {_list.What}.");
            _list = null;
            _lastListFiles = null;
        }

        // ---------------------------------------------------------------- banner

        public static void DrawBanner()
        {
            if (_list == null) return;

            var who = _list.Character != null ? SweepRunner.CharName(_list.Character) : "";
            string how;
            var browser = BrowserFor(_list.Kind);
            switch (_list.Kind)
            {
                case PickKind.Character:
                    how = browser != null
                        ? $"Double-click cards in the {browser} (or select a card in the {(SweepSelection.IsFemale(_list.Character) ? "Female" : "Male")} list and press Load)."
                        : $"In the {(SweepSelection.IsFemale(_list.Character) ? "Female" : "Male")} character list, select a card and press Load or Change.";
                    break;
                case PickKind.Outfit:
                    how = browser != null
                        ? $"Double-click outfits in the {browser} (or select one in {who}'s Costume list and press Load)."
                        : $"In {who}'s Costume list, select an outfit and press Load.";
                    break;
                default:
                    how = browser != null
                        ? $"Double-click poses in the {browser} (or select one in the Pose list and press Load)."
                        : "In the Pose list, select a pose and press Load.";
                    break;
            }

            var w = Mathf.Min(620f, Screen.width - 40f);
            // Bottom of the screen, above the progress bar's spot, clear of Studio's menus at the top.
            var area = new Rect((Screen.width - w) / 2f, Screen.height - 112f, w, 64f);
            GUI.Box(area, "");
            GUILayout.BeginArea(new Rect(area.x + 8, area.y + 6, area.width - 16, area.height - 10));
            GUILayout.Label($"<b>Camera Sweep - picking {_list.What} for {who}</b>   <color={Theme.HintHex}>{_list.Count} in the list" +
                            (_added > 0 ? $", {_added} added now" : "") + "</color>", Theme.Rich);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Theme.Hint(how), Theme.Rich);
            GUI.enabled = _lastListFiles != null;
            if (GUILayout.Button(new GUIContent("Add all in this list", "Everything the list you last clicked in is showing" +
                                                                       (_lastListName.Length > 0 ? $" ({_lastListName})" : "")), GUILayout.Width(130)))
                Accept(SafeFiles(), true);
            GUI.enabled = true;
            if (GUILayout.Button("Done", Theme.Primary, GUILayout.Width(70))) Stop();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// <summary>Studio Browser's browser for this kind, if it's installed and can pick for Camera Sweep.</summary>
        private static string BrowserFor(PickKind kind)
        {
            if (!_browserLooked)
            {
                _browserLooked = true;
                // Studio Browser builds that pick for Camera Sweep have CameraSweepBridge.PickingFor.
                var bridge = AccessTools.TypeByName("HS2StudioBrowser.Game.CameraSweepBridge");
                _browserCanPick = bridge != null && AccessTools.Method(bridge, "PickingFor") != null;
            }
            if (!_browserCanPick) return null;
            switch (kind)
            {
                case PickKind.Character: return AccessTools.TypeByName("HS2StudioBrowser.Characters.CharacterKind") != null ? "Character Browser" : null;
                case PickKind.Outfit: return AccessTools.TypeByName("HS2StudioBrowser.Outfits.OutfitKind") != null ? "Outfit Browser" : null;
                default: return AccessTools.TypeByName("HS2StudioBrowser.Poses.PoseKind") != null ? "Pose Browser" : null;
            }
        }

        private static bool _browserLooked, _browserCanPick;

        private static IEnumerable<string> SafeFiles()
        {
            try { return _lastListFiles?.Invoke()?.ToList() ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        private static void Accept(IEnumerable<string> files, bool many)
        {
            if (_list == null) return;
            var list = files.Where(f => !string.IsNullOrEmpty(f) && File.Exists(f)).ToList();
            var added = _list.Add(list);
            _added += added;
            var name = list.Count == 1 ? Path.GetFileNameWithoutExtension(list[0]) : $"{list.Count} files";
            CameraSweepPlugin.Logger.LogMessage(added > 0
                ? $"Camera Sweep: added {(many ? $"{added} {_list.What}" : name)}."
                : $"Camera Sweep: {name} {(list.Count == 1 ? "is" : "are")} already in the list.");
        }

        private static IEnumerator OpenList(PickKind kind, OCIChar oci)
        {
            yield return null;
            if (_list == null) yield break;
            try
            {
                switch (kind)
                {
                    case PickKind.Character: OpenCharaList(SweepSelection.IsFemale(oci)); break;
                    case PickKind.Outfit: OpenCostumeList(); break;
                    default: OpenPoseList(oci); break;
                }
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogDebug("Pick mode: couldn't open the list: " + ex.Message);
            }
        }

        /// <summary>Add > Character > Female (or Male): Studio's root Add menu, then the right character list.</summary>
        private static void OpenCharaList(bool female)
        {
            var studio = Studio.Studio.Instance;
            var root = studio?.rootButtonCtrl;
            if (root == null) return;
            if (root.select != 0) root.OnClick(0); // 0 = Add (OnClick toggles, so only when it isn't open)

            var chara = Resources.FindObjectsOfTypeAll<CharaList>()
                .FirstOrDefault(c => Traverse.Create(c).Field("sex").GetValue<int>() == (female ? 1 : 0));
            var add = Resources.FindObjectsOfTypeAll<AddButtonCtrl>().FirstOrDefault();
            if (chara == null || add == null) return;

            // Find which Add sub-menu holds that list and open it.
            var infos = Traverse.Create(add).Field("commonInfo").GetValue<Array>();
            var select = Traverse.Create(add).Field("select").GetValue<int>();
            if (infos == null) return;
            for (var i = 0; i < infos.Length; i++)
            {
                var obj = Traverse.Create(infos.GetValue(i)).Field("obj").GetValue<GameObject>();
                if (obj == null || !chara.transform.IsChildOf(obj.transform)) continue;
                if (select != i) add.OnClick(i);
                break;
            }
        }

        /// <summary>The selected character's Costume tab (Manipulate > character > Costume).</summary>
        private static void OpenCostumeList()
        {
            var root = Studio.Studio.Instance?.rootButtonCtrl;
            if (root == null) return;
            if (root.select != 1) root.OnClick(1); // 1 = Manipulate
            var mp = Resources.FindObjectsOfTypeAll<MPCharCtrl>().FirstOrDefault();
            mp?.OnClickRoot(4); // 4 = Costume (0 State, 1 FK/IK, 2 Anim, 3 Voice, 4 Costume, 5 Joint)
        }

        private static void OpenPoseList(OCIChar oci)
        {
            var list = Resources.FindObjectsOfTypeAll<PauseRegistrationList>().FirstOrDefault();
            if (list == null || oci == null) return;
            AccessTools.Property(typeof(PauseRegistrationList), "ociChar")?.SetValue(list, oci, null);
            AccessTools.Property(typeof(PauseRegistrationList), "active")?.SetValue(list, true, null);
        }

        // ---------------------------------------------------------------- Studio list hooks

        public static void Install(Harmony harmony)
        {
            var self = typeof(PickMode);
            void Prefix(Type type, string method, string patch)
            {
                var target = type == null ? null : AccessTools.Method(type, method);
                if (target == null) { CameraSweepPlugin.Logger.LogWarning($"Pick mode: {type?.Name}.{method} not found - that list can't be used for picking."); return; }
                harmony.Patch(target, prefix: new HarmonyMethod(self, patch));
            }
            void Postfix(Type type, string method, string patch)
            {
                var target = type == null ? null : AccessTools.Method(type, method);
                if (target != null) harmony.Patch(target, postfix: new HarmonyMethod(self, patch));
            }

            var costume = AccessTools.TypeByName("Studio.MPCharCtrl+CostumeInfo");

            Prefix(typeof(CharaList), "LoadCharaFemale", nameof(CharaLoadPrefix));
            Prefix(typeof(CharaList), "LoadCharaMale", nameof(CharaLoadPrefix));
            Prefix(typeof(CharaList), "ChangeCharaFemale", nameof(CharaLoadPrefix));
            Prefix(typeof(CharaList), "ChangeCharaMale", nameof(CharaLoadPrefix));
            Prefix(costume, "OnClickLoad", nameof(CostumeLoadPrefix));
            Prefix(typeof(PauseRegistrationList), "OnClickLoad", nameof(PoseLoadPrefix));

            Postfix(typeof(CharaList), "OnSelectChara", nameof(CharaSelectPostfix));
            Postfix(costume, "OnSelect", nameof(CostumeSelectPostfix));
            Postfix(typeof(PauseRegistrationList), "OnClickSelect", nameof(PoseSelectPostfix));
        }

        private static CharaFileSort FileSort(object owner, string field) =>
            Traverse.Create(owner).Field(field).GetValue<CharaFileSort>();

        private static IEnumerable<string> SortFiles(CharaFileSort sort) =>
            sort?.cfiList == null ? Enumerable.Empty<string>() : sort.cfiList.Select(c => c?.file);

        // Character list (Add > Character): Load and Change.
        private static bool CharaLoadPrefix(CharaList __instance)
        {
            if (_list == null || _list.Kind != PickKind.Character) return true;

            var listSex = Traverse.Create(__instance).Field("sex").GetValue<int>();
            var wantFemale = SweepSelection.IsFemale(_list.Character);
            if ((listSex == 1) != wantFemale)
            {
                CameraSweepPlugin.Logger.LogMessage($"Camera Sweep: that's the {(listSex == 1 ? "female" : "male")} list - " +
                                                    $"{SweepRunner.CharName(_list.Character)} needs {(wantFemale ? "female" : "male")} cards.");
                return false;
            }

            Accept(new[] { FileSort(__instance, "charaFileSort")?.selectPath }, false);
            return false;
        }

        private static void CharaSelectPostfix(CharaList __instance)
        {
            if (_list == null || _list.Kind != PickKind.Character) return;
            var sort = FileSort(__instance, "charaFileSort");
            _lastListFiles = () => SortFiles(sort);
            _lastListName = "character list";
        }

        // A character's Costume list: Load.
        private static bool CostumeLoadPrefix(object __instance)
        {
            if (_list == null || _list.Kind != PickKind.Outfit) return true;
            Accept(new[] { FileSort(__instance, "fileSort")?.selectPath }, false);
            return false;
        }

        private static void CostumeSelectPostfix(object __instance)
        {
            if (_list == null || _list.Kind != PickKind.Outfit) return;
            var sort = FileSort(__instance, "fileSort");
            _lastListFiles = () => SortFiles(sort);
            _lastListName = "costume list";
        }

        // Pose list: Load.
        private static bool PoseLoadPrefix(PauseRegistrationList __instance)
        {
            if (_list == null || _list.Kind != PickKind.Pose) return true;
            var t = Traverse.Create(__instance);
            var paths = t.Field("listPath").GetValue<List<string>>();
            var select = t.Field("select").GetValue<int>();
            if (paths != null && select >= 0 && select < paths.Count) Accept(new[] { paths[select] }, false);
            return false;
        }

        private static void PoseSelectPostfix(PauseRegistrationList __instance)
        {
            if (_list == null || _list.Kind != PickKind.Pose) return;
            var paths = Traverse.Create(__instance).Field("listPath").GetValue<List<string>>();
            _lastListFiles = () => paths?.ToList() ?? new List<string>();
            _lastListName = "pose list";
        }
    }
}
