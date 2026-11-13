using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace HS2CameraSweep.Capture
{
    /// <summary>
    /// Captures what the Studio camera currently sees and writes it to disk as PNG.
    /// Prefers Screencap (same quality, upsampling, DOF handling and alpha as its render shot);
    /// falls back to rendering Camera.main directly.
    ///
    /// Pixels are pulled off the GPU with AsyncGPUReadback in the same formats Screencap's own writer
    /// uses (RGBA32 for alpha, RGBAFloat for opaque), so colours match a normal Screencap shot.
    /// </summary>
    internal sealed class ShotWriter
    {
        private readonly CameraSweepPlugin _plugin;

        public ShotWriter(CameraSweepPlugin plugin)
        {
            _plugin = plugin;
        }

        public string Extension => ".png";

        public bool UsingScreencap => ScreencapBridge.Available;

        public string Description => ScreencapBridge.Description;

        /// <summary>Call once before a batch of shots.</summary>
        public void BeginBatch() => ScreencapBridge.FirePreCapture();

        /// <summary>Call once after the batch.</summary>
        public void EndBatch() => ScreencapBridge.FirePostCapture();

        /// <summary>Must be run as a coroutine. Path should already include the extension.</summary>
        public IEnumerator Capture(string fullPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

            // Capture after the frame has finished rendering, like Screencap does.
            yield return new WaitForEndOfFrame();

            var transparent = _plugin.Transparent.Value;
            RenderTexture rt = null;

            if (UsingScreencap)
            {
                rt = ScreencapBridge.CaptureRender(_plugin.Width.Value, _plugin.Height.Value, _plugin.Upsampling.Value, transparent);
                if (rt == null)
                    CameraSweepPlugin.Logger.LogWarning("Screencap returned nothing for this shot; using built-in renderer.");
            }

            if (rt == null)
            {
                transparent = false;
                rt = RenderMainCamera();
                if (rt == null) yield break;
            }

            yield return WritePng(rt, transparent, fullPath);
        }

        /// <summary>Plain render of the main camera at the configured (or screen) size. No UI, no alpha.</summary>
        private RenderTexture RenderMainCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                CameraSweepPlugin.Logger.LogError("No main camera to capture from.");
                return null;
            }

            var w = _plugin.Width.Value > 0 ? _plugin.Width.Value : Screen.width;
            var h = _plugin.Height.Value > 0 ? _plugin.Height.Value : Screen.height;

            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.Default, RenderTextureReadWrite.Default);
            var prevTarget = cam.targetTexture;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
            }
            finally
            {
                cam.targetTexture = prevTarget;
            }

            return rt;
        }

        /// <summary>Reads the texture back from the GPU and encodes it. Releases the RenderTexture.</summary>
        private static IEnumerator WritePng(RenderTexture rt, bool alpha, string fullPath)
        {
            var width = rt.width;
            var height = rt.height;

            var req = AsyncGPUReadback.Request(rt, 0, 0, width, 0, height, 0, 1,
                alpha ? TextureFormat.RGBA32 : TextureFormat.RGBAFloat);
            while (!req.done) yield return null;

            RenderTexture.ReleaseTemporary(rt);

            if (req.hasError)
            {
                CameraSweepPlugin.Logger.LogError($"GPU readback failed for {Path.GetFileName(fullPath)}");
                yield break;
            }

            // linear = true: store the values as-is, no extra colour-space conversion.
            var tex = new Texture2D(width, height, alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false, true);
            try
            {
                if (alpha)
                {
                    var data = req.GetData<Color32>();
                    tex.SetPixels32(data.ToArray());
                }
                else
                {
                    var data = req.GetData<Color>();
                    tex.SetPixels(data.ToArray());
                }

                tex.Apply(false);
                File.WriteAllBytes(fullPath, tex.EncodeToPNG());
            }
            catch (Exception ex)
            {
                CameraSweepPlugin.Logger.LogError($"Failed to write {fullPath}: {ex}");
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }
    }
}
