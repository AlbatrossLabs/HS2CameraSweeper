using System;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;

namespace HS2CameraSweep.Sweep
{
    internal static class FileNames
    {
        private static readonly char[] Invalid = Path.GetInvalidFileNameChars().Concat(new[] { ' ', '.' }).ToArray();

        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw.Trim())
                sb.Append(Array.IndexOf(Invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        /// <summary>Resolves a config path: absolute stays as-is, relative is under the game folder.</summary>
        public static string ResolveFromGameRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return Paths.GameRootPath;
            path = path.Replace('/', Path.DirectorySeparatorChar);
            return Path.IsPathRooted(path) ? path : Path.Combine(Paths.GameRootPath, path);
        }

        /// <summary>Resolves a path relative to UserData (or absolute).</summary>
        public static string ResolveFromUserData(string path)
        {
            if (string.IsNullOrEmpty(path)) return Path.Combine(Paths.GameRootPath, "UserData");
            path = path.Replace('/', Path.DirectorySeparatorChar);
            return Path.IsPathRooted(path) ? path : Path.Combine(Path.Combine(Paths.GameRootPath, "UserData"), path);
        }
    }
}
