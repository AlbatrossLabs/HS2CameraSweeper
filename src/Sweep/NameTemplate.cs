using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using Studio;

namespace HS2CameraSweep.Sweep
{
    /// <summary>
    /// Builds file names (and optional subfolders) from a template such as
    ///   {scene}/{character}_{outfit}_{face}_{camera}
    ///
    /// Tokens: {prefix} {scene} {camera} {n} {date} {time} {character} {axes}
    ///         and one per variation: {swap} {outfit} {clothes} {face} {extras}
    /// A token for a variation that's off is empty, and the separators around it are tidied away.
    /// "/" starts a subfolder.
    /// </summary>
    internal static class NameTemplate
    {
        public const string Default = "{prefix}_{axes}_{camera}";

        public static readonly string[] Tokens =
            { "prefix", "scene", "camera", "n", "date", "time", "character", "axes", "swap", "outfit", "clothes", "face", "extras" };

        private static readonly Regex TokenRx = new Regex(@"\{([A-Za-z]+)\}", RegexOptions.Compiled);
        private static readonly Regex SeparatorRunRx = new Regex(@"[_\- ]{2,}", RegexOptions.Compiled);

        /// <summary>Token values for one shot. axisLabels = (variation name, value label) for the variations in use.</summary>
        public static Dictionary<string, string> Values(string prefix, string camera, int n, int total, DateTime started,
            string character, IEnumerable<KeyValuePair<string, string>> axisLabels)
        {
            var axes = axisLabels.ToList();
            var digits = Math.Max(3, total.ToString().Length);
            var v = new Dictionary<string, string>
            {
                { "prefix", prefix },
                { "scene", SceneName.Current },
                { "camera", camera },
                { "n", n.ToString().PadLeft(digits, '0') },
                { "date", started.ToString("yyyyMMdd") },
                { "time", started.ToString("HHmmss") },
                { "character", character ?? "" },
                { "axes", string.Join("_", axes.Select(kv => kv.Key + "-" + kv.Value).ToArray()) },
            };
            foreach (var kv in axes) v[kv.Key] = kv.Value;
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
