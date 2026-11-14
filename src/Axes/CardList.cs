using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HS2CameraSweep.Sweep;
using HS2CameraSweep.UI;
using Studio;
using UnityEngine;

namespace HS2CameraSweep.Axes
{
    /// <summary>What kind of file a list holds, so the right Studio list is used to pick them.</summary>
    internal enum PickKind
    {
        Character, // Studio's Add > Character list
        Outfit,    // the character's Costume list (Anim > Costume)
        Pose,      // the character's Pose list
    }

    /// <summary>
    /// A hand-picked list of files (character cards, coordinate cards, pose files) for one character's
    /// variation. Files are picked in Studio's own lists (so Browser Folders, sorting and search all work):
    /// "Pick in Studio" turns on pick mode, and while it's on, the list's Load button adds the selected file
    /// here instead of loading it - as many as you like - until Done. Nothing has to be moved or reorganised.
    /// Saved with the scene as one path per line; a folder entry (from older versions) means every file in it.
    /// </summary>
    internal sealed class CardList
    {
        private readonly Setting<string> _entries;
        private readonly string _what;          // "character cards"
        private readonly string _pattern;       // "*.png"
        private readonly PickKind _kind;
        private readonly OCIChar _character;

        private List<string> _files = new List<string>();
        private float _filesTime = -10f;
        private string _filesSource;

        public CardList(Setting<string> entries, string what, string pattern, PickKind kind, OCIChar character)
        {
            _entries = entries;
            _what = what;
            _pattern = pattern;
            _kind = kind;
            _character = character;
        }

        public PickKind Kind => _kind;
        public OCIChar Character => _character;
        public string What => _what;
        /// <summary>The picked entries: files, or folders (meaning every matching file in them).</summary>
        public List<string> Entries =>
            (_entries.Value ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).Where(e => e.Length > 0).ToList();

        /// <summary>Every file the entries stand for, in the order picked, no duplicates (refreshed at most once a second).</summary>
        public List<string> Files()
        {
            var source = _entries.Value ?? "";
            if (source == _filesSource && Time.realtimeSinceStartup - _filesTime < 1f) return _files;
            _filesSource = source;
            _filesTime = Time.realtimeSinceStartup;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            foreach (var entry in Entries)
            {
                var path = FileNames.ResolveFromUserData(entry);
                try
                {
                    if (Directory.Exists(path))
                    {
                        foreach (var f in Directory.GetFiles(path, _pattern).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                            if (seen.Add(f)) files.Add(f);
                    }
                    else if (File.Exists(path) && seen.Add(path))
                    {
                        files.Add(path);
                    }
                }
                catch
                {
                    // Unreadable folder: skip it.
                }
            }

            _files = files;
            return _files;
        }

        public int Count => Files().Count;

        /// <summary>File-name-safe labels, numbered when two files share a name.</summary>
        public static List<string> Labels(IEnumerable<string> paths)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var labels = new List<string>();
            foreach (var p in paths)
            {
                var label = FileNames.Sanitize(Path.GetFileNameWithoutExtension(p));
                if (label.Length == 0) label = "item";
                if (seen.TryGetValue(label, out var n)) { seen[label] = n + 1; label += n + 1; }
                else seen[label] = 1;
                labels.Add(label);
            }
            return labels;
        }

        // ---------------------------------------------------------------- editor

        public void Draw()
        {
            var entries = Entries;
            var userData = Path.GetFullPath(Path.Combine(Paths.GameRootPath, "UserData")).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

            for (var i = 0; i < entries.Count; i++)
            {
                var path = FileNames.ResolveFromUserData(entries[i]);
                var isFolder = Directory.Exists(path);
                var shown = path.StartsWith(userData, StringComparison.OrdinalIgnoreCase) ? path.Substring(userData.Length) : path;
                var name = isFolder
                    ? $"[folder] {Path.GetFileName(path.TrimEnd('\\', '/'))}  <color=#8B91A1>({SafeCount(path)})</color>"
                    : Path.GetFileNameWithoutExtension(path) + (File.Exists(path) ? "" : "  <color=#F87171>(missing)</color>");

                GUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent(name, shown), Theme.Rich, GUILayout.Width(250));
                if (GUILayout.Button(new GUIContent("x", "Remove from the list"), GUILayout.Width(22)))
                {
                    entries.RemoveAt(i);
                    Save(entries);
                    GUILayout.EndHorizontal();
                    break;
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            var picking = PickMode.IsPickingFor(this);
            if (GUILayout.Toggle(picking, new GUIContent(picking ? "Picking… (Done in the banner)" : "+ Pick in Studio's list",
                    "Use Studio's own list (with your folders, sorting and search). Its Load button adds files here until you press Done."),
                    Theme.Choice) != picking)
            {
                if (picking) PickMode.Stop();
                else PickMode.Start(this);
                Gui.Relayout();
            }
            if (entries.Count > 0 && GUILayout.Button("Clear", GUILayout.Width(50)))
                Save(new List<string>());
            GUILayout.EndHorizontal();

            var count = Count;
            if (entries.Count == 0)
                GUILayout.Label(Theme.Hint($"No {_what} yet. They stay where they are - nothing to move."), Theme.Rich);
            else if (count == 0)
                GUILayout.Label(Theme.Hint($"None of the listed {_what} were found."), Theme.Rich);
        }

        private int SafeCount(string folder)
        {
            try { return Directory.GetFiles(folder, _pattern).Length; }
            catch { return 0; }
        }

        private void Save(List<string> entries)
        {
            _entries.Value = string.Join("\n", entries.ToArray());
            _filesTime = -10f;
        }

        /// <summary>Adds files (skipping ones already listed). Returns how many were new.</summary>
        public int Add(IEnumerable<string> paths)
        {
            var entries = Entries;
            var have = new HashSet<string>(entries.Select(FileNames.ResolveFromUserData), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                var full = Path.GetFullPath(p);
                if (!have.Add(full)) continue;
                entries.Add(full);
                added++;
            }
            if (added > 0) Save(entries);
            return added;
        }

        /// <summary>UserData subfolder, falling back to UserData itself.</summary>
        public static string UserDataFolder(string relative)
        {
            var full = Path.Combine(Path.Combine(Paths.GameRootPath, "UserData"), relative);
            return Directory.Exists(full) ? full : Path.Combine(Paths.GameRootPath, "UserData");
        }
    }
}
