using System;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using HS2CameraSweep.Capture;
using UnityEngine;

namespace HS2CameraSweep.Sweep
{
    /// <summary>
    /// The window's "how many / how big / how long" line.
    ///
    /// Size and time start as rough guesses and switch to your own numbers after a sweep: each run
    /// records bytes per pixel and seconds per shot (or per animation cycle for video) in F1 under
    /// "Stats (measured)". Changing resolution or upsampling scales the measured numbers.
    /// </summary>
    internal sealed class Estimate
    {
        // Rough defaults until a run has been measured.
        private const double GuessBytesPerPixel = 1.6;      // opaque PNG of a 3D render
        private const double GuessBytesPerPixelAlpha = 2.4; // RGBA PNG
        private const double GuessSecondsBase = 0.6;        // settle frames, readback, encode
        private const double GuessSecondsPerMegapixel = 0.08;

        public int Cameras;
        public int Combinations;
        public int Total;
        public string Breakdown;
        public bool Video;

        public int Width;
        public int Height;
        public int Upsampling;

        public long BytesEach;   // -1 = unknown
        public double SecondsEach; // -1 = unknown
        public bool SizeMeasured;
        public bool TimeMeasured;
        public long FreeBytes = -1;

        public long BytesTotal => BytesEach < 0 ? -1 : BytesEach * Total;
        public double SecondsTotal => SecondsEach < 0 ? -1 : SecondsEach * Total;

        public static Estimate Compute(CameraSweepPlugin plugin, int camerasOn)
        {
            var e = new Estimate { Cameras = camerasOn, Video = plugin.Mode.Value == OutputMode.Video };

            var parts = new System.Collections.Generic.List<string> { $"{camerasOn} camera{(camerasOn == 1 ? "" : "s")}" };
            e.Combinations = 1;
            foreach (var axis in plugin.Axes.Where(a => a.Enabled))
            {
                var n = Math.Max(0, axis.Count);
                if (n == 0)
                {
                    // The runner skips an axis with nothing to cycle, so it doesn't multiply.
                    parts.Add($"no {axis.Name} (skipped)");
                    continue;
                }
                e.Combinations *= n;
                parts.Add($"{n} {axis.Name}");
            }
            e.Total = camerasOn * e.Combinations;
            e.Breakdown = string.Join(" × ", parts.ToArray());

            RenderSize(plugin, out e.Width, out e.Height, out e.Upsampling);

            if (e.Video)
            {
                var cycles = plugin.VideoCycles.Value;
                var prewarm = plugin.VideoPrewarm.Value;
                var bpc = plugin.StatVideoBytesPerCycle.Value;
                var spc = plugin.StatVideoSecondsPerCycle.Value;
                e.BytesEach = bpc > 0 ? (long)(bpc * cycles) : -1;
                e.SecondsEach = spc > 0 ? spc * (cycles + prewarm) : -1;
                e.SizeMeasured = bpc > 0;
                e.TimeMeasured = spc > 0;
            }
            else
            {
                var alpha = plugin.Transparent.Value && ScreencapBridge.Available;
                var measuredBpp = alpha ? plugin.StatBytesPerPixelAlpha.Value : plugin.StatBytesPerPixel.Value;
                var bpp = measuredBpp > 0 ? measuredBpp : alpha ? GuessBytesPerPixelAlpha : GuessBytesPerPixel;
                e.BytesEach = (long)(bpp * e.Width * e.Height);
                e.SizeMeasured = measuredBpp > 0;

                var work = WorkMegapixels(e.Width, e.Height, e.Upsampling);
                var measuredSec = plugin.StatSecondsPerShot.Value;
                var measuredWork = plugin.StatWorkMegapixels.Value;
                if (measuredSec > 0 && measuredWork > 0)
                {
                    // Scale with rendered pixels, but not wildly: most of a shot's time is fixed overhead.
                    var ratio = Mathf.Clamp((float)(work / measuredWork), 0.25f, 4f);
                    e.SecondsEach = measuredSec * (0.5 + 0.5 * ratio);
                    e.TimeMeasured = true;
                }
                else
                {
                    e.SecondsEach = GuessSecondsBase + GuessSecondsPerMegapixel * work;
                }
            }

            try
            {
                var root = Path.GetPathRoot(FileNames.ResolveFromGameRoot(plugin.OutputFolder.Value));
                if (!string.IsNullOrEmpty(root)) e.FreeBytes = new DriveInfo(root).AvailableFreeSpace;
            }
            catch
            {
                // Network drives and odd paths: just don't show free space.
            }

            return e;
        }

