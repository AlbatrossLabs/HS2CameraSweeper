using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Studio;

namespace HS2CameraSweep.Sweep
{
    /// <summary>One variation's current value for one character, for file names.</summary>
    internal struct VariationLabel
    {
        public VariationLabel(string who, string kind, string label)
        {
            Who = who;
            Kind = kind;
            Label = label;
        }

        public string Who;   // character name
        public string Kind;  // "outfit", "face"...
        public string Label; // "casual", "smile"...
    }

    /// <summary>
    /// Builds file names (and optional subfolders) from a template such as
    ///   {scene}/{character}_{outfit}_{face}_{camera}
    ///
    /// Tokens: {prefix} {scene} {camera} {n} {date} {time} {character} {characters} {axes}
    ///         and one per variation: {swap} {outfit} {clothes} {face} {extras}
    /// With one character, {face} is just "smile". When several characters use the same variation it
    /// becomes "Aiko-smile_Kenji-grin", and {axes} names each character.
    /// A token for a variation nobody uses is empty, and the separators around it are tidied away.
    /// "/" starts a subfolder.
    /// </summary>
    internal static class NameTemplate
    {
        public const string Default = "{prefix}_{axes}_{camera}";

        public static readonly string[] Tokens =
            { "prefix", "scene", "camera", "n", "date", "time", "character", "characters", "axes", "swap", "outfit", "pose", "clothes", "face", "extras" };

        private static readonly Regex TokenRx = new Regex(@"\{([A-Za-z]+)\}", RegexOptions.Compiled);
        private static readonly Regex SeparatorRunRx = new Regex(@"[_\- ]{2,}", RegexOptions.Compiled);

        /// <param name="character">The first character in the sweep (their current name, after any swap).</param>
        /// <param name="characters">Every character in the sweep.</param>
        public static Dictionary<string, string> Values(string prefix, string camera, int n, int total, DateTime started,
            string character, IEnumerable<string> characters, IEnumerable<VariationLabel> labels)
        {
            var list = labels.Where(l => !string.IsNullOrEmpty(l.Label)).ToList();
            var several = list.Select(l => l.Who).Distinct().Count() > 1;
            string Who(VariationLabel l) => FileNames.Sanitize(l.Who);

            var digits = Math.Max(3, total.ToString().Length);
            var v = new Dictionary<string, string>
            {
                { "prefix", prefix },
                { "scene", SceneName.Current },
                { "camera", camera },
                { "n", n.ToString().PadLeft(digits, '0') },
                { "date", started.ToString("yyyyMMdd") },
                { "time", started.ToString("HHmmss") },
                { "character", FileNames.Sanitize(character ?? "") },
                { "characters", string.Join("_", characters.Select(FileNames.Sanitize).Where(x => x.Length > 0).ToArray()) },
                { "axes", string.Join("_", list.Select(l => (several ? Who(l) + "-" : "") + l.Kind + "-" + l.Label).ToArray()) },
            };

            foreach (var group in list.GroupBy(l => l.Kind))
            {
                var items = group.ToList();
                v[group.Key] = items.Count == 1
                    ? items[0].Label
                    : string.Join("_", items.Select(l => Who(l) + "-" + l.Label).ToArray());
            }

            return v;
        }

        /// <summary>Relative path without extension, e.g. "MyScene\aiko_casual_slot01".</summary>
        public static string Build(string template, IDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(template)) template = Default;

            var filled = TokenRx.Replace(template, m =>
                values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v) && v != null ? v : "");

            var segments = filled.Split('/', '\\')
                .Select(Clean)
                .Where(s => s.Length > 0)
                .ToList();

            if (segments.Count == 0) segments.Add("shot");
            return string.Join(Path.DirectorySeparatorChar.ToString(), segments.ToArray());
        }

        private static string Clean(string segment)
        {
            var s = FileNames.Sanitize(segment);
            s = SeparatorRunRx.Replace(s, m => m.Value[0].ToString());
            return s.Trim('_', '-', ' ');
        }

        /// <summary>Keeps names unique within a run: a repeat gets _2, _3, ...</summary>
        public sealed class Uniquifier
        {
            private readonly HashSet<string> _used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public string Make(string relativePath)
            {
                if (_used.Add(relativePath)) return relativePath;
                for (var i = 2; ; i++)
                {
                    var candidate = relativePath + "_" + i;
                    if (_used.Add(candidate)) return candidate;
                }
            }
        }
    }

    /// <summary>Remembers the name of the scene file last loaded or saved, for {scene}.</summary>
    internal static class SceneName
    {
        public static string Current { get; private set; } = "untitled";

        public static void Reset() => Current = "untitled";

        private static void Set(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(name)) Current = name;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(SceneInfo), nameof(SceneInfo.Load), typeof(string))]
        private static void OnLoad(string _path) => Set(_path);

        [HarmonyPrefix, HarmonyPatch(typeof(SceneInfo), nameof(SceneInfo.Load),
             new[] { typeof(string), typeof(Version) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
        private static void OnLoadVersion(string _path) => Set(_path);

        [HarmonyPrefix, HarmonyPatch(typeof(SceneInfo), nameof(SceneInfo.Save), typeof(string))]
        private static void OnSave(string _path) => Set(_path);
    }
}
