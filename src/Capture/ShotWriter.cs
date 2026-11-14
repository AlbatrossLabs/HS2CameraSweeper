using System.Collections;
using System.IO;
using HS2CameraSweep.Sweep;
using UnityEngine;
using UnityEngine.Rendering;

namespace HS2CameraSweep.Capture
{
    /// <summary>
    /// Captures what the Studio camera sees and saves it.
    ///
    /// Render: Screencap's render shot, or Camera Sweep's own renderer (BuiltInRenderer), per the
    /// "Capture engine" setting. Either way the pixels come off the GPU with AsyncGPUReadback and the rest
    /// (combining alpha, downscaling, PNG / WebP encoding) runs on worker threads, so the sweep moves on
    /// to the next camera while the previous image is still compressing. At most two images wait at once.
    /// </summary>
    internal sealed class ShotWriter
    {
        private const int MaxInFlight = 2;

        private readonly CameraSweepPlugin _plugin;
        private bool _warnedWebp;

        public ShotWriter(CameraSweepPlugin plugin)
        {
            _plugin = plugin;
        }

        /// <summary>The format files are actually written in (WebP falls back to PNG if libwebp is missing).</summary>
        public ImageFormat Format
        {
            get
            {
                var f = _plugin.Format.Value;
                return (f == ImageFormat.WebP || f == ImageFormat.WebPLossy) && !WebpEncoder.Available ? ImageFormat.Png : f;
            }
        }

        public string Extension => ExtensionOf(Format);

        public static string ExtensionOf(ImageFormat f) =>
            f == ImageFormat.Png ? ".png" : f == ImageFormat.Jpg ? ".jpg" : ".webp";

        /// <summary>Forces an engine for the next captures (engine comparison); null = the setting.</summary>
        public CaptureEngine? EngineOverride { get; set; }

        public bool UsingScreencap => (EngineOverride ?? _plugin.Engine.Value) == CaptureEngine.Screencap && ScreencapBridge.Available;

        public string Description => UsingScreencap ? ScreencapBridge.Description : "Camera Sweep renderer";

        /// <summary>Call once before a batch of shots.</summary>
        public void BeginBatch()
        {
            var f = _plugin.Format.Value;
            if ((f == ImageFormat.WebP || f == ImageFormat.WebPLossy) && !WebpEncoder.Available && !_warnedWebp)
            {
                _warnedWebp = true;
                CameraSweepPlugin.Logger.LogMessage("Camera Sweep: WebP encoder not available - saving PNG. See the log for why.");
            }

            // Screencap only applies these to its own F11 shots, so do it here for both engines.
            RenderOverrides.ApplyShadows(_plugin.ShadowResolution.Value, _plugin.ShadowCascades.Value);
            ScreencapBridge.FirePreCapture();
        }

        /// <summary>Call once after the batch (also on cancel).</summary>
        public void EndBatch()
        {
            ScreencapBridge.FirePostCapture();
            RenderOverrides.RestoreShadows();
        }

        /// <summary>Coroutine: wait for every image still being saved.</summary>
        public IEnumerator Flush() => EncodeQueue.WaitUntilBelow(0);

        /// <summary>Coroutine. fullPath should already include Extension.</summary>
        public IEnumerator Capture(string fullPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

            // Don't pile up full-size images in memory if encoding is slower than rendering.
            yield return EncodeQueue.WaitUntilBelow(MaxInFlight - 1);

            // Capture after the frame has finished rendering, like Screencap does.
            yield return new WaitForEndOfFrame();

            var format = Format;
            var transparent = _plugin.Transparent.Value && format != ImageFormat.Jpg;

            RenderTexture color = null, alpha = null;
            var factor = 1;
            var mode = transparent ? PixelMode.Straight : PixelMode.Opaque;

            if (UsingScreencap)
            {
                color = ScreencapBridge.CaptureRender(_plugin.Width.Value, _plugin.Height.Value, _plugin.Upsampling.Value, transparent);
                if (color == null)
                    CameraSweepPlugin.Logger.LogWarning("Screencap returned nothing for this shot; using Camera Sweep's renderer.");
            }

            if (color == null)
            {
                Estimate.RenderSize(_plugin, out var w, out var h, out var up);
                var result = BuiltInRenderer.Render(w, h, up, transparent, _plugin.AmbientOcclusion.Value);
                if (result == null) yield break;
                color = result.Color;
                alpha = result.Alpha;
                factor = result.CpuFactor;
                if (transparent) mode = PixelMode.Premultiplied;
            }

            var srcW = color.width;
            var srcH = color.height;
            var colorReq = AsyncGPUReadback.Request(color, 0, TextureFormat.RGBA32);
            var hasAlpha = alpha != null;
            var alphaReq = hasAlpha ? AsyncGPUReadback.Request(alpha, 0, TextureFormat.RGBA32) : colorReq;
            while (!colorReq.done || !alphaReq.done) yield return null;

            RenderTexture.ReleaseTemporary(color);
            if (hasAlpha) RenderTexture.ReleaseTemporary(alpha);

            if (colorReq.hasError || alphaReq.hasError)
            {
                CameraSweepPlugin.Logger.LogError($"GPU readback failed for {Path.GetFileName(fullPath)}");
                yield break;
            }

            var colorBytes = colorReq.GetData<byte>().ToArray();
            var alphaBytes = hasAlpha ? alphaReq.GetData<byte>().ToArray() : null;
            var linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var quality = Mathf.Clamp(_plugin.Quality.Value, 1, 100);
            var effort = _plugin.WebpEffort.Value;

            EncodeQueue.Run(() =>
            {
                var pixels = PixelProcessor.Process(colorBytes, alphaBytes, srcW, srcH, factor, mode, linear,
                    format != ImageFormat.Jpg, out var width, out var height, out var channels);
                colorBytes = alphaBytes = null;

                switch (format)
                {
                    case ImageFormat.WebP:
                        WebpEncoder.Write(fullPath, pixels, width, height, channels, true, 100f, effort);
                        break;
                    case ImageFormat.WebPLossy:
                        WebpEncoder.Write(fullPath, pixels, width, height, channels, false, quality, Mathf.Min(effort, 6));
                        break;
                    case ImageFormat.Jpg:
                        // Unity's JPG encoder needs the main thread; the pixels are already bottom-up RGB for it.
                        EncodeQueue.OnMainThread(() =>
                        {
                            var tex = new Texture2D(width, height, TextureFormat.RGB24, false, true);
                            try
                            {
                                tex.LoadRawTextureData(pixels);
                                tex.Apply(false);
                                var jpg = tex.EncodeToJPG(quality);
                                EncodeQueue.Run(() => File.WriteAllBytes(fullPath, jpg));
                            }
                            finally
                            {
                                Object.Destroy(tex);
                            }
                        });
                        break;
                    default:
                        PngEncoder.Write(fullPath, pixels, width, height, channels);
                        break;
                }
            });
        }
    }
}