        public static void RenderSize(CameraSweepPlugin plugin, out int width, out int height, out int upsampling)
        {
            ScreencapBridge.GetRenderSettings(out var sw, out var sh, out var su);
            width = plugin.Width.Value > 0 ? plugin.Width.Value : sw;
            height = plugin.Height.Value > 0 ? plugin.Height.Value : sh;
            upsampling = plugin.Upsampling.Value > 0 ? plugin.Upsampling.Value : Math.Max(1, su);
            if (!ScreencapBridge.Available) upsampling = 1;
        }

        public static double WorkMegapixels(int w, int h, int up) => (double)w * h * up * up / 1e6;

        // ---------------------------------------------------------------- recording

        /// <summary>Called after a sweep: turns what it produced into per-shot numbers for next time.</summary>
        public static void Record(CameraSweepPlugin plugin, string runFolder, DateTime started, int done, double seconds,
            bool video, bool transparent, int width, int height, int upsampling, float cycles, int prewarm)
        {
            if (done <= 0 || seconds <= 0 || !Directory.Exists(runFolder)) return;

            try
            {
                // Templates can make subfolders, and the output folder may hold earlier runs: only count this run's files.
                var files = new DirectoryInfo(runFolder).GetFiles("*", SearchOption.AllDirectories)
                    .Where(f => f.LastWriteTime >= started.AddSeconds(-2)).ToArray();
                if (video)
                {
                    var vids = files.Where(f => f.Extension != ".png").ToArray();
                    if (vids.Length == 0 || cycles <= 0) return;
                    plugin.StatVideoBytesPerCycle.Value = (float)(vids.Sum(f => f.Length) / (double)vids.Length / cycles);
                    plugin.StatVideoSecondsPerCycle.Value = (float)(seconds / done / (cycles + prewarm));
                }
                else
                {
                    var pngs = files.Where(f => f.Extension == ".png").ToArray();
                    if (pngs.Length == 0) return;

                    // Use the real size of what was written; Screencap may round the resolution.
                    if (!PngSize(pngs[0].FullName, out var w, out var h)) { w = width; h = height; }
                    var bpp = (float)(pngs.Sum(f => f.Length) / (double)pngs.Length / ((double)w * h));
                    if (transparent) plugin.StatBytesPerPixelAlpha.Value = bpp;
                    else plugin.StatBytesPerPixel.Value = bpp;

                    plugin.StatSecondsPerShot.Value = (float)(seconds / done);
                    plugin.StatWorkMegapixels.Value = (float)WorkMegapixels(w, h, upsampling);
                }
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogDebug("Could not record sweep stats: " + ex.Message);
            }
        }

        private static bool PngSize(string path, out int w, out int h)
        {
            w = h = 0;
            try
            {
                var b = new byte[24];
                using (var fs = File.OpenRead(path))
                    if (fs.Read(b, 0, 24) != 24) return false;
                // PNG signature (8) + IHDR length/type (8), then width and height, big-endian.
                w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                return w > 0 && h > 0;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- formatting

        public static string Size(long bytes)
        {
            if (bytes < 0) return "?";
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.#} MB";
            return $"{bytes / 1024.0:0} KB";
        }

        public static string Duration(double seconds)
        {
            if (seconds < 0) return "?";
            var t = TimeSpan.FromSeconds(Math.Round(seconds));
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} min {t.Seconds} s";
            return $"{t.Seconds} s";
        }
    }
}
